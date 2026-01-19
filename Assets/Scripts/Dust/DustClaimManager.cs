using System;
using System.Collections.Generic;
using UnityEngine;

public enum DustClaimType
{
    KeepClear = 10,       // PhaseStar / vehicle pockets
    Occupancy = 20,       // Collectable is sitting here
    TemporaryCarve = 30,  // MineNode erosion / corridor
    PeekHole = 40,        // micro-hole for visibility
}

public sealed class DustClaimManager : MonoBehaviour
{
    private struct Claim
    {
        public string owner;
        public DustClaimType type;
        public float expiresAt; // Time.time seconds; <=0 means "until released"
    }

    // Each cell can have multiple claims.
    private readonly Dictionary<Vector2Int, List<Claim>> _claims = new();

    public bool IsBlocked(Vector2Int cell)
    {
        CleanupExpired(cell);
        return _claims.TryGetValue(cell, out var list) && list.Count > 0;
    }

    public float GetMaxRemaining(Vector2Int cell)
    {
        CleanupExpired(cell);
        if (!_claims.TryGetValue(cell, out var list) || list.Count == 0) return 0f;

        float now = Time.time;
        float max = 0f;
        for (int i = 0; i < list.Count; i++)
        {
            float exp = list[i].expiresAt;
            if (exp <= 0f) return float.PositiveInfinity; // non-expiring claim
            max = Mathf.Max(max, exp - now);
        }
        return max;
    }

    public void ClaimCell(string owner, Vector2Int cell, DustClaimType type, float seconds = -1f, bool refresh = true)
    {
        if (string.IsNullOrEmpty(owner)) owner = "Unknown";

        CleanupExpired(cell);

        if (!_claims.TryGetValue(cell, out var list))
        {
            list = new List<Claim>(2);
            _claims[cell] = list;
        }

        float exp = (seconds > 0f) ? (Time.time + seconds) : 0f;

        // Refresh existing claim from same owner+type, else add new.
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].owner == owner && list[i].type == type)
            {
                if (refresh) list[i] = new Claim { owner = owner, type = type, expiresAt = exp };
                return;
            }
        }

        list.Add(new Claim { owner = owner, type = type, expiresAt = exp });
    }

    public void ClaimCells(string owner, IEnumerable<Vector2Int> cells, DustClaimType type, float seconds = -1f, bool refresh = true)
    {
        if (cells == null) return;
        foreach (var c in cells)
            ClaimCell(owner, c, type, seconds, refresh);
    }

    public void ReleaseOwner(string owner)
    {
        if (string.IsNullOrEmpty(owner)) return;

        var keys = ListPool<Vector2Int>.Get();
        try
        {
            foreach (var kvp in _claims)
                keys.Add(kvp.Key);

            for (int k = 0; k < keys.Count; k++)
            {
                var cell = keys[k];
                var list = _claims[cell];
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i].owner == owner)
                        list.RemoveAt(i);

                if (list.Count == 0) _claims.Remove(cell);
            }
        }
        finally
        {
            ListPool<Vector2Int>.Release(keys);
        }
    }

    public void ReleaseCell(string owner, Vector2Int cell, DustClaimType? type = null)
    {
        CleanupExpired(cell);

        if (!_claims.TryGetValue(cell, out var list)) return;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            bool ownerMatch = list[i].owner == owner;
            bool typeMatch  = !type.HasValue || list[i].type == type.Value;
            if (ownerMatch && typeMatch) list.RemoveAt(i);
        }

        if (list.Count == 0) _claims.Remove(cell);
    }

    private void CleanupExpired(Vector2Int cell)
    {
        if (!_claims.TryGetValue(cell, out var list)) return;

        float now = Time.time;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            float exp = list[i].expiresAt;
            if (exp > 0f && now >= exp)
                list.RemoveAt(i);
        }

        if (list.Count == 0)
            _claims.Remove(cell);
    }

    // Small pool helper so we don’t alloc in ReleaseOwner
    private static class ListPool<T>
    {
        private static readonly Stack<List<T>> _pool = new();
        public static List<T> Get() => _pool.Count > 0 ? _pool.Pop() : new List<T>(64);
        public static void Release(List<T> list)
        {
            list.Clear();
            _pool.Push(list);
        }
    }
}