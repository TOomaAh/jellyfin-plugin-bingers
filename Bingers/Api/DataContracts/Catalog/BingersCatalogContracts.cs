#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1649 // File name should match first type name
#pragma warning disable CA2227 // Collection properties should be read only
#pragma warning disable CA1002 // Do not expose generic lists

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bingers.Api.DataContracts.Catalog;

/// <summary>
/// Response of the <c>/search/titles</c> endpoint.
/// </summary>
public class BingersSearchResponse
{
    /// <summary>
    /// Gets or sets the results.
    /// </summary>
    [JsonPropertyName("results")]
    public List<BingersSearchResult> Results { get; set; }
}

/// <summary>
/// A title returned by the search endpoint.
/// </summary>
public class BingersSearchResult
{
    /// <summary>
    /// Gets or sets the title id.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>
    /// Gets or sets the kind (<c>movie</c> or <c>show</c>).
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; }

    /// <summary>
    /// Gets or sets the metadata grain version token.
    /// </summary>
    [JsonPropertyName("metadata")]
    public string Metadata { get; set; }

    /// <summary>
    /// Gets or sets the card.
    /// </summary>
    [JsonPropertyName("card")]
    public BingersTitleCard Card { get; set; }
}

/// <summary>
/// Summary card of a title.
/// </summary>
public class BingersTitleCard
{
    /// <summary>
    /// Gets or sets the original title.
    /// </summary>
    [JsonPropertyName("originalTitle")]
    public string OriginalTitle { get; set; }

    /// <summary>
    /// Gets or sets the year.
    /// </summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the localized titles.
    /// </summary>
    [JsonPropertyName("titlesI18n")]
    public Dictionary<string, string> TitlesI18n { get; set; }
}

/// <summary>
/// Metadata grain of a catalog title.
/// </summary>
public class BingersMetadataGrain
{
    /// <summary>
    /// Gets or sets the title id.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>
    /// Gets or sets the kind.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; }

    /// <summary>
    /// Gets or sets the year.
    /// </summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the external ids.
    /// </summary>
    [JsonPropertyName("external_ids")]
    public List<BingersExternalId> ExternalIds { get; set; }
}

/// <summary>
/// External id (imdb, tmdb, tvdb...) of a catalog title.
/// </summary>
public class BingersExternalId
{
    /// <summary>
    /// Gets or sets the id (either a string or a number).
    /// </summary>
    [JsonPropertyName("id")]
    public JsonElement Id { get; set; }

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; }

    /// <summary>
    /// Gets the id as a string.
    /// </summary>
    /// <returns>The id, or an empty string.</returns>
    public string GetIdString() => Id.ValueKind switch
    {
        JsonValueKind.String => Id.GetString() ?? string.Empty,
        JsonValueKind.Number => Id.GetRawText(),
        _ => string.Empty
    };
}

/// <summary>
/// Versions index of a catalog title.
/// </summary>
public class BingersVersionsIndex
{
    /// <summary>
    /// Gets or sets the title id.
    /// </summary>
    [JsonPropertyName("titleId")]
    public string TitleId { get; set; }

    /// <summary>
    /// Gets or sets the file versions.
    /// </summary>
    [JsonPropertyName("files")]
    public BingersVersionFiles Files { get; set; }
}

/// <summary>
/// Grain version tokens of a catalog title.
/// </summary>
public class BingersVersionFiles
{
    /// <summary>
    /// Gets or sets the metadata grain token.
    /// </summary>
    [JsonPropertyName("metadata")]
    public string Metadata { get; set; }

    /// <summary>
    /// Gets or sets the season grain tokens, keyed by season number.
    /// </summary>
    [JsonPropertyName("seasons")]
    public Dictionary<string, string> Seasons { get; set; }
}

/// <summary>
/// Season grain of a show.
/// </summary>
public class BingersSeasonGrain
{
    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    [JsonPropertyName("season")]
    public int? Season { get; set; }

    /// <summary>
    /// Gets or sets the episodes.
    /// </summary>
    [JsonPropertyName("episodes")]
    public List<BingersSeasonEpisode> Episodes { get; set; }
}

/// <summary>
/// Episode of a season grain.
/// </summary>
public class BingersSeasonEpisode
{
    /// <summary>
    /// Gets or sets the episode entity id.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    [JsonPropertyName("n")]
    public int? Number { get; set; }
}
