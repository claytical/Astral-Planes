using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ── Record departure ────────────────────────────────────────────────────
// When a completed motif's record spins off, every marker on the timeline swirls into the
// record's centre, then attaches to the record and leaves with it. The loop keeps playing through the interstitial maze, but
// marker creation stays suppressed until BeginNewMotif_ClearAll (path chosen) resets it.
public partial class NoteVisualizer
{
    // True from the record's departure until the next motif begins: no loop markers are drawn.
    private bool _loopMarkersDetached;
    private Coroutine _followRecordRoutine;
    private List<Transform> _departingMarkers;

    /// <summary>
    /// Swirls every timeline marker into <paramref name="record"/>'s centre over
    /// <paramref name="convergeSeconds"/>, then attaches it to the record so it tilts and scales
    /// away with it; markers are destroyed after <paramref name="totalSeconds"/>.
    /// With a null record the markers are destroyed immediately.
    /// </summary>
    public void SendLoopMarkersWithRecord(Transform record, float convergeSeconds, float totalSeconds)
    {
        ascensionDirector?.ClearAllTasks();

        var markers = new HashSet<Transform>();
        if (noteMarkers != null)
            foreach (var kv in noteMarkers)
                if (kv.Value != null) markers.Add(kv.Value);

        if (trackRows != null)
        {
            foreach (var row in trackRows)
            {
                if (!row) continue;
                var tags = row.GetComponentsInChildren<MarkerTag>(includeInactive: true);
                foreach (var tag in tags)
                    if (tag != null) markers.Add(tag.transform);
            }
        }

        noteMarkers?.Clear();
        _stepBurst.Clear();
        _ghostNoteSteps.Clear();
        _loopMarkersDetached = true;

        if (record == null || !isActiveAndEnabled)
        {
            foreach (var m in markers)
                if (m != null) SafeDestroy(m.gameObject);
            return;
        }

        // Markers stay unparented from the record while they converge: the record spins a full
        // 360° over the same window, and inheriting that rotation made every marker sweep round
        // together as one rigid disc, drowning out the per-marker swirl. They attach to the
        // record only once they've reached its centre, where its rotation is invisible.
        //
        // They do leave their track rows, though: row-scanning layout passes (RecomputeTrackLayout,
        // ReconcileDuplicateMarkersInRow, CanonicalizeTrackMarkers) would otherwise re-adopt them
        // into noteMarkers mid-swirl and fight the motion every frame.
        // The rows' shared parent keeps them on the same canvas/sorting as before.
        Transform holder = transform;
        if (trackRows != null)
            foreach (var row in trackRows)
                if (row && row.parent) { holder = row.parent; break; }

        var list = new List<Transform>(markers.Count);
        foreach (var m in markers)
        {
            if (m == null) continue;
            m.SetParent(holder, worldPositionStays: true);
            list.Add(m);
        }

        StopFollowRecordRoutine(destroyMarkers: true); // a previous departure still in flight
        _departingMarkers = list;
        _followRecordRoutine = StartCoroutine(FollowRecordRoutine(record, list, convergeSeconds, totalSeconds));
    }

    private IEnumerator FollowRecordRoutine(Transform record, List<Transform> markers, float convergeSeconds, float totalSeconds)
    {
        // Converge as a swirl, not a uniform spiral: each marker gets its own start delay,
        // travel time, twist and pull curve. They drift wide first, then whip inward faster
        // as they near the centre (vortex-like), with a small wobble across the flow.
        float converge = Mathf.Max(0.01f, convergeSeconds);
        int n = markers.Count;
        var startScale = new Vector3[n];
        var r0         = new float[n];
        var theta0     = new float[n];
        var z0         = new float[n];
        var delay      = new float[n];
        var dur        = new float[n];
        var twist      = new float[n];   // total radians swept around the centre
        var pullPow    = new float[n];   // >1: lingers wide, then rushes in
        var wobbleAmp  = new float[n];
        var wobbleFreq = new float[n];
        var wobblePh   = new float[n];

        // One shared swirl direction so it reads as a single vortex; a few stragglers counter-drift
        // briefly via the wobble rather than spinning the other way outright.
        float dir = Random.value < 0.5f ? 1f : -1f;
        Vector3 centre0 = record.position;
        for (int i = 0; i < n; i++)
        {
            var m = markers[i];
            if (m == null) continue;
            // World-space offset from the record's centre (independent of the record's rotation).
            Vector3 p = m.position - centre0;
            startScale[i] = m.localScale;
            r0[i]         = new Vector2(p.x, p.y).magnitude;
            theta0[i]     = Mathf.Atan2(p.y, p.x);
            z0[i]         = p.z;
            delay[i]      = Random.Range(0f, 0.35f) * converge;
            dur[i]        = Mathf.Max(0.01f, (converge - delay[i]) * Random.Range(0.65f, 1f));
            twist[i]      = dir * Random.Range(0.6f, 1.6f) * Mathf.PI * 2f;
            pullPow[i]    = Random.Range(1.6f, 3.2f);
            wobbleAmp[i]  = r0[i] * Random.Range(0.04f, 0.14f);
            wobbleFreq[i] = Random.Range(2f, 5f);
            wobblePh[i]   = Random.Range(0f, Mathf.PI * 2f);
        }

        float elapsed = 0f;
        while (elapsed < converge)
        {
            if (record == null) { DestroyMarkers(markers); yield break; }
            elapsed += Time.deltaTime;
            Vector3 centre = record.position;
            for (int i = 0; i < n; i++)
            {
                var m = markers[i];
                if (m == null) continue;

                float t = Mathf.Clamp01((elapsed - delay[i]) / dur[i]);
                float pull = Mathf.Pow(t, pullPow[i]);              // 0 → 1, back-loaded
                float r = r0[i] * (1f - pull);
                // Angular sweep tracks the pull, so a marker whips round fastest near the centre.
                float theta = theta0[i] + twist[i] * Mathf.Pow(t, 1.4f);
                // Wobble across the flow, fading out as the marker is absorbed.
                float wobble = wobbleAmp[i] * (1f - pull) * Mathf.Sin(wobblePh[i] + t * wobbleFreq[i] * Mathf.PI * 2f);
                r = Mathf.Max(0f, r + wobble);

                m.position   = centre + new Vector3(Mathf.Cos(theta) * r, Mathf.Sin(theta) * r, z0[i] * (1f - pull));
                m.localScale = Vector3.Lerp(startScale[i], startScale[i] * 0.35f, pull);
            }
            yield return null;
        }

        if (record == null) { DestroyMarkers(markers); yield break; }

        // Now gathered at the centre: ride the record's tilt + scale-down, then clean up
        // (the record's transform is reused).
        foreach (var m in markers)
            if (m != null) m.SetParent(record, worldPositionStays: true);

        float remaining = Mathf.Max(0f, totalSeconds - elapsed);
        if (remaining > 0f) yield return new WaitForSeconds(remaining);

        DestroyMarkers(markers);
        _followRecordRoutine = null;
        _departingMarkers = null;
    }

    // Deactivation mid-swirl would otherwise strand half-scaled markers until the next motif.
    private void OnDisable() => StopFollowRecordRoutine(destroyMarkers: true);

    private void StopFollowRecordRoutine(bool destroyMarkers)
    {
        if (_followRecordRoutine != null) StopCoroutine(_followRecordRoutine);
        _followRecordRoutine = null;
        if (destroyMarkers && _departingMarkers != null) DestroyMarkers(_departingMarkers);
        _departingMarkers = null;
    }

    private void DestroyMarkers(List<Transform> markers)
    {
        foreach (var m in markers)
            if (m != null) SafeDestroy(m.gameObject);
    }
}
