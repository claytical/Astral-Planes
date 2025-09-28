// PhasePersonalityRegistry.cs
using UnityEngine;

[CreateAssetMenu(menuName="Astral Planes/Phase Personality Registry")]
public class PhasePersonalityRegistry : ScriptableObject
{
    [System.Serializable]
    public struct Entry
    {
        public MusicalPhase phase;
        public PhaseStarBehaviorProfile profile;
    }

    public Entry[] entries;

    public PhaseStarBehaviorProfile Get(MusicalPhase phase)
    {
        if (entries == null) return null;
        for (int i = 0; i < entries.Length; i++)
            if (entries[i].phase == phase) return entries[i].profile;
        return null;
    }
}