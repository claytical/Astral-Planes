using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class Collectable
{
    public void BeginSpawnArrival(
        Vector3 originWorld,
        Vector3 targetWorld,
        int note,
        int duration,
        InstrumentTrack track,
        NoteSet noteSet,
        List<int> steps)
    {
        if (_spawnArrivalRoutine != null)
            StopCoroutine(_spawnArrivalRoutine);

        // Protect before the first frame of flight: vehicles pass through and
        // cannot collect until the note lands.
        SetSpawnArrivalProtection(true);

        _spawnArrivalRoutine = StartCoroutine(SpawnArrivalRoutine(
            originWorld,
            targetWorld,
            note,
            duration,
            track,
            noteSet,
            steps));
    }

    private IEnumerator SpawnArrivalRoutine(
        Vector3 originWorld,
        Vector3 targetWorld,
        int note,
        int duration,
        InstrumentTrack track,
        NoteSet noteSet,
        List<int> steps)
    {
        EnsureTuning();

        assignedNote            = note;
        noteDurationTicks       = duration;
        assignedInstrumentTrack = track;

        // _role/_roleProfile aren't populated until Initialize() runs at the end of this
        // flight, but the track is already assigned, so resolve them locally to shape
        // the launch trajectory per role.
        var (flightRole, flightProfile) = ResolveRoleAndProfile(track);
        var archetype = flightProfile != null ? flightProfile.movementArchetype : null;

        transform.position = originWorld;
        if (_rb == null) TryGetComponent(out _rb);

        // Use kinematic during flight instead of simulated=false.
        // Kinematic keeps the body in the physics world and tracks position exactly —
        // so there is no re-insertion event at the end that can trigger Box2D
        // depenetration and teleport the body.
        RigidbodyType2D originalBodyType = RigidbodyType2D.Dynamic;
        if (_rb != null)
        {
            originalBodyType = _rb.bodyType;
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _rb.position = (Vector2)(Vector3)originWorld;
        }

        // Flight time = the note's duration in musical time — the launch note rings
        // for exactly as long as the streak to its grid cell.
        var drums = track != null ? track.drumTrack : null;
        float bpm = (drums != null && drums.drumLoopBPM > 1f) ? drums.drumLoopBPM : 120f;
        float dur = Mathf.Max(0.25f, duration * 60f / (bpm * 480f));

        // Carve dust along the flight path (quick regrow). Per-cell-change dedupe only,
        // so re-crossing an already-regrown cell carves it again.
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var arrivalDustGen = _gfm != null ? _gfm.dustGenerator : null;
        Vector2Int lastCarvedCell = new Vector2Int(int.MinValue, int.MinValue);

        Vector2 start = (Vector2)(Vector3)originWorld;
        Vector2 end2 = (Vector2)(Vector3)targetWorld;
        float t = 0f;

        // Harmony's launch bows into a randomized "firework tail" curl — one fresh draw
        // per launch (not the deterministic per-instance _rng, which doesn't exist yet at
        // this point and is meant for post-landing determinism, not this one-off flourish)
        // so simultaneous Harmony launches fan out looking like distinct tails.
        float harmonyCurlSign = 1f;
        float harmonyCurlAmountFrac = 0f;
        if (flightRole == MusicalRole.Harmony)
        {
            float draw = UnityEngine.Random.Range(-1f, 1f);
            harmonyCurlSign = draw >= 0f ? 1f : -1f;
            float minAmp = archetype != null ? archetype.launchCurlAmplitudeMin : 0.15f;
            float maxAmp = archetype != null ? archetype.launchCurlAmplitudeMax : 0.45f;
            harmonyCurlAmountFrac = Mathf.Lerp(minAmp, maxAmp, Mathf.Abs(draw));
        }

        while (t < dur)
        {
            t += Time.deltaTime;
            float uLinear = Mathf.Clamp01(t / dur);
            float u = Mathf.SmoothStep(0f, 1f, uLinear);
            Vector2 pos = ComputeLaunchPosition(flightRole, archetype, start, end2, u, uLinear, harmonyCurlSign, harmonyCurlAmountFrac);

            if (drums != null && arrivalDustGen != null)
            {
                Vector2Int cell = drums.CellOf(pos);
                if (cell != lastCarvedCell)
                {
                    lastCarvedCell = cell;
                    if (arrivalDustGen.HasDustAt(cell))
                        arrivalDustGen.CarveCellPreserveGray(cell, arrivalCarveFadeSeconds, DustClearSource.CollectableArrival, runPreExplode: true);
                }
            }

            transform.position = new Vector3(pos.x, pos.y, originWorld.z);
            if (_rb != null) _rb.position = pos;
            yield return null;
        }

        // Landed — snap exactly onto the destination cell.
        transform.position = new Vector3(end2.x, end2.y, originWorld.z);
        if (_rb != null)
        {
            _rb.position = end2;
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            _rb.bodyType = originalBodyType;
        }

        _spawnArrivalRoutine = null;
        SetSpawnArrivalProtection(false);

        Initialize(note, duration, track, noteSet, steps);
    }

    /// <summary>
    /// Shapes the launch flight path per role, on top of the shared eased start→end travel:
    /// Bass stays a straight line; Groove weaves a lateral sine wave; Harmony bows into a
    /// randomized-curl arc ("firework tail"); Lead traces a tight corkscrew (matched-frequency
    /// sin/cos, 90° out of phase, across the travel axis and its perpendicular — this is a 2D
    /// top-down game, so a literal 3D helix isn't representable, only its in-plane silhouette).
    /// All oscillating shapes taper to zero at both endpoints (via a Sin(pi*uLinear) envelope)
    /// so there is no visible pop at launch or landing.
    /// </summary>
    private static Vector2 ComputeLaunchPosition(
        MusicalRole role, MusicalRoleMovementProfile archetype,
        Vector2 start, Vector2 end, float u, float uLinear,
        float harmonyCurlSign, float harmonyCurlAmountFrac)
    {
        Vector2 straight = Vector2.Lerp(start, end, u);

        Vector2 travel = end - start;
        float dist = travel.magnitude;
        if (dist < 0.0001f) return straight;

        Vector2 dir = travel / dist;
        Vector2 perp = new Vector2(-dir.y, dir.x);

        switch (role)
        {
            case MusicalRole.Groove:
            {
                float ampFrac = archetype != null ? archetype.launchSineAmplitude : 0.12f;
                float cycles  = archetype != null ? archetype.launchSineCycles     : 1.5f;
                float envelope = Mathf.Sin(Mathf.PI * uLinear);
                float wave     = Mathf.Sin(2f * Mathf.PI * cycles * uLinear);
                return straight + perp * (dist * ampFrac * envelope * wave);
            }
            case MusicalRole.Harmony:
            {
                Vector2 control = Vector2.Lerp(start, end, 0.5f)
                                 + perp * (harmonyCurlSign * harmonyCurlAmountFrac * dist);
                float inv = 1f - u;
                return inv * inv * start + 2f * inv * u * control + u * u * end;
            }
            case MusicalRole.Lead:
            {
                float ampFrac = archetype != null ? archetype.launchCorkscrewAmplitude : 0.06f;
                float cycles  = archetype != null ? archetype.launchCorkscrewCycles    : 3f;
                float envelope = Mathf.Sin(Mathf.PI * uLinear);
                float phase    = 2f * Mathf.PI * cycles * uLinear;
                float along  = Mathf.Sin(phase) * dist * ampFrac * envelope;
                float across = Mathf.Cos(phase) * dist * ampFrac * envelope;
                return straight + dir * along + perp * across;
            }
            default:
                return straight;
        }
    }
}
