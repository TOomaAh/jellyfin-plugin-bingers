#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1649 // File name should match first type name
#pragma warning disable CA2227 // Collection properties should be read only
#pragma warning disable CA1002 // Do not expose generic lists

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Bingers.Api.DataContracts.Sync;

/// <summary>
/// Response of the <c>/sync/pull</c> endpoint.
/// </summary>
public class BingersSyncPullResponse
{
    /// <summary>
    /// Gets or sets the entries.
    /// </summary>
    [JsonPropertyName("entries")]
    public List<BingersSyncEntry> Entries { get; set; }
}

/// <summary>
/// A watched entry of the user.
/// </summary>
public class BingersSyncEntry
{
    /// <summary>
    /// Gets or sets the entity kind.
    /// </summary>
    [JsonPropertyName("entityKind")]
    public string EntityKind { get; set; }

    /// <summary>
    /// Gets or sets the entity id.
    /// </summary>
    [JsonPropertyName("entityId")]
    public string EntityId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the entity is watched.
    /// </summary>
    [JsonPropertyName("watched")]
    public bool? Watched { get; set; }

    /// <summary>
    /// Gets or sets the play count.
    /// </summary>
    [JsonPropertyName("plays")]
    public int? Plays { get; set; }
}

/// <summary>
/// Body of the <c>/sync/push</c> endpoint.
/// </summary>
public class BingersSyncPushRequest
{
    /// <summary>
    /// Gets or sets the client batch id.
    /// </summary>
    [JsonPropertyName("clientBatchId")]
    public string ClientBatchId { get; set; }

    /// <summary>
    /// Gets or sets the operations.
    /// </summary>
    [JsonPropertyName("ops")]
    public List<BingersSyncOperation> Ops { get; set; }
}

/// <summary>
/// A single sync operation.
/// </summary>
public class BingersSyncOperation
{
    /// <summary>
    /// Gets or sets the operation id.
    /// </summary>
    [JsonPropertyName("opId")]
    public string OpId { get; set; }

    /// <summary>
    /// Gets or sets the table.
    /// </summary>
    [JsonPropertyName("table")]
    public string Table { get; set; }

    /// <summary>
    /// Gets or sets the primary key.
    /// </summary>
    [JsonPropertyName("pk")]
    public BingersEntityKey Pk { get; set; }

    /// <summary>
    /// Gets or sets the fields.
    /// </summary>
    [JsonPropertyName("fields")]
    public BingersEntryFields Fields { get; set; }
}

/// <summary>
/// Primary key of an entry.
/// </summary>
public class BingersEntityKey
{
    /// <summary>
    /// Gets or sets the entity kind.
    /// </summary>
    [JsonPropertyName("entityKind")]
    public string EntityKind { get; set; }

    /// <summary>
    /// Gets or sets the entity id.
    /// </summary>
    [JsonPropertyName("entityId")]
    public string EntityId { get; set; }
}

/// <summary>
/// Fields of an entry.
/// </summary>
public class BingersEntryFields
{
    /// <summary>
    /// Gets or sets a value indicating whether the entity is watched.
    /// </summary>
    [JsonPropertyName("watched")]
    public bool Watched { get; set; }

    /// <summary>
    /// Gets or sets the play count.
    /// </summary>
    [JsonPropertyName("plays")]
    public int Plays { get; set; }

    /// <summary>
    /// Gets or sets the batch id.
    /// </summary>
    [JsonPropertyName("batchId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string BatchId { get; set; }
}

/// <summary>
/// Error body returned by the API.
/// </summary>
public class BingersErrorResponse
{
    /// <summary>
    /// Gets or sets the error.
    /// </summary>
    [JsonPropertyName("error")]
    public BingersError Error { get; set; }

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; }
}

/// <summary>
/// Error details.
/// </summary>
public class BingersError
{
    /// <summary>
    /// Gets or sets the code.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; }

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; }

    /// <summary>
    /// Gets or sets the retry delay in seconds.
    /// </summary>
    [JsonPropertyName("retryAfterSeconds")]
    public double? RetryAfterSeconds { get; set; }
}
