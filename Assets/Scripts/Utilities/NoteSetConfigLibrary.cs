using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "NoteSetConfigLibrary", menuName = "Astral Planes/NoteSet Config Library")]
public class NoteSetConfigLibrary : ScriptableObject
{
    public List<RolePhaseNoteSetConfig> configs;
    private Dictionary<(MusicalRole, MusicalPhase), RolePhaseNoteSetConfig> lookup;

    public void Initialize()
    {
        lookup = new();
        foreach (var cfg in configs)
        {
            lookup[(cfg.role, cfg.phase)] = cfg;
        }
    }

    public RolePhaseNoteSetConfig GetConfig(MusicalRole role, MusicalPhase phase)
    {
        if (lookup == null) Initialize();
        lookup.TryGetValue((role, phase), out var config);
        return config;
    }
}

