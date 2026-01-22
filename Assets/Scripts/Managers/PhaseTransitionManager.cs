using UnityEngine;

public class PhaseTransitionManager : MonoBehaviour
{
    public MusicalPhase previousPhase;
    public MusicalPhase currentPhase;
    private PhaseStarBehaviorProfile _activeBehaviorProfile;
    public NoteSetFactory noteSetFactory;
    private bool _phaseAdvanceArmed;
    [Header("Motif / BeatMood (motif-based music)")]
    [SerializeField] private MotifQueue motifQueue;
    [SerializeField] private MotifLibrary motifLibrary;

    // Index into motifQueue, if you want a fixed album-like order
    private int _motifIndex = 0;

    // The motif associated with the currentPhase (if any)
    public MotifProfile currentMotif { get; private set; }

    public event System.Action<MusicalPhase, MusicalPhase> OnPhaseChanged;

    public void HandlePhaseTransition(MusicalPhase nextPhase, string who)
    {
        var oldPrev = previousPhase;
        var oldCur  = currentPhase;

        // Even if nextPhase == currentPhase, we still want to advance the motif.
        if (nextPhase == currentPhase)
        {
            Debug.Log($"[MOTIF] Same-phase transition {currentPhase}→{nextPhase} requested by {who}. Advancing motif anyway.");
        }

        previousPhase = currentPhase;
        currentPhase  = nextPhase;

        Debug.Log($"[MOTIF] HandlePhaseTransition called: from={oldCur} to={nextPhase}, reason={who}");
        OnPhaseChanged?.Invoke(oldCur, currentPhase);

        // 2) choose a motif for this phase
        currentMotif = SelectMotifForPhase(currentPhase);

        // 3) drive drums from motif’s BeatMood
        var drums = GameFlowManager.Instance?.activeDrumTrack;
        drums.SetMotifBeatSequence(currentMotif);
        ConfigureTracksForCurrentPhaseAndMotif();
    }
private void ConfigureTracksForCurrentPhaseAndMotif()
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
            /*
            else
            {
                // Fallback: phase-based generation
                Debug.Log($"[MOTIF] No current motif; generating NoteSet for track '{track.name}' from phase {currentPhase}.");

                noteSet = noteSetFactory.Generate(track, currentPhase, baseEntropy);
            }
            */
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

    private MotifProfile SelectMotifForPhase(MusicalPhase nextPhase)
    {
        MotifProfile motif = null;

        if (motifQueue != null && motifQueue.motifs != null && motifQueue.motifs.Count > 0)
        {
            int count = motifQueue.motifs.Count;

            // Use a session-scoped RNG so picks are random but reproducible per play session
            var rng = SessionGenome.For("motifQueue");
            int idx = rng.Next(count);

            motif = motifQueue.motifs[idx];
            Debug.Log($"[MOTIF] Random queue pick index={idx}/{count}, phase={nextPhase}, motif={motif}");
        }
        else
        {
            Debug.LogWarning("[MOTIF] MotifQueue empty or null; falling back to library.");
        }

        if (motif == null && motifLibrary != null)
        {
            motif = motifLibrary.PickRandomWeighted(SessionGenome.For("motifphase"));
            Debug.Log($"[MOTIF] Fallback library pick motif={motif}");
        }

        return motif;
    }

    private void HandlePhaseStarSpawned(MusicalPhase phase, PhaseStarBehaviorProfile profile) {

        // Only update if we're actually changing phases
        if (phase != currentPhase) {
            previousPhase = currentPhase;
            currentPhase  = phase;
        }
        _activeBehaviorProfile = profile;
    }

    void OnEnable()
    {
        DrumTrack drums = GameFlowManager.Instance.activeDrumTrack;
        if (drums != null)
        { 
            drums.OnPhaseStarSpawned += HandlePhaseStarSpawned;
        }        
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
