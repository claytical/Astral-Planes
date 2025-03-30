using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Spawner Phase Queue")]
public class SpawnerPhaseQueue : ScriptableObject
{
    public List<SpawnerPhaseGroup> phaseGroups;
}

[System.Serializable]
public class SpawnerPhaseGroup
{
    public SpawnerPhase phase;
    public List<MineNodeSpawnerSet> spawnerOptions;
    public bool allowRandomSelection = true;
}