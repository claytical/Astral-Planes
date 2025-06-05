using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Musical Phase Queue")]
public class MusicalPhaseQueue : ScriptableObject
{
    public List<MusicalPhaseGroup> phaseGroups;
}

[System.Serializable]
public class MusicalPhaseGroup
{
    public MusicalPhase phase;
    public float hollowRadius = 2.5f;
    public List<MineNodeSpawnerSet> spawnerOptions;
    public bool allowRandomSelection = true;
}