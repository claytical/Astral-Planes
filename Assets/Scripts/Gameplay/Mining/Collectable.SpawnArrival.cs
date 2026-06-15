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
        // Set the note fields immediately so GetNote() returns the correct value
        // even if the vehicle collects this collectable during the drift animation.
        assignedNote            = note;
        noteDurationTicks       = duration;
        assignedInstrumentTrack = track;

        transform.position = originWorld;
        if (_rb == null) TryGetComponent(out _rb);

        // Use kinematic during drift instead of simulated=false.
        // Kinematic keeps the body in the physics world (collisions/triggers still fire)
        // and tracks position exactly — so there is no re-insertion event at the end that
        // can trigger Box2D depenetration and teleport the body.
        RigidbodyType2D originalBodyType = RigidbodyType2D.Dynamic;
        if (_rb != null)
        {
            originalBodyType = _rb.bodyType;
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _rb.position = (Vector2)(Vector3)originWorld;
        }

        // Scale arrival duration by distance — slower overall drift.
        float dist = Vector3.Distance(originWorld, targetWorld);
        float baseDur = Mathf.Max(0.01f, spawnArrivalSeconds);
        float dur = Mathf.Lerp(baseDur * 0.8f, baseDur * 1.4f, Mathf.Clamp01(dist / 8f));
        dur = Mathf.Max(3.0f, dur);
        float t = 0f;

        Vector2 end2 = (Vector2)(Vector3)targetWorld;
        // Clamp convergence target inside the viewport so the drift never converges off-screen.
        if (_cam == null) _cam = Camera.main;
        if (_cam != null)
        {
            const float arrivalMargin = 1.5f;
            Vector2 vMin = _cam.ViewportToWorldPoint(Vector3.zero);
            Vector2 vMax = _cam.ViewportToWorldPoint(Vector3.one);
            end2.x = Mathf.Clamp(end2.x, vMin.x + arrivalMargin, vMax.x - arrivalMargin);
            end2.y = Mathf.Clamp(end2.y, vMin.y + arrivalMargin, vMax.y - arrivalMargin);
        }

        // Per-instance noise seeds so simultaneous collectables don't wiggle in sync.
        float noiseSeedX = UnityEngine.Random.value * 100f;
        float noiseSeedY = UnityEngine.Random.value * 100f;

        // Velocity-based Perlin drift — two octaves for the first 70% of the duration.
        // The final 30% is a convergence zone: noise fades to zero and the attractor ramps up
        // so the path spirals smoothly into end2, eliminating the need for a hard position snap.
        Vector2 pos = (Vector2)(Vector3)originWorld;
        Vector2 vel = Vector2.zero;
        const float attractPower = 1.2f;

        while (t < dur)
        {
            float dt = Time.deltaTime;
            t += dt;

            float u = t / dur;
            // convergeU: 0 during free drift, ramps 0→1 over the last 30% of duration.
            float convergeU = Mathf.Clamp01((u - 0.70f) / 0.30f);
            float noiseScale = 1f - convergeU;
            float velLerpRate = Mathf.Lerp(4f, 10f, convergeU);

            // Octave A — broad slow swirl.
            float ntA = t * spawnArrivalNoiseFrequency;
            float vxA = (Mathf.PerlinNoise(noiseSeedX + ntA, 0.3f) - 0.5f) * 2f;
            float vyA = (Mathf.PerlinNoise(0.7f, noiseSeedY + ntA) - 0.5f) * 2f;

            // Octave B — faster tight curls (2.4× frequency, 0.4× amplitude).
            float ntB = t * spawnArrivalNoiseFrequency * 2.4f;
            float vxB = (Mathf.PerlinNoise(noiseSeedX + 50f + ntB, 0.1f) - 0.5f) * 2f * 0.4f;
            float vyB = (Mathf.PerlinNoise(0.1f, noiseSeedY + 50f + ntB) - 0.5f) * 2f * 0.4f;

            Vector2 noiseVel = new Vector2(vxA + vxB, vyA + vyB) * spawnArrivalNoiseStrength * noiseScale;

            // Attractor: constant magnitude during free phase; distance-proportional spring during convergence.
            // At convergeU=1 with 3 units remaining → ~18 units/s pull, guaranteed arrival.
            Vector2 toTarget = end2 - pos;
            float toTargetDist = toTarget.magnitude;
            Vector2 attractVel;
            if (toTargetDist > 0.001f)
            {
                float scale = Mathf.Lerp(attractPower, Mathf.Max(toTargetDist * 6f, attractPower), convergeU * convergeU);
                attractVel = (toTarget / toTargetDist) * scale;
            }
            else
            {
                attractVel = Vector2.zero;
            }

            vel = Vector2.Lerp(vel, noiseVel + attractVel, dt * velLerpRate);
            pos += vel * dt;

            transform.position = new Vector3(pos.x, pos.y, originWorld.z);
            if (_rb != null) _rb.position = pos;
            yield return null;
        }

        // Drift converged — pos is at or very near end2; no hard snap needed.
        transform.position = new Vector3(pos.x, pos.y, originWorld.z);
        if (_rb != null) _rb.position = pos;

        if (_rb != null)
        {
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            _rb.bodyType = originalBodyType;
        }

        _spawnArrivalRoutine = null;

        Initialize(note, duration, track, noteSet, steps);
    }

    private IEnumerator DarkTimeoutRoutine(InstrumentTrack track)
    {
        var drums = track != null ? track.drumTrack : null;
        if (drums == null) yield break;

        // ~just over one loop; tweak to taste or make profile-driven
        float ttl = Mathf.Max(3f, drums.GetLoopLengthInSeconds() * 1.1f);
        yield return new WaitForSeconds(ttl);

        // If the vehicle already picked this up, let the carry/release path handle cleanup.
        if (_handled) yield break;

        // Release the burst slot so the StarPool gate can clear and new stars can
        // spawn, but do NOT destroy — collectables must persist until the player
        // collects or discards them (or the phase ends via ForceDestroyCollectablesInFlight).
        track.spawnedCollectables?.Remove(gameObject);
        if (burstId != 0)
            track.ReleaseSpawnGate(burstId);

        // No longer reachable via spawnedCollectables (and thus not reachable via
        // ForceDestroyCollectablesInFlight) — stop counting toward _s_liveByTrack so
        // AnyLiveForTrack/AnyLiveFromOtherTracks don't stay stuck true for the rest of the session.
        MarkNoLongerLive();
    }
}
