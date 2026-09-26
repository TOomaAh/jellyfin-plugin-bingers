using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bingers.Api;
using Bingers.Api.DataContracts.Sync;
using Bingers.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Bingers.ScheduledTasks;

/// <summary>
/// Task that removes from bingers.app the watched entries whose library items are not played in Jellyfin, e.g. items
/// that were marked played by mistake and synced before being marked unplayed.
/// Entries that are not in the library are never touched (they may have been watched outside Jellyfin).
/// </summary>
public class CleanupWatchedHistoryTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly BingersApi _bingersApi;
    private readonly BingersLibraryMatcher _matcher;
    private readonly ILogger<CleanupWatchedHistoryTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanupWatchedHistoryTask"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{CleanupWatchedHistoryTask}"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="bingersApi">The <see cref="BingersApi"/>.</param>
    /// <param name="catalog">The <see cref="BingersCatalogResolver"/>.</param>
    public CleanupWatchedHistoryTask(
        ILogger<CleanupWatchedHistoryTask> logger,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        BingersApi bingersApi,
        BingersCatalogResolver catalog)
    {
        _logger = logger;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _bingersApi = bingersApi;
        _matcher = new BingersLibraryMatcher(libraryManager, catalog, logger);
    }

    /// <inheritdoc />
    public string Key => "BingersCleanupWatchedHistoryTask";

    /// <inheritdoc />
    public string Name => "Clean up watched history on bingers.app";

    /// <inheritdoc />
    public string Category => "Bingers";

    /// <inheritdoc />
    public string Description => "Marks as not watched on bingers.app the entries whose library items are not played in Jellyfin (only for users who enabled it; dry run by default)";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Enumerable.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var users = UserHelper.GetTaskUsers(
            _userManager,
            _logger,
            u => u.CleanupWatchedHistory,
            "Jellyfin user {User} did not enable the Bingers watched history clean up");

        if (users.Count == 0)
        {
            return;
        }

        var percentPerUser = 100d / users.Count;
        for (var i = 0; i < users.Count; i++)
        {
            var (user, bingersUser) = users[i];
            var dryRun = bingersUser.CleanupDryRun;
            var baseProgress = i * percentPerUser;
            var stopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "Bingers clean up for user {User} ({Mode})",
                user.Username,
                dryRun ? "dry run: nothing is changed on Bingers" : "entries are marked as not watched on Bingers");

            try
            {
                var entries = await _bingersApi.PullWatchedEntriesAsync(bingersUser, cancellationToken).ConfigureAwait(false);
                // Entries are cleaned up batch by batch while the matching goes on: a cancelled or failing run keeps
                // what was already done. Entries marked as not watched are remembered: if another copy of the item
                // shows up played in a later batch (a show that could not be grouped with the first one), the entry
                // is marked watched again.
                var removedEntries = new Dictionary<string, BingersSyncEntry>(StringComparer.Ordinal);
                var toRemoveCount = 0;
                var removed = 0;
                var restored = 0;
                var kept = 0;

                var matches = await _matcher.MatchAsync(
                    user,
                    bingersUser,
                    entries,
                    async batch =>
                    {
                        var toRemove = new List<BingersSyncEntry>();
                        var toRestore = new List<BingersSyncEntry>();
                        foreach (var match in batch)
                        {
                            // Keep an entry as soon as one of its library items (duplicates, versions) is played.
                            var played = match.Items.Any(item => _userDataManager.GetUserData(user, item)?.Played == true);
                            var key = match.Entry.EntityKind + ":" + match.Entry.EntityId;

                            if (match.Repeated)
                            {
                                if (played && removedEntries.Remove(key))
                                {
                                    toRestore.Add(match.Entry);
                                    _logger.LogInformation(
                                        "{Action} {Item} ({EntityKind} {EntityId}): another copy is played in Jellyfin",
                                        dryRun ? "Would keep as watched" : "Marking as watched again",
                                        match.Label,
                                        match.Entry.EntityKind,
                                        match.Entry.EntityId);
                                }

                                continue;
                            }

                            if (played)
                            {
                                kept++;
                                continue;
                            }

                            toRemove.Add(match.Entry);
                            removedEntries[key] = match.Entry;
                            _logger.LogInformation(
                                "{Action} {Item} ({EntityKind} {EntityId}): watched on Bingers but not played in Jellyfin",
                                dryRun ? "Would mark as not watched" : "Marking as not watched",
                                match.Label,
                                match.Entry.EntityKind,
                                match.Entry.EntityId);
                        }

                        toRemoveCount += toRemove.Count - toRestore.Count;
                        if (dryRun)
                        {
                            return;
                        }

                        if (toRemove.Count > 0)
                        {
                            removed += await _bingersApi.SetEntriesWatchedAsync(bingersUser, toRemove, false, cancellationToken).ConfigureAwait(false);
                        }

                        if (toRestore.Count > 0)
                        {
                            restored += await _bingersApi.SetEntriesWatchedAsync(bingersUser, toRestore, true, cancellationToken).ConfigureAwait(false);
                        }
                    },
                    new Progress<double>(percent => progress.Report(baseProgress + (percent * percentPerUser / 100d))),
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Bingers clean up for user {User} finished in {Elapsed}: {ToRemove} entries not played in Jellyfin{Result}, {Kept} kept (played in Jellyfin), {Unmatched} not in the library (left untouched)",
                    user.Username,
                    stopwatch.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                    toRemoveCount,
                    dryRun
                        ? " (dry run, nothing changed: disable the dry run in the plugin settings to apply)"
                        : $" ({removed - restored} marked as not watched{(restored > 0 ? $", {restored} restored" : string.Empty)})",
                    kept,
                    matches.Unmatched.Count);
            }
            catch (BingersApiException ex) when (ex.IsAuthError)
            {
                _logger.LogWarning("Bingers session of user {User} is no longer valid; link the account again in the plugin settings", user.Username);
            }
            catch (BingersApiException ex) when (ex.IsRateLimited)
            {
                _logger.LogWarning("bingers.app rate limited the clean up of user {User} after {Elapsed}; run it again later", user.Username, stopwatch.Elapsed);
            }

            progress.Report(baseProgress + percentPerUser);
        }
    }
}
