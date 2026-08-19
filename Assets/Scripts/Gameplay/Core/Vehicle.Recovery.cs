using System.Collections;
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

    private bool IsFarOutsideViewport(float margin)
{
    var cam = Camera.main;
    if (cam == null) return false;

    Vector3 vp = cam.WorldToViewportPoint(transform.position);

    // If behind camera (z < 0), treat as OOB.
    if (vp.z < 0f) return true;

    return (vp.x < -margin || vp.x > 1f + margin || vp.y < -margin || vp.y > 1f + margin);
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
    return !gen.TryGetCellState(gp, out var st) || st != DustCellState.Solid;
}
}
