using UnityEngine;
using System.Collections;

public partial class Vehicle
{
    private void RefreshVehicleKeepClearIfNeeded() {
        if (gfm.BridgePending || gfm.GhostCycleInProgress) return;

        // Throttle refresh — collision safety clearance, not a dust design feature
        if (Time.time < _nextVehicleKeepClearRefreshAt) return;
        _nextVehicleKeepClearRefreshAt = Time.time + Mathf.Max(0.02f, vehicleConfig.vehicleKeepClearRefreshSeconds);
        if (gfm == null) return;

        var gen = gfm.dustGenerator;
        var drum = gfm.activeDrumTrack;
        if (gen == null || drum == null) return;

        int ownerId = _id;

        Vector2Int centerCell = drum.WorldToGridPosition(rb.position);

        if (!boosting)
        {
            // Claim just the center cell (radius=0, no force-remove) so dust can never
            // regrow directly under the vehicle, without actively clearing any dust.
            gen.SetVehicleKeepClear(ownerId, centerCell, 0, forceRemoveExisting: false);
            return;
        }

        // While boosting, claim the pocket (prevents regrowth) but don't force-remove existing
        // dust — the plow owns carving with resistance applied. forceRemoveExisting is only
        // used for the one-time spawn rest pocket, not for live carving.
        gen.SetVehicleKeepClear(
            ownerId,
            centerCell,
            Mathf.Max(0, vehicleKeepClearRadiusCells),
            forceRemoveExisting: false
        );
    }
    private IEnumerator Co_CarveSpawnRestPocket()
    {
        // Optional delay to allow spawn ordering (dust grid, drumTrack, etc.) to settle.
        if (vehicleConfig.spawnRestPocketDelaySeconds > 0f)
            yield return new WaitForSeconds(vehicleConfig.spawnRestPocketDelaySeconds);
        else
            yield return null; // at least one frame so the dust grid exists

        if (gfm == null) yield break;

        var gen = gfm.dustGenerator;

        if (gen == null || drumTrack == null) yield break;

        // Compute which cell we're currently in.
        Vector2 pos = (rb != null) ? rb.position : (Vector2)transform.position;
        Vector2Int centerCell = drumTrack.WorldToGridPosition(pos);

        // Choose a radius that guarantees we are not born overlapping walls.
        int radiusCells = Mathf.Max(0, vehicleConfig.spawnRestPocketRadiusCells);
        if (vehicleConfig.spawnRestPocketAutoRadius)
        {
            float cellWorld = Mathf.Max(0.01f, drumTrack.GetCellWorldSize());
            float rWorld = 0.0f;
            var col = GetComponent<Collider2D>();
            if (col != null)
                rWorld = Mathf.Max(col.bounds.extents.x, col.bounds.extents.y);
            else
                rWorld = 0.35f; // conservative fallback

            // Expand by a small margin so resting contacts don't continuously resolve.
            float rWithMargin = rWorld + (cellWorld * 0.15f);
            radiusCells = Mathf.Max(0, Mathf.CeilToInt(rWithMargin / cellWorld));
        }

        // Carve a small pocket *once*, then release keep-clear so regrowth behaves normally.
        // This creates a "rest" volume without creating a tunnel or permanently preventing regrowth.
        int ownerId = _id;
        gen.SetVehicleKeepClear(
            ownerId,
            centerCell,
            radiusCells,
            forceRemoveExisting: true,
            forceRemoveFadeSeconds: Mathf.Max(0.01f, vehicleConfig.spawnRestPocketFadeSeconds)
        );
        gen.ReleaseVehicleKeepClear(ownerId);
    }

    private void DoPlowTick()
    {
        _plowVelocityDrain = 0f;
        if (gfm == null || gfm.dustGenerator == null || drumTrack == null) return;
        if (gfm.BridgePending || gfm.GhostCycleInProgress) return;
        if (!boosting) return;

        Vector2 vel = rb.linearVelocity;
        var gen = gfm.dustGenerator;
        float cellSize  = Mathf.Max(0.001f, drumTrack.GetCellWorldSize());
        Vector2 forward = vel.normalized;
        Vector2 perp    = new Vector2(-forward.y, forward.x);
        float fade      = Mathf.Max(0.01f, vehicleConfig.plowFadeSeconds);
        int halfW       = Mathf.Max(0, profile.plowHalfWidthCells);
        int depth       = Mathf.Max(0, profile.plowDepthCells);
        int chipAmount  = Mathf.Max(1, profile.plowChipAmount);

        // Drain is felt regardless of speed; chipping requires min speed to carve.
        bool canCarve = vel.magnitude >= profile.plowMinSpeed;
        float totalVelocityDrain = 0f;

        for (int d = 0; d <= depth; d++)
        {
            for (int s = -halfW; s <= halfW; s++)
            {
                Vector2    sampleWorld = rb.position
                    + forward * (d * cellSize)
                    + perp    * (s * cellSize);
                Vector2Int cell = drumTrack.WorldToGridPosition(sampleWorld);
                if (!gen.TryGetCellState(cell, out var cellState)) continue;
                bool isSolid    = cellState == DustCellState.Solid;
                bool isClearing = cellState == DustCellState.Clearing;
                if (!isSolid && !isClearing) continue;

                float liveRes      = gen.GetLiveCarveResistance01(cell);
                float effectiveRes = liveRes * (1f - Mathf.Clamp01(profile.carveResistanceBypass01));

                if (profile.carveVelocityDrainPerCell > 0f)
                    totalVelocityDrain = Mathf.Max(totalVelocityDrain, liveRes * profile.carveVelocityDrainPerCell);

                if (!isSolid) continue;
                if (effectiveRes >= 1f) continue;
                if (!canCarve) continue;

                gen.SuppressCellColliderForPlow(cell);
                gen.ChipDustByVehicle(cell, chipAmount, fade, profile.carveResistanceBypass01, profile);
                _isActivePlow = true;
            }
        }

        _plowVelocityDrain = totalVelocityDrain;
    }
}
