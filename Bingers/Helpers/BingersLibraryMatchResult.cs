using System.Collections.Generic;

namespace Bingers.Helpers;

/// <summary>
/// Result of matching bingers.app entries with the library. The matches themselves are handed out in batches.
/// </summary>
internal sealed class BingersLibraryMatchResult
{
    public int Matched { get; set; }

    public List<string> Unmatched { get; } = new();
}
