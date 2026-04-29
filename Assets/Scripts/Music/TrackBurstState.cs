using System;
using System.Collections.Generic;

/// <summary>
/// Invariants:
/// - Remaining, total spawned, and collected counters are keyed by burstId.
/// - Burst lifecycle cleanup removes all keyed bookkeeping together.
/// - Destroy handlers are removed when collectables are destroyed/unregistered.
/// </summary>
public sealed class TrackBurstState
{
    public readonly Dictionary<int, int> Remaining = new();
    public readonly Dictionary<int, int> TotalSpawned = new();
    public readonly Dictionary<int, int> Collected = new();
    public readonly Dictionary<int, HashSet<int>> BurstSteps = new();
    public readonly Dictionary<int, int> LeaderBinsBeforeWrite = new();
    public readonly Dictionary<int, int> WroteBin = new();
    public readonly Dictionary<int, int> TargetBin = new();
    public readonly Dictionary<Collectable, Action> DestroyHandlers = new();

    public void InitializeBurst(int burstId, int spawnedCount, int targetBin)
    {
        Remaining[burstId] = spawnedCount;
        TotalSpawned[burstId] = spawnedCount;
        Collected[burstId] = 0;
        TargetBin[burstId] = targetBin;
    }

    public void IncrementCollected(int burstId)
    {
        Collected.TryGetValue(burstId, out var current);
        Collected[burstId] = current + 1;
    }

    public bool TryDecrementRemaining(int burstId, out int remaining)
    {
        if (!Remaining.TryGetValue(burstId, out remaining))
            return false;

        remaining = Math.Max(0, remaining - 1);
        Remaining[burstId] = remaining;
        return true;
    }

    public void ClearBurst(int burstId)
    {
        Remaining.Remove(burstId);
        TotalSpawned.Remove(burstId);
        Collected.Remove(burstId);
        BurstSteps.Remove(burstId);
        TargetBin.Remove(burstId);
        WroteBin.Remove(burstId);
        LeaderBinsBeforeWrite.Remove(burstId);
    }

    public void ClearAll()
    {
        Remaining.Clear();
        TotalSpawned.Clear();
        Collected.Clear();
        BurstSteps.Clear();
        TargetBin.Clear();
        WroteBin.Clear();
        LeaderBinsBeforeWrite.Clear();
        DestroyHandlers.Clear();
    }
}
