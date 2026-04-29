using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Invariants:
/// - Bin cursor is always non-negative.
/// - Bin allocation/fill lists are always sized to requested bin capacity.
/// - Fill/chord metadata arrays stay in lockstep with bin count.
/// </summary>
public sealed class TrackExpansionState
{
    public readonly List<bool> BinFilled = new();
    public bool[] BinAllocated;
    public int BinCursor;
    public List<int> BinFillOrder = new();
    public List<int> BinChordIndex = new();
    public int NextFillOrdinal = 1;

    public void SetBinCursor(int value) => BinCursor = Mathf.Max(0, value);
    public void AdvanceBinCursor(int by = 1) => BinCursor = Mathf.Max(0, BinCursor + Mathf.Max(1, by));
    public void ResetBinCursor() => BinCursor = 0;

    public void EnsureBinCount(int count)
    {
        var want = Mathf.Max(1, count);
        while (BinFilled.Count < want) BinFilled.Add(false);
        if (BinFilled.Count > want) BinFilled.RemoveRange(want, BinFilled.Count - want);

        if (BinAllocated == null || BinAllocated.Length != want)
            BinAllocated = new bool[want];

        EnsureMetadataSize(want);
    }

    public void EnsureMetadataSize(int want)
    {
        while (BinFillOrder.Count < want) BinFillOrder.Add(0);
        if (BinFillOrder.Count > want) BinFillOrder.RemoveRange(want, BinFillOrder.Count - want);

        while (BinChordIndex.Count < want) BinChordIndex.Add(-1);
        if (BinChordIndex.Count > want) BinChordIndex.RemoveRange(want, BinChordIndex.Count - want);

        if (NextFillOrdinal < 1) NextFillOrdinal = 1;
    }
}
