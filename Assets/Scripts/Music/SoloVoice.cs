using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Plays a transient procedural solo derived from the current loop's note pool.
/// Uses MidiVoice for output. Does not modify any track loop state.
/// </summary>
[DisallowMultipleComponent]
public class SoloVoice : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private MidiVoice midiVoice;
    [SerializeField] private DrumTrack drumTrack;
    private bool _armedStart;
    private bool _armedStop;
    private List<PoolNote> _pendingPool;

    [Header("Solo Shape")]
    [Range(4, 64)] public int soloSteps = 16;          // typically 1 bin
    [Range(1, 16)] public int maxNotes = 10;           // cap density
    [Range(0f, 1f)] public float restChance = 0.35f;   // base rests
    [Range(0f, 1f)] public float accentChance = 0.25f; // occasional extra velocity

    [Header("Register")]
    public int lowestAllowedNote = 48;     // C3
    public int highestAllowedNote = 96;    // C7
    public int preferAboveCeilingSemis = 7;

    [Header("Timing")]
    [Tooltip("Assumes 16 steps per bar (4/4). stepSec = (60/BPM)/4.")]
    public int stepsPerBar = 16;

    private Coroutine _soloCo;
    private bool _stopRequested;

    private struct PoolNote
    {
        public int midi;
        public float vel01;
    }

    private void Awake()
    {
        if (!midiVoice) midiVoice = GetComponent<MidiVoice>();
    }

    public bool IsPlaying => _soloCo != null;

    public void StopSolo()
    {
        _stopRequested = true;
        if (_soloCo != null)
        {
            StopCoroutine(_soloCo);
            _soloCo = null;
        }
    }
    public void ArmSoloOnNextBoundary(IEnumerable<InstrumentTrack> tracks)
    {
        if (midiVoice == null || drumTrack == null) return;

        _pendingPool = BuildPool(tracks);
        if (_pendingPool == null || _pendingPool.Count == 0) return;

        // Wait for boundary to start cleanly.
        _armedStart = true;
    }

    private void HandleLoopBoundary()
    {
        if (!EnsureAuthority())
            return;
        if (_armedStart)
        {
            _armedStart = false;
            StopSolo();
            _stopRequested = false;
            _soloCo = StartCoroutine(SoloRoutine(_pendingPool));
            _pendingPool = null;

            // End at the NEXT boundary after starting.
            _armedStop = true;
            return;
        }

        if (_armedStop && IsPlaying)
        {
            _armedStop = false;
            StopSolo();
        }
    }
    private void OnEnable()
    {
        if (drumTrack != null)
            drumTrack.OnLoopBoundary += HandleLoopBoundary;

        // Timing authority propagation
        if (midiVoice != null && drumTrack != null)
            midiVoice.SetDrumTrack(drumTrack);
    }

    private void OnDisable()
    {
        if (drumTrack != null)
            drumTrack.OnLoopBoundary -= HandleLoopBoundary;
    }

    /// <summary>
    /// Kick off a solo using the current loop notes from all tracks.
    /// </summary>
    public void PlaySoloFromTracks(IEnumerable<InstrumentTrack> tracks)
    {
        // If already playing or already armed to start/stop, ignore.
        if (IsPlaying || _armedStart || _armedStop)
            return;

        ArmSoloOnNextBoundary(tracks);
    }

     public void PlayImmediateQuantizedLickFromTracks(IEnumerable<InstrumentTrack> tracks, int lickSteps = 4)
    {
        if (tracks == null) return;

        var pool = BuildPool(tracks);
        if (pool == null || pool.Count == 0) return;

        // Don’t stack licks
        if (_soloCo != null || IsPlaying) return;

        // Make sure we have timing authority before starting
        if (!EnsureAuthority()) return;

        lickSteps = Mathf.Clamp(lickSteps, 1, 32);

        _stopRequested = false;
        _soloCo = StartCoroutine(QuantizedLickRoutine(pool, lickSteps));
    }
    private IEnumerator QuantizedLickRoutine(List<PoolNote> pool, int lickSteps)
    {
        // We already ensured authority, but keep it safe.
        if (!EnsureAuthority())
        {
            _soloCo = null;
            yield break;
        }

        // Ask DrumTrack for the next step boundary (phase-locked).
        if (!drumTrack.TryGetNextBaseStepDsp(out double nextStepDsp, out float stepSec, stepOffset: 1))
        {
            _soloCo = null;
            yield break;
        }

        // Wait until the quantized start
        while (!_stopRequested && AudioSettings.dspTime < nextStepDsp)
            yield return null;

        int ceiling = pool.Max(p => p.midi);
        int target = Mathf.Clamp(ceiling + preferAboveCeilingSemis, lowestAllowedNote, highestAllowedNote);

        int lastMidi = -1;
        int placed = 0;

        // Now run for N quantized steps.
        double stepDsp = nextStepDsp;

        for (int s = 0; s < lickSteps; s++)
        {
            if (_stopRequested) break;
            if (placed >= maxNotes) break;
            if (Random.value < restChance) { stepDsp += stepSec; continue; }

            int midi = PickFromPool(pool, target, lastMidi);
            lastMidi = midi;

            int durTicks = PickDurationTicks(stepSec, drumTrack.drumLoopBPM);

            float v01 = pool[Random.Range(0, pool.Count)].vel01;
            if (Random.value < accentChance) v01 = Mathf.Clamp01(v01 * 1.35f);
            int v127 = Mathf.Clamp(Mathf.RoundToInt(v01 * 127f), 1, 127);

            midiVoice.PlayNoteTicks(midi, durTicks, v127);
            placed++;

            // Wait until next step boundary (DSP stable)
            stepDsp += stepSec;
            while (!_stopRequested && AudioSettings.dspTime < stepDsp)
                yield return null;
        }

        _soloCo = null;
    }

    private List<PoolNote> BuildPool(IEnumerable<InstrumentTrack> tracks)
    {
        var pool = new List<PoolNote>(128);
        if (tracks == null) return pool;

        foreach (var t in tracks)
        {
            if (t == null) continue;
            var notes = t.GetPersistentLoopNotes();
            if (notes == null) continue;

            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                int midi = FoldIntoRange(n.note, lowestAllowedNote, highestAllowedNote);
                float v01 = Mathf.Clamp01(n.velocity); // IMPORTANT: 0..1
                pool.Add(new PoolNote { midi = midi, vel01 = v01 });
                Debug.Log($"[SOLO] Adding {midi} at {v01}");
            }
        }

        return pool;
    }
    private bool EnsureAuthority()
    {
        if (drumTrack == null)
            drumTrack = GameFlowManager.Instance ? GameFlowManager.Instance.activeDrumTrack : null;

        if (drumTrack == null || midiVoice == null)
            return false;

        midiVoice.SetDrumTrack(drumTrack);
        return true;
    }

    private IEnumerator SoloRoutine(List<PoolNote> pool)
    {
        // Derive ensemble ceiling to bias register upward.
        int ceiling = pool.Max(p => p.midi);
        int target = Mathf.Clamp(ceiling + preferAboveCeilingSemis, lowestAllowedNote, highestAllowedNote);

        float stepSec = GetStepSeconds();
        int steps = Mathf.Clamp(soloSteps, 1, 64);

        int lastMidi = -1;
        int placed = 0;

        float startTime = Time.time;

        for (int step = 0; step < steps; step++)
        {
            if (_stopRequested) break;

            // align to step boundaries (simple wall-clock; OK for now)
            float t = startTime + step * stepSec;
            while (!_stopRequested && Time.time < t) yield return null;
            if (_stopRequested) break;

            if (placed >= maxNotes) continue;
            if (Random.value < restChance) continue;

            int midi = PickFromPool(pool, target, lastMidi);
            lastMidi = midi;

            // Short, punchy by default
            int durTicks = PickDurationTicks(stepSec, drumTrack.drumLoopBPM);

            // Velocity: base from pool with occasional accent
            float v01 = pool[Random.Range(0, pool.Count)].vel01;
            if (Random.value < accentChance) v01 = Mathf.Clamp01(v01 * 1.35f);

            int v127 = Mathf.Clamp(Mathf.RoundToInt(v01 * 127f), 1, 127);

            // Uses your current “ticks → ms” conversion inside MidiVoice
            midiVoice.PlayNoteTicks(midi, durTicks, v127);

            placed++;
        }

        _soloCo = null;
    }

    private int PickFromPool(List<PoolNote> pool, int target, int last)
    {
        // Pick a note near target, avoid repeating exact same midi.
        int best = pool[0].midi;
        float bestScore = float.NegativeInfinity;

        for (int i = 0; i < pool.Count; i++)
        {
            int m = pool[i].midi;
            float dist = Mathf.Abs(m - target);
            float score = -dist;

            if (m == last) score -= 12f;

            if (score > bestScore)
            {
                bestScore = score;
                best = m;
            }
        }

        // Small melodic motion nudge
        if (last >= 0 && Random.value < 0.35f)
        {
            int dir = (best >= last) ? 1 : -1;
            best = Mathf.Clamp(best + dir * 2, lowestAllowedNote, highestAllowedNote);
        }

        return best;
    }

    private float GetStepSeconds()
    {
        // Assumes 4/4 with 16 steps per bar:
        // quarter note duration = 60/BPM
        // 16 steps per bar => 4 steps per quarter => step = (60/BPM)/4
        float bpm = Mathf.Max(1f, drumTrack.drumLoopBPM);
        return (60f / bpm) / 4f;
    }

    private int PickDurationTicks(float stepSec, float bpm)
    {
        // Keep it simple: 1–2 steps in ticks.
        // Your system uses 480 ticks per quarter note.
        // 1 step = quarter/4 => 480/4 = 120 ticks.
        int ticksPerStep = 120;

        int steps = (Random.value < 0.75f) ? 1 : 2;
        return Mathf.Max(30, ticksPerStep * steps);
    }

    private int FoldIntoRange(int midi, int lo, int hi)
    {
        while (midi < lo) midi += 12;
        while (midi > hi) midi -= 12;
        return Mathf.Clamp(midi, lo, hi);
    }
}
