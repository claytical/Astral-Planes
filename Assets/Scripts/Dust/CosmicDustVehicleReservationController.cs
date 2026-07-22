using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Orchestrates vehicle and PhaseStar keep-clear footprints: diffs a disk of cells against
/// <see cref="CosmicDustExclusionMap"/>, mirrors the result into DustClaimManager, and requests
/// regrow on released cells (optionally force-clearing newly claimed ones). The exclusion map
/// itself stays pure data per its own doc comment; this controller owns the orchestration around it.
/// </summary>
public sealed class CosmicDustVehicleReservationController
{
    private readonly CosmicDustExclusionMap _exclusions;
    private readonly Func<int> _drumsWidth;
    private readonly Func<int> _drumsHeight;
    private readonly Func<DustClaimManager> _dustClaims;
    private readonly Func<Vector2Int, bool> _isPermanentClearCell;
    private readonly Action<Vector2Int> _requestRegrowCellAt;
    private readonly Func<Vector2Int, bool> _hasDustAt;
    private readonly Action<Vector2Int, float> _carveDustByVehicle;
    private readonly Func<Vector2Int, GameObject> _tryGetCellGo;
    private readonly Action<Vector2Int, DustClearMode, float, bool> _clearCell;

    private readonly List<Vector2Int> _tmpReleased = new List<Vector2Int>(512);
    private readonly List<Vector2Int> _tmpClaimed = new List<Vector2Int>(512);
    private readonly List<Vector2Int> _reservedVehicleCells = new List<Vector2Int>(64);

    public CosmicDustVehicleReservationController(
        CosmicDustExclusionMap exclusions,
        Func<int> drumsWidth,
        Func<int> drumsHeight,
        Func<DustClaimManager> dustClaims,
        Func<Vector2Int, bool> isPermanentClearCell,
        Action<Vector2Int> requestRegrowCellAt,
        Func<Vector2Int, bool> hasDustAt,
        Action<Vector2Int, float> carveDustByVehicle,
        Func<Vector2Int, GameObject> tryGetCellGo,
        Action<Vector2Int, DustClearMode, float, bool> clearCell)
    {
        _exclusions = exclusions;
        _drumsWidth = drumsWidth;
        _drumsHeight = drumsHeight;
        _dustClaims = dustClaims;
        _isPermanentClearCell = isPermanentClearCell;
        _requestRegrowCellAt = requestRegrowCellAt;
        _hasDustAt = hasDustAt;
        _carveDustByVehicle = carveDustByVehicle;
        _tryGetCellGo = tryGetCellGo;
        _clearCell = clearCell;
    }

    private static void FillDisk(HashSet<Vector2Int> result, Vector2Int center, int r, int w, int h)
    {
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        {
            if (dx * dx + dy * dy > r * r) continue;
            int x = center.x + dx; int y = center.y + dy;
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
            result.Add(new Vector2Int(x, y));
        }
    }

    public void SetVehicleKeepClear(int ownerId, Vector2Int centerCell, int radiusCells, bool forceRemoveExisting, float forceRemoveFadeSeconds = 0.20f)
    {
        int w = _drumsWidth();
        int h = _drumsHeight();
        if (w <= 0 || h <= 0) return;

        centerCell.x = Mathf.Clamp(centerCell.x, 0, w - 1);
        centerCell.y = Mathf.Clamp(centerCell.y, 0, h - 1);

        int r = Mathf.Max(0, radiusCells);

        // Build new footprint (disk).
        var next = new HashSet<Vector2Int>();
        FillDisk(next, centerCell, r, w, h);

        _tmpReleased.Clear();
        _tmpClaimed.Clear();
        _exclusions.UpdateVehicleFootprint(ownerId, next, _tmpReleased, _tmpClaimed);

        // DustClaimManager is the authority for keep-clear vetoes.
        string claimOwner = $"Vehicle#{ownerId}";
        var dustClaims = _dustClaims();
        if (dustClaims != null)
        {
            // Released cells: remove our keep-clear claim.
            for (int i = 0; i < _tmpReleased.Count; i++)
                dustClaims.ReleaseCell(claimOwner, _tmpReleased[i], DustClaimType.KeepClear);

            // Claimed cells: add/refresh keep-clear claim.
            for (int i = 0; i < _tmpClaimed.Count; i++)
                dustClaims.ClaimCell(claimOwner, _tmpClaimed[i], DustClaimType.KeepClear, seconds: -1f, refresh: true);
        }

        // Released: allow regrow.
        for (int i = 0; i < _tmpReleased.Count; i++)
        {
            var cell = _tmpReleased[i];
            if (_isPermanentClearCell(cell)) continue;
            _requestRegrowCellAt(cell);
        }

        // Claimed: optionally force-remove dust for legibility (boosting behavior).
        if (forceRemoveExisting)
        {
            float fade = Mathf.Max(0.01f, forceRemoveFadeSeconds);

            for (int i = 0; i < _tmpClaimed.Count; i++)
            {
                var cell = _tmpClaimed[i];
                if (_isPermanentClearCell(cell)) continue;

                if (_hasDustAt(cell))
                {
                    // This path handles visual fade + authoritative state.
                    _carveDustByVehicle(cell, fade);
                }

                // Ensure a regrow attempt exists (it will self-delay while the keep-clear claim is active).
                _requestRegrowCellAt(cell);
            }
        }
    }

    public void ReleaseVehicleKeepClear(int ownerId)
    {
        _tmpReleased.Clear();
        _exclusions.ReleaseVehicleFootprint(ownerId, _tmpReleased);

        // DustClaimManager is the authority for keep-clear vetoes.
        string claimOwner = $"Vehicle#{ownerId}";
        var dustClaims = _dustClaims();
        if (dustClaims != null)
        {
            for (int i = 0; i < _tmpReleased.Count; i++)
                dustClaims.ReleaseCell(claimOwner, _tmpReleased[i], DustClaimType.KeepClear);
        }

        for (int i = 0; i < _tmpReleased.Count; i++)
        {
            var cell = _tmpReleased[i];
            if (_isPermanentClearCell(cell)) continue;
            _requestRegrowCellAt(cell);
        }
    }

    public void SetStarKeepClear(Vector2Int centerCell, int radiusCells, bool forceRemoveExisting)
    {
        int w = _drumsWidth();
        int h = _drumsHeight();
        if (w <= 0 || h <= 0) return;

        radiusCells = Mathf.Max(0, radiusCells);

        var next = new HashSet<Vector2Int>();
        FillDisk(next, centerCell, radiusCells, w, h);

        _exclusions.UpdateStarPocket(next, _tmpReleased, _tmpClaimed);

        const string claimOwner = "PhaseStarPocket";
        var dustClaims = _dustClaims();
        if (dustClaims != null)
        {
            for (int i = 0; i < _tmpReleased.Count; i++)
                dustClaims.ReleaseCell(claimOwner, _tmpReleased[i], DustClaimType.KeepClear);

            for (int i = 0; i < _tmpClaimed.Count; i++)
                dustClaims.ClaimCell(claimOwner, _tmpClaimed[i], DustClaimType.KeepClear, seconds: -1f, refresh: true);
        }

        for (int i = 0; i < _tmpReleased.Count; i++)
        {
            var cell = _tmpReleased[i];
            _requestRegrowCellAt(cell);
        }

        if (forceRemoveExisting)
        {
            for (int i = 0; i < _tmpClaimed.Count; i++)
            {
                var cell = _tmpClaimed[i];
                var go = _tryGetCellGo(cell);
                if (go != null)
                    _clearCell(cell, DustClearMode.FadeAndHide, 2f, false);
            }
        }
    }

    public void SetReservedVehicleCells(IReadOnlyList<Vector2Int> cells)
    {
        _reservedVehicleCells.Clear();
        if (cells == null) return;
        for (int i = 0; i < cells.Count; i++)
            _reservedVehicleCells.Add(cells[i]);
    }
}
