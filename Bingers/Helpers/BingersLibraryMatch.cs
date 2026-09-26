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
/// <param name="Repeated">
/// Whether the entry was already part of an earlier batch: another copy of the item lives in a show that could not be
/// grouped with the first one (no common provider id nor name). Only <paramref name="Items"/> of this batch are listed.
/// </param>
internal sealed record BingersLibraryMatch(BingersSyncEntry Entry, IReadOnlyList<BaseItem> Items, string Label, bool Repeated);
