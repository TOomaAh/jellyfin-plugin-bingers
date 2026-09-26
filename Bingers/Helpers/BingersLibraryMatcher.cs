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
/// Finds the library items of bingers.app entries.
/// Movie entries point to catalog titles and are matched through their external ids; episode entries only carry the
/// episode id, so the library episodes are resolved in the catalog (show and season lookups are cached).
/// </summary>
internal sealed class BingersLibraryMatcher
{
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
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var result = new BingersLibraryMatchResult();
        await MatchMoviesAsync(user, bingersUser, entries, result, cancellationToken).ConfigureAwait(false);
        progress?.Report(10);
        await MatchEpisodesAsync(
            user,
            bingersUser,
            entries,
            result,
            new Progress<double>(percent => progress?.Report(10 + (percent * 0.9))),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task MatchMoviesAsync(
        User user,
        BingersUser bingersUser,
        IReadOnlyList<BingersSyncEntry> entries,
        BingersLibraryMatchResult result,
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
            result.Matches.Add(new BingersLibraryMatch(entry, matches, label));
        }
    }

    private async Task MatchEpisodesAsync(
        User user,
        BingersUser bingersUser,
        IReadOnlyList<BingersSyncEntry> entries,
        BingersLibraryMatchResult result,
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

        _logger.LogInformation(
            "Matching {Entries} watched Bingers episodes against {Episodes} library episodes of {Series} series of user {User}",
            episodeEntries.Count,
            episodes.Count,
            episodes.Select(e => e.SeriesId).Distinct().Count(),
            user.Username);

        var matched = new Dictionary<string, (List<BaseItem> Items, string Label)>(StringComparer.Ordinal);
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
                foreach (var entityRef in refs.Where(r => episodeEntries.ContainsKey(r.EntityId)))
                {
                    if (!matched.TryGetValue(entityRef.EntityId, out var match))
                    {
                        match = (new List<BaseItem>(), label);
                        matched[entityRef.EntityId] = match;
                        var series = episode.SeriesName ?? string.Empty;
                        matchedPerSeries[series] = matchedPerSeries.GetValueOrDefault(series) + 1;
                    }

                    match.Items.Add(episode);
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
                _logger.LogInformation("Progress: {Done}/{Total} library episodes checked, {Matched} Bingers episodes matched", i + 1, episodes.Count, matched.Count);
            }

            progress?.Report(100d * (i + 1) / episodes.Count);
        }

        foreach (var (series, count) in matchedPerSeries.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogVerbose(verbose, "\"{Series}\": {Count} watched episodes found on Bingers", series, count);
        }

        if (failedSeries.Count > 0)
        {
            _logger.LogInformation("{Count} series of user {User} could not be found in the Bingers catalog", failedSeries.Count, user.Username);
        }

        foreach (var (entityId, entry) in episodeEntries)
        {
            if (matched.TryGetValue(entityId, out var match))
            {
                result.Matches.Add(new BingersLibraryMatch(entry, match.Items, match.Label));
            }
            else
            {
                result.Unmatched.Add($"episode {entityId}");
            }
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
