using System.Collections.Generic;

namespace Bingers.Helpers;

/// <summary>
/// Result of matching bingers.app entries with the library.
/// </summary>
internal sealed class BingersLibraryMatchResult
{
    public List<BingersLibraryMatch> Matches { get; } = new();

    public List<string> Unmatched { get; } = new();
}
