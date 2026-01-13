using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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
    [Tooltip("If true, on Start() we snap immediately to the current motif's profile, if any.")]
    [SerializeField] private int remixRings = 0;
    [SerializeField, Range(0f, 4f)] private float commitWindowBeats = 1f; // last N beats before downbeat
    [SerializeField] private bool burnIfOutsideWindow;            // consume rings even if mistimed (no chord change)
    [SerializeField] private bool spreadOverLoops = true;                 // retune 1 track per loop if true
    [SerializeField] private TrackRetunePolicy retunePolicy = TrackRetunePolicy.BassHarmonyLead;
    [SerializeField] private bool seedFromProfileOnPhaseStart;    // leave false for Option C

    [Header("Motif Integration")]
    [SerializeField] private bool useMotifChordProfiles = true;

    [Tooltip("If true, on Start() we snap immediately to the current motif's profile, if any.")]
    [SerializeField] private bool applyInitialMotifProfileImmediately = true;

    private enum ConflictPolicy { DeferPlayerToNextLoop, LetPlayerOvertake }
    [SerializeField] private ConflictPolicy conflictPolicy = ConflictPolicy.DeferPlayerToNextLoop;
    private bool _forceCommitNextBoundary;

    void OnEnable()  { if (GameFlowManager.Instance.activeDrumTrack != null) GameFlowManager.Instance.activeDrumTrack.OnLoopBoundary += OnLoopBoundary; }
    void OnDisable() { if (GameFlowManager.Instance.activeDrumTrack != null) GameFlowManager.Instance.activeDrumTrack.OnLoopBoundary -= OnLoopBoundary; }
    public void Initialize(DrumTrack d, InstrumentTrackController t) {
        // (your existing Initialize body)
        // After you set tracks, initialize per-track sequences:
        if (GameFlowManager.Instance.controller?.tracks != null)
        {
            _trackSeq.Clear();
            _trackPos.Clear();
            foreach (var tr in GameFlowManager.Instance.controller.tracks)
            {
                if (tr == null) continue;
                _trackSeq[tr] = new List<int> { 0 }; // start at I for every track
                _trackPos[tr] = 0;
            }
        }
    }
    public void AdvanceChordAndRetuneAll(int steps = 1)
    {
        if (steps == 0)
        {
            Debug.Log("[CHORD][HD] AdvanceChordAndRetuneAll called with steps=0; ignoring");
            return;
        }
        if (profile == null)
        {
            Debug.LogWarning("[CHORD][HD] No ChordProgressionProfile set; cannot advance");
            return;
        }
        if (profile.chordSequence == null || profile.chordSequence.Count == 0)
        {
            Debug.LogWarning("[CHORD][HD] Profile has empty chordSequence; cannot advance");
            return;
        }

        int old = cursor;
        cursor = (cursor + steps) % profile.chordSequence.Count;
        Debug.Log($"[CHORD][HD] Advance {steps} → cursor {old} → {cursor} (seq len {profile.chordSequence.Count})");

        ApplyChordToAllTracks(cursor);
    }
    void Start()
{
    var ptm = GameFlowManager.Instance?.phaseTransitionManager;
    if (ptm != null)
    {
        ptm.OnPhaseChanged += HandlePhaseChangedBridgeAware;

        // Optionally snap to the current motif's chord profile at startup.
        if (useMotifChordProfiles &&
            applyInitialMotifProfileImmediately &&
            ptm.currentMotif != null &&
            ptm.currentMotif.chordProgression != null)
        {
            Debug.Log($"[CHORD][HD] Initializing from motif '{ptm.currentMotif.name}' profile '{ptm.currentMotif.chordProgression.name}'.");
            SetActiveProfile(ptm.currentMotif.chordProgression, applyImmediately: true);
        }
    }
}

void OnDestroy()
{
    var ptm = GameFlowManager.Instance?.phaseTransitionManager;
    if (ptm != null)
        ptm.OnPhaseChanged -= HandlePhaseChangedBridgeAware;
}

private void HandlePhaseChangedBridgeAware(MusicalPhase from, MusicalPhase to)
{
    // 1) If enabled, stage the motif's chord progression as the next profile.
    MotifProfile motif = null;
    var ptm = GameFlowManager.Instance?.phaseTransitionManager;

    if (useMotifChordProfiles && ptm != null)
    {
        motif = ptm.currentMotif;
        if (motif != null && motif.chordProgression != null)
        {
            // Stage for swap at the next commit (do not apply immediately here).
            SetActiveProfile(motif.chordProgression, applyImmediately: false);
            Debug.Log($"[CHORD][HD] Staged motif chord profile '{motif.chordProgression.name}' for phase transition {from} → {to}.");
        }
        else
        {
            if (motif == null)
                Debug.Log($"[CHORD][HD] Phase {from} → {to}: no current motif; keeping existing chord profile.");
            else
                Debug.Log($"[CHORD][HD] Phase {from} → {to}: motif '{motif.name}' has no chordProgression; keeping existing chord profile.");
        }
    }

    // 2) Preserve existing bridge timing behavior.
    var sig = BridgeLibrary.For(from, to);
    switch (sig.commitTiming)
    {
        case HarmonyCommit.AtBridgeStart:
            // Retune everyone to chord 0 on next downbeat
            CommitNextChordNow();
            break;

        case HarmonyCommit.MidBridge:
            // Stage a one-loop delay: commit on the *following* boundary
            StartCoroutine(CommitOnNextBoundary());
            break;

        case HarmonyCommit.AtBridgeEnd:
        default:
            // Do nothing here; your bridge coroutine should call CommitNextChordNow() at the end.
            break;
    }
}

private System.Collections.IEnumerator CommitOnNextBoundary()
{
    // Debounced: wait one full loop, then commit
    var d = GameFlowManager.Instance?.activeDrumTrack;
    if (d == null) yield break;

    double target = AudioSettings.dspTime + d.GetLoopLengthInSeconds();
    while (AudioSettings.dspTime < target)
        yield return null;

    CommitNextChordNow();
}


    public void SetActiveProfile(ChordProgressionProfile profile, bool applyImmediately)
    {
        if (profile == null) return;

        if (applyImmediately)
        {
            // Swap NOW and snap everyone to chord 0
            this.profile = profile;
            _globalBuiltCount = (this.profile.chordSequence != null && this.profile.chordSequence.Count > 0) ? 1 : 0;
            _previewChordIdx  = -1;

            if (GameFlowManager.Instance.controller?.tracks != null && this.profile.chordSequence.Count > 0)
            {
                var chord0 = this.profile.chordSequence[0];
                foreach (var tr in GameFlowManager.Instance.controller.tracks)
                {
                    if (tr == null) continue;
                    _trackSeq[tr] = new List<int> { 0 };
                    _trackPos[tr] = 0;
                    if (tr.GetPersistentLoopNotes().Count > 0)
                        tr.RetuneLoopToChord(chord0);
                }
            }
        }
        else
        {
            // Stage for the next downbeat commit
            _pendingProfile        = profile;
            _hasPendingProfileSwap = true;
        }
    }
    public bool TryGetChordAt(int index, out Chord chord)
    {
        chord = default;
        if (profile == null || profile.chordSequence == null || profile.chordSequence.Count == 0)
            return false;

        int i = ((index % profile.chordSequence.Count) + profile.chordSequence.Count) % profile.chordSequence.Count;
        chord = profile.chordSequence[i];
        return true;
    }

    public int ProgressionLength
    {
        get
        {
            if (profile == null || profile.chordSequence == null) return 0;
            return profile.chordSequence.Count;
        }
    }
    
    public void CommitNextChordNow() {
        Debug.Log("[HD] CommitNextChordNow -> force commit at next downbeat");
        _forceCommitNextBoundary = true;
    }

    private void ApplyChordToAllTracks(int chordIndex)
    {
        if (profile == null || GameFlowManager.Instance.controller == null || GameFlowManager.Instance.controller.tracks == null) return;
        var seq = profile.chordSequence;
        if (seq == null || seq.Count == 0) return;

        var chord = seq[chordIndex % seq.Count];
        Debug.Log($"[CHORD][HD] RetuneAll → chord[{chordIndex}]={chord.rootNote}");
        foreach (var tr in GameFlowManager.Instance.controller.tracks)
        {
            Debug.Log($"Applying chord {chord.rootNote}");
            Debug.Log($"[CHORD][HD] Retuned track={tr.name} role={tr.assignedRole}");
            tr.RetuneLoopToChord(chord); // <-- new helper on InstrumentTrack (below)
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
        if (profile == null || GameFlowManager.Instance.controller.tracks == null)
        {
            ResetLoopFlags();
            return;
        }

        // === Phase/Chord COMMIT path (system-driven) ===
        if (_forceCommitNextBoundary || _hasPendingProfileSwap)
        {
            // Swap to pending profile if any
            if (_hasPendingProfileSwap && _pendingProfile != null)
            {
                profile = _pendingProfile;
                _pendingProfile = null;
                _hasPendingProfileSwap = false;
            }

            // Snap every track’s personal sequence back to I (index 0), retune to chord 0
            if (profile.chordSequence != null && profile.chordSequence.Count > 0)
            {
                var chord0 = profile.chordSequence[0];

                foreach (var tr in GameFlowManager.Instance.controller.tracks)
                {
                    if (tr == null) continue;
                    _trackSeq[tr] = new List<int> { 0 };
                    _trackPos[tr] = 0;

                    // If a loop exists, retune it so the whole band lands together
                    if (tr.GetPersistentLoopNotes().Count > 0)
                        tr.RetuneLoopToChord(chord0);
                }

                _globalBuiltCount = 1; // I is now the built baseline again
                _previewChordIdx = -1;
            }

            _forceCommitNextBoundary = false;
            ResetLoopFlags();
            return; // Important: skip the player-build step this boundary
        }
    }





}
