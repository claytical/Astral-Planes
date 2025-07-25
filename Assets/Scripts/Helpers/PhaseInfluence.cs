using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Drums/Phase Influence")]
public class PhaseInfluence : DrumVolumeInfluence
{
    public AnimationCurve phaseVolumeCurve = AnimationCurve.Linear(0, 1, 1, 1); // phase progression → volume

    public override float EvaluateVolume(DrumTrack drumTrack)
    {
        switch (drumTrack.currentPhase)
        {
            case MusicalPhase.Establish: return 0.3f;
            case MusicalPhase.Evolve: return 0.5f;
            case MusicalPhase.Intensify: return 1f;
            case MusicalPhase.Release: return 0.25f;
            case MusicalPhase.Wildcard: return 0.6f;
            case MusicalPhase.Pop: return 0.8f;
            default: return 0.5f;
        }
    }
}
