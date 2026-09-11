using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Phase/Phase Chapter Library", fileName = "PhaseLibrary")]
public class PhaseLibrary : ScriptableObject
{
    [Serializable]
    public class Phase
    {
        public string phaseName;

        /// <summary>Stable identifier, independent of this phase's position in the list.
        /// Auto-assigned via OnValidate; edit only if you want a readable slug instead
        /// of the generated GUID. Used to re-resolve saved sessions and navigation
        /// targets if phases are reordered/inserted/deleted after being authored.</summary>
        public string phaseId;

        public List<MotifProfile> motifs = new();
        public bool loopMotifs = true;
    }

    public List<Phase> phases = new List<Phase>();

    private void OnValidate()
    {
        if (phases == null) return;

        var seen = new HashSet<string>();
        foreach (var p in phases)
        {
            if (p == null) continue;
            if (string.IsNullOrEmpty(p.phaseId))
                p.phaseId = Guid.NewGuid().ToString("N");
            if (!seen.Add(p.phaseId))
                Debug.LogWarning($"[PhaseLibrary] Duplicate phaseId '{p.phaseId}' (phase '{p.phaseName}'). Assign a unique id.");
        }
    }
}