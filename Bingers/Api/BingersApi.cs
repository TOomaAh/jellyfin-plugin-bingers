using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Bingers.Api.DataContracts.Auth;
using Bingers.Api.DataContracts.Sync;
using Bingers.Helpers;
using Bingers.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Bingers.Api;

/// <summary>
/// Bingers.app API client.
/// </summary>
public partial class BingersApi
{
    // Keep sync/push batches small; the export task can send thousands of entries.
    private const int PushBatchSize = 50;

    // Safety net for the (undocumented) sync/pull pagination.
    private const int MaxPullPages = 1000;

    private static readonly string[] _cursorPropertyNames = { "nextCursor", "next_cursor", "cursor" };

    // better-auth refreshes the session cookie on get-session once it gets old enough.
    private static readonly TimeSpan _sessionRefreshWindow = TimeSpan.FromDays(2);

    private static readonly SearchValues<char> _urlCharacters = SearchValues.Create("/?#=");
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userLocks = new();
    private static readonly object _configurationLock = new();

    private readonly ILogger<BingersApi> _logger;
    private readonly BingersCatalogResolver _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="BingersApi"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger{BingersApi}"/>.</param>
    /// <param name="catalog">The <see cref="BingersCatalogResolver"/>.</param>
    public BingersApi(ILogger<BingersApi> logger, BingersCatalogResolver catalog)
    {
        _logger = logger;
        _catalog = catalog;
    }

    /// <summary>
    /// Extracts the token of a magic link. The raw token is also accepted.
    /// </summary>
    /// <param name="input">The magic link received by email, or its token.</param>
    /// <returns>The token.</returns>
    /// <exception cref="BingersApiException">No token could be found.</exception>
    public static string ExtractMagicLinkToken(string input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new BingersApiException("Magic link is required", HttpStatusCode.BadRequest);
        }

        // Raw token (no URL structure).
        if (trimmed.AsSpan().IndexOfAny(_urlCharacters) < 0)
        {
            return trimmed;
        }

        var withProtocol = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : "https://placeholder.local/" + trimmed.TrimStart('/');
        if (Uri.TryCreate(withProtocol, UriKind.Absolute, out var uri))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            var token = query.Get("token") ?? query.Get("magic_link_token");
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token.Trim();
            }
        }

        var match = TokenRegex().Match(trimmed);
        if (match.Success)
        {
            return Uri.UnescapeDataString(match.Groups[1].Value);
        }

        throw new BingersApiException("Could not extract a magic link token from the provided value", HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Verifies a magic link and stores the resulting session on the user.
    /// </summary>
    /// <param name="bingersUser">The user to link.</param>
    /// <param name="magicLink">The magic link received by email.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>A <see cref="Task"/>.</returns>
    public async Task LinkAsync(BingersUser bingersUser, string magicLink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bingersUser);

        var token = ExtractMagicLinkToken(magicLink);
        var url = BingersUris.VerifyMagicLink + "?token=" + Uri.EscapeDataString(token);

        BingersCookie[] jar;
        using (var request = BingersHttp.CreateRequest(HttpMethod.Get, url))
        using (var response = await BingersHttp.SendAsync(request, _logger, bingersUser.ExtraLogging, cancellationToken).ConfigureAwait(false))
        {
            jar = BingersCookieJar.Merge(null, BingersCookieJar.GetSetCookieHeaders(response));
            _logger.LogVerbose(
                bingersUser.ExtraLogging,
                "Magic link verification returned {Status} with cookies: {Cookies}",
                (int)response.StatusCode,
                jar.Length == 0 ? "none" : string.Join(", ", jar.Select(c => c.Name)));
            var status = (int)response.StatusCode;

            if (jar.Length == 0)
            {
                if (status is >= 300 and < 400)
                {
                    throw new BingersApiException(
                        "Magic link already used or expired; request a new sign-in link from Bingers",
                        HttpStatusCode.BadRequest,
                        "magic_link_invalid");
                }

                if (!response.IsSuccessStatusCode)
                {
                    await BingersHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                }

                throw new BingersApiException("Magic link verification did not return session cookies", HttpStatusCode.BadRequest);
            }
        }

        var session = await GetSessionAsync(jar, bingersUser.ExtraLogging, cancellationToken).ConfigureAwait(false);

        var otherUser = Plugin.Instance.PluginConfiguration.GetAllBingersUsers().FirstOrDefault(u =>
            u.LinkedMbUserId != bingersUser.LinkedMbUserId
            && u.IsLinked()
            && !string.IsNullOrEmpty(session.Account?.Id)
            && u.BingersUserId == session.Account.Id);
        if (otherUser != null)
        {
            throw new BingersApiException(
                "This Bingers account is already linked to another Jellyfin user",
                HttpStatusCode.Conflict,
                "bingers_already_linked");
        }

        UpdateUser(bingersUser, u =>
        {
            ApplySession(u, session);
            u.NeedsReauthorization = false;
        });

        _logger.LogInformation("Bingers account {BingersUser} linked to Jellyfin user {UserId}", bingersUser.Username, bingersUser.LinkedMbUserId);
    }

    /// <summary>
    /// Removes the stored session of a user.
    /// </summary>
    /// <param name="bingersUser">The user.</param>
    public static void Unlink(BingersUser bingersUser)
    {
        ArgumentNullException.ThrowIfNull(bingersUser);

        UpdateUser(bingersUser, u =>
        {
            u.Cookies = Array.Empty<BingersCookie>();
            u.SessionExpiresAt = null;
            u.BingersUserId = null;
            u.Username = null;
            u.Email = null;
            u.NeedsReauthorization = false;
        });
    }

    /// <summary>
    /// Validates the stored session with bingers.app and refreshes the user profile and cookies.
    /// Transient failures keep the stored session.
    /// </summary>
    /// <param name="bingersUser">The user.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns><c>true</c> if the session is (still) usable.</returns>
    public async Task<bool> ValidateSessionAsync(BingersUser bingersUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bingersUser);

        var verbose = bingersUser.ExtraLogging;
        if (!bingersUser.IsLinked())
        {
            _logger.LogVerbose(verbose, "Jellyfin user {UserId} has no Bingers session", bingersUser.LinkedMbUserId);
            return false;
        }

        if (!BingersCookieJar.HasSessionCookie(bingersUser.Cookies))
        {
            _logger.LogWarning(
                "Bingers session cookie of Jellyfin user {UserId} is missing or expired (stored cookies: {Cookies}); the account must be linked again",
                bingersUser.LinkedMbUserId,
                string.Join(", ", bingersUser.Cookies.Select(c => c.Name + (c.Expires.HasValue ? " until " + c.Expires.Value.ToString("u", CultureInfo.InvariantCulture) : string.Empty))));
            ClearSession(bingersUser);
            return false;
        }

        try
        {
            var session = await GetSessionAsync(bingersUser.Cookies, verbose, cancellationToken).ConfigureAwait(false);
            UpdateUser(bingersUser, u => ApplySession(u, session));
            _logger.LogVerbose(
                verbose,
                "Bingers session of Jellyfin user {UserId} is valid: account {Account} ({BingersUserId}), session expires {ExpiresAt}",
                bingersUser.LinkedMbUserId,
                bingersUser.Username,
                bingersUser.BingersUserId,
                bingersUser.SessionExpiresAt?.ToString("u", CultureInfo.InvariantCulture) ?? "unknown");
            return true;
        }
        catch (BingersApiException ex) when (ex.IsAuthError)
        {
            _logger.LogWarning("Bingers session of Jellyfin user {UserId} was rejected; the account must be linked again", bingersUser.LinkedMbUserId);
            ClearSession(bingersUser);
            return false;
        }
        catch (Exception ex) when (ex is BingersApiException or HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to validate Bingers session of Jellyfin user {UserId}; keeping the stored session", bingersUser.LinkedMbUserId);
            return true;
        }
    }

    /// <summary>
    /// Marks a movie or an episode as watched on bingers.app after it was played.
    /// </summary>
    /// <param name="bingersUser">The user.</param>
    /// <param name="item">The movie or episode.</param>
    /// <param name="allowRewatch">Whether an already watched entry gets its play count incremented.</param>
    /// <param name="localPlayCount">The Jellyfin play count including this play, when the item was played before.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>A <see cref="Task"/>.</returns>
    public async Task MarkWatchedAsync(
        BingersUser bingersUser,
        BaseItem item,
        bool allowRewatch,
        int? localPlayCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bingersUser);

        var verbose = bingersUser.ExtraLogging;
        _logger.LogVerbose(
            verbose,
            "Marking {Item} as watched on Bingers (rewatch allowed: {AllowRewatch}, local play count: {PlayCount})",
            DescribeItem(item),
            allowRewatch,
            localPlayCount?.ToString(CultureInfo.InvariantCulture) ?? "first play");

        var entities = await _catalog.ResolveAsync(item, verbose, cancellationToken).ConfigureAwait(false);

        var userLock = _userLocks.GetOrAdd(bingersUser.LinkedMbUserId, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshSessionIfNeededAsync(bingersUser, cancellationToken).ConfigureAwait(false);

            var ops = new List<BingersSyncOperation>();
            foreach (var entity in entities)
            {
                var remote = await FetchRemoteEntryAsync(bingersUser, entity, cancellationToken).ConfigureAwait(false);
                var (plays, skip) = ComputePlays(remote, allowRewatch, localPlayCount);
                if (skip)
                {
                    _logger.LogVerbose(verbose, "Skipping {Item} ({EntityKind} {EntityId}): already watched on Bingers ({Remote})", item.Name, entity.EntityKind, entity.EntityId, DescribeRemote(remote));
                    continue;
                }

                _logger.LogVerbose(verbose, "Marking {Item} ({EntityKind} {EntityId}) as watched on Bingers with {Plays} plays (remote: {Remote})", item.Name, entity.EntityKind, entity.EntityId, plays, DescribeRemote(remote));
                ops.Add(CreateWatchedOperation(entity, plays));
            }

            if (ops.Count > 0)
            {
                await PushAsync(bingersUser, ops, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Marked {Item} as watched on Bingers for Jellyfin user {UserId}", DescribeItem(item), bingersUser.LinkedMbUserId);
            }
        }
        finally
        {
            userLock.Release();
        }
    }

    /// <summary>
    /// Marks a movie or an episode as not watched on bingers.app, e.g. after it was marked played by mistake.
    /// Entries that are not watched on bingers.app are left untouched.
    /// </summary>
    /// <param name="bingersUser">The user.</param>
    /// <param name="item">The movie or episode.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>A <see cref="Task"/>.</returns>
    public async Task MarkUnwatchedAsync(BingersUser bingersUser, BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bingersUser);

        var verbose = bingersUser.ExtraLogging;
        _logger.LogVerbose(verbose, "Marking {Item} as unwatched on Bingers", DescribeItem(item));

        var entities = await _catalog.ResolveAsync(item, verbose, cancellationToken).ConfigureAwait(false);

        var userLock = _userLocks.GetOrAdd(bingersUser.LinkedMbUserId, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshSessionIfNeededAsync(bingersUser, cancellationToken).ConfigureAwait(false);

            var ops = new List<BingersSyncOperation>();
            foreach (var entity in entities)
            {
                var remote = await FetchRemoteEntryAsync(bingersUser, entity, cancellationToken).ConfigureAwait(false);
                if (remote?.Watched != true)
                {
                    _logger.LogVerbose(verbose, "Skipping {Item} ({EntityKind} {EntityId}): not watched on Bingers ({Remote})", item.Name, entity.EntityKind, entity.EntityId, DescribeRemote(remote));
                    continue;
                }

                _logger.LogVerbose(verbose, "Marking {Item} ({EntityKind} {EntityId}) as unwatched on Bingers (remote: {Remote})", item.Name, entity.EntityKind, entity.EntityId, DescribeRemote(remote));
                ops.Add(CreateEntryOperation(entity, false, 0));
            }

            if (ops.Count > 0)
            {
                await PushAsync(bingersUser, ops, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Marked {Item} as unwatched on Bingers for Jellyfin user {UserId}", DescribeItem(item), bingersUser.LinkedMbUserId);
            }
        }
        finally
        {
            userLock.Release();
        }
    }

    /// <summary>
    /// Exports watched items to bingers.app. Entries already watched remotely are left untouched.
    /// </summary>
    /// <param name="bingersUser">The user.</param>
    /// <param name="items">The watched items with their Jellyfin play count.</param>
    /// <param name="progress">Progress reporting, between 0 and 100.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>The export statistics.</returns>
    public async Task<BingersExportResult> ExportWatchedAsync(
        BingersUser bingersUser,
        IReadOnlyList<(BaseItem Item, int PlayCount)> items,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bingersUser);
        ArgumentNullException.ThrowIfNull(items);

        var verbose = bingersUser.ExtraLogging;
        var result = new BingersExportResult();
        var ops = new List<BingersSyncOperation>();
        var batch = 0;

        // Several library items (duplicates, versions in different folders) can map to the same Bingers entity.
        // Bingers rejects a batch with a duplicate key, and checking/sending the same entity twice is useless.
        var handled = new Dictionary<(string Kind, string Id), string>();
        var pending = new Dictionary<(string Kind, string Id), BingersSyncOperation>();

        var userLock = _userLocks.GetOrAdd(bingersUser.LinkedMbUserId, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (item, playCount) = items[i];
                var label = DescribeItem(item);

                try
                {
                    var entities = await _catalog.ResolveAsync(item, verbose, cancellationToken).ConfigureAwait(false);
                    foreach (var entity in entities)
                    {
                        var key = (entity.EntityKind, entity.EntityId);
                        if (handled.TryGetValue(key, out var firstItem))
                        {
                            if (pending.TryGetValue(key, out var queued) && playCount > queued.Fields.Plays)
                            {
                                queued.Fields.Plays = playCount;
                            }

                            result.Duplicates++;
                            _logger.LogVerbose(verbose, "[{Index}/{Total}] {Item}: same Bingers {EntityKind} {EntityId} as {FirstItem}, skipped", i + 1, items.Count, label, entity.EntityKind, entity.EntityId, firstItem);
                            continue;
                        }

                        handled[key] = label;
                        var remote = await FetchRemoteEntryAsync(bingersUser, entity, cancellationToken).ConfigureAwait(false);
                        if (remote is { Watched: true, Plays: > 0 })
                        {
                            _logger.LogVerbose(verbose, "[{Index}/{Total}] {Item}: already watched on Bingers ({Remote}), skipped", i + 1, items.Count, label, DescribeRemote(remote));
                            result.AlreadyWatched++;
                            continue;
                        }

                        var plays = Math.Max(1, playCount);
                        _logger.LogVerbose(verbose, "[{Index}/{Total}] {Item}: queued as watched with {Plays} plays ({EntityKind} {EntityId}, remote: {Remote})", i + 1, items.Count, label, plays, entity.EntityKind, entity.EntityId, DescribeRemote(remote));
                        var operation = CreateWatchedOperation(entity, plays);
                        ops.Add(operation);
                        pending[key] = operation;
                        result.Exported++;
                    }
                }
                catch (BingersApiException ex) when (!ex.IsAuthError && !ex.IsRateLimited)
                {
                    result.NotFound++;
                    result.NotFoundItems.Add(label);
                    _logger.LogInformation("[{Index}/{Total}] {Item}: not exported: {Message}", i + 1, items.Count, label, ex.Message);
                }

                if (ops.Count >= PushBatchSize)
                {
                    await PushBatchAsync(bingersUser, ops, ++batch, result, cancellationToken).ConfigureAwait(false);
                    ops.Clear();
                    pending.Clear();
                }

                if (verbose && (i + 1) % 25 == 0)
                {
                    _logger.LogInformation(
                        "Bingers export progress: {Done}/{Total} items ({Exported} to export, {AlreadyWatched} already watched, {NotFound} not found)",
                        i + 1,
                        items.Count,
                        result.Exported,
                        result.AlreadyWatched,
                        result.NotFound);
                }

                progress?.Report(100d * (i + 1) / items.Count);
            }

            if (ops.Count > 0)
            {
                await PushBatchAsync(bingersUser, ops, ++batch, result, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            userLock.Release();
        }

        return result;
    }

    /// <summary>
    /// Gets all the entries of the user marked watched on bingers.app.
    /// </summary>
    /// <param name="bingersUser">The user.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>The watched entries.</returns>
    public async Task<IReadOnlyList<BingersSyncEntry>> PullWatchedEntriesAsync(BingersUser bingersUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bingersUser);

        await RefreshSessionIfNeededAsync(bingersUser, cancellationToken).ConfigureAwait(false);

        var entries = new Dictionary<(string Kind, string Id), BingersSyncEntry>();
        string cursor = null;
        for (var page = 0; page < MaxPullPages; page++)
        {
            var url = BingersUris.SyncPull + "?domains=entries";
            if (cursor != null)
            {
                url += "&cursor=" + Uri.EscapeDataString(cursor);
            }

            using var request = BingersHttp.CreateRequest(HttpMethod.Get, url, BingersCookieJar.ToHeader(bingersUser.Cookies));
            using var response = await BingersHttp.SendAsync(request, _logger, bingersUser.ExtraLogging, cancellationToken).ConfigureAwait(false);
            StoreCookies(bingersUser, response);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Bingers sync/pull was rejected with {Status}; the account must be linked again", (int)response.StatusCode);
                ClearSession(bingersUser);
                throw BingersApiException.Reauth(response.StatusCode);
            }

            await BingersHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;

                if (bingersUser.ExtraLogging && root.ValueKind == JsonValueKind.Object)
                {
                    _logger.LogInformation(
                        "Bingers sync/pull page {Page} fields: {Fields}",
                        page,
                        string.Join(", ", root.EnumerateObject().Select(DescribeJsonField)));
                }

                var added = 0;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("entries", out var entriesElement)
                    && entriesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entriesElement.Deserialize<List<BingersSyncEntry>>(BingersHttp.JsonOptions) ?? new List<BingersSyncEntry>())
                    {
                        if (!string.IsNullOrEmpty(entry?.EntityKind)
                            && !string.IsNullOrEmpty(entry.EntityId)
                            && entries.TryAdd((entry.EntityKind, entry.EntityId), entry))
                        {
                            added++;
                        }
                    }
                }

                // The pagination of a full pull is not documented: follow a cursor if the API returns one,
                // and stop as soon as a page brings nothing new.
                var nextCursor = GetCursor(root);
                _logger.LogVerbose(
                    bingersUser.ExtraLogging,
                    "Bingers sync/pull page {Page}: {Added} new entries ({Total} so far), next cursor: {Cursor}",
                    page,
                    added,
                    entries.Count,
                    nextCursor ?? "none");
                if (added == 0 || string.IsNullOrEmpty(nextCursor) || nextCursor == cursor)
                {
                    break;
                }

                cursor = nextCursor;
            }
        }

        var watched = entries.Values.Where(e => e.Watched == true).ToList();
        _logger.LogInformation(
            "Pulled {Count} Bingers entries for Jellyfin user {UserId}: {Watched} watched ({Movies} movies, {Episodes} episodes), {NotWatched} not watched",
            entries.Count,
            bingersUser.LinkedMbUserId,
            watched.Count,
            watched.Count(e => e.EntityKind == BingersEntityRef.Movie),
            watched.Count(e => e.EntityKind == BingersEntityRef.Episode),
            entries.Count - watched.Count);
        return watched;
    }

    private static string GetCursor(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("hasMore", out var hasMore) && hasMore.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        foreach (var name in _cursorPropertyNames)
        {
            if (root.TryGetProperty(name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Marks entries as watched (keeping their play count) or not watched on bingers.app, in batches. A batch rejected
    /// by Bingers or failing on the network is logged and the next ones are still sent; an expired session or rate
    /// limiting stops.
    /// </summary>
    /// <param name="bingersUser">The user.</param>
    /// <param name="entries">The entries, each at most once.</param>
    /// <param name="watched">Whether the entries are marked watched or not watched.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>The number of entries updated.</returns>
    public async Task<int> SetEntriesWatchedAsync(BingersUser bingersUser, IReadOnlyList<BingersSyncEntry> entries, bool watched, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bingersUser);
        ArgumentNullException.ThrowIfNull(entries);

        var userLock = _userLocks.GetOrAdd(bingersUser.LinkedMbUserId, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshSessionIfNeededAsync(bingersUser, cancellationToken).ConfigureAwait(false);

            var removed = 0;
            var batch = 0;
            foreach (var chunk in entries.Chunk(PushBatchSize))
            {
                batch++;
                var ops = chunk
                    .Select(e => CreateEntryOperation(
                        new BingersEntityRef(e.EntityKind, e.EntityId, null),
                        watched,
                        watched ? Math.Max(1, e.Plays ?? 1) : 0))
                    .ToList();
                _logger.LogVerbose(bingersUser.ExtraLogging, "Sending Bingers {Kind} batch {Batch} with {Count} entries", watched ? "watched" : "unwatch", batch, ops.Count);
                try
                {
                    await PushAsync(bingersUser, ops, cancellationToken).ConfigureAwait(false);
                    removed += ops.Count;
                }
                catch (Exception ex) when (IsBatchFailure(ex, cancellationToken))
                {
                    _logger.LogWarning(
                        "Bingers {Kind} batch {Batch} ({Count} entries) failed: {Message}. Entries: {Entries}",
                        watched ? "watched" : "unwatch",
                        batch,
                        ops.Count,
                        ex.Message,
                        string.Join(", ", ops.Select(o => o.Pk.EntityKind + ":" + o.Pk.EntityId)));
                }
            }

            return removed;
        }
        finally
        {
            userLock.Release();
        }
    }

    /// <summary>
    /// Sends an export batch. A batch rejected by Bingers is logged and counted so the export goes on with the next
    /// ones; an expired session or rate limiting still stops the export.
    /// </summary>
    private async Task PushBatchAsync(BingersUser bingersUser, List<BingersSyncOperation> ops, int batch, BingersExportResult result, CancellationToken cancellationToken)
    {
        _logger.LogVerbose(bingersUser.ExtraLogging, "Sending Bingers batch {Batch} with {Count} entries", batch, ops.Count);
        try
        {
            await PushAsync(bingersUser, ops, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBatchFailure(ex, cancellationToken))
        {
            result.Exported -= ops.Count;
            result.Failed += ops.Count;
            _logger.LogWarning(
                "Bingers rejected export batch {Batch} ({Count} entries): {Message}. Entries: {Entries}",
                batch,
                ops.Count,
                ex.Message,
                string.Join(", ", ops.Select(o => o.Pk.EntityKind + ":" + o.Pk.EntityId)));
        }
    }

    /// <summary>
    /// Failures of a single batch that must not stop a scheduled task: rejected by Bingers, timeout, network error.
    /// </summary>
    private static bool IsBatchFailure(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        BingersApiException api => !api.IsAuthError && !api.IsRateLimited,
        TaskCanceledException => !cancellationToken.IsCancellationRequested,
        HttpRequestException => true,
        _ => false
    };

    private static (int Plays, bool Skip) ComputePlays(BingersSyncEntry remote, bool allowRewatch, int? localPlayCount)
    {
        var remotePlays = remote?.Plays > 0 ? remote.Plays.Value : 0;
        var remoteAlreadyWatched = remote?.Watched == true && remotePlays >= 1;
        var hasLocalPrior = localPlayCount > 0;
        var isRewatch = allowRewatch && (hasLocalPrior || remoteAlreadyWatched);

        if (!isRewatch)
        {
            return remoteAlreadyWatched ? (remotePlays, true) : (1, false);
        }

        var localTarget = localPlayCount > 0 ? localPlayCount.Value : 2;
        return (Math.Max(localTarget, remotePlays + 1), false);
    }

    private static BingersSyncOperation CreateWatchedOperation(BingersEntityRef entity, int plays) => CreateEntryOperation(entity, true, plays);

    private static BingersSyncOperation CreateEntryOperation(BingersEntityRef entity, bool watched, int plays) => new()
    {
        OpId = Guid.NewGuid().ToString(),
        Table = "entries",
        Pk = new BingersEntityKey
        {
            EntityKind = entity.EntityKind,
            EntityId = entity.EntityId
        },
        Fields = new BingersEntryFields
        {
            Watched = watched,
            Plays = plays,
            BatchId = null
        }
    };

    private static void ApplySession(BingersUser user, SessionInfo session)
    {
        user.Cookies = session.Cookies;
        user.SessionExpiresAt = session.ExpiresAt;
        user.BingersUserId = session.Account?.Id ?? user.BingersUserId;
        user.Email = session.Account?.Email ?? user.Email;
        user.Username = session.Username ?? user.Username ?? user.Email;
    }

    private static void ClearSession(BingersUser bingersUser)
    {
        // Keep the profile so the configuration page can show which account must be linked again.
        UpdateUser(bingersUser, u =>
        {
            u.Cookies = Array.Empty<BingersCookie>();
            u.SessionExpiresAt = null;
            u.NeedsReauthorization = true;
        });
    }

    /// <summary>
    /// Applies a change to the stored user and saves the plugin configuration.
    /// The configuration may have been reloaded since <paramref name="bingersUser"/> was read, so the change is
    /// applied to both instances.
    /// </summary>
    private static void UpdateUser(BingersUser bingersUser, Action<BingersUser> update)
    {
        lock (_configurationLock)
        {
            update(bingersUser);

            var stored = Plugin.Instance.PluginConfiguration.GetAllBingersUsers()
                .FirstOrDefault(u => u.LinkedMbUserId == bingersUser.LinkedMbUserId);
            if (stored == null)
            {
                Plugin.Instance.PluginConfiguration.AddUser(bingersUser.LinkedMbUserId);
                stored = Plugin.Instance.PluginConfiguration.GetAllBingersUsers()
                    .First(u => u.LinkedMbUserId == bingersUser.LinkedMbUserId);
            }

            if (!ReferenceEquals(stored, bingersUser))
            {
                update(stored);
            }

            Plugin.Instance.SaveConfiguration();
        }
    }

    private static DateTime? ParseExpiresAt(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var number):
                // Heuristic: seconds vs milliseconds.
                return number < 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeSeconds(number).UtcDateTime
                    : DateTimeOffset.FromUnixTimeMilliseconds(number).UtcDateTime;
            case JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date):
                return date.UtcDateTime;
            default:
                return null;
        }
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private async Task<SessionInfo> GetSessionAsync(BingersCookie[] jar, bool verbose, CancellationToken cancellationToken)
    {
        var cookieHeader = BingersCookieJar.ToHeader(jar);
        if (string.IsNullOrEmpty(cookieHeader))
        {
            throw BingersApiException.Reauth();
        }

        BingersSessionResponse body;
        using (var request = BingersHttp.CreateRequest(HttpMethod.Get, BingersUris.GetSession, cookieHeader))
        using (var response = await BingersHttp.SendAsync(request, _logger, verbose, cancellationToken).ConfigureAwait(false))
        {
            jar = BingersCookieJar.Merge(jar, BingersCookieJar.GetSetCookieHeaders(response));

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw BingersApiException.Reauth(response.StatusCode);
            }

            await BingersHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadFromJsonAsync<BingersSessionResponse>(BingersHttp.JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        if (body == null || (body.Session == null && body.User == null))
        {
            throw BingersApiException.Reauth();
        }

        var session = new SessionInfo
        {
            Cookies = jar,
            Account = body.User,
            ExpiresAt = body.Session == null ? null : ParseExpiresAt(body.Session.ExpiresAt),
            Username = FirstNonEmpty(body.User?.Username, body.User?.Name)
        };

        // Profile enrichment is best effort; the session cookies remain valid.
        try
        {
            using var request = BingersHttp.CreateRequest(HttpMethod.Get, BingersUris.Me, BingersCookieJar.ToHeader(session.Cookies));
            using var response = await BingersHttp.SendAsync(request, _logger, verbose, cancellationToken).ConfigureAwait(false);
            session.Cookies = BingersCookieJar.Merge(session.Cookies, BingersCookieJar.GetSetCookieHeaders(response));

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw BingersApiException.Reauth(response.StatusCode);
            }

            if (response.IsSuccessStatusCode)
            {
                var me = await response.Content.ReadFromJsonAsync<BingersMeResponse>(BingersHttp.JsonOptions, cancellationToken).ConfigureAwait(false);
                session.Username = FirstNonEmpty(me?.Profile?.Handle, me?.Profile?.DisplayName, me?.User?.Name, session.Username);
                if (!string.IsNullOrEmpty(me?.User?.Id))
                {
                    session.Account ??= new BingersAccount();
                    session.Account.Id = me.User.Id;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogVerbose(verbose, "Failed to fetch the Bingers profile: {Error}", ex.Message);
        }

        return session;
    }

    private async Task RefreshSessionIfNeededAsync(BingersUser bingersUser, CancellationToken cancellationToken)
    {
        if (!bingersUser.IsLinked())
        {
            throw BingersApiException.Reauth();
        }

        if (bingersUser.SessionExpiresAt == null || bingersUser.SessionExpiresAt - DateTime.UtcNow < _sessionRefreshWindow)
        {
            _logger.LogVerbose(
                bingersUser.ExtraLogging,
                "Refreshing the Bingers session of Jellyfin user {UserId} (expires {ExpiresAt})",
                bingersUser.LinkedMbUserId,
                bingersUser.SessionExpiresAt?.ToString("u", CultureInfo.InvariantCulture) ?? "unknown");
            if (!await ValidateSessionAsync(bingersUser, cancellationToken).ConfigureAwait(false))
            {
                throw BingersApiException.Reauth();
            }
        }
    }

    private async Task<BingersSyncEntry> FetchRemoteEntryAsync(BingersUser bingersUser, BingersEntityRef entity, CancellationToken cancellationToken)
    {
        var url = $"{BingersUris.SyncPull}?domains=entries&entityKind={Uri.EscapeDataString(entity.EntityKind)}&entityId={Uri.EscapeDataString(entity.EntityId)}";

        try
        {
            using var request = BingersHttp.CreateRequest(HttpMethod.Get, url, BingersCookieJar.ToHeader(bingersUser.Cookies));
            using var response = await BingersHttp.SendAsync(request, _logger, bingersUser.ExtraLogging, cancellationToken).ConfigureAwait(false);
            StoreCookies(bingersUser, response);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogVerbose(bingersUser.ExtraLogging, "Bingers entry pull for {EntityKind} {EntityId} failed with {Status}; assuming no remote entry", entity.EntityKind, entity.EntityId, (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<BingersSyncPullResponse>(BingersHttp.JsonOptions, cancellationToken).ConfigureAwait(false);
            return body?.Entries?.FirstOrDefault(e => e.EntityKind == entity.EntityKind && e.EntityId == entity.EntityId);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogVerbose(bingersUser.ExtraLogging, "Failed to fetch remote Bingers entry {EntityKind} {EntityId}; assuming no remote entry: {Error}", entity.EntityKind, entity.EntityId, ex.Message);
            return null;
        }
    }

    private async Task PushAsync(BingersUser bingersUser, List<BingersSyncOperation> ops, CancellationToken cancellationToken)
    {
        var body = new BingersSyncPushRequest
        {
            ClientBatchId = Guid.NewGuid().ToString(),
            Ops = ops.ToList()
        };

        _logger.LogVerbose(bingersUser.ExtraLogging, "Bingers sync/push body: {Body}", JsonSerializer.Serialize(body, BingersHttp.JsonOptions));

        using var request = BingersHttp.CreateRequest(HttpMethod.Post, BingersUris.SyncPush, BingersCookieJar.ToHeader(bingersUser.Cookies));
        request.Content = JsonContent.Create(body, options: BingersHttp.JsonOptions);
        using var response = await BingersHttp.SendAsync(request, _logger, bingersUser.ExtraLogging, cancellationToken).ConfigureAwait(false);
        StoreCookies(bingersUser, response);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Bingers sync/push was rejected with {Status}; the account must be linked again", (int)response.StatusCode);
            ClearSession(bingersUser);
            throw BingersApiException.Reauth(response.StatusCode);
        }

        await BingersHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        // The API may report an error with a success status code.
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogVerbose(bingersUser.ExtraLogging, "Bingers sync/push response: {Response}", text.Length > 2000 ? text[..2000] + "..." : text);
            BingersErrorResponse error = null;
            try
            {
                error = JsonSerializer.Deserialize<BingersErrorResponse>(text, BingersHttp.JsonOptions);
            }
            catch (JsonException)
            {
                // Not an error payload.
            }

            if (error?.Error != null)
            {
                throw BingersApiException.FromResponse(response.StatusCode, text);
            }
        }
    }

    private static void StoreCookies(BingersUser bingersUser, HttpResponseMessage response)
    {
        var headers = BingersCookieJar.GetSetCookieHeaders(response);
        if (headers.Count == 0)
        {
            return;
        }

        var merged = BingersCookieJar.Merge(bingersUser.Cookies, headers);
        if (!BingersCookieJar.AreEqual(merged, bingersUser.Cookies))
        {
            UpdateUser(bingersUser, u => u.Cookies = merged);
        }
    }

    private static string DescribeRemote(BingersSyncEntry remote) => remote == null
        ? "no entry"
        : $"watched={remote.Watched?.ToString() ?? "null"}, plays={remote.Plays?.ToString(CultureInfo.InvariantCulture) ?? "null"}";

    private static string DescribeItem(BaseItem item) => item switch
    {
        Episode episode => string.Format(
            CultureInfo.InvariantCulture,
            "\"{0}\" S{1:00}E{2:00} \"{3}\"",
            episode.SeriesName,
            episode.ParentIndexNumber ?? episode.Season?.IndexNumber,
            episode.IndexNumber,
            episode.Name),
        _ => item.ProductionYear.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "\"{0}\" ({1})", item.Name, item.ProductionYear)
            : "\"" + item.Name + "\""
    };

    private static string DescribeJsonField(JsonProperty property) => property.Value.ValueKind switch
    {
        JsonValueKind.Array => $"{property.Name}: array[{property.Value.GetArrayLength()}]",
        JsonValueKind.Object => $"{property.Name}: object{{{string.Join(", ", property.Value.EnumerateObject().Select(p => p.Name))}}}",
        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
            => $"{property.Name}: {property.Value.GetRawText()}",
        _ => $"{property.Name}: {property.Value.ValueKind}"
    };

    [GeneratedRegex("[?&#]token=([^&#\\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    private sealed class SessionInfo
    {
        public BingersCookie[] Cookies { get; set; }

        public BingersAccount Account { get; set; }

        public DateTime? ExpiresAt { get; set; }

        public string Username { get; set; }
    }
}
