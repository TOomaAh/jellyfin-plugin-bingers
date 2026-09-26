using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using Bingers.Model;

namespace Bingers.Api;

/// <summary>
/// Helpers to keep the bingers.app session cookies (better-auth) between requests.
/// </summary>
public static class BingersCookieJar
{
    /// <summary>
    /// Returns the Set-Cookie headers of a response.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <returns>The Set-Cookie header values.</returns>
    public static IReadOnlyList<string> GetSetCookieHeaders(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToList()
            : Array.Empty<string>();
    }

    /// <summary>
    /// Merges Set-Cookie headers into an existing jar.
    /// </summary>
    /// <param name="jar">The current cookies.</param>
    /// <param name="setCookieHeaders">The Set-Cookie header values.</param>
    /// <returns>The merged cookies.</returns>
    public static BingersCookie[] Merge(IEnumerable<BingersCookie> jar, IEnumerable<string> setCookieHeaders)
    {
        var next = new Dictionary<string, BingersCookie>(StringComparer.Ordinal);
        foreach (var cookie in jar ?? Enumerable.Empty<BingersCookie>())
        {
            if (!string.IsNullOrEmpty(cookie?.Name))
            {
                next[cookie.Name] = cookie;
            }
        }

        foreach (var header in setCookieHeaders ?? Enumerable.Empty<string>())
        {
            var cookie = ParseSetCookie(header);
            if (cookie == null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(cookie.Value)
                || cookie.Value.Contains("deleted", StringComparison.OrdinalIgnoreCase)
                || cookie.Expires <= DateTime.UtcNow)
            {
                next.Remove(cookie.Name);
                continue;
            }

            next[cookie.Name] = cookie;
        }

        return next.Values.ToArray();
    }

    /// <summary>
    /// Builds the Cookie request header from a jar, skipping expired cookies.
    /// </summary>
    /// <param name="jar">The cookies.</param>
    /// <returns>The header value.</returns>
    public static string ToHeader(IEnumerable<BingersCookie> jar)
    {
        var now = DateTime.UtcNow;
        return string.Join(
            "; ",
            (jar ?? Enumerable.Empty<BingersCookie>())
                .Where(cookie => !string.IsNullOrEmpty(cookie?.Name) && (cookie.Expires == null || cookie.Expires > now))
                .Select(cookie => cookie.Name + "=" + cookie.Value));
    }

    /// <summary>
    /// Checks whether the jar contains a non expired better-auth session cookie.
    /// </summary>
    /// <param name="jar">The cookies.</param>
    /// <returns><c>true</c> if the jar holds a usable session cookie.</returns>
    public static bool HasSessionCookie(IEnumerable<BingersCookie> jar)
    {
        var now = DateTime.UtcNow;
        return (jar ?? Enumerable.Empty<BingersCookie>()).Any(cookie =>
            cookie?.Name != null
            && (cookie.Name == "session_token" || cookie.Name.EndsWith(".session_token", StringComparison.Ordinal))
            && !string.IsNullOrEmpty(cookie.Value)
            && (cookie.Expires == null || cookie.Expires > now));
    }

    /// <summary>
    /// Compares two jars.
    /// </summary>
    /// <param name="left">The first jar.</param>
    /// <param name="right">The second jar.</param>
    /// <returns><c>true</c> if both jars hold the same cookies.</returns>
    public static bool AreEqual(IReadOnlyCollection<BingersCookie> left, IReadOnlyCollection<BingersCookie> right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        return left.All(l => right.Any(r => r.Name == l.Name && r.Value == l.Value && r.Expires == l.Expires));
    }

    private static BingersCookie ParseSetCookie(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split(';');
        var pair = parts[0];
        var eq = pair.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return null;
        }

        var cookie = new BingersCookie
        {
            Name = pair[..eq].Trim(),
            Value = pair[(eq + 1)..].Trim()
        };

        foreach (var attribute in parts.Skip(1))
        {
            var attributeEq = attribute.IndexOf('=', StringComparison.Ordinal);
            if (attributeEq <= 0)
            {
                continue;
            }

            var name = attribute[..attributeEq].Trim();
            var value = attribute[(attributeEq + 1)..].Trim();

            if (name.Equals("Max-Age", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                // Max-Age takes precedence over Expires.
                cookie.Expires = DateTime.UtcNow.AddSeconds(seconds);
                break;
            }

            if (name.Equals("Expires", StringComparison.OrdinalIgnoreCase)
                && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expires))
            {
                cookie.Expires = expires;
            }
        }

        return cookie;
    }
}
