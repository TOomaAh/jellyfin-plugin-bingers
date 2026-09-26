using System;
using System.Net;
using System.Text.Json;
using Bingers.Api.DataContracts.Sync;

namespace Bingers.Api;

/// <summary>
/// Error returned by the bingers.app API.
/// </summary>
public class BingersApiException : Exception
{
    /// <summary>
    /// Message used when the stored session is no longer accepted.
    /// </summary>
    public const string ReauthMessage = "Bingers session expired or revoked; link the account again in the plugin settings";

    /// <summary>
    /// Initializes a new instance of the <see cref="BingersApiException"/> class.
    /// </summary>
    public BingersApiException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BingersApiException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public BingersApiException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BingersApiException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public BingersApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BingersApiException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="code">The API error code.</param>
    /// <param name="isAuthError">Whether the session was rejected.</param>
    /// <param name="isRateLimited">Whether the request was rate limited.</param>
    public BingersApiException(string message, HttpStatusCode statusCode, string code = null, bool isAuthError = false, bool isRateLimited = false)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        IsAuthError = isAuthError;
        IsRateLimited = isRateLimited;
    }

    /// <summary>
    /// Gets the HTTP status code.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Gets the API error code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets a value indicating whether the session was rejected.
    /// </summary>
    public bool IsAuthError { get; }

    /// <summary>
    /// Gets a value indicating whether the request was rate limited.
    /// </summary>
    public bool IsRateLimited { get; }

    /// <summary>
    /// Creates an exception for a session that must be re-linked.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>The exception.</returns>
    public static BingersApiException Reauth(HttpStatusCode statusCode = HttpStatusCode.Unauthorized)
        => new(ReauthMessage, statusCode, isAuthError: true);

    /// <summary>
    /// Creates an exception from an error response.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="body">The response body.</param>
    /// <returns>The exception.</returns>
    public static BingersApiException FromResponse(HttpStatusCode statusCode, string body)
    {
        BingersErrorResponse parsed = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                parsed = JsonSerializer.Deserialize<BingersErrorResponse>(body);
            }
            catch (JsonException)
            {
                parsed = null;
            }
        }

        var code = parsed?.Error?.Code;
        var message = parsed?.Error?.Message ?? parsed?.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = string.IsNullOrWhiteSpace(body)
                ? $"Bingers API error: {(int)statusCode}"
                : body.Trim()[..Math.Min(body.Trim().Length, 200)];
        }

        var retryAfter = parsed?.Error?.RetryAfterSeconds;
        var isRateLimited = string.Equals(code, "magic_link_recently_sent", StringComparison.Ordinal)
            || statusCode == HttpStatusCode.TooManyRequests
            || retryAfter > 0;
        var isAuthError = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        return new BingersApiException(message, statusCode, code, isAuthError, isRateLimited);
    }
}
