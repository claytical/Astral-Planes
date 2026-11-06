using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ── Note pulse: track selection's sounding ring stays still while each note's dip swells for the
//    note's sounding length (driven by RingPreviewPlayer.OnNotePulse) ──
public partial class MotifRingGlyphApplicator
{
    private struct NotePulse
    {
        public double StartDsp;
        public float  DurationSec;
    }

    private sealed class RingPulseState
    {
        public Vector3[]  Base;                 // the ring's baked, full-depth contour
        public float[]    Dip;                  // outerR - |Base[i]| per point
        public int[]      Centers;              // point index of each dip's deepest point
        public NotePulse?[] Pulses;             // latest pulse per center (null = at rest)
        public int        BinSteps;
        public int        PointCount;           // N (excludes the closing duplicate)
    }

    private readonly Dictionary<LineRenderer, RingPulseState> _pulseStates = new();

    // Per-ring idle animation (note pulse or vinyl spin), one entry per _recordRings ring, in order.
    // Lets SetNotePulse swap modes in place without rebuilding the rings.
    private readonly List<Coroutine> _idleCoroutines = new();
    private readonly List<float>     _idleRotDeg     = new();
    // Per-ring queue of pending turns (processed in order by one coroutine per ring) and the
    // (ring, dip) pairs whose turn was already scheduled ahead of their pulse.
    private const double FallbackTurnSec = 0.3;
    private struct RotateCmd { public double BeginDsp, ArriveDsp; public float TargetDeg; }
    private readonly Dictionary<int, Queue<RotateCmd>> _rotateQueues = new();
    private readonly Dictionary<int, Coroutine> _rotateCoroutines = new();
    private readonly HashSet<(int ring, int dip)> _approached = new();
    private int  _idleBinSteps = 1;
    private bool _notePulseOn;

    private void ResetIdleState()
    {
        _rotateCoroutines.Clear();
        _rotateQueues.Clear();
        _approached.Clear();
        _idleCoroutines.Clear();
        _idleRotDeg.Clear();
        _notePulseOn = false;
    }

    /// <summary>
    /// Switches the already-drawn record rings between the note-driven dip pulse (centered,
    /// selected glyph) and the spinning-vinyl idle (everything else) without redrawing them, so
    /// the glyph keeps its rings through the selected/unselected transition. No-op for glyphs
    /// that weren't drawn by AnimateApply(notePulse)/ApplyVinyl (e.g. locked previews).
    /// </summary>
    public void SetNotePulse(bool on)
    {
        if (on == _notePulseOn || config == null) return;
        if (_idleCoroutines.Count == 0 || _idleCoroutines.Count != _recordRings.Count) return;

        StopRingRotations();
        for (int i = 0; i < _recordRings.Count; i++)
        {
            if (_idleCoroutines[i] != null) StopCoroutine(_idleCoroutines[i]);
            var ring = _recordRings[i];
            if (ring.Root == null || ring.Contour == null) { _idleCoroutines[i] = null; continue; }

            // Back to the baked, unpulsed shape either way; the pulse reads it as its base.
            ApplyContourPoints(ring.Contour, ring.ContourPoints);

            if (on)
            {
                // Dips are placed in ring-local space, so the ring must sit at its authored angle
                // for them to line up with the sounding notes.
                ring.Root.transform.localRotation = Quaternion.identity;
                _idleCoroutines[i] = StartCoroutine(AnimateNotePulseRing(
                    ring.Contour, ring.OuterRadius, _idleBinSteps, () => _recordFadingOut));
            }
            else
            {
                _pulseStates.Remove(ring.Contour);
                _idleCoroutines[i] = StartCoroutine(SpinRingContinuous(i, ring.Root.transform, _idleRotDeg[i]));
            }
        }
        _notePulseOn = on;
    }

    private void StopRingRotations()
    {
        foreach (var kv in _rotateCoroutines)
            if (kv.Value != null) StopCoroutine(kv.Value);
        _rotateCoroutines.Clear();
        _rotateQueues.Clear();
        _approached.Clear();
    }

    // Queues a turn for one ring; its coroutine runs the turns in order, so an early (look-ahead)
    // turn for a later note never cancels one still in progress for an earlier note.
    private void EnqueueRotation(int ringIdx, RotateCmd cmd)
    {
        if (!_rotateQueues.TryGetValue(ringIdx, out var q)) _rotateQueues[ringIdx] = q = new Queue<RotateCmd>();
        q.Enqueue(cmd);
        if (_rotateCoroutines.TryGetValue(ringIdx, out var co) && co != null) return;
        _rotateCoroutines[ringIdx] = StartCoroutine(RunRotations(ringIdx, q));
    }

    // Each turn goes clockwise at constant speed between its begin and arrive dsp times (same
    // latency-shifted clock as PulseAmplitude). Consecutive turns chain end to start, so a ring
    // never stops: each note's pulse fires as the ring passes 12 o'clock, and it keeps moving to
    // cue up the next note.
    private IEnumerator RunRotations(int ringIdx, Queue<RotateCmd> q)
    {
        while (q.Count > 0)
        {
            var cmd = q.Dequeue();
            if (ringIdx >= _recordRings.Count) break;
            var t = _recordRings[ringIdx].Root != null ? _recordRings[ringIdx].Root.transform : null;
            if (t == null) break;

            while (AudioSettings.dspTime - config.notePulseLatencySec < cmd.BeginDsp)
            {
                yield return null;
                if (t == null) yield break;
            }

            // Clockwise only (the direction notes play in): the rotation from here to the target in (-360, 0].
            float from = t.localEulerAngles.z;
            float cw   = Mathf.Repeat(from - cmd.TargetDeg, 360f);
            float delta = (cw < 0.5f || cw > 359.5f) ? Mathf.DeltaAngle(from, cmd.TargetDeg) : -cw;
            double span = System.Math.Max(0.01, cmd.ArriveDsp - cmd.BeginDsp);
            while (true)
            {
                if (t == null) yield break;
                float u = Mathf.Clamp01((float)((AudioSettings.dspTime - config.notePulseLatencySec - cmd.BeginDsp) / span));
                t.localRotation = Quaternion.Euler(0f, 0f, from + delta * u);
                if (u >= 1f) break;
                yield return null;
            }
        }
        _rotateCoroutines.Remove(ringIdx);
    }

    private IEnumerator DrawInThenNotePulse(
        LineRenderer lr, List<Vector2> pts, float delay, float outerR, int binSteps,
        Transform ringTransform, System.Func<bool> shouldStop)
    {
        yield return AnimateSingleRing(
            lr, pts, delay, config.ringDrawInDuration,
            ringTransform, 0f,
            shouldStop, spinAfter: false);
        yield return AnimateNotePulseRing(lr, outerR, binSteps, shouldStop);
    }

    // Finds the ring for (role, bin) and the baked dip nearest the step.
    private bool TryResolveDip(MusicalRole role, int binIndex, int localStep,
        out int ringIdx, out RingPulseState s, out int dip)
    {
        for (int i = 0; i < _recordRings.Count; i++)
        {
            var ring = _recordRings[i];
            if (ring.BinIndex != binIndex || ring.Role != role || ring.Contour == null) continue;
            ringIdx = i; dip = 0;
            if (!_pulseStates.TryGetValue(ring.Contour, out s) || s.Centers.Length == 0) return false;

            int target = Mathf.RoundToInt(localStep / (float)s.BinSteps * s.PointCount) % s.PointCount;
            int bestD = int.MaxValue;
            for (int k = 0; k < s.Centers.Length; k++)
            {
                int d = Mathf.Abs(s.Centers[k] - target);
                d = Mathf.Min(d, s.PointCount - d);
                if (d < bestD) { bestD = d; dip = k; }
            }
            return true;
        }
        ringIdx = -1; s = null; dip = 0;
        return false;
    }

    // Root Z (degrees) that puts this dip at 12 o'clock; the dip's local angle is built the same
    // way as MotifRingGlyphGenerator.BuildRingPoints.
    private float TopRotationDeg(RingEntry ring, RingPulseState s, int dip)
    {
        float noteAngle = s.Centers[dip] / (float)s.PointCount * Mathf.PI * 2f
            + config.ringStartAngleOffset
            + MotifRingGlyphGenerator.RingOrdinalAngleJitter(ring.RingOrdinal);
        return (Mathf.PI * 0.5f - noteAngle) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Look-ahead from RingPreviewPlayer: turns the note's ring clockwise, continuously, from the
    /// previous note's start (<paramref name="prevStartDsp"/>) to this note's start, so this note
    /// is at 12 o'clock exactly as it pulses. Other rings are untouched.
    /// </summary>
    public void ApproachNote(MusicalRole role, int binIndex, int localStep, double startDsp, double prevStartDsp)
    {
        if (config == null || !config.notePulseRotateToTop) return;
        if (!TryResolveDip(role, binIndex, localStep, out int i, out var s, out int dip)) return;
        var ring = _recordRings[i];
        if (ring.Root == null) return;

        _approached.Add((i, dip));
        EnqueueRotation(i, new RotateCmd
        {
            BeginDsp  = System.Math.Min(prevStartDsp, startDsp - 0.05),
            ArriveDsp = startDsp,
            TargetDeg = TopRotationDeg(ring, s, dip),
        });
    }

    // Routes a note from RingPreviewPlayer to the dip it belongs to on the matching ring.
    public void PulseNote(MusicalRole role, int binIndex, int localStep, double startDsp, float durationSec)
    {
        if (!TryResolveDip(role, binIndex, localStep, out int i, out var s, out int dip)) return;
        s.Pulses[dip] = new NotePulse { StartDsp = startDsp, DurationSec = durationSec };

        // Normally ApproachNote already turned this ring. If it didn't (e.g. the first notes after
        // playback starts), turn now so the dip still ends up at the top, just without the lead.
        var ring = _recordRings[i];
        if (config.notePulseRotateToTop && ring.Root != null && !_approached.Remove((i, dip)))
            EnqueueRotation(i, new RotateCmd
            {
                BeginDsp  = startDsp,
                ArriveDsp = startDsp + FallbackTurnSec,
                TargetDeg = TopRotationDeg(ring, s, dip),
            });
    }

    // Registers a ring's pulse state straight from its baked contour at build time. Without this
    // the state only appeared after the draw-in finished, so the first loop's look-aheads were
    // dropped and the rings only turned (late, via the fallback) once playback was under way.
    private void PreparePulseState(RingEntry entry, float outerR, int binSteps)
    {
        if (entry.Contour == null || entry.ContourPoints == null || entry.ContourPoints.Count < 5) return;
        var pts = new Vector3[entry.ContourPoints.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = entry.ContourPoints[i];
        _pulseStates[entry.Contour] = BuildPulseState(pts, outerR, binSteps);
        PrunePulseStates();
    }

    // Dip depth per point and the point index at the deepest part of each dip.
    private static RingPulseState BuildPulseState(Vector3[] basePts, float outerR, int binSteps)
    {
        int count = basePts.Length;
        int n = count - 1; // last point duplicates the first to close the ring
        var s = new RingPulseState
        {
            Base = basePts, Dip = new float[count],
            BinSteps = Mathf.Max(1, binSteps), PointCount = n,
        };
        for (int i = 0; i < n; i++) s.Dip[i] = outerR - s.Base[i].magnitude;

        // One center per dip: a local maximum of dip depth, ignoring numerical noise.
        float minDip = outerR * 0.02f;
        var centers = new List<int>();
        for (int i = 0; i < n; i++)
        {
            float d = s.Dip[i];
            if (d < minDip) continue;
            if (d >= s.Dip[(i + n - 1) % n] && d > s.Dip[(i + 1) % n]) centers.Add(i);
        }
        s.Centers = centers.ToArray();
        s.Pulses  = new NotePulse?[s.Centers.Length];
        return s;
    }

    private IEnumerator AnimateNotePulseRing(
        LineRenderer lr, float outerR, int binSteps, System.Func<bool> shouldStop)
    {
        if (lr == null || config == null) yield break;
        int count = lr.positionCount;
        if (count < 5) yield break;
        int n = count - 1; // last point duplicates the first to close the ring

        // Normally already registered when the ring was built (PreparePulseState), so look-aheads
        // work during the draw-in; the lookup also keeps any pulses queued meanwhile.
        if (!_pulseStates.TryGetValue(lr, out var s) || s == null)
        {
            var basePts = new Vector3[count];
            lr.GetPositions(basePts);
            s = BuildPulseState(basePts, outerR, binSteps);
        }

        // Nearest center for every point, fixed because the contour is pre-baked.
        var owner = new int[n];
        for (int i = 0; i < n; i++)
        {
            int best = -1, bestD = int.MaxValue;
            for (int k = 0; k < s.Centers.Length; k++)
            {
                int d = Mathf.Abs(i - s.Centers[k]);
                d = Mathf.Min(d, n - d);
                if (d < bestD) { bestD = d; best = k; }
            }
            owner[i] = best;
        }

        _pulseStates[lr] = s;
        PrunePulseStates();

        var amps   = new float[s.Centers.Length];
        var output = new Vector3[count];

        bool dirty = true; // draw the resting shape once on entry
        while (!shouldStop())
        {
            if (lr == null) yield break;

            double now  = AudioSettings.dspTime - config.notePulseLatencySec;
            float  rest = config.waveRestDepth;
            bool   active = false;
            for (int k = 0; k < amps.Length; k++)
            {
                amps[k] = PulseAmplitude(s.Pulses[k], now);
                if (Mathf.Abs(amps[k] - rest) > 0.0005f) active = true;
            }

            // Rebuilding a LineRenderer's geometry is the expensive part, and with several bins
            // most rings sit idle at any moment. Only touch the line while something is moving,
            // plus one frame to settle back to rest.
            if (!active && !dirty) { yield return null; continue; }
            dirty = active;

            for (int i = 0; i < n; i++)
            {
                float dip = s.Dip[i];
                if (dip <= 0f || owner[i] < 0) { output[i] = s.Base[i]; continue; }
                float baseR = outerR - dip;
                output[i] = baseR > 0.0001f
                    ? s.Base[i] * ((outerR - dip * amps[owner[i]]) / baseR)
                    : s.Base[i];
            }
            output[n] = output[0];
            lr.SetPositions(output);
            yield return null;
        }
    }

    // Rest depth before/after the note; swells to full depth over the attack, then relaxes back
    // to rest across the note's duration (floored so very short notes still read).
    private float PulseAmplitude(NotePulse? pulse, double now)
    {
        float rest = config.waveRestDepth;
        if (pulse == null) return rest;

        float t = (float)(now - pulse.Value.StartDsp);
        if (t < 0f) return rest;

        float attack = config.notePulseAttackSec;
        if (t < attack) return Mathf.Lerp(rest, 1f, t / attack);

        float length = Mathf.Max(pulse.Value.DurationSec, config.notePulseMinSec);
        float u = Mathf.Clamp01((t - attack) / Mathf.Max(0.0001f, length - attack));
        return Mathf.Lerp(1f, rest, Mathf.SmoothStep(0f, 1f, u));
    }

    // Drops entries whose LineRenderer was destroyed with its ring.
    private void PrunePulseStates()
    {
        List<LineRenderer> dead = null;
        foreach (var kv in _pulseStates)
            if (kv.Key == null) (dead ??= new List<LineRenderer>()).Add(kv.Key);
        if (dead == null) return;
        foreach (var key in dead) _pulseStates.Remove(key);
    }
}
