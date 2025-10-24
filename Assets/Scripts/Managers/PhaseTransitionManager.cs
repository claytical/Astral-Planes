using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using UnityEngine;

public class PhaseTransitionManager : MonoBehaviour
{
    public MusicalPhase previousPhase;
    public MusicalPhase currentPhase;
    private PhaseStarBehaviorProfile _activeBehaviorProfile;
    [SerializeField] private NoteSetFactory noteSetFactory;
    private bool _phaseAdvanceArmed;
    public event System.Action<MusicalPhase, MusicalPhase> OnPhaseChanged;
// === Bin-aware auto-bridge orchestration ===
private bool _awaitingFullPlaythrough;
private int _loopsRemainingUntilBridge;

private bool AllTracksHaveAtLeastOneBinFilled()
{
    var tracks = GameObject.FindObjectsOfType<InstrumentTrack>();
    if (tracks == null || tracks.Length == 0) return false;
    foreach (var t in tracks)
        if (t == null || t.GetFilledBinCount() < 1) return false;
    return true;
}

private void HandleLoopBoundaryForBridge()
{
    // If we just armed, count down to bridge start.
    if (_awaitingFullPlaythrough)
    {
        _loopsRemainingUntilBridge = Mathf.Max(0, _loopsRemainingUntilBridge - 1);
        if (_loopsRemainingUntilBridge == 0)
        {
            _awaitingFullPlaythrough = false;
            StartCoroutine(PlayPhaseBridgeThenAdvance());
        }
        return;
    }
    // If not armed, check condition: all tracks have ≥1 filled bin.
    if (!_phaseAdvanceArmed && AllTracksHaveAtLeastOneBinFilled())
    {
        _phaseAdvanceArmed = true;
        _awaitingFullPlaythrough = true;
        _loopsRemainingUntilBridge = 1; // one full loop as requested
    }
}

private System.Collections.IEnumerator PlayPhaseBridgeThenAdvance()
{
    var drums = GetComponent<DrumTrack>();
    if (drums == null) yield break;

    // Step 1: prune to one bin and remix all 4 tracks, then play two loops.
    var tracks = GameObject.FindObjectsOfType<InstrumentTrack>().Where(t=>t!=null).ToList();
    foreach (var t in tracks) t.PruneToSingleCoreBin();
    foreach (var t in tracks) t.TriggerRemixBurst();
    yield return WaitLoops(drums, 2);

    // Step 2: remove 2 tracks (least-dense first; random as tie-break), then one loop.
    var prunable = tracks.OrderBy(t => t.GetFilledBinCount())
                         .ThenBy(_=>UnityEngine.Random.value)
                         .Take(2).ToList();
    foreach (var t in prunable) t.ClearLoopedNotes(TrackClearType.Remix, null);
    yield return WaitLoops(drums, 1);

    // Step 3: advance phase via the existing progression logic.
    var mpm = GameObject.FindObjectOfType<MineNodeProgressionManager>();
    var next = (mpm != null) ? mpm.ComputeNextPhase() : currentPhase;

    _phaseAdvanceArmed = false;
    HandlePhaseTransition(next, "AutoBridge");
}

private System.Collections.IEnumerator WaitLoops(DrumTrack drums, int count)
{
    int target = Mathf.Max(1, count);
    int start = drums.completedLoops;
    while (drums.completedLoops < start + target) yield return null;
}

    public void HandlePhaseTransition(MusicalPhase nextPhase, string who)
    {
        if (nextPhase == currentPhase)
        {
            Debug.LogWarning($"[PTM] No-op transition {currentPhase}→{nextPhase} requested by {who}. Ignored.");
            return;
        }

        var oldPrev = previousPhase;
        var oldCur  = currentPhase;

        previousPhase = currentPhase;
        currentPhase  = nextPhase;

        Debug.Log($"[PTM] {who}: {oldCur}→{nextPhase} (prev {oldPrev}→{previousPhase})");
        OnPhaseChanged?.Invoke(previousPhase, currentPhase);
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
        if (drums != null) { 
            drums.OnPhaseStarSpawned += HandlePhaseStarSpawned; 
            drums.OnLoopBoundary     += HandleLoopBoundaryForBridge;
        }        
    }
    void OnDisable() {
        DrumTrack drums = GameFlowManager.Instance.activeDrumTrack;

        if (drums != null) { 
            drums.OnPhaseStarSpawned -= HandlePhaseStarSpawned; 
            drums.OnLoopBoundary     -= HandleLoopBoundaryForBridge;
        }
    }


}
