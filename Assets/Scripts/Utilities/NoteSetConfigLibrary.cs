using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "NoteSetConfigLibrary", menuName = "Astral Planes/NoteSet Config Library")]
public class NoteSetConfigLibrary : ScriptableObject
{
    public List<RolePhaseNoteSetConfig> configs;
    private Dictionary<(MusicalRole, MusicalPhase), RolePhaseNoteSetConfig> _lookup;

    private void Initialize()
    {
        _lookup = new();
        foreach (var cfg in configs)
        {
            _lookup[(cfg.role, cfg.phase)] = cfg;
        }
    }
    public RolePhaseNoteSetConfig GetConfig(MusicalRole role, MusicalPhase phase)
    {
        if (_lookup == null) Initialize();
        _lookup.TryGetValue((role, phase), out var config);
        return config;
    }

}

