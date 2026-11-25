using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public partial class Vehicle
{
    private void UpdateSafeAnchor()
{
    if (rb == null || drumTrack == null) return;

    // Only record anchor when we are reasonably “in play”.
    // (Avoid recording while already offscreen or during a trap.)
    if (IsFarOutsideViewport(vehicleConfig.viewportOobMargin * 0.5f)) return;
    if (IsOutsidePlayArea(vehicleConfig.playAreaOobMargin * 0.5f)) return;

    // If you want, you can also avoid anchoring while inside a void:
    // if (IsInsideGravityVoid(out _, out _)) return;

    _lastSafeWorld = rb.position;
    _lastSafeCell = drumTrack.WorldToGridPosition(rb.position);
    _hasSafeAnchor = true;
}

    private void RecoverIfNeeded()
{
    if (_movementLocked) return; // already animating a return — don't re-trigger mid-glide
    if (Time.time - _lastRecoverAt < vehicleConfig.minSecondsBetweenRecoveries) return;
    if (rb == null || drumTrack == null || gfm == null || gfm.dustGenerator == null) return;

    // 1) Hard OOB: if we’re well outside camera viewport, snap back.
    if (IsFarOutsideViewport(vehicleConfig.viewportOobMargin))
    {
        DoSnapRespawn("viewport_oob");
        return;
    }

    // 1b) Hard OOB: outside the safe play area (e.g. tunneled into the NoteVisualizer
    // strip at the bottom, which is still within the full camera viewport and so
    // wouldn't trip the check above).
    if (IsOutsidePlayArea(vehicleConfig.playAreaOobMargin))
    {
        DoSnapRespawn("play_area_oob");
        return;
    }

    // 2) Void trap: if we’re inside a void collider AND not moving for a while, do localized eject or snap.
    if (IsInsideGravityVoid(out var voidCenter, out var voidRadius))
    {
        float speed = rb.linearVelocity.magnitude;

        if (speed <= vehicleConfig.stuckSpeedThreshold)
            _timeStuckInVoid += Time.fixedDeltaTime;
        else
            _timeStuckInVoid = 0f;

        if (_timeStuckInVoid >= vehicleConfig.stuckSecondsInVoid)
        {
            // Prefer local eject (feels physical), but fall back to snap if we can’t find a clean spot.
            // TryEjectFromVoid/DoSnapRespawn both go through BeginAnimatedRecovery, which already
            // sets _lastRecoverAt/_timeStuckInVoid — no need to duplicate that here.
            if (!TryEjectFromVoid(voidCenter, voidRadius))
                DoSnapRespawn("void_trap_snap");
        }
    }
    else
    {
        _timeStuckInVoid = 0f;
    }
}

    // General "am I moving" tracking, independent of gravity voids — consumed by CosmicDust to
    // suppress dust-pluck triggering for a vehicle that's stuck or nearly idle, without CosmicDust
    // needing to know about void state or duplicate its own velocity sampling.
    private void UpdateIdleTracking()
{
    if (rb == null) { _timeBelowMoveThreshold = 0f; return; }
    float speed = rb.linearVelocity.magnitude;
    if (speed <= vehicleConfig.stuckSpeedThreshold)
        _timeBelowMoveThreshold += Time.fixedDeltaTime;
    else
        _timeBelowMoveThreshold = 0f;

    UpdateStuckProbe(speed);
}

    // ---- Stuck probe (diagnostic) ----
    // Input held but not moving (and not in a scripted recovery glide) for kStuckProbeSeconds →
    // log, once per episode, every collider touching/overlapping the hull, with dust cell state.
    // Exists to name the culprit if the "pinned in visibly cleared dust" bug ever recurs.
    private const float kStuckProbeSeconds = 0.75f;
    private float _stuckProbeSeconds;
    private bool _stuckProbeLogged;
    private static readonly ContactPoint2D[] _stuckContacts = new ContactPoint2D[16];
    private static readonly List<Collider2D> _stuckOverlaps = new List<Collider2D>(16);

    private void UpdateStuckProbe(float speed)
    {
        bool pushing = _moveInput.sqrMagnitude > 0.0001f && !_movementLocked;
        if (!pushing || speed > vehicleConfig.stuckSpeedThreshold)
        {
            _stuckProbeSeconds = 0f;
            _stuckProbeLogged = false;
            return;
        }

        _stuckProbeSeconds += Time.fixedDeltaTime;
        if (_stuckProbeLogged || _stuckProbeSeconds < kStuckProbeSeconds) return;
        _stuckProbeLogged = true;
        LogStuckProbe();
    }

    private void LogStuckProbe()
    {
        var gen = gfm != null ? gfm.dustGenerator : null;
        var sb = new System.Text.StringBuilder();
        sb.Append($"[VEHICLE:STUCK] {name} pinned at {rb.position} (cell {(drumTrack != null ? drumTrack.WorldToGridPosition(rb.position).ToString() : "?")}) " +
                  $"input={_moveInput} boosting={boosting} energy={energyLevel:F2} plowDrain={_plowVelocityDrain:F2}");
        if (gen != null && drumTrack != null)
        {
            Vector2Int own = drumTrack.WorldToGridPosition(rb.position);
            string ownState = gen.TryGetCellState(own, out var ownSt) ? ownSt.ToString() : "?";
            sb.Append($"\n  ownCell={own} state={ownState} interior={IsInteriorCell(own)} " +
                      $"bridge={(gfm != null && (gfm.BridgePending || gfm.GhostCycleInProgress))} " +
                      $"wedgeT={_wallWedgeSeconds:F2} locked={_movementLocked}");
        }

        int contacts = rb.GetContacts(_stuckContacts);
        sb.Append($"\n  contacts={contacts}");
        for (int i = 0; i < contacts; i++)
            AppendStuckColliderInfo(sb, _stuckContacts[i].collider, gen, $"normal={_stuckContacts[i].normal} sep={_stuckContacts[i].separation:F3}");

        var filter = ContactFilter2D.noFilter;
        filter.useTriggers = false;
        _stuckOverlaps.Clear();
        int overlaps = rb.Overlap(filter, _stuckOverlaps);
        sb.Append($"\n  overlaps={overlaps}");
        for (int i = 0; i < overlaps; i++)
            AppendStuckColliderInfo(sb, _stuckOverlaps[i], gen, null);

        Debug.LogWarning(sb.ToString(), this);
    }

    private void AppendStuckColliderInfo(System.Text.StringBuilder sb, Collider2D col, CosmicDustGenerator gen, string extra)
    {
        if (col == null) { sb.Append("\n    <null>"); return; }
        sb.Append($"\n    {col.name} layer={LayerMask.LayerToName(col.gameObject.layer)} enabled={col.enabled} trigger={col.isTrigger}");
        if (extra != null) sb.Append(' ').Append(extra);

        var dust = col.GetComponentInParent<CosmicDust>();
        if (dust == null || gen == null || drumTrack == null) return;
        // Registry cell, not re-derived from world position (that mislabels cells after grid drift).
        bool fromRegistry = gen.TryGetCellOfDust(dust.gameObject, out var gp);
        if (!fromRegistry) gp = drumTrack.WorldToGridPosition(dust.transform.position);
        string state = gen.TryGetCellState(gp, out var st) ? st.ToString() : "?";
        sb.Append($" | dust cell={gp}{(fromRegistry ? "" : "(pos)")} state={state} role={dust.Role} charge={dust.Charge01:F2} " +
                  $"tintA={dust.CurrentTint.a:F2} shownA={dust.DisplayedAlpha:F2} chippable={gen.CanVehicleChip(gp)} " +
                  $"liveRes={gen.GetLiveCarveResistance01(gp):F2}");
    }

    public bool IsDustPluckIdleSuppressed =>
        _timeBelowMoveThreshold >= vehicleConfig.dustPluckIdleSuppressSeconds;

    private bool IsFarOutsideViewport(float margin)
{
    var cam = Camera.main;
    if (cam == null) return false;

    // Unzoomed gameplay frame: a presentation zoom must never trigger recovery.
    Vector3 vp = GameplayFrame.WorldToViewport(transform.position);

    // If behind camera (z < 0), treat as OOB.
    if (vp.z < 0f) return true;

    return (vp.x < -margin || vp.x > 1f + margin || vp.y < -margin || vp.y > 1f + margin);
}

    private bool IsOutsidePlayArea(float margin)
{
    if (drumTrack == null || !drumTrack.TryGetPlayAreaWorld(out var area)) return false;

    Vector2 pos = rb.position;
    return pos.x < area.left - margin || pos.x > area.right + margin ||
           pos.y < area.bottom - margin || pos.y > area.top + margin;
}

    private void UpdateVoidEnergyRecovery(float dt)
{
    bool refueling = vehicleConfig.voidEnergyRecoveryPerSecond > 0f
                     && energyLevel < capacity
                     && !_isDead
                     && IsInsideGravityVoid(out _, out _);
    // Visible tell (ship glow + fuel bar shimmer, Vehicle.StatusGlow.cs) — LEGIBILITY_AUDIT D2.
    SetVoidRefueling(refueling);
    if (!refueling) return;

    RecoverEnergy(vehicleConfig.voidEnergyRecoveryPerSecond * dt);
}

    private bool IsInsideGravityVoid(out Vector2 center, out float radius)
{
    center = default;
    radius = 0f;

    if (gravityVoidMask.value == 0)
        return false; // explicitly disabled

    Vector2 pos = rb != null ? rb.position : (Vector2)transform.position;

    Collider2D hit = Physics2D.OverlapCircle(
        pos,
        vehicleConfig.voidProbeRadiusWorld,
        gravityVoidMask
    );

    if (hit == null)
        return false;

    var cc = hit as CircleCollider2D;
    if (cc != null)
    {
        center = cc.bounds.center;
        radius = Mathf.Max(0.05f, cc.bounds.extents.x);
        return true;
    }

    center = hit.bounds.center;
    radius = Mathf.Max(
        0.05f,
        Mathf.Max(hit.bounds.extents.x, hit.bounds.extents.y)
    );
    return true;
}

    private bool TryEjectFromVoid(Vector2 voidCenter, float voidRadius)
{
    if (!_hasSafeAnchor) return false;

    // Aim to put the vehicle just outside the void boundary, away from center.
    Vector2 pos = rb.position;
    Vector2 away = (pos - voidCenter);
    if (away.sqrMagnitude < 0.0001f) away = Random.insideUnitCircle.normalized;
    else away.Normalize();

    // Candidate target position on rim + small margin
    float margin = Mathf.Max(0.15f, vehicleConfig.voidProbeRadiusWorld * 0.5f);
    Vector2 targetWorld = voidCenter + away * (voidRadius + margin);

    // Convert to a grid cell and look for a nearby empty cell (localized)
    Vector2Int targetCell = drumTrack.WorldToGridPosition(targetWorld);
    if (TryFindNearbyEmptyCell(targetCell, maxRadius: 4, out var emptyCell))
    {
        Vector2 world = (Vector2)drumTrack.GridToWorldPosition(emptyCell);

        // Glide to the rim, then apply the outward exit impulse once arrived so we don't
        // immediately re-stick — impulse now lands after the animated return, not before it.
        BeginAnimatedRecovery(world, away * Mathf.Max(2.5f, arcadeMaxSpeed * vehicleConfig.voidEjectExitSpeedMultiplier), "void_trap_eject");
        Debug.LogWarning($"[VEHICLE:RECOVER] {name} void eject to cell {emptyCell}", this);
        return true;
    }

    return false;
}

    private void DoSnapRespawn(string reason)
{
    if (!_hasSafeAnchor) return;

    // Try to find an empty cell near the last safe anchor.
    if (!TryFindNearbyEmptyCell(_lastSafeCell, vehicleConfig.respawnSearchRadiusCells, out var respawnCell))
    {
        // Absolute fallback: last safe world position.
        BeginAnimatedRecovery(_lastSafeWorld, Vector2.zero, reason);
        Debug.LogWarning($"[VEHICLE:RECOVER] {name} fallback to lastSafeWorld (reason={reason})", this);
        return;
    }

    Vector2 respawnWorld = (Vector2)drumTrack.GridToWorldPosition(respawnCell);

    BeginAnimatedRecovery(respawnWorld, Vector2.zero, reason);

    Debug.LogWarning($"[VEHICLE:RECOVER] {name} respawn to cell {respawnCell} (reason={reason})", this);
}

    /// <summary>
    /// Replaces the old instant, silent rb.position teleport: locks movement input (Vehicle.Move /
    /// UpdateMovementAndFuel both check _movementLocked) and glides the vehicle to targetWorld over
    /// vehicleConfig.recoveryReturnSeconds, so the player gets a legible beat instead of an
    /// unannounced pop. Falls back to the old instant behavior if lockMovementDuringRecovery is off.
    /// </summary>
    private void BeginAnimatedRecovery(Vector2 targetWorld, Vector2 exitVelocity, string reason)
{
    if (!vehicleConfig.lockMovementDuringRecovery)
    {
        rb.position = targetWorld;
        rb.linearVelocity = exitVelocity;
        rb.angularVelocity = 0f;
        _lastRecoverAt = Time.time;
        _timeStuckInVoid = 0f;
        return;
    }

    if (_recoveryRoutine != null) StopCoroutine(_recoveryRoutine);
    _movementLocked = true;
    _lastRecoverAt = Time.time;
    _timeStuckInVoid = 0f;
    _recoveryRoutine = StartCoroutine(AnimatedRecoveryRoutine(targetWorld, exitVelocity, reason));
}

    private IEnumerator AnimatedRecoveryRoutine(Vector2 targetWorld, Vector2 exitVelocity, string reason)
{
    Vector2 start = rb.position;
    float duration = Mathf.Max(0.01f, vehicleConfig.recoveryReturnSeconds);
    float elapsed = 0f;
    while (elapsed < duration)
    {
        elapsed += Time.fixedDeltaTime;
        float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
        rb.position = Vector2.Lerp(start, targetWorld, u);
        // Re-zero every tick — same reasoning as the bomb's Kinematic-immunity comment: prevents
        // dust/void contact forces from fighting the scripted glide.
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;
        yield return new WaitForFixedUpdate();
    }

    rb.position = targetWorld;
    rb.linearVelocity = exitVelocity;

    _movementLocked = false;
    _recoveryRoutine = null;
    Debug.LogWarning($"[VEHICLE:RECOVER] {name} animated return complete (reason={reason})", this);
}

    // ---- Wall unwedge ----
    // A ship pushing without moving for kWallWedgeSeconds while touching or overlapping dust it
    // can never carve glides to the nearest interior cell with a cell of clearance all round.
    // During the bridge (interstitial maze / path choice) every Solid wall counts: walls are
    // uncarveable there and both the plow and RecoverIfNeeded are off. Outside it, only
    // unchippable Solid cells count — chippable dust is the plow's job.
    private const float kWallWedgeSeconds = 1.0f;
    private float _wallWedgeSeconds;
    private bool _wallUnwedgeFailLogged;

    private void UpdateWallUnwedge()
    {
        if (rb == null || drumTrack == null || gfm == null || _movementLocked || _recoveryRoutine != null
            || rb.bodyType != RigidbodyType2D.Dynamic || !isActiveAndEnabled)
        {
            ResetWallWedge();
            return;
        }

        bool bridge = gfm.BridgePending || gfm.GhostCycleInProgress;
        bool pushing = _moveInput.sqrMagnitude > 0.0001f;
        if (!pushing || rb.linearVelocity.magnitude > vehicleConfig.stuckSpeedThreshold
            || !TouchingUncarveableDust(bridge))
        {
            ResetWallWedge();
            return;
        }

        _wallWedgeSeconds += Time.fixedDeltaTime;
        if (_wallWedgeSeconds < kWallWedgeSeconds) return;
        _wallWedgeSeconds = 0f;

        Vector2Int here = drumTrack.WorldToGridPosition(rb.position);
        if (!TryFindNearbyClearCell(here, maxRadius: 4, out var cell) &&
            !TryFindNearbyClearCell(here, maxRadius: 8, out cell))
        {
            if (!_wallUnwedgeFailLogged)
            {
                _wallUnwedgeFailLogged = true;
                Debug.LogWarning($"[VEHICLE:RECOVER] {name} unwedge failed at {here}: no interior clear/empty cell within 8 (bridge={bridge})", this);
            }
            return;
        }
        Vector2 world = (Vector2)drumTrack.GridToWorldPosition(cell);
        BeginAnimatedRecovery(world, Vector2.zero, bridge ? "interstitial_unwedge" : "wall_unwedge");
        Debug.LogWarning($"[VEHICLE:RECOVER] {name} unwedge {here} -> {cell} (bridge={bridge})", this);
    }

    private void ResetWallWedge()
    {
        _wallWedgeSeconds = 0f;
        _wallUnwedgeFailLogged = false;
    }

    private static readonly List<Collider2D> _wedgeOverlaps = new List<Collider2D>(16);

    private bool TouchingUncarveableDust(bool anySolid)
    {
        var gen = gfm != null ? gfm.dustGenerator : null;
        if (gen == null) return false;
        int n = rb.GetContacts(_stuckContacts);
        for (int i = 0; i < n; i++)
            if (IsUncarveableDustCollider(_stuckContacts[i].collider, gen, anySolid)) return true;

        var filter = ContactFilter2D.noFilter;
        filter.useTriggers = false;
        _wedgeOverlaps.Clear();
        int m = rb.Overlap(filter, _wedgeOverlaps);
        for (int i = 0; i < m; i++)
            if (IsUncarveableDustCollider(_wedgeOverlaps[i], gen, anySolid)) return true;
        return false;
    }

    private bool IsUncarveableDustCollider(Collider2D col, CosmicDustGenerator gen, bool anySolid)
    {
        var dust = col != null ? col.GetComponentInParent<CosmicDust>() : null;
        if (dust == null) return false;
        if (!gen.TryGetCellOfDust(dust.gameObject, out var gp))
            gp = drumTrack.WorldToGridPosition(dust.transform.position);
        if (!gen.TryGetCellState(gp, out var st) || st != DustCellState.Solid) return false;
        return anySolid || !gen.CanVehicleChip(gp);
    }

    // Nearest cell (not the current one) that is empty with every neighbour empty too, so the
    // hull lands in open corridor rather than against the same wall. Falls back to any empty cell.
    private bool TryFindNearbyClearCell(Vector2Int around, int maxRadius, out Vector2Int found)
    {
        found = around;
        for (int r = 1; r <= maxRadius; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue;
                    var c = new Vector2Int(around.x + dx, around.y + dy);
                    if (IsInteriorCell(c) && IsCellClear(c)) { found = c; return true; }
                }
        return TryFindNearbyEmptyCell(around, maxRadius, out found) && found != around && IsInteriorCell(found);
    }

    // In-bounds and off the outer ring, so a glide never lands in a border gap or off-grid.
    private bool IsInteriorCell(Vector2Int c)
    {
        if (drumTrack == null) return false;
        int w = drumTrack.GetSpawnGridWidth(), h = drumTrack.GetSpawnGridHeight();
        return c.x > 0 && c.x < w - 1 && c.y > 0 && c.y < h - 1;
    }

    private bool IsCellClear(Vector2Int c)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (!IsCellEmpty(new Vector2Int(c.x + dx, c.y + dy))) return false;
        return true;
    }

    private bool TryFindNearbyEmptyCell(Vector2Int around, int maxRadius, out Vector2Int found) {
        found = around;

    var gen = gfm.dustGenerator;
    if (gen == null) return false;

    // radius 0 first
    if (IsCellEmpty(around)) { found = around; return true; }

    int rMax = Mathf.Clamp(maxRadius, 1, 64);

    // ring scan
    for (int r = 1; r <= rMax; r++)
    {
        // top/bottom rows
        for (int dx = -r; dx <= r; dx++)
        {
            var a = new Vector2Int(around.x + dx, around.y + r);
            var b = new Vector2Int(around.x + dx, around.y - r);
            if (IsCellEmpty(a)) { found = a; return true; }
            if (IsCellEmpty(b)) { found = b; return true; }
        }

        // left/right cols (skip corners already checked)
        for (int dy = -r + 1; dy <= r - 1; dy++)
        {
            var a = new Vector2Int(around.x + r, around.y + dy);
            var b = new Vector2Int(around.x - r, around.y + dy);
            if (IsCellEmpty(a)) { found = a; return true; }
            if (IsCellEmpty(b)) { found = b; return true; }
        }
    }

    return false;
}

    private bool IsCellEmpty(Vector2Int gp)
{
    var gen = gfm.dustGenerator;
    if (gen == null) return false;

    // Cells are pooled forever and never destroyed, so TryGetDustAt() alone can't tell us
    // whether a cell is currently passable — it only tells us whether dust ever spawned
    // there. "Empty enough to respawn into" means the cell isn't currently Solid.
    // Off-grid cells have no state, which used to read as "empty" and let a glide (which moves
    // rb.position straight through walls) park the ship outside/under the border wall.
    if (!gen.IsCellInBounds(gp)) return false;
    if (gen.IsBlockAt(gp)) return false; // Cosmic Block: unbreakable wall, never respawn into it
    // The sealed record vault: its chamber is permanently empty, but recovery glides by setting
    // rb.position (straight through walls), so landing there would shut the ship in.
    if (gen.IsRecordVaultSealed && gen.IsRecordVaultCell(gp)) return false;
    return !gen.TryGetCellState(gp, out var st) || st != DustCellState.Solid;
}
}
