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
using Bingers.Model;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Bingers.ScheduledTasks;

/// <summary>
/// Task that marks as played in Jellyfin the movies and episodes watched on each linked user's bingers.app account.
/// </summary>
public class ImportWatchedHistoryTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly BingersApi _bingersApi;
    private readonly BingersLibraryMatcher _matcher;
    private readonly ILogger<ImportWatchedHistoryTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportWatchedHistoryTask"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{ImportWatchedHistoryTask}"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="bingersApi">The <see cref="BingersApi"/>.</param>
    /// <param name="catalog">The <see cref="BingersCatalogResolver"/>.</param>
    public ImportWatchedHistoryTask(
        ILogger<ImportWatchedHistoryTask> logger,
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
    public string Key => "BingersImportWatchedHistoryTask";

    /// <inheritdoc />
    public string Name => "Import watched history from bingers.app";

    /// <inheritdoc />
    public string Category => "Bingers";

    /// <inheritdoc />
    public string Description => "Marks as played in Jellyfin the movies and episodes watched on each linked user's bingers.app account";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Enumerable.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var users = UserHelper.GetTaskUsers(
            _userManager,
            _logger,
            u => !u.SkipWatchedImportFromBingers,
            "Jellyfin user {User} disabled the watched history import from Bingers");

        if (users.Count == 0)
        {
            return;
        }

        var percentPerUser = 100d / users.Count;
        for (var i = 0; i < users.Count; i++)
        {
            var (user, bingersUser) = users[i];
            var verbose = bingersUser.ExtraLogging;
            var baseProgress = i * percentPerUser;
            var stopwatch = Stopwatch.StartNew();

            _logger.LogVerbose(
                verbose,
                "Bingers import for user {User}: account {Account}, excluded folders: {Excluded}",
                user.Username,
                bingersUser.Username ?? bingersUser.Email,
                bingersUser.LocationsExcluded is { Length: > 0 } ? string.Join(", ", bingersUser.LocationsExcluded) : "none");

            try
            {
                var entries = await _bingersApi.PullWatchedEntriesAsync(bingersUser, cancellationToken).ConfigureAwait(false);
                var matches = await _matcher.MatchAsync(
                    user,
                    bingersUser,
                    entries,
                    new Progress<double>(percent => progress.Report(baseProgress + (percent * percentPerUser / 100d))),
                    cancellationToken).ConfigureAwait(false);

                var updated = 0;
                var upToDate = 0;
                foreach (var match in matches.Matches)
                {
                    foreach (var item in match.Items)
                    {
                        if (MarkPlayed(user, bingersUser, item, match.Label, match.Entry, cancellationToken))
                        {
                            updated++;
                        }
                        else
                        {
                            upToDate++;
                        }
                    }
                }

                _logger.LogInformation(
                    "Bingers import for user {User} finished in {Elapsed}: {Updated} items updated, {UpToDate} already up to date, {Unmatched} watched entries not found in the library",
                    user.Username,
                    stopwatch.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                    updated,
                    upToDate,
                    matches.Unmatched.Count);

                if (matches.Unmatched.Count > 0)
                {
                    _logger.LogVerbose(verbose, "Bingers entries of user {User} not found in the library: {Entries}", user.Username, string.Join("; ", matches.Unmatched));
                }
            }
            catch (BingersApiException ex) when (ex.IsAuthError)
            {
                _logger.LogWarning("Bingers session of user {User} is no longer valid; link the account again in the plugin settings", user.Username);
            }
            catch (BingersApiException ex) when (ex.IsRateLimited)
            {
                _logger.LogWarning("bingers.app rate limited the import of user {User} after {Elapsed}; the remaining items will be imported on the next run", user.Username, stopwatch.Elapsed);
            }

            progress.Report(baseProgress + percentPerUser);
        }
    }

    private bool MarkPlayed(User user, BingersUser bingersUser, BaseItem item, string label, BingersSyncEntry entry, CancellationToken cancellationToken)
    {
        var userData = _userDataManager.GetUserData(user, item);
        if (userData == null)
        {
            return false;
        }

        var wasPlayed = userData.Played;
        var previousPlayCount = userData.PlayCount;
        var remotePlays = entry.Plays ?? 0;

        var changed = false;
        if (!userData.Played)
        {
            userData.Played = true;
            userData.LastPlayedDate ??= DateTime.UtcNow;
            changed = true;
        }

        if (userData.PlayCount < remotePlays)
        {
            userData.PlayCount = remotePlays;
            changed = true;
        }

        if (!changed)
        {
            _logger.LogVerbose(bingersUser.ExtraLogging, "{Item}: already played in Jellyfin ({Local} plays, Bingers {Remote}), unchanged", label, previousPlayCount, remotePlays);
            return false;
        }

        _logger.LogVerbose(
            bingersUser.ExtraLogging,
            "{Item}: played {WasPlayed} -> true, play count {PreviousCount} -> {PlayCount} (from Bingers)",
            label,
            wasPlayed,
            previousPlayCount,
            userData.PlayCount);

        // The Import reason is ignored by the plugin's own user data handler, so this does not get pushed back.
        _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.Import, cancellationToken);
        return true;
    }
}
