using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class PhaseTransitionManager : MonoBehaviour
{
    public InstrumentTrackController trackController;
    public MusicalPhase previousPhase;
    public MusicalPhase currentPhase;
    private PhaseStarBehaviorProfile _activeBehaviorProfile;

    public void HandlePhaseTransition(MusicalPhase nextPhase)
    {
        previousPhase = currentPhase;
        currentPhase = nextPhase;

        switch (currentPhase)
        {
            case MusicalPhase.Evolve:
            case MusicalPhase.Wildcard:
                break;

            case MusicalPhase.Intensify:
            case MusicalPhase.Pop:
                break;

            case MusicalPhase.Release:
            case MusicalPhase.Establish:
                break;
        }
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
