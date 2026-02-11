using UnityEngine;

public class PhaseTransitionManager : MonoBehaviour
{
    public MazeArchetype previousPhase;
    public MazeArchetype currentPhase;
    private PhaseStarBehaviorProfile _activeBehaviorProfile;
    public NoteSetFactory noteSetFactory;
    private bool _phaseAdvanceArmed;
    [Header("Motif / BeatMood (motif-based music)")]
    [SerializeField] private MotifQueue motifQueue;
    [SerializeField] private MotifLibrary motifLibrary;
    private bool _initialized = false;
    private bool _booted = false;
    private int _motifQueueIndex = -1;  
    private bool _hasCommittedMotif = false;
    [SerializeField] private bool loopMotifQueue = true; // optional
    [SerializeField] private bool _hasCommittedFirstMotif;

    // The motif associated with the currentPhase (if any)
    public MotifProfile currentMotif { get; private set; }

    public event System.Action<MotifProfile, MotifProfile> OnMotifChanged;
// Add this near your existing events / public API
public event System.Action<MazeArchetype, MazeArchetype> OnPhaseChanged;

/// <summary>
/// Canonical entrypoint for updating phase and/or motif.
///
/// Phase: controls maze/gameplay architecture. It may remain constant (e.g., Establish)
///        while multiple motifs play.
/// Motif: musical "level". Should advance whenever a PhaseStar completes (out of shards),
///        and may advance multiple times within the same phase.
/// </summary>

/// <summary>
/// Backward-compatible wrapper: treats a true phase change as a motif advance.
/// (So: new phase => new motif; same phase => no motif advance)
/// </summary>
    public void HandlePhaseTransition(MazeArchetype nextPhase, string who)
    {
        var oldPrev = previousPhase;
        var oldCur  = currentPhase;
        bool advanceMotif = (oldCur != nextPhase);
        CommitPhaseAndMaybeAdvanceMotif(nextPhase, advanceMotif, who: who);
    }

        public MotifProfile CommitPhaseAndMaybeAdvanceMotif(MazeArchetype nextPhase, bool advanceMotif, string who) {
            var oldCur = currentPhase;
            // Phase commit (always keep these coherent; phase may remain equal in your current Establish-only test)
            previousPhase = currentPhase;
            currentPhase  = nextPhase;
        
            if (oldCur != currentPhase) { 
                Debug.Log($"[PHASE] {oldCur}→{currentPhase} by {who}"); 
                OnPhaseChanged?.Invoke(oldCur, currentPhase);
            }
            else{ 
                Debug.Log($"[PHASE] Same-phase commit ({currentPhase}) by {who}");
            }
            // Ensure we have *some* motif selected before any drum boot tries to read it.
            if (!_hasCommittedFirstMotif || currentMotif == null) {
                currentMotif = GetCurrentMotifFromQueue(); 
                _hasCommittedFirstMotif = (currentMotif != null); 
                Debug.Log($"[MOTIF] Boot select motif={(currentMotif ? currentMotif.motifId : "null")} idx={_motifQueueIndex} by {who}");
            } 
            // Optional motif advance (this is what you want when PhaseStar runs out of shards)
            if (advanceMotif) {
                currentMotif = GetNextMotifFromQueue(); 
                _hasCommittedFirstMotif = (currentMotif != null); 
                Debug.Log($"[MOTIF] Advance by {who}: motif={(currentMotif ? currentMotif.motifId : "null")} idx={_motifQueueIndex}");
            }
        
                    // Drive drums + tracks from the motif (always) — this is motif-level behavior
            var drums = GameFlowManager.Instance?.activeDrumTrack; 
            if (drums != null && currentMotif != null) 
                drums.SetMotifBeatSequence(currentMotif, /*armAtNextBoundary*/ true, who, /*restartTransport*/ false);
            
            ConfigureTracksForCurrentPhaseAndMotif(); 
            return currentMotif;
        }
// <summary>
/// Ensure currentMotif is non-null without advancing the queue.
/// Useful for DrumTrack boot when ManualStart can fire before any explicit commit.
/// </summary>
public void EnsureMotifInitialized(string who)
{
    if (currentMotif != null)
        return;

    var m = GetCurrentMotifFromQueue();
    currentMotif = m; 
    Debug.Log($"[MOTIF] Init-only: currentMotif={(currentMotif ? currentMotif.motifId : "null")} (by {who})");
    if (currentMotif == null) 
        return;
    var gfm = GameFlowManager.Instance; 
    var drums = gfm != null ? gfm.activeDrumTrack : null;
    // Drive drums from motif (timing + beat selection). No transport restart on init-only.
    if (drums != null) { 
        // Use the canonical signature if available:
        drums.SetMotifBeatSequence(currentMotif, armAtNextBoundary: false, who: $"PTM/EnsureMotifInitialized({who})", restartTransport: false); 
        // (Alternatively, if you added the overload in DrumTrack.cs, this also works:)
        // drums.SetMotifBeatSequence(currentMotif, false);
    }
    
    // Apply motif to instrument tracks via the existing PTM pipeline.
    // This is the correct replacement for the old controller.SetMotifNoteSets(...)
    ConfigureTracksForCurrentPhaseAndMotif();    
}



    public void BootIfNeeded(MazeArchetype bootPhase, string who)
    {
        if (_booted && currentMotif != null) return;

        previousPhase = bootPhase;
        currentPhase  = bootPhase;
// Initialize motif pointer to the first motif in MotifQueue order (e.g., tres).
        if (_motifQueueIndex < 0) _motifQueueIndex = 0; 
        currentMotif = GetCurrentMotifFromQueue();
        _booted = (currentMotif != null);

        Debug.Log($"[MOTIF][BOOT] phase={currentPhase} motif={(currentMotif ? currentMotif.motifId : "null")} by={who}");

        var drums = GameFlowManager.Instance != null ? GameFlowManager.Instance.activeDrumTrack : null; 
        if (drums != null && currentMotif != null) 
            drums.SetMotifBeatSequence(currentMotif, armAtNextBoundary: false, who: $"PTM/BootIfNeeded:{who}"); 
        ConfigureTracksForCurrentPhaseAndMotif();
    }

    public MotifProfile AdvanceMotif(string who, bool restartDrumsTransport = false) { 
        if (!_booted || currentMotif == null)
            BootIfNeeded(currentPhase, who);
        currentMotif = GetNextMotifFromQueue(); 
        Debug.Log($"[MOTIF] AdvanceMotif by {who}: motif={(currentMotif ? currentMotif.motifId : "null")} idx={_motifQueueIndex}");
        if (currentMotif == null) return null;
        
        var drums = GameFlowManager.Instance != null ? GameFlowManager.Instance.activeDrumTrack : null;
        if (drums != null) 
            drums.SetMotifBeatSequence(currentMotif, armAtNextBoundary: true, who: $"PTM/AdvanceMotif:{who}", restartTransport: restartDrumsTransport);
        ConfigureTracksForCurrentPhaseAndMotif();
                return currentMotif;
    }
    // ------------------------------------------------------------
    // MOTIF TRANSITION (level changes)
    // ------------------------------------------------------------


    public MotifProfile PeekCurrentMotif()
    {
        if (!_booted) BootIfNeeded(currentPhase, "PeekCurrentMotif");
        return currentMotif;
    }

    private MotifProfile GetNextMotifFromQueue()
    {
        if (motifQueue == null || motifQueue.motifs == null || motifQueue.motifs.Count == 0)
        {
            Debug.LogError("[MOTIF] MotifQueue missing/empty.");
            return null;
        }

        if (_motifQueueIndex < 0) _motifQueueIndex = 0;
        else _motifQueueIndex++;
        if (_motifQueueIndex >= motifQueue.motifs.Count)
            _motifQueueIndex = 0;
        return motifQueue.motifs[_motifQueueIndex];
    }


  
    private MotifProfile GetCurrentMotifFromQueue()
    {
        if (motifQueue == null || motifQueue.motifs == null || motifQueue.motifs.Count == 0)
            return null;

        _motifQueueIndex = Mathf.Clamp(_motifQueueIndex, 0, motifQueue.motifs.Count - 1);
        return motifQueue.motifs[_motifQueueIndex];
    }

    private void AdvanceMotifFromQueue(string who)
    {
        if (motifQueue == null || motifQueue.motifs == null || motifQueue.motifs.Count == 0)
        {
            Debug.LogWarning($"[MOTIF] AdvanceMotif ignored (no motifQueue) by {who}");
            return;
        }

        int prev = _motifQueueIndex;
        _motifQueueIndex = (_motifQueueIndex + 1) % motifQueue.motifs.Count;
        currentMotif = motifQueue.motifs[_motifQueueIndex];

        Debug.Log($"[MOTIF] AdvanceMotif by {who}: {prev}→{_motifQueueIndex}/{motifQueue.motifs.Count} motif={(currentMotif ? currentMotif.motifId : "null")}");
    }

    public void ConfigureTracksForCurrentPhaseAndMotif()
{
    var gfm = GameFlowManager.Instance;
    if (gfm == null)
    {
        Debug.LogWarning("[MOTIF] GameFlowManager.Instance is null; cannot configure tracks.");
        return;
    }

    if (noteSetFactory == null)
    {
        Debug.LogWarning("[MOTIF] NoteSetFactory reference is null on PhaseTransitionManager; cannot configure tracks.");
        return;
    }

    var controller = gfm.controller;
    if (controller == null || controller.tracks == null || controller.tracks.Length == 0)
    {
        Debug.LogWarning("[MOTIF] No InstrumentTrackController or tracks found; skipping track configuration.");
        return;
    }

    // Optional: deterministic but per-phase/per-motif entropy base.
    // You can change this if you want “remix” variations.
    int baseEntropy = 0;

    foreach (var track in controller.tracks)
    {
        if (track == null) continue;

        NoteSet noteSet = null;

        try
        {
            // Prefer motif-based generation when we have a motif defined.
            if (currentMotif != null)
            {
                string keyInfo = $"motif={currentMotif.motifId} role={track.assignedRole}";
                Debug.Log($"[MOTIF] Generating NoteSet for track '{track.name}' via motif ({keyInfo}).");
                noteSet = noteSetFactory.Generate(track, currentMotif, baseEntropy);
            }

        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MOTIF] Exception while generating NoteSet for track '{track.name}': {ex.Message}");
            noteSet = null;
        }

        if (noteSet == null)
        {
            Debug.LogWarning($"[MOTIF] NoteSetFactory returned null for track '{track.name}'. This track will keep its previous NoteSet (if any).");
            continue;
        }

        // Attach NoteSet to track. This ensures GetActiveNoteSet() / GetCurrentNoteSet() sees it.
        track.SetNoteSet(noteSet);
        Debug.Log($"[MOTIF] Assigned new NoteSet to track '{track.name}' (role={track.assignedRole}).");
    }
}

    private void HandlePhaseStarSpawned(MazeArchetype phase, PhaseStarBehaviorProfile profile) {

        // Only update if we're actually changing phases
        if (phase != currentPhase) {
            previousPhase = currentPhase;
            currentPhase  = phase;
        }
        _activeBehaviorProfile = profile;
    }

    void OnEnable()
    {
        InitializeIfNeeded("PTM/OnEnable");
        DrumTrack drums = GameFlowManager.Instance.activeDrumTrack;
        if (drums != null)
        { 
            drums.OnPhaseStarSpawned += HandlePhaseStarSpawned;
        }        
    }
    public void InitializeIfNeeded(string who)
    {
        if (_initialized) return;

        // Phase defaults
        if (currentPhase == 0) currentPhase = MazeArchetype.Establish;
        previousPhase = currentPhase;

        // Motif defaults
        if (motifQueue != null && motifQueue.motifs != null && motifQueue.motifs.Count > 0)
        {
            _motifQueueIndex = Mathf.Clamp(_motifQueueIndex, 0, motifQueue.motifs.Count - 1);
            currentMotif = motifQueue.motifs[_motifQueueIndex];
            Debug.Log($"[MOTIF] PTM init by {who}: phase={currentPhase} motifIndex={_motifQueueIndex}/{motifQueue.motifs.Count} motif={(currentMotif ? currentMotif.motifId : "null")}");
        }
        else
        {
            currentMotif = null;
            Debug.LogWarning($"[MOTIF] PTM init by {who}: motifQueue empty or missing.");
        }

        _initialized = true;
    }

    void OnDisable()
    {
        DrumTrack drums = GameFlowManager.Instance.activeDrumTrack;
        if (drums != null)
        { 
            drums.OnPhaseStarSpawned -= HandlePhaseStarSpawned;
        }
    }



}
