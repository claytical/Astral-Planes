using System.Collections;
using UnityEngine;

// ── Ring phasing ─────────────────────────────────────────────────────────
// When the motif ring stack's solid collider appears (or grows) on top of a vehicle, the vehicle
// "phases": collision with the ring is ignored and the ship is drawn ghosted until it has fully
// left the disc under its own control. Replaces the old instant teleport-out, whose landing spot
// depended on nearby dust and often still touched the ring (firing an accidental note break).
public partial class Vehicle
{
    private bool             _ringPhasing;
    private float            _postCommitRingImmuneUntil;
    private CircleCollider2D _phasingRing;
    private Coroutine        _ringPhaseRoutine;

    /// <summary>True if any part of this vehicle's collider overlaps the given circle.</summary>
    public bool OverlapsCircle(Vector2 center, float radius)
    {
        Vector2 pos = rb != null ? rb.position : (Vector2)transform.position;
        float r = radius + VehicleBodyRadius();
        return (pos - center).sqrMagnitude < r * r;
    }

    /// <summary>
    /// Start ignoring collision with <paramref name="ring"/> until this vehicle is fully outside
    /// it. No-op if already phasing through the same ring.
    /// </summary>
    public void BeginRingPhase(CircleCollider2D ring)
    {
        if (ring == null || !isActiveAndEnabled) return;
        var myCollider = GetComponent<Collider2D>();
        if (myCollider == null) return;

        if (_ringPhasing && _phasingRing == ring) return;
        if (_ringPhaseRoutine != null) StopCoroutine(_ringPhaseRoutine);

        _ringPhasing = true;
        _phasingRing = ring;
        Physics2D.IgnoreCollision(myCollider, ring, true);
        _ringPhaseRoutine = StartCoroutine(RingPhaseRoutine(myCollider, ring));
    }

    private IEnumerator RingPhaseRoutine(Collider2D myCollider, CircleCollider2D ring)
    {
        float ghostAlpha = vehicleConfig != null ? vehicleConfig.ringPhaseGhostAlpha : 0.45f;
        const float exitMargin = 0.05f;

        // yield null runs after every Update, so re-applying alpha each frame wins over other
        // per-frame sprite tints (NoteTrailVisuals, flicker routines) without fighting them.
        while (ring != null && ring.enabled && myCollider != null)
        {
            if (!OverlapsCircle(ring.bounds.center, ring.bounds.extents.x + exitMargin)) break;
            SetSpriteAlpha(ghostAlpha);
            yield return null;
        }

        EndRingPhase(myCollider, ring);
    }

    private void EndRingPhase(Collider2D myCollider, Collider2D ring)
    {
        _ringPhasing      = false;
        _phasingRing      = null;
        _ringPhaseRoutine = null;
        SetSpriteAlpha(_vehicleDefaultColor.a);

        // Post-commit immunity still running → its own coroutine restores collision when it ends.
        if (Time.time < _postCommitRingImmuneUntil) return;
        if (myCollider != null && ring != null)
            Physics2D.IgnoreCollision(myCollider, ring, false);
    }

    // Disable/destroy mid-phase: the coroutine dies with it, so restore state here.
    private void CancelRingPhase()
    {
        if (!_ringPhasing) return;
        if (_ringPhaseRoutine != null) StopCoroutine(_ringPhaseRoutine);
        EndRingPhase(GetComponent<Collider2D>(), _phasingRing);
    }

    private float VehicleBodyRadius()
    {
        var c = GetComponent<Collider2D>();
        if (c == null) return 0f;
        Vector3 e = c.bounds.extents;
        return Mathf.Max(e.x, e.y);
    }

    private void SetSpriteAlpha(float a)
    {
        if (baseSprite == null) return;
        var col = baseSprite.color;
        if (Mathf.Approximately(col.a, a)) return;
        col.a = a;
        baseSprite.color = col;
    }
}
