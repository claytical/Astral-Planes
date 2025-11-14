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
