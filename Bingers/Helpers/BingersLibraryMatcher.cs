using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bingers.Api;
using Bingers.Api.DataContracts.Catalog;
using Bingers.Api.DataContracts.Sync;
using Bingers.Model;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Bingers.Helpers;

/// <summary>
/// Finds the library items of bingers.app entries and hands them out in batches, so callers apply their changes while
/// the (long) matching goes on.
/// Movie entries point to catalog titles and are matched through their external ids, with all their library copies at
/// once. Episode entries only carry the episode id, so the library episodes are resolved in the catalog, show by show:
/// the copies of a show (same provider id or name) are resolved together and a batch never splits them.
/// </summary>
internal sealed class BingersLibraryMatcher
{
    /// <summary>
    /// Minimum number of matches per batch; episode batches are only cut between two shows.
    /// </summary>
    public const int BatchSize = 50;

    private readonly ILibraryManager _libraryManager;
    private readonly BingersCatalogResolver _catalog;
    private readonly ILogger _logger;

    public BingersLibraryMatcher(ILibraryManager libraryManager, BingersCatalogResolver catalog, ILogger logger)
    {
        _libraryManager = libraryManager;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<BingersLibraryMatchResult> MatchAsync(
        User user,
        BingersUser bingersUser,
        IReadOnlyList<BingersSyncEntry> entries,
        Func<IReadOnlyList<BingersLibraryMatch>, Task> onBatch,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var result = new BingersLibraryMatchResult();
        await MatchMoviesAsync(user, bingersUser, entries, result, onBatch, cancellationToken).ConfigureAwait(false);
        progress?.Report(10);
        await MatchEpisodesAsync(
            user,
            bingersUser,
            entries,
            result,
            onBatch,
            new Progress<double>(percent => progress?.Report(10 + (percent * 0.9))),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task MatchMoviesAsync(
        User user,
        BingersUser bingersUser,
        IReadOnlyList<BingersSyncEntry> entries,
        BingersLibraryMatchResult result,
        Func<IReadOnlyList<BingersLibraryMatch>, Task> onBatch,
        CancellationToken cancellationToken)
    {
        var verbose = bingersUser.ExtraLogging;
        var movieEntries = entries.Where(e => e.EntityKind == BingersEntityRef.Movie).ToList();
        if (movieEntries.Count == 0)
        {
            _logger.LogVerbose(verbose, "No watched Bingers movie for user {User}", user.Username);
            return;
        }

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

        var batch = new List<BingersLibraryMatch>();
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
                result.Unmatched.Add($"movie {entry.EntityId} (no catalog metadata)");
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
                result.Unmatched.Add($"movie {entry.EntityId} ({metadata.Year}, {ids})");
                continue;
            }

            var label = string.Join(", ", matches.Select(m => $"\"{m.Name}\" ({m.ProductionYear})").Distinct());
            _logger.LogVerbose(
                verbose,
                "[{Index}/{Total}] Bingers movie {EntityId} ({Ids}, {Plays} plays) matches {Movies}",
                i + 1,
                movieEntries.Count,
                entry.EntityId,
                ids,
                entry.Plays,
                label);
            batch.Add(new BingersLibraryMatch(entry, matches, label, false));
            result.Matched++;

            if (batch.Count >= BatchSize)
            {
                await onBatch(batch).ConfigureAwait(false);
                batch = new List<BingersLibraryMatch>();
            }
        }

        if (batch.Count > 0)
        {
            await onBatch(batch).ConfigureAwait(false);
        }
    }

    private async Task MatchEpisodesAsync(
        User user,
        BingersUser bingersUser,
        IReadOnlyList<BingersSyncEntry> entries,
        BingersLibraryMatchResult result,
        Func<IReadOnlyList<BingersLibraryMatch>, Task> onBatch,
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
            _logger.LogVerbose(verbose, "No watched Bingers episode for user {User}", user.Username);
            return;
        }

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

        // Copies of a show (several libraries, 4K versions...) share a provider id or a name: keep them together.
        var shows = GroupByShow(episodes);

        _logger.LogInformation(
            "Matching {Entries} watched Bingers episodes against {Episodes} library episodes of {Shows} shows of user {User}",
            episodeEntries.Count,
            episodes.Count,
            shows.Count,
            user.Username);

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Dictionary<string, (List<BaseItem> Items, string Label, bool Repeated)>(StringComparer.Ordinal);
        var failedShows = 0;
        var done = 0;

        foreach (var show in shows)
        {
            var showName = show[0].SeriesName;
            var showFailure = (string)null;
            var matchedInShow = 0;

            foreach (var episode in show)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                progress?.Report(100d * done / episodes.Count);

                if (showFailure != null)
                {
                    continue;
                }

                var label = $"\"{episode.SeriesName}\" S{episode.ParentIndexNumber ?? episode.Season?.IndexNumber:00}E{episode.IndexNumber:00}";
                try
                {
                    var refs = await _catalog.ResolveAsync(episode, verbose, cancellationToken).ConfigureAwait(false);
                    foreach (var entityRef in refs.Where(r => episodeEntries.ContainsKey(r.EntityId)))
                    {
                        if (!pending.TryGetValue(entityRef.EntityId, out var match))
                        {
                            match = (new List<BaseItem>(), label, emitted.Contains(entityRef.EntityId));
                            pending[entityRef.EntityId] = match;
                            matchedInShow++;
                        }

                        match.Items.Add(episode);
                    }
                }
                catch (BingersApiException ex) when (!ex.IsAuthError && !ex.IsRateLimited)
                {
                    // A show that can't be found or whose requests keep failing would fail for each of its episodes:
                    // skip the rest of the show. Season/episode level misses only skip the episode.
                    if (ex.Message.StartsWith("Could not resolve Bingers show", StringComparison.Ordinal)
                        || ex.Message.StartsWith("Bingers request failed after", StringComparison.Ordinal))
                    {
                        showFailure = ex.Message;
                        failedShows++;
                        _logger.LogInformation("Skipping show \"{Show}\" ({Count} episodes): {Message}", showName, show.Count, ex.Message);
                    }
                    else
                    {
                        _logger.LogVerbose(verbose, "Skipping {Episode}: {Message}", label, ex.Message);
                    }
                }
            }

            if (matchedInShow > 0)
            {
                _logger.LogVerbose(verbose, "\"{Show}\": {Count} watched episodes found on Bingers", showName, matchedInShow);
            }

            // Cut batches between shows only, so all the copies of an episode are decided together.
            if (pending.Count >= BatchSize)
            {
                await FlushAsync(pending, emitted, episodeEntries, result, onBatch).ConfigureAwait(false);
            }

            if (verbose)
            {
                _logger.LogInformation("Progress: {Done}/{Total} library episodes checked, {Matched} Bingers episodes matched", done, episodes.Count, result.Matched + pending.Count);
            }
        }

        await FlushAsync(pending, emitted, episodeEntries, result, onBatch).ConfigureAwait(false);

        if (failedShows > 0)
        {
            _logger.LogInformation("{Count} shows of user {User} were skipped (not found in the Bingers catalog or requests failing)", failedShows, user.Username);
        }

        foreach (var entityId in episodeEntries.Keys.Where(id => !emitted.Contains(id)))
        {
            result.Unmatched.Add($"episode {entityId}");
        }
    }

    private static async Task FlushAsync(
        Dictionary<string, (List<BaseItem> Items, string Label, bool Repeated)> pending,
        HashSet<string> emitted,
        Dictionary<string, BingersSyncEntry> episodeEntries,
        BingersLibraryMatchResult result,
        Func<IReadOnlyList<BingersLibraryMatch>, Task> onBatch)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var batch = pending
            .Select(kv => new BingersLibraryMatch(episodeEntries[kv.Key], kv.Value.Items, kv.Value.Label, kv.Value.Repeated))
            .ToList();
        foreach (var match in batch)
        {
            if (emitted.Add(match.Entry.EntityId))
            {
                result.Matched++;
            }
        }

        pending.Clear();
        await onBatch(batch).ConfigureAwait(false);
    }

    /// <summary>
    /// Groups episodes by show, merging the shows that share a provider id or a name, ordered by show name.
    /// </summary>
    private static List<List<Episode>> GroupByShow(List<Episode> episodes)
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Find(string key)
        {
            while (parent[key] != key)
            {
                key = parent[key] = parent[parent[key]];
            }

            return key;
        }

        void Union(string a, string b)
        {
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            var ra = Find(a);
            var rb = Find(b);
            if (!string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase))
            {
                parent[ra] = rb;
            }
        }

        // Link every series item to its provider ids and name; series sharing any of them end up in the same group.
        var seriesKeys = new Dictionary<Guid, string>();
        foreach (var series in episodes.Select(e => e.Series).Where(s => s != null).GroupBy(s => s.Id).Select(g => g.First()))
        {
            var own = "series:" + series.Id.ToString("N");
            parent.TryAdd(own, own);
            foreach (var key in ShowKeys(series))
            {
                Union(own, key);
            }

            seriesKeys[series.Id] = own;
        }

        return episodes
            .GroupBy(e => Find(seriesKeys[e.Series.Id]), StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderBy(e => e.ParentIndexNumber ?? e.Season?.IndexNumber)
                .ThenBy(e => e.IndexNumber)
                .ToList())
            .OrderBy(g => g[0].SeriesName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ShowKeys(Series series)
    {
        foreach (var (name, provider) in new[] { ("tvdb", MetadataProvider.Tvdb), ("tmdb", MetadataProvider.Tmdb), ("imdb", MetadataProvider.Imdb) })
        {
            var id = series.GetProviderId(provider);
            if (!string.IsNullOrWhiteSpace(id))
            {
                yield return name + ":" + id.Trim();
            }
        }

        var normalized = new string((series.Name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (normalized.Length > 0)
        {
            yield return "name:" + normalized + ":" + series.ProductionYear;
        }
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
}
