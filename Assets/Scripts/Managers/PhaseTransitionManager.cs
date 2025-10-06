using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class PhaseTransitionManager : MonoBehaviour
{
    public InstrumentTrackController trackController;
    public MusicalPhase previousPhase;
    public MusicalPhase currentPhase;
    private PhaseStarBehaviorProfile _activeBehaviorProfile;

    public event System.Action<MusicalPhase, MusicalPhase> OnPhaseChanged;

    /// Preferred
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

    /// Backward-compatible shim
    public void HandlePhaseTransition(MusicalPhase nextPhase)
    {
        HandlePhaseTransition(nextPhase, "UnknownCaller");
    }

    private void HandlePhaseStarSpawned(MusicalPhase phase, PhaseStarBehaviorProfile profile) {
        // Only update if we're actually changing phases
        if (phase != currentPhase) {
            previousPhase = currentPhase;
            currentPhase  = phase;
        }
        _activeBehaviorProfile = profile;
    }

    void OnEnable() {
        var drums = GetComponent<DrumTrack>();
        if (drums != null) drums.OnPhaseStarSpawned += HandlePhaseStarSpawned;
    }
    void OnDisable() {
        var drums = GetComponent<DrumTrack>();
        if (drums != null) drums.OnPhaseStarSpawned -= HandlePhaseStarSpawned;
    }


}
