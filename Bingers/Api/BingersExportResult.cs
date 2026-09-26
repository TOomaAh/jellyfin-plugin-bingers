using System.Collections.ObjectModel;

namespace Bingers.Api;

/// <summary>
/// Statistics of a watched history export.
/// </summary>
public class BingersExportResult
{
    /// <summary>
    /// Gets or sets the number of entries marked watched on bingers.app.
    /// </summary>
    public int Exported { get; set; }

    /// <summary>
    /// Gets or sets the number of entries that were already watched on bingers.app.
    /// </summary>
    public int AlreadyWatched { get; set; }

    /// <summary>
    /// Gets or sets the number of items that could not be matched in the bingers.app catalog.
    /// </summary>
    public int NotFound { get; set; }

    /// <summary>
    /// Gets the items that could not be matched in the bingers.app catalog.
    /// </summary>
    public Collection<string> NotFoundItems { get; } = new();
}
