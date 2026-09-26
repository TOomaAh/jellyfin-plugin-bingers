using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bingers.Api;
using Bingers.Api.DataContracts.Catalog;
using Bingers.Api.DataContracts.Sync;
using Bingers.Helpers;
using Bingers.Model;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
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
    private readonly ILibraryManager _libraryManager;
    private readonly BingersApi _bingersApi;
    private readonly BingersCatalogResolver _catalog;
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
        _libraryManager = libraryManager;
        _bingersApi = bingersApi;
        _catalog = catalog;
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
        var allUsers = _userManager.GetUsers().ToList();
        var users = new List<(User User, BingersUser BingersUser)>();
        foreach (var user in allUsers)
        {
            var bingersUser = UserHelper.GetBingersUser(user);
            if (bingersUser == null)
            {
                _logger.LogDebug("Jellyfin user {User} has no Bingers configuration", user.Username);
            }
            else if (!bingersUser.IsLinked())
            {
                _logger.LogInformation("Jellyfin user {User} is not linked to a Bingers account{Reauth}", user.Username, bingersUser.NeedsReauthorization ? " (session expired, link it again)" : string.Empty);
            }
            else if (bingersUser.SkipWatchedImportFromBingers)
            {
                _logger.LogInformation("Jellyfin user {User} disabled the watched history import from Bingers", user.Username);
            }
            else
            {
                users.Add((user, bingersUser));
            }
        }

        _logger.LogInformation("Bingers import started for {Count} of {Total} Jellyfin users", users.Count, allUsers.Count);
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

                var stats = new ImportStats();
                await ImportMoviesAsync(user, bingersUser, entries, stats, cancellationToken).ConfigureAwait(false);
                progress.Report(baseProgress + (percentPerUser / 3));

                await ImportEpisodesAsync(
                    user,
                    bingersUser,
                    entries,
                    stats,
                    new Progress<double>(percent => progress.Report(baseProgress + (percentPerUser / 3) + (percent * percentPerUser * 2 / 300d))),
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Bingers import for user {User} finished in {Elapsed}: {Updated} items updated, {UpToDate} already up to date, {Unmatched} watched entries not found in the library",
                    user.Username,
                    stopwatch.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                    stats.Updated,
                    stats.UpToDate,
                    stats.Unmatched.Count);

                if (stats.Unmatched.Count > 0)
                {
                    _logger.LogVerbose(verbose, "Bingers entries of user {User} not found in the library: {Entries}", user.Username, string.Join("; ", stats.Unmatched));
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

    private async Task ImportMoviesAsync(
        User user,
        BingersUser bingersUser,
        IReadOnlyList<BingersSyncEntry> entries,
        ImportStats stats,
        CancellationToken cancellationToken)
    {
        var verbose = bingersUser.ExtraLogging;
        var movieEntries = entries.Where(e => e.EntityKind == BingersEntityRef.Movie).ToList();
        if (movieEntries.Count == 0)
        {
            _logger.LogVerbose(verbose, "No watched Bingers movie to import for user {User}", user.Username);
            return;
        }

        // Movie entries point to catalog titles: match them to the library through their external ids.
        var movies = new List<Movie>();
        foreach (var movie in _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Movie>())
        {
            var reason = UserHelper.GetSyncBlockReason(movie, bingersUser);
            if (reason != null)
            {
                _logger.LogVerbose(verbose, "Ignoring library movie \"{Movie}\" ({Path}): {Reason}", movie.Name, movie.Path, reason);
                continue;
            }

            movies.Add(movie);
        }

        var byProviderId = new Dictionary<string, List<Movie>>(StringComparer.OrdinalIgnoreCase);
        var withoutIds = 0;
        foreach (var movie in movies)
        {
            var added = AddProviderId(byProviderId, "imdb", movie.GetProviderId(MetadataProvider.Imdb), movie)
                | AddProviderId(byProviderId, "tmdb", movie.GetProviderId(MetadataProvider.Tmdb), movie)
                | AddProviderId(byProviderId, "tvdb", movie.GetProviderId(MetadataProvider.Tvdb), movie);
            if (!added)
            {
                withoutIds++;
                _logger.LogVerbose(verbose, "Library movie \"{Movie}\" has no IMDb/TMDB/TVDB id and can't be matched", movie.Name);
            }
        }

        _logger.LogInformation(
            "Matching {Entries} watched Bingers movies against {Movies} library movies of user {User} ({WithoutIds} without provider ids)",
            movieEntries.Count,
            movies.Count,
            user.Username,
            withoutIds);

        for (var i = 0; i < movieEntries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = movieEntries[i];

            BingersMetadataGrain metadata;
            try
            {
                metadata = await _catalog.GetTitleMetadataAsync(entry.EntityId, verbose, cancellationToken).ConfigureAwait(false);
            }
            catch (BingersApiException ex) when (!ex.IsAuthError && !ex.IsRateLimited)
            {
                _logger.LogVerbose(verbose, "[{Index}/{Total}] Bingers movie {EntityId}: failed to get its catalog metadata: {Message}", i + 1, movieEntries.Count, entry.EntityId, ex.Message);
                stats.Unmatched.Add($"movie {entry.EntityId} (no catalog metadata)");
                continue;
            }

            var ids = BingersCatalogResolver.FormatExternalIds(metadata);
            var matches = (metadata.ExternalIds ?? new List<BingersExternalId>())
                .Select(id => (Source: NormalizeSource(id.Source), Id: id.GetIdString()))
                .Where(id => id.Source != null && !string.IsNullOrEmpty(id.Id))
                .SelectMany(id => byProviderId.TryGetValue(id.Source + ":" + id.Id, out var found) ? found : Enumerable.Empty<Movie>())
                .Distinct()
                .ToList();

            if (matches.Count == 0)
            {
                _logger.LogVerbose(verbose, "[{Index}/{Total}] Bingers movie {EntityId} ({Year}, {Ids}): not in the library", i + 1, movieEntries.Count, entry.EntityId, metadata.Year, ids);
                stats.Unmatched.Add($"movie {entry.EntityId} ({metadata.Year}, {ids})");
                continue;
            }

            _logger.LogVerbose(
                verbose,
                "[{Index}/{Total}] Bingers movie {EntityId} ({Ids}, {Plays} plays) matches {Movies}",
                i + 1,
                movieEntries.Count,
                entry.EntityId,
                ids,
                entry.Plays,
                string.Join(", ", matches.Select(m => $"\"{m.Name}\" ({m.ProductionYear})")));

            foreach (var movie in matches)
            {
                MarkPlayed(user, bingersUser, movie, $"\"{movie.Name}\" ({movie.ProductionYear})", entry, stats, cancellationToken);
            }
        }
    }

    private async Task ImportEpisodesAsync(
        User user,
        BingersUser bingersUser,
        IReadOnlyList<BingersSyncEntry> entries,
        ImportStats stats,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var verbose = bingersUser.ExtraLogging;
        var episodeEntries = entries
            .Where(e => e.EntityKind == BingersEntityRef.Episode)
            .GroupBy(e => e.EntityId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (episodeEntries.Count == 0)
        {
            _logger.LogVerbose(verbose, "No watched Bingers episode to import for user {User}", user.Username);
            return;
        }

        // Episode entries only carry the episode id: resolve the library episodes (show and season lookups are
        // cached by the resolver) and look them up in the watched entries.
        var episodes = new List<Episode>();
        foreach (var episode in _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Episode>())
        {
            var reason = UserHelper.GetSyncBlockReason(episode, bingersUser);
            if (reason != null)
            {
                _logger.LogVerbose(verbose, "Ignoring library episode \"{Series}\" \"{Episode}\" ({Path}): {Reason}", episode.SeriesName, episode.Name, episode.Path, reason);
                continue;
            }

            episodes.Add(episode);
        }

        var seriesCount = episodes.Select(e => e.SeriesId).Distinct().Count();
        _logger.LogInformation(
            "Matching {Entries} watched Bingers episodes against {Episodes} library episodes of {Series} series of user {User}",
            episodeEntries.Count,
            episodes.Count,
            seriesCount,
            user.Username);

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var failedSeries = new HashSet<Guid>();
        var matchedPerSeries = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < episodes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episode = episodes[i];
            var label = $"\"{episode.SeriesName}\" S{episode.ParentIndexNumber ?? episode.Season?.IndexNumber:00}E{episode.IndexNumber:00}";

            try
            {
                var refs = await _catalog.ResolveAsync(episode, verbose, cancellationToken).ConfigureAwait(false);
                foreach (var entityRef in refs)
                {
                    if (episodeEntries.TryGetValue(entityRef.EntityId, out var entry))
                    {
                        matched.Add(entityRef.EntityId);
                        matchedPerSeries[episode.SeriesName ?? string.Empty] = matchedPerSeries.GetValueOrDefault(episode.SeriesName ?? string.Empty) + 1;
                        MarkPlayed(user, bingersUser, episode, label, entry, stats, cancellationToken);
                    }
                }
            }
            catch (BingersApiException ex) when (!ex.IsAuthError && !ex.IsRateLimited)
            {
                // Show-level failures would repeat for every episode: report them once per series.
                var isSeriesFailure = ex.Message.StartsWith("Could not resolve Bingers show", StringComparison.Ordinal);
                if (!isSeriesFailure || failedSeries.Add(episode.SeriesId))
                {
                    _logger.LogVerbose(verbose, "Skipping {Episode}: {Message}", label, ex.Message);
                }
            }

            if (verbose && (i + 1) % 100 == 0)
            {
                _logger.LogInformation("Bingers import progress: {Done}/{Total} library episodes checked, {Matched} watched entries matched", i + 1, episodes.Count, matched.Count);
            }

            progress.Report(100d * (i + 1) / episodes.Count);
        }

        foreach (var (series, count) in matchedPerSeries.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogVerbose(verbose, "\"{Series}\": {Count} watched episodes found on Bingers", series, count);
        }

        if (failedSeries.Count > 0)
        {
            _logger.LogInformation("{Count} series of user {User} could not be found in the Bingers catalog", failedSeries.Count, user.Username);
        }

        foreach (var entityId in episodeEntries.Keys.Where(id => !matched.Contains(id)))
        {
            stats.Unmatched.Add($"episode {entityId}");
        }
    }

    private void MarkPlayed(User user, BingersUser bingersUser, BaseItem item, string label, BingersSyncEntry entry, ImportStats stats, CancellationToken cancellationToken)
    {
        var userData = _userDataManager.GetUserData(user, item);
        if (userData == null)
        {
            return;
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
            stats.UpToDate++;
            _logger.LogVerbose(bingersUser.ExtraLogging, "{Item}: already played in Jellyfin ({Local} plays, Bingers {Remote}), unchanged", label, previousPlayCount, remotePlays);
            return;
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
        stats.Updated++;
    }

    private static bool AddProviderId(Dictionary<string, List<Movie>> index, string source, string id, Movie movie)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var key = source + ":" + id.Trim();
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<Movie>();
            index[key] = list;
        }

        list.Add(movie);
        return true;
    }

    private static string NormalizeSource(string source) => source?.ToLowerInvariant() switch
    {
        "imdb" => "imdb",
        "tmdb" or "themoviedb.com" => "tmdb",
        "tvdb" => "tvdb",
        _ => null
    };

    private sealed class ImportStats
    {
        public int Updated { get; set; }

        public int UpToDate { get; set; }

        public List<string> Unmatched { get; } = new();
    }
}
