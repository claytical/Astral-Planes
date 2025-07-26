using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class PhaseTransitionManager : MonoBehaviour
{
    public InstrumentTrackController trackController;
    public MusicalPhase previousPhase;
    public MusicalPhase currentPhase;

    public void HandlePhaseTransition(MusicalPhase nextPhase)
    {
        previousPhase = currentPhase;
        currentPhase = nextPhase;

        switch (currentPhase)
        {
            case MusicalPhase.Evolve:
            case MusicalPhase.Wildcard:
//                ApplyDriftToTracks(1);
                break;

            case MusicalPhase.Intensify:
            case MusicalPhase.Pop:
//                ApplyRemixToAllTracks();
                break;

            case MusicalPhase.Release:
            case MusicalPhase.Establish:
                // Soft transition: no action or subtle visuals
                break;
        }
    }



}
