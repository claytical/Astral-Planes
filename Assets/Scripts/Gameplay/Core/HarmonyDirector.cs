using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;

public enum TrackRetunePolicy { BassHarmonyLead, DensestFirst, Manual }
public class HarmonyDirector : MonoBehaviour
{
    
    private const int MaxCharges = 4;
    [SerializeField] private ChordProgressionProfile profile;
    [SerializeField] private int cursor = 0;
    private const int MaxRings = 4;

    private bool _armedChordAdvance, _previewActiveThisLoop, _heldThroughBoundary, _previewStartedInsideWindow, _hasPendingProfileSwap;
    private int  _pendingCharges = 0, _lastPreviewLoopIdx    = -1, _ringsArmedThisLoop    = 0, _globalBuiltCount = 1, _previewChordIdx = -1;   // the palette index we're previewing this loop

    private readonly Dictionary<InstrumentTrack, List<int>> _trackSeq = new();
    private readonly Dictionary<InstrumentTrack, int> _trackPos = new();
    private ChordProgressionProfile _pendingProfile;

    [SerializeField] private int remixRings = 0;
    [SerializeField, Range(0f, 4f)] private float commitWindowBeats = 1f; // last N beats before downbeat
    [SerializeField] private bool burnIfOutsideWindow;            // consume rings even if mistimed (no chord change)
    [SerializeField] private bool spreadOverLoops = true;                 // retune 1 track per loop if true
    [SerializeField] private TrackRetunePolicy retunePolicy = TrackRetunePolicy.BassHarmonyLead;
    [SerializeField] private bool seedFromProfileOnPhaseStart;    // leave false for Option C
    [Header("Scene refs")]
    public DrumTrack drums;
    public InstrumentTrackController tracks;
    public ChordChangeArpeggiator arpeggiator;

    private enum ConflictPolicy { DeferPlayerToNextLoop, LetPlayerOvertake }
    [SerializeField] private ConflictPolicy conflictPolicy = ConflictPolicy.DeferPlayerToNextLoop;
    void OnEnable()  { if (drums != null) drums.OnLoopBoundary += OnLoopBoundary; }
    void OnDisable() { if (drums != null) drums.OnLoopBoundary -= OnLoopBoundary; }
    public void Initialize(DrumTrack d, InstrumentTrackController t, ChordChangeArpeggiator a = null) {
        // (your existing Initialize body)
        // After you set tracks, initialize per-track sequences:
        if (tracks?.tracks != null)
        {
            _trackSeq.Clear();
            _trackPos.Clear();
            foreach (var tr in tracks.tracks)
            {
                if (tr == null) continue;
                _trackSeq[tr] = new List<int> { 0 }; // start at I for every track
                _trackPos[tr] = 0;
            }
        }
    }
    public void SetActiveProfile(ChordProgressionProfile profile, bool applyImmediately)
    {
        if (profile == null) return;
        this.profile = profile;

        // Reset builder state for the new phase
        _globalBuiltCount = Mathf.Clamp(this.profile.chordSequence.Count > 0 ? 1 : 0, 0, this.profile.chordSequence.Count);
        _previewChordIdx = -1;

        // Do NOT seed notes (Option C): only retune existing notes to I for tracks that already have notes
// HarmonyDirector.SetActiveProfile(...)
        if (applyImmediately && tracks?.tracks != null)
        {
            if (this.profile.chordSequence.Count > 0)
            {
                var chord0 = this.profile.chordSequence[0]; // I

                foreach (var tr in tracks.tracks)
                {
                    if (tr == null) continue;

                    _trackSeq[tr] = new List<int> { 0 };
                    _trackPos[tr] = 0;

                    if (tr.GetPersistentLoopNotes().Count > 0)
                        tr.RetuneLoopToChord(chord0);
                }
            }
        }

    }
    public void BeginBoostArp(float secondsRemaining)
    {
        if (profile == null || profile.chordSequence == null || profile.chordSequence.Count == 0 || arpeggiator == null || drums == null)
            return;

        // One preview per loop (you already debounce elsewhere; this is a cheap extra guard)
        int loopIdx = Mathf.FloorToInt((float)((AudioSettings.dspTime - drums.startDspTime) / Mathf.Max(0.001f, drums.GetLoopLengthInSeconds())));
        if (_previewActiveThisLoop && loopIdx == _lastPreviewLoopIdx) return;
        _previewActiveThisLoop = true;
        _lastPreviewLoopIdx = loopIdx;

        // Next unbuilt palette chord = index _globalBuiltCount (I=0 already "built")
        _previewChordIdx = Mathf.Clamp(_globalBuiltCount, 0, profile.chordSequence.Count - 1);

        float beatsToDownbeat = secondsRemaining / Mathf.Max(0.001f, 60f / drums.drumLoopBPM);
        _previewStartedInsideWindow = (beatsToDownbeat <= Mathf.Max(0f, commitWindowBeats));

        arpeggiator.Begin(profile.chordSequence[_previewChordIdx], Mathf.Max(0.05f, secondsRemaining));
    }
    public void AddRemixRings(int count = 1) {
        remixRings = Mathf.Clamp(remixRings + count, 0, MaxRings);
    }
    public void UseRemixRings(int count = 1)
    {
        if (count <= 0 || remixRings <= 0) return;
        int take = Mathf.Min(count, remixRings);
        _ringsArmedThisLoop = Mathf.Clamp(_ringsArmedThisLoop + take, 0, MaxRings);
        // Do NOT consume immediately; we decide at boundary whether to burn or commit
    }

    // Request from PhaseStar (or anything system-driven)
    public void RequestSystemChordAdvance(int steps = 1)
    {
        Mathf.Max(1, steps);
    }
    public void CommitNextChordNow() {
        Debug.Log($"Committing next chord now");
        _armedChordAdvance = true;
        _pendingCharges = Mathf.Clamp(_pendingCharges + 1, 1, MaxCharges);
        Debug.Log($"[HD] CommitNextChordNow armed=true pendingCharges={_pendingCharges}");
    }
    public void CancelBoostArp() => arpeggiator?.Cancel();
    private IEnumerable<InstrumentTrack> SelectTracksByPolicy()
    {
        if (tracks?.tracks == null) yield break;

        var list = tracks.tracks.Where(t => t != null && t.assignedRole != MusicalRole.Groove).ToList();

        switch (retunePolicy)
        {
            case TrackRetunePolicy.BassHarmonyLead:
                var order = new[] { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead };
                foreach (var role in order)
                    foreach (var t in list.Where(x => x.assignedRole == role))
                        yield return t;
                break;

            case TrackRetunePolicy.DensestFirst:
                foreach (var t in list.OrderByDescending(t => t.GetNoteDensity()))
                    yield return t;
                break;

            case TrackRetunePolicy.Manual:
                // For manual selection, you’d push chosen tracks into a queue from UI.
                // Fall back to BassHarmonyLead if queue empty.
                foreach (var t in SelectTracksByPolicy()) yield return t;
                break;
        }
    }
    private void ApplyChordToAllTracks(int chordIndex)
    {
        if (profile == null || tracks == null || tracks.tracks == null) return;
        var seq = profile.chordSequence;
        if (seq == null || seq.Count == 0) return;

        var chord = seq[chordIndex % seq.Count];
        foreach (var tr in tracks.tracks)
        {
            Debug.Log($"Applying chord {chord.rootNote}");
            tr.RetuneLoopToChord(chord); // <-- new helper on InstrumentTrack (below)
        }
    }
    private void ApplyToAllTracksWithOffset(int offset)
    {
        Debug.Log($"[HD] ApplyToAllTracksWithOffset offset={offset}");
        if (profile == null || tracks == null || tracks.tracks == null) return;

        // Rotate a transient profile for Option C
        var rotated = ScriptableObject.CreateInstance<ChordProgressionProfile>();
        rotated.beatsPerChord  = profile.beatsPerChord;
        rotated.chordSequence  = new List<Chord>(profile.chordSequence.Count);
        int n = profile.chordSequence.Count;
        for (int i = 0; i < n; i++)
            rotated.chordSequence.Add(profile.chordSequence[(i + offset) % n]);

        foreach (var tr in tracks.tracks)
        {
            EnsureTrackHasNoteSet(tr);
            tr.ApplyChordProgression(rotated);
        }

        // Clean up the transient asset
        Destroy(rotated);
    }

    private void EnsureTrackHasNoteSet(InstrumentTrack tr)
    {
        if (tr == null || tr.HasNoteSet()) return;

        var factory = GameFlowManager.Instance != null ? GameFlowManager.Instance.noteSetFactory : null;
        var phase   = drums != null ? drums.currentPhase : MusicalPhase.Establish;

        if (factory != null)
        {
            var ns = factory.Generate(tr, phase);
            tr.SetNoteSet(ns);
            Debug.Log($"[HarmonyDirector] Created NoteSet for {tr.name} (phase {phase}).");
        }
        else
        {
            Debug.LogWarning("[HarmonyDirector] NoteSetFactory not available; durations may fallback.");
        }
    }
    private void ResetLoopFlags()
    {
        _previewActiveThisLoop     = false;
        _previewStartedInsideWindow= false;
        _heldThroughBoundary       = false;
        _ringsArmedThisLoop        = 0;
    }
    private void OnLoopBoundary()
    {
        if (profile == null || tracks?.tracks == null) { ResetLoopFlags(); return; }

        int n = profile.chordSequence.Count;
        var palette = profile.chordSequence;

        // ===== PLAYER-DRIVEN BUILD (uses rings + timing) =====
        bool commitOk = _heldThroughBoundary && _ringsArmedThisLoop > 0 &&
                        (_previewStartedInsideWindow || !burnIfOutsideWindow);
        var chosen = new List<InstrumentTrack>();

        if (commitOk)
        {
            // Which tracks are affected this downbeat?
            int toRetune = spreadOverLoops ? 1 : Mathf.Min(_ringsArmedThisLoop, remixRings);
            var candidates = SelectTracksByPolicy()
                             .Where(t => t != null && t.GetPersistentLoopNotes().Count > 0)
                             .ToList();

            if (candidates.Count > 0 && toRetune > 0)
            {
                chosen = candidates.Take(toRetune).ToList();

                bool addedNewChord = false;
                foreach (var tr in chosen)
                {
                    var seq = _trackSeq[tr];
                    if (!seq.Contains(_previewChordIdx))
                    {
                        seq.Add(_previewChordIdx);      // extend the personal loop: I → [I, IV] → [I, IV, V]
                        addedNewChord = true;
                    }
                }
                if (addedNewChord)
                    _globalBuiltCount = Mathf.Min(_globalBuiltCount + 1, n); // unlock next palette chord globally

                // Consume exactly what we used
                remixRings = Mathf.Max(0, remixRings - chosen.Count);
            }
        }
        else
        {
            // Burn (optional): consume rings without changing sequences
            if (_ringsArmedThisLoop > 0 && burnIfOutsideWindow)
                remixRings = Mathf.Max(0, remixRings - _ringsArmedThisLoop);
        }

        // ===== STEP EVERY TRACK ONE CHORD FOR THIS LOOP =====
        foreach (var kv in _trackSeq.ToList())
        {
            var tr   = kv.Key;
            var seq  = kv.Value;
            if (tr == null || seq.Count == 0) continue;

            int curPos = _trackPos.TryGetValue(tr, out var p) ? p : 0;

            int nextPos;
            if (chosen.Contains(tr) && seq.Contains(_previewChordIdx))
            {
                // If this track was chosen, *land on the newly previewed chord now*
                nextPos = seq.IndexOf(_previewChordIdx);
            }
            else
            {
                // Otherwise, just advance by one within its personal sequence
                nextPos = (curPos + 1) % seq.Count;
            }

            _trackPos[tr] = nextPos;
            int paletteIdx = seq[nextPos];
            tr.RetuneLoopToChord(palette[paletteIdx]);
        }

        ResetLoopFlags();
    }
    private void OnPreviewHeldThroughBoundary() { _heldThroughBoundary = true; }



}
