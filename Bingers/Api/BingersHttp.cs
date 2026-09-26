using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Bingers.Helpers;
using Microsoft.Extensions.Logging;

namespace Bingers.Api;

/// <summary>
/// Shared HTTP plumbing for bingers.app.
/// Cookies are handled manually per Jellyfin user, so the handler must never keep its own cookie container,
/// and redirects are not followed so the magic link verification can capture the session cookies.
/// </summary>
internal static class BingersHttp
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static readonly HttpClient Client = CreateClient();

    private static readonly TimeSpan[] _retryDelays = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) };

    public static HttpRequestMessage CreateRequest(HttpMethod method, string url, string cookieHeader = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Origin", BingersUris.WebBaseUrl);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        return request;
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ILogger logger, bool verbose, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            logger.LogVerbose(
                verbose,
                "Bingers HTTP {Method} {Url} -> {Status} ({Elapsed} ms)",
                request.Method,
                SanitizeUrl(request.RequestUri),
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogVerbose(
                verbose,
                "Bingers HTTP {Method} {Url} failed after {Elapsed} ms: {Error}",
                request.Method,
                SanitizeUrl(request.RequestUri),
                stopwatch.ElapsedMilliseconds,
                ex.Message);
            throw;
        }
    }

    /// <summary>
    /// GETs and deserializes JSON. Timeouts, network errors and server errors (5xx) are retried; when every attempt
    /// failed, a (non auth, non rate limit) <see cref="BingersApiException"/> is thrown so callers skip the item
    /// instead of aborting a whole scheduled task.
    /// </summary>
    /// <typeparam name="T">The response type.</typeparam>
    /// <param name="url">The URL.</param>
    /// <param name="logger">The logger of the caller.</param>
    /// <param name="verbose">Whether requests are logged in detail.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>The deserialized response.</returns>
    public static async Task<T> GetJsonAsync<T>(string url, ILogger logger, bool verbose, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, url);
                using var response = await SendAsync(request, logger, verbose, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                if (attempt >= _retryDelays.Length + 1)
                {
                    throw new BingersApiException($"Bingers request failed after {attempt} attempts ({ex.Message}): {url}", ex);
                }

                var delay = _retryDelays[attempt - 1];
                logger.LogWarning("Bingers request {Url} failed ({Error}); retry {Attempt} in {Delay} s", url, ex.Message, attempt, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        // An HttpClient timeout surfaces as a TaskCanceledException while the task itself was not cancelled.
        TaskCanceledException => !cancellationToken.IsCancellationRequested,
        HttpRequestException => true,
        BingersApiException api => (int)api.StatusCode >= 500,
        _ => false
    };

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw BingersApiException.FromResponse(response.StatusCode, body);
    }

    /// <summary>
    /// Removes the magic link token from logged URLs.
    /// </summary>
    private static string SanitizeUrl(Uri uri)
    {
        var url = uri?.ToString() ?? string.Empty;
        var index = url.IndexOf("token=", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? url : url[..(index + 6)] + "***";
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        var version = typeof(BingersHttp).Assembly.GetName().Version?.ToString() ?? "1.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-Plugin-Bingers", version));
        return client;
    }
}
