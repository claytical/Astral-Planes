using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public enum MusicalPhase
{
    Establish,   // early stable loop
    Evolve,      // moderate complexity
    Intensify,   // denser and brighter
    Release,     // breath or breakdown
    Wildcard,    // glitchy, unpredictable
    Pop          // catchy hook
}
public static class MusicalPhaseLibrary
{
    private static Dictionary<MusicalPhase, MusicalPhaseProfile> _profiles;

    public static void Load()
    {
        _profiles = Resources.LoadAll<MusicalPhaseProfile>("MusicalPhases")
            .ToDictionary(p => p.phase, p => p);
    }

    public static MusicalPhaseProfile Get(MusicalPhase phase)
    {
        if (_profiles == null) Load();
        return _profiles.TryGetValue(phase, out var profile) ? profile : null;
    }

    public static AudioClip GetRandomClip(MusicalPhase phase)
    {
        var profile = Get(phase);
        if (profile == null || profile.drumClips == null || profile.drumClips.Length == 0) return null;
        return profile.drumClips[Random.Range(0, profile.drumClips.Length)];
    }
}
