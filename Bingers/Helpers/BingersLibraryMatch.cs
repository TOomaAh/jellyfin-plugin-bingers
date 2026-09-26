using System.Collections.Generic;
using Bingers.Api.DataContracts.Sync;
using MediaBrowser.Controller.Entities;

namespace Bingers.Helpers;

/// <summary>
/// A bingers.app entry and the library items it corresponds to.
/// </summary>
/// <param name="Entry">The bingers.app entry.</param>
/// <param name="Items">The library items (several when the library holds duplicates).</param>
/// <param name="Label">A readable description of the entry for logs.</param>
internal sealed record BingersLibraryMatch(BingersSyncEntry Entry, IReadOnlyList<BaseItem> Items, string Label);
