using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName="Astral/Phase Chapter Library")]
public class PhaseLibrary : ScriptableObject
{
    [Serializable]
    public class Phase
    {
        public string phaseName;
        public List<MotifProfile> motifs = new();
        public bool loopMotifs = true;
    }

    public List<Phase> phases = new List<Phase>();
}