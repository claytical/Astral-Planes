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
        assignedNote            = note;
        noteDurationTicks       = duration;
        assignedInstrumentTrack = track;

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

        while (t < dur)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / dur));
            Vector2 pos = Vector2.Lerp(start, end2, u);

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
}
