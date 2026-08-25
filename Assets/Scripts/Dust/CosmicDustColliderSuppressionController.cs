using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns the plow-driven collider-suppression stamp (disable a cell's collider while a
/// vehicle plows through it, re-enable once the plow moves on and the grace period
/// elapses) and the vehicle-overlap physics query it depends on for both suppression
/// recovery and the regrow veto path.
/// </summary>
public sealed class CosmicDustColliderSuppressionController
{
    private readonly DustGridState _gridState;
    private readonly Func<Vector2Int, bool> _isInBounds;
    private readonly Func<Vector2Int, bool> _isCellSolid;
    private readonly Func<bool> _drumsReady;
    private readonly Func<float> _getCellWorldSize;
    private readonly Func<Vector2Int, Vector3> _gridToWorldPosition;
    private readonly Func<LayerMask> _getVehicleMask;
    private readonly Func<float> _getRegrowVetoBoxMul;
    private readonly Func<int> _getRegrowVetoMaxHits;
    private readonly Action<CosmicDust, bool> _setDustCollision;

    // Cells whose colliders are temporarily disabled by an active vehicle plow.
    // Vehicle.DoPlowTick refreshes the stamp each tick; Tick() re-enables colliders
    // on cells that survive once the plow moves on.
    private readonly Dictionary<Vector2Int, float> _plowSuppressedColliders = new();
    // First-suppressed timestamp per cell, set once and never refreshed — used to bound
    // total suppression time regardless of continued vehicle overlap (see Tick()).
    private readonly Dictionary<Vector2Int, float> _plowSuppressionStartTime = new();
    // Reusable snapshot buffer for Tick() to avoid per-frame allocation.
    private List<Vector2Int> _plowSuppressedKeys;
    private const float kPlowColliderRecoveryGraceSeconds = 0.25f;
    // Hard cap on suppression regardless of continued overlap — a vehicle parked on a still-
    // solid cell (stopped mid-carve, or driven back onto one) must eventually be repelled
    // again instead of sitting inside it indefinitely, which is what an unbounded "defer while
    // overlapping" wait would otherwise allow.
    private const float kPlowColliderMaxSuppressionSeconds = 1.0f;
    private Collider2D[] _vehicleVetoHits;

    public CosmicDustColliderSuppressionController(
        DustGridState gridState,
        Func<Vector2Int, bool> isInBounds,
        Func<Vector2Int, bool> isCellSolid,
        Func<bool> drumsReady,
        Func<float> getCellWorldSize,
        Func<Vector2Int, Vector3> gridToWorldPosition,
        Func<LayerMask> getVehicleMask,
        Func<float> getRegrowVetoBoxMul,
        Func<int> getRegrowVetoMaxHits,
        Action<CosmicDust, bool> setDustCollision)
    {
        _gridState = gridState;
        _isInBounds = isInBounds;
        _isCellSolid = isCellSolid;
        _drumsReady = drumsReady;
        _getCellWorldSize = getCellWorldSize;
        _gridToWorldPosition = gridToWorldPosition;
        _getVehicleMask = getVehicleMask;
        _getRegrowVetoBoxMul = getRegrowVetoBoxMul;
        _getRegrowVetoMaxHits = getRegrowVetoMaxHits;
        _setDustCollision = setDustCollision;
    }

    public void DisableCellCollider(Vector2Int cell)
    {
        if (!_isInBounds(cell)) return;
        var dust = _gridState.CellDust?[cell.x, cell.y];
        if (dust != null) _setDustCollision(dust, false);
    }

    // Plow pass-through: the collider comes back via Tick() once the plow stops
    // refreshing the stamp, so surviving Solid cells re-solidify.
    public void SuppressCellColliderForPlow(Vector2Int cell)
    {
        if (!_isInBounds(cell)) return;
        DisableCellCollider(cell);
        if (!_plowSuppressedColliders.ContainsKey(cell))
            _plowSuppressionStartTime[cell] = Time.time;
        _plowSuppressedColliders[cell] = Time.time;
    }

    /// <summary>
    /// Drops the suppression stamp for a cell without touching its collider — used when
    /// the cell has been fully carved and the clear/regrow path now owns the collider.
    /// </summary>
    public void ClearSuppression(Vector2Int cell)
    {
        _plowSuppressedColliders.Remove(cell);
        _plowSuppressionStartTime.Remove(cell);
    }

    public void Tick()
    {
        if (_plowSuppressedColliders.Count == 0) return;

        float now = Time.time;
        _plowSuppressedKeys ??= new List<Vector2Int>(32);
        _plowSuppressedKeys.Clear();
        _plowSuppressedKeys.AddRange(_plowSuppressedColliders.Keys);

        for (int i = 0; i < _plowSuppressedKeys.Count; i++)
        {
            var gp = _plowSuppressedKeys[i];
            if (now - _plowSuppressedColliders[gp] < kPlowColliderRecoveryGraceSeconds) continue;

            if (!_isCellSolid(gp))
            {
                // Fully carved (or repurposed): the clear/regrow path owns the collider now.
                _plowSuppressedColliders.Remove(gp);
                _plowSuppressionStartTime.Remove(gp);
                continue;
            }

            // Defer while a vehicle still overlaps so the collider doesn't pop it out --
            // unless suppression has run past the hard cap, in which case a still-solid cell
            // must re-collide regardless (a parked vehicle would otherwise never be repelled).
            bool suppressedTooLong = _plowSuppressionStartTime.TryGetValue(gp, out float startedAt)
                && (now - startedAt) >= kPlowColliderMaxSuppressionSeconds;
            if (IsVehicleOverlappingCell(gp) && !suppressedTooLong) continue;

            var dust = _gridState.CellDust?[gp.x, gp.y];
            if (dust != null) _setDustCollision(dust, true);
            _plowSuppressedColliders.Remove(gp);
            _plowSuppressionStartTime.Remove(gp);
        }
    }

    public bool IsVehicleOverlappingCell(Vector2Int gp)
    {
        if (!_drumsReady()) return false;
        var vehicleMask = _getVehicleMask();
        if (vehicleMask.value == 0) return false;

        float cellWorld = Mathf.Max(0.001f, _getCellWorldSize());
        Vector2 center = _gridToWorldPosition(gp);
        return OverlapVehicleAt(center, cellWorld);
    }

    public bool IsVehicleOverlappingCellWorld(Vector3 cellWorld, float cellWorldSize) =>
        OverlapVehicleAt(cellWorld, cellWorldSize);

    // NOTE: even if vehicleMask is broad/misconfigured, we only veto if a Vehicle is present.
    private bool OverlapVehicleAt(Vector2 center, float cellWorldSize)
    {
        Vector2 size = Vector2.one * Mathf.Max(0.001f, cellWorldSize * _getRegrowVetoBoxMul());

        EnsureVetoHitsBuffer();

        var filter = new ContactFilter2D();
        filter.SetLayerMask(_getVehicleMask());
        filter.useTriggers = Physics2D.queriesHitTriggers;
        int hits = Physics2D.OverlapBox(center, size, 0f, filter, _vehicleVetoHits);
        if (hits <= 0) return false;

        for (int i = 0; i < hits; i++)
        {
            var col = _vehicleVetoHits[i];
            if (col == null) continue;

            if (col.GetComponentInParent<Vehicle>() != null)
                return true;
        }

        return false;
    }

    private void EnsureVetoHitsBuffer()
    {
        int maxHits = Mathf.Max(1, _getRegrowVetoMaxHits());
        if (_vehicleVetoHits == null || _vehicleVetoHits.Length != maxHits)
            _vehicleVetoHits = new Collider2D[maxHits];
    }
}
