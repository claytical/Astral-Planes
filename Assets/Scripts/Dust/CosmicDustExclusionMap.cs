using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Centralized keep-clear / exclusion bookkeeping for CosmicDustGenerator.
///
/// Responsibilities:
/// - Vehicle keep-clear footprints with per-cell refcounts (supports overlaps).
/// - PhaseStar keep-clear set (single source).
///
/// Non-responsibilities:
/// - Regrow scheduling.
/// - Dust removal/pooling.
/// - Any external vetoes (claims, spawn-grid occupancy, vehicle overlap physics, etc.).
/// </summary>
public sealed class CosmicDustExclusionMap
{
    // Vehicle footprints
    private readonly Dictionary<int, HashSet<Vector2Int>> _vehicleCellsByOwner = new Dictionary<int, HashSet<Vector2Int>>();
    private readonly Dictionary<Vector2Int, int> _vehicleRefCountByCell = new Dictionary<Vector2Int, int>();

    // PhaseStar pocket
    private readonly HashSet<Vector2Int> _starCells = new HashSet<Vector2Int>();

    public bool IsKeepClearCell(Vector2Int cell)
    {
        if (_starCells.Contains(cell)) return true;
        return _vehicleRefCountByCell.TryGetValue(cell, out int rc) && rc > 0;
    }

    // ------------------------
    // Vehicle footprint API
    // ------------------------

    /// <summary>
    /// Replace (diff) the vehicle keep-clear footprint for ownerId.
    /// Returns which cells were released and which were newly claimed.
    /// </summary>
    public void UpdateVehicleFootprint(int ownerId, HashSet<Vector2Int> next,
        List<Vector2Int> released, List<Vector2Int> claimed)
    {
        released?.Clear();
        claimed?.Clear();

        if (!_vehicleCellsByOwner.TryGetValue(ownerId, out var prev) || prev == null)
        {
            prev = new HashSet<Vector2Int>();
            _vehicleCellsByOwner[ownerId] = prev;
        }

        // Released = prev - next
        foreach (var cell in prev)
        {
            if (next != null && next.Contains(cell)) continue;
            DecrementVehicleCell(cell);
            released?.Add(cell);
        }

        // Claimed = next - prev
        if (next != null)
        {
            foreach (var cell in next)
            {
                if (prev.Contains(cell)) continue;
                IncrementVehicleCell(cell);
                claimed?.Add(cell);
            }
        }

        // Replace prev contents
        prev.Clear();
        if (next != null)
        {
            foreach (var c in next) prev.Add(c);
        }
    }

    /// <summary>
    /// Release and remove a vehicle footprint for ownerId.
    /// Returns released cells.
    /// </summary>
    public void ReleaseVehicleFootprint(int ownerId, List<Vector2Int> released)
    {
        released?.Clear();
        if (!_vehicleCellsByOwner.TryGetValue(ownerId, out var prev) || prev == null) return;

        foreach (var cell in prev)
        {
            DecrementVehicleCell(cell);
            released?.Add(cell);
        }

        prev.Clear();
        _vehicleCellsByOwner.Remove(ownerId);
    }

    // ------------------------
    // Star pocket API
    // ------------------------

    /// <summary>
    /// Replace (diff) the PhaseStar keep-clear set.
    /// Returns which cells were released and which were newly claimed.
    /// </summary>
    public void UpdateStarPocket(HashSet<Vector2Int> next,
        List<Vector2Int> released, List<Vector2Int> claimed)
    {
        released?.Clear();
        claimed?.Clear();

        // Released = current - next
        foreach (var cell in _starCells)
        {
            if (next != null && next.Contains(cell)) continue;
            released?.Add(cell);
        }

        // Claimed = next - current
        if (next != null)
        {
            foreach (var cell in next)
            {
                if (_starCells.Contains(cell)) continue;
                claimed?.Add(cell);
            }
        }

        _starCells.Clear();
        if (next != null)
        {
            foreach (var c in next) _starCells.Add(c);
        }
    }

    /// <summary>
    /// Clear the PhaseStar pocket completely. Returns released cells.
    /// </summary>
    public void ClearStarPocket(List<Vector2Int> released)
    {
        released?.Clear();
        foreach (var c in _starCells) released?.Add(c);
        _starCells.Clear();
    }

    // ------------------------
    // Internals
    // ------------------------

    private void IncrementVehicleCell(Vector2Int cell)
    {
        if (_vehicleRefCountByCell.TryGetValue(cell, out int rc))
            _vehicleRefCountByCell[cell] = rc + 1;
        else
            _vehicleRefCountByCell[cell] = 1;
    }

    private void DecrementVehicleCell(Vector2Int cell)
    {
        if (!_vehicleRefCountByCell.TryGetValue(cell, out int rc)) return;
        rc--;
        if (rc <= 0) _vehicleRefCountByCell.Remove(cell);
        else _vehicleRefCountByCell[cell] = rc;
    }
}
