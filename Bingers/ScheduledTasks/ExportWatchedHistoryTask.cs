using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bingers.Api;
using Bingers.Helpers;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Bingers.ScheduledTasks;

/// <summary>
/// Task that exports the watched movies and episodes of each linked user to bingers.app.
/// </summary>
public class ExportWatchedHistoryTask : IScheduledTask
{
    private const int PageSize = 100;

    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly BingersApi _bingersApi;
    private readonly ILogger<ExportWatchedHistoryTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportWatchedHistoryTask"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{ExportWatchedHistoryTask}"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="bingersApi">The <see cref="BingersApi"/>.</param>
    public ExportWatchedHistoryTask(
        ILogger<ExportWatchedHistoryTask> logger,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        BingersApi bingersApi)
    {
        _logger = logger;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _bingersApi = bingersApi;
    }

    /// <inheritdoc />
    public string Key => "BingersExportWatchedHistoryTask";

    /// <inheritdoc />
    public string Name => "Export watched history to bingers.app";

    /// <inheritdoc />
    public string Category => "Bingers";

    /// <inheritdoc />
    public string Description => "Marks the movies and episodes watched in Jellyfin as watched on each linked user's bingers.app account";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Enumerable.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var users = UserHelper.GetTaskUsers(
            _userManager,
            _logger,
            u => u.PostWatchedHistory,
            "Jellyfin user {User} disabled the watched history export to Bingers");

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
                "Bingers export for user {User}: account {Account}, excluded folders: {Excluded}",
                user.Username,
                bingersUser.Username ?? bingersUser.Email,
                bingersUser.LocationsExcluded is { Length: > 0 } ? string.Join(", ", bingersUser.LocationsExcluded) : "none");

            if (!await _bingersApi.ValidateSessionAsync(bingersUser, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Bingers session of user {User} is no longer valid; link the account again in the plugin settings", user.Username);
                continue;
            }

            var items = CollectWatchedItems(user, bingersUser, verbose, cancellationToken);

            _logger.LogInformation(
                "Exporting {Count} watched items of user {User} to bingers.app ({Movies} movies, {Episodes} episodes)",
                items.Count,
                user.Username,
                items.Count(x => x.Item is Movie),
                items.Count(x => x.Item is not Movie));

            try
            {
                var userProgress = new Progress<double>(percent => progress.Report(baseProgress + (percent * percentPerUser / 100d)));
                var result = await _bingersApi.ExportWatchedAsync(bingersUser, items, userProgress, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Bingers export for user {User} finished in {Elapsed}: {Exported} marked watched, {AlreadyWatched} already watched, {NotFound} not found in the catalog, {Duplicates} duplicates, {Failed} rejected by Bingers",
                    user.Username,
                    stopwatch.Elapsed.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture),
                    result.Exported,
                    result.AlreadyWatched,
                    result.NotFound,
                    result.Duplicates,
                    result.Failed);

                if (result.NotFoundItems.Count > 0)
                {
                    _logger.LogInformation(
                        "Items of user {User} not found in the Bingers catalog (check their IMDb/TMDB/TVDB ids): {Items}",
                        user.Username,
                        string.Join("; ", result.NotFoundItems));
                }
            }
            catch (BingersApiException ex) when (ex.IsAuthError)
            {
                _logger.LogWarning("Bingers session of user {User} is no longer valid; link the account again in the plugin settings", user.Username);
            }
            catch (BingersApiException ex) when (ex.IsRateLimited)
            {
                _logger.LogWarning("bingers.app rate limited the export of user {User} after {Elapsed}; the remaining items will be sent on the next run", user.Username, stopwatch.Elapsed);
            }

            progress.Report(baseProgress + percentPerUser);
        }
    }

    private List<(BaseItem Item, int PlayCount)> CollectWatchedItems(
        Jellyfin.Database.Implementations.Entities.User user,
        Model.BingersUser bingersUser,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var items = new List<(BaseItem Item, int PlayCount)>();
        var skipped = 0;
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsPlayed = true,
            IsVirtualItem = false,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.SeriesSortName, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending) },
            Limit = PageSize,
            StartIndex = 0
        };

        IReadOnlyList<BaseItem> page;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            page = _libraryManager.GetItemList(query);
            query.StartIndex += PageSize;

            foreach (var item in page)
            {
                var reason = UserHelper.GetSyncBlockReason(item, bingersUser);
                if (reason != null)
                {
                    skipped++;
                    _logger.LogVerbose(verbose, "Not exporting \"{Item}\" ({Path}): {Reason}", item.Name, item.Path, reason);
                    continue;
                }

                var playCount = _userDataManager.GetUserData(user, item)?.PlayCount ?? 0;
                items.Add((item, playCount));
            }
        }
        while (page.Count == PageSize);

        _logger.LogVerbose(verbose, "Found {Count} played items in the library of user {User}, {Skipped} skipped", items.Count + skipped, user.Username, skipped);
        return items;
    }
}
