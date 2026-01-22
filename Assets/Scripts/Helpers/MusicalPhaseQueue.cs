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
    public bool allowRandomSelection = true;
}