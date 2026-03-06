using System.Collections.Generic;
using UnityEngine;

public sealed class CosmicDustCollectableHoldMap
{
    private readonly Dictionary<Vector2Int, float> _until = new Dictionary<Vector2Int, float>();

    // Scratch buffer for expiry purge to avoid alloc inside PurgeExpired.
    private readonly List<Vector2Int> _expiredScratch = new List<Vector2Int>();

    public bool Release(Vector2Int cell)
    {
        return _until.Remove(cell);
    }

    public void ExtendHold(Vector2Int cell, float untilTime)
    {
        if (_until.TryGetValue(cell, out float prev))
            _until[cell] = Mathf.Max(prev, untilTime);
        else
            _until[cell] = untilTime;
    }

    public bool IsHeld(Vector2Int cell, float nowTime)
    {
        if (!_until.TryGetValue(cell, out float until)) return false;
        if (until <= nowTime) return false;
        return true;
    }

    public float GetUntilOrZero(Vector2Int cell)
    {
        return _until.TryGetValue(cell, out float until) ? until : 0f;
    }

    /// <summary>
    /// Remove all entries whose hold time has already elapsed.
    /// Call periodically (e.g. once per musical step or on a timer) to prevent
    /// stale entries from silently blocking regrowth indefinitely.
    /// </summary>
    public void PurgeExpired(float nowTime)
    {
        if (_until.Count == 0) return;

        _expiredScratch.Clear();
        foreach (var kvp in _until)
        {
            if (kvp.Value <= nowTime)
                _expiredScratch.Add(kvp.Key);
        }

        for (int i = 0; i < _expiredScratch.Count; i++)
            _until.Remove(_expiredScratch[i]);
    }
}