using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Bingers.Api.DataContracts.Catalog;
using Bingers.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Bingers.Api;

/// <summary>
/// Maps Jellyfin movies and episodes to bingers.app catalog entities.
/// </summary>
public class BingersCatalogResolver
{
    private const string MovieKind = "movie";
    private const string ShowKind = "show";

    private static readonly TimeSpan _titleCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan _titleMissCacheDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan _versionsCacheDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan _versionsMinRefreshAge = TimeSpan.FromMinutes(5);

    private readonly ILogger<BingersCatalogResolver> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry<string>> _titleCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<BingersVersionsIndex>> _versionsCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BingersSeasonGrain> _seasonCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BingersMetadataGrain> _metadataCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="BingersCatalogResolver"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger{BingersCatalogResolver}"/>.</param>
    public BingersCatalogResolver(ILogger<BingersCatalogResolver> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resolves the bingers.app entities of a movie or an episode.
    /// Multi-episode files resolve to several entities.
    /// </summary>
    /// <param name="item">The movie or episode.</param>
    /// <param name="verbose">Whether the steps of the resolution are logged in detail.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>The entities.</returns>
    /// <exception cref="BingersApiException">The item could not be found in the bingers.app catalog.</exception>
    public Task<IReadOnlyList<BingersEntityRef>> ResolveAsync(BaseItem item, bool verbose, CancellationToken cancellationToken)
    {
        return item switch
        {
            Movie movie => ResolveMovieAsync(movie, verbose, cancellationToken),
            Episode episode => ResolveEpisodeAsync(episode, verbose, cancellationToken),
            _ => throw new ArgumentException($"Unsupported item type: {item?.GetType().Name}", nameof(item))
        };
    }

    /// <summary>
    /// Gets the metadata (kind, year and external ids) of a catalog title.
    /// For movies the title id is also the watched entity id.
    /// </summary>
    /// <param name="titleId">The catalog title id.</param>
    /// <param name="verbose">Whether the requests are logged in detail.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>The metadata grain.</returns>
    /// <exception cref="BingersApiException">The title has no metadata.</exception>
    public async Task<BingersMetadataGrain> GetTitleMetadataAsync(string titleId, bool verbose, CancellationToken cancellationToken)
    {
        var versions = await GetVersionsAsync(titleId, false, verbose, cancellationToken).ConfigureAwait(false);
        var token = versions.Files?.Metadata;
        if (string.IsNullOrEmpty(token))
        {
            throw new BingersApiException($"Bingers catalog has no metadata for title {titleId}");
        }

        // Metadata grains are immutable for a given version token.
        var cacheKey = $"{titleId}/metadata@{token}";
        if (_metadataCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var metadata = await FetchMetadataAsync(titleId, token, verbose, cancellationToken).ConfigureAwait(false);
        _metadataCache[cacheKey] = metadata;
        return metadata;
    }

    /// <summary>
    /// Formats the external ids of a metadata grain for logging.
    /// </summary>
    /// <param name="metadata">The metadata grain.</param>
    /// <returns>The external ids, e.g. <c>imdb=tt0111161, tmdb=278</c>.</returns>
    public static string FormatExternalIds(BingersMetadataGrain metadata)
    {
        var ids = (metadata?.ExternalIds ?? new List<BingersExternalId>())
            .Where(e => e.Source is "imdb" or "tmdb" or "themoviedb.com" or "tvdb")
            .Select(e => e.Source + "=" + e.GetIdString())
            .Distinct()
            .ToList();
        return ids.Count == 0 ? "none" : string.Join(", ", ids);
    }

    private async Task<BingersMetadataGrain> FetchMetadataAsync(string titleId, string token, bool verbose, CancellationToken cancellationToken)
    {
        return await BingersHttp.GetJsonAsync<BingersMetadataGrain>(
            $"{BingersUris.CatalogBaseUrl}/catalog/{titleId}/metadata@{token}.json",
            _logger,
            verbose,
            cancellationToken).ConfigureAwait(false) ?? new BingersMetadataGrain();
    }

    private async Task<IReadOnlyList<BingersEntityRef>> ResolveMovieAsync(Movie movie, bool verbose, CancellationToken cancellationToken)
    {
        var titleId = await MatchTitleAsync(movie, MovieKind, verbose, cancellationToken).ConfigureAwait(false)
            ?? throw new BingersApiException($"Could not resolve Bingers movie entity for \"{movie.Name}\" ({DescribeIds(movie)})");

        return new[] { new BingersEntityRef(BingersEntityRef.Movie, titleId, titleId) };
    }

    private async Task<IReadOnlyList<BingersEntityRef>> ResolveEpisodeAsync(Episode episode, bool verbose, CancellationToken cancellationToken)
    {
        var series = episode.Series
            ?? throw new BingersApiException($"Episode \"{episode.Name}\" has no series");
        var seasonNumber = episode.ParentIndexNumber ?? episode.Season?.IndexNumber;
        if (seasonNumber == null || episode.IndexNumber == null)
        {
            throw new BingersApiException($"Episode \"{episode.Name}\" of \"{series.Name}\" requires season and episode numbers");
        }

        var titleId = await MatchTitleAsync(series, ShowKind, verbose, cancellationToken).ConfigureAwait(false)
            ?? throw new BingersApiException($"Could not resolve Bingers show entity for \"{series.Name}\" ({DescribeIds(series)})");

        var season = await GetSeasonAsync(titleId, seasonNumber.Value, series.Name, verbose, cancellationToken).ConfigureAwait(false);

        var first = episode.IndexNumber.Value;
        var last = Math.Max(first, episode.IndexNumberEnd ?? first);
        var refs = new List<BingersEntityRef>();
        for (var number = first; number <= last; number++)
        {
            var match = season.Episodes?.FirstOrDefault(e => e.Number == number);
            if (string.IsNullOrEmpty(match?.Id))
            {
                throw new BingersApiException(
                    $"Bingers catalog has no S{seasonNumber}E{number} for \"{series.Name}\" (season {seasonNumber} has {season.Episodes?.Count ?? 0} episodes)");
            }

            refs.Add(new BingersEntityRef(BingersEntityRef.Episode, match.Id, titleId));
        }

        _logger.LogVerbose(
            verbose,
            "Resolved \"{Series}\" S{Season}E{Episode} to Bingers episode {EntityIds} (show {TitleId})",
            series.Name,
            seasonNumber,
            first == last ? first.ToString(CultureInfo.InvariantCulture) : $"{first}-{last}",
            string.Join(", ", refs.Select(r => r.EntityId)),
            titleId);

        return refs;
    }

    private async Task<BingersSeasonGrain> GetSeasonAsync(string titleId, int seasonNumber, string showName, bool verbose, CancellationToken cancellationToken)
    {
        var versions = await GetVersionsAsync(titleId, false, verbose, cancellationToken).ConfigureAwait(false);
        var seasonKey = seasonNumber.ToString(CultureInfo.InvariantCulture);
        if (versions.Files?.Seasons?.ContainsKey(seasonKey) != true
            && (!_versionsCache.TryGetValue(titleId, out var entry) || entry.Age > _versionsMinRefreshAge))
        {
            // The season may have been added since the index was cached.
            _logger.LogVerbose(verbose, "Season {Season} of \"{Show}\" not in the cached Bingers versions index; refreshing it", seasonNumber, showName);
            versions = await GetVersionsAsync(titleId, true, verbose, cancellationToken).ConfigureAwait(false);
        }

        string token = null;
        if (versions.Files?.Seasons?.TryGetValue(seasonKey, out token) != true || string.IsNullOrEmpty(token))
        {
            var available = versions.Files?.Seasons?.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() ?? new List<string>();
            throw new BingersApiException(
                $"Bingers catalog has no season {seasonNumber} for \"{showName}\" (available seasons: {(available.Count == 0 ? "none" : string.Join(", ", available))})");
        }

        // Season grains are immutable for a given version token.
        var cacheKey = $"{titleId}/season-{seasonKey}@{token}";
        if (_seasonCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var season = await BingersHttp.GetJsonAsync<BingersSeasonGrain>(
            $"{BingersUris.CatalogBaseUrl}/catalog/{titleId}/season-{seasonKey}@{token}.json",
            _logger,
            verbose,
            cancellationToken).ConfigureAwait(false) ?? new BingersSeasonGrain();
        _seasonCache[cacheKey] = season;
        _logger.LogVerbose(verbose, "Loaded season {Season} of \"{Show}\" from the Bingers catalog: {Count} episodes", seasonNumber, showName, season.Episodes?.Count ?? 0);
        return season;
    }

    private async Task<BingersVersionsIndex> GetVersionsAsync(string titleId, bool forceRefresh, bool verbose, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _versionsCache.TryGetValue(titleId, out var cached) && !cached.IsExpired)
        {
            return cached.Value;
        }

        var versions = await BingersHttp.GetJsonAsync<BingersVersionsIndex>(
            $"{BingersUris.CatalogBaseUrl}/catalog/{titleId}/versions.json",
            _logger,
            verbose,
            cancellationToken).ConfigureAwait(false) ?? new BingersVersionsIndex();
        _versionsCache[titleId] = new CacheEntry<BingersVersionsIndex>(versions, _versionsCacheDuration);
        return versions;
    }

    private async Task<string> MatchTitleAsync(BaseItem item, string preferKind, bool verbose, CancellationToken cancellationToken)
    {
        var ids = new ExternalIds(
            item.GetProviderId(MetadataProvider.Imdb),
            item.GetProviderId(MetadataProvider.Tmdb),
            item.GetProviderId(MetadataProvider.Tvdb),
            item.ProductionYear);

        var cacheKey = string.Join('|', preferKind, ids.Imdb, ids.Tmdb, ids.Tvdb, ids.Year, NormalizeTitle(item.Name));
        if (_titleCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            return cached.Value;
        }

        _logger.LogVerbose(verbose, "Matching {Kind} \"{Title}\" ({Ids}) in the Bingers catalog", preferKind, item.Name, DescribeIds(item));

        if (ids.Imdb == null && ids.Tmdb == null && ids.Tvdb == null)
        {
            _logger.LogVerbose(verbose, "\"{Title}\" has no IMDb/TMDB/TVDB id in Jellyfin: only a title and year match is possible", item.Name);
        }

        var matched = await PickTitleAsync(item.Name, preferKind, ids, true, verbose, cancellationToken).ConfigureAwait(false);

        // The Jellyfin name may be localized while the search runs in English: retry with the original title.
        var originalTitle = item.OriginalTitle;
        if (matched == null
            && !string.IsNullOrWhiteSpace(originalTitle)
            && NormalizeTitle(originalTitle) != NormalizeTitle(item.Name))
        {
            _logger.LogVerbose(verbose, "No match for \"{Title}\"; retrying with the original title \"{OriginalTitle}\"", item.Name, originalTitle);
            matched = await PickTitleAsync(originalTitle, preferKind, ids, false, verbose, cancellationToken).ConfigureAwait(false);
        }

        if (matched == null)
        {
            _logger.LogVerbose(verbose, "No Bingers {Kind} matches \"{Title}\" ({Ids})", preferKind, item.Name, DescribeIds(item));
        }

        // Misses are cached too, so resolving every episode of an unknown show doesn't search again each time.
        _titleCache[cacheKey] = matched != null
            ? new CacheEntry<string>(matched.Id, _titleCacheDuration)
            : new CacheEntry<string>(null, _titleMissCacheDuration);

        return matched?.Id;
    }

    private async Task<BingersSearchResult> PickTitleAsync(
        string title,
        string preferKind,
        ExternalIds ids,
        bool allowTitleFallback,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var candidates = await SearchTitlesAsync(title, preferKind, verbose, cancellationToken).ConfigureAwait(false);

        using var throttle = new SemaphoreSlim(3);
        var scored = await Task.WhenAll(candidates
            .Take(12)
            .Where(candidate => !string.IsNullOrEmpty(candidate.Metadata))
            .Select(async candidate =>
            {
                await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var metadata = await FetchMetadataAsync(candidate.Id, candidate.Metadata, verbose, cancellationToken).ConfigureAwait(false);
                    var score = ScoreMetadataMatch(metadata, ids, preferKind);
                    _logger.LogVerbose(
                        verbose,
                        "  Candidate {TitleId} \"{CandidateTitle}\" ({Kind}, {Year}) [{Ids}] -> score {Score}",
                        candidate.Id,
                        candidate.Card?.OriginalTitle,
                        candidate.Kind,
                        candidate.Card?.Year,
                        FormatExternalIds(metadata),
                        score);
                    return (Candidate: candidate, Score: score);
                }
                catch (BingersApiException ex) when (!ex.IsRateLimited && !ex.IsAuthError)
                {
                    _logger.LogVerbose(verbose, "  Candidate {TitleId}: failed to fetch its metadata: {Message}", candidate.Id, ex.Message);
                    return (Candidate: candidate, Score: 0);
                }
                finally
                {
                    throttle.Release();
                }
            })).ConfigureAwait(false);

        var best = scored.Where(s => s.Score > 0).OrderByDescending(s => s.Score).FirstOrDefault();
        if (best.Candidate != null)
        {
            _logger.LogVerbose(verbose, "Matched \"{Title}\" to Bingers title {TitleId} by external id (score {Score})", title, best.Candidate.Id, best.Score);
            return best.Candidate;
        }

        if (!allowTitleFallback)
        {
            return null;
        }

        // Soft fallback only when both title and year match - never pick an unverified hit.
        var wanted = NormalizeTitle(title);
        if (ids.Year.HasValue)
        {
            var byTitleAndYear = candidates.FirstOrDefault(c =>
                c.Card?.Year == ids.Year
                && CandidateTitles(c).Any(t => NormalizeTitle(t) == wanted));
            if (byTitleAndYear != null)
            {
                _logger.LogVerbose(verbose, "Matched \"{Title}\" to Bingers title {TitleId} by title and year {Year} (no external id match)", title, byTitleAndYear.Id, ids.Year);
                return byTitleAndYear;
            }
        }
        else if (preferKind == ShowKind)
        {
            var titleMatches = candidates
                .Where(c => c.Kind == ShowKind && CandidateTitles(c).Any(t => NormalizeTitle(t) == wanted))
                .ToList();
            if (titleMatches.Count == 1)
            {
                _logger.LogVerbose(verbose, "Matched show \"{Title}\" to Bingers title {TitleId} as the only show with that title (no year in Jellyfin)", title, titleMatches[0].Id);
                return titleMatches[0];
            }

            _logger.LogVerbose(verbose, "{Count} Bingers shows are named \"{Title}\"; not guessing without a year", titleMatches.Count, title);
        }

        return null;
    }

    private async Task<IReadOnlyList<BingersSearchResult>> SearchTitlesAsync(string query, string preferKind, bool verbose, CancellationToken cancellationToken)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            throw new BingersApiException("Title is required to search Bingers catalog");
        }

        var url = $"{BingersUris.SearchTitles}?q={HttpUtility.UrlEncode(query)}&page=0&lang=en";
        var response = await BingersHttp.GetJsonAsync<BingersSearchResponse>(url, _logger, verbose, cancellationToken).ConfigureAwait(false);
        var results = response?.Results ?? new List<BingersSearchResult>();

        var preferred = results.Where(r => r.Kind == preferKind).ToList();
        _logger.LogVerbose(verbose, "Bingers search \"{Query}\": {Count} results, {Preferred} of kind {Kind}", query, results.Count, preferred.Count, preferKind);
        return preferred.Count > 0 ? preferred : results;
    }

    private static string DescribeIds(BaseItem item)
    {
        var parts = new List<string>();
        void Add(string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(name + "=" + value);
            }
        }

        Add("imdb", item.GetProviderId(MetadataProvider.Imdb));
        Add("tmdb", item.GetProviderId(MetadataProvider.Tmdb));
        Add("tvdb", item.GetProviderId(MetadataProvider.Tvdb));
        if (item.ProductionYear.HasValue)
        {
            parts.Add("year=" + item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "no ids" : string.Join(", ", parts);
    }

    private static int ScoreMetadataMatch(BingersMetadataGrain metadata, ExternalIds ids, string preferKind)
    {
        var score = 0;
        var externalIds = metadata?.ExternalIds ?? new List<BingersExternalId>();

        bool HasId(string value, params string[] sources) => !string.IsNullOrEmpty(value)
            && externalIds.Any(e =>
                sources.Contains(e.Source?.ToLowerInvariant())
                && string.Equals(e.GetIdString(), value, StringComparison.OrdinalIgnoreCase));

        if (HasId(ids.Imdb, "imdb"))
        {
            score += 100;
        }

        if (HasId(ids.Tmdb, "tmdb", "themoviedb.com"))
        {
            score += 80;
        }

        if (HasId(ids.Tvdb, "tvdb"))
        {
            score += 80;
        }

        if (score == 0)
        {
            return 0;
        }

        if (ids.Year.HasValue && metadata.Year == ids.Year)
        {
            score += 10;
        }

        if (metadata.Kind == preferKind)
        {
            score += 5;
        }

        return score;
    }

    private static IEnumerable<string> CandidateTitles(BingersSearchResult candidate)
    {
        if (!string.IsNullOrEmpty(candidate.Card?.OriginalTitle))
        {
            yield return candidate.Card.OriginalTitle;
        }

        if (candidate.Card?.TitlesI18n != null)
        {
            foreach (var title in candidate.Card.TitlesI18n.Values)
            {
                yield return title;
            }
        }
    }

    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(title.Length);
        var pendingSpace = false;
        foreach (var c in title.ToLowerInvariant().Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(c);
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private sealed record ExternalIds(string Imdb, string Tmdb, string Tvdb, int? Year);

    private sealed class CacheEntry<T>
    {
        private readonly DateTime _createdAt;
        private readonly DateTime _expiresAt;

        public CacheEntry(T value, TimeSpan duration)
        {
            Value = value;
            _createdAt = DateTime.UtcNow;
            _expiresAt = _createdAt + duration;
        }

        public T Value { get; }

        public bool IsExpired => DateTime.UtcNow >= _expiresAt;

        public TimeSpan Age => DateTime.UtcNow - _createdAt;
    }
}
