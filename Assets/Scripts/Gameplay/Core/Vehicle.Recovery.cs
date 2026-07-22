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
            if (!TryEjectFromVoid(voidCenter, voidRadius))
            {
                DoSnapRespawn("void_trap_snap");
            }
            else
            {
                _lastRecoverAt = Time.time;
                _timeStuckInVoid = 0f;
            }
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

        // Teleport + impulse outward so we don’t immediately re-stick
        rb.position = world;
        rb.linearVelocity = away * Mathf.Max(2.5f, arcadeMaxSpeed * 0.35f);
        rb.angularVelocity = 0f;

        _lastRecoverAt = Time.time;
        _timeStuckInVoid = 0f;
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
        rb.position = _lastSafeWorld;
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;

        _lastRecoverAt = Time.time;
        _timeStuckInVoid = 0f;
        Debug.LogWarning($"[VEHICLE:RECOVER] {name} fallback to lastSafeWorld (reason={reason})", this);
        return;
    }

    Vector2 respawnWorld = (Vector2)drumTrack.GridToWorldPosition(respawnCell);

    rb.position = respawnWorld;
    rb.linearVelocity = Vector2.zero;
    rb.angularVelocity = 0f;

    _lastRecoverAt = Time.time;
    _timeStuckInVoid = 0f;

    Debug.LogWarning($"[VEHICLE:RECOVER] {name} respawn to cell {respawnCell} (reason={reason})", this);
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

    // IMPORTANT:
    // In your current usage, TryGetDustAt() seems to return false OR dust==null
    // when there is no dust cell at that position (or out of bounds).
    // We’re using it as “empty enough to respawn”.
    if (!gen.TryGetDustAt(gp, out var dust)) return true;
    return (dust == null);
}
}
