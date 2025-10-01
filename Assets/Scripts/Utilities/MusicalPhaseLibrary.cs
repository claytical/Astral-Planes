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
    private static Dictionary<MusicalPhase, MusicalPhaseProfile> profileLookup;

    public static void InitializeProfiles(List<MusicalPhaseProfile> profiles)
    {
        profileLookup = profiles.ToDictionary(p => p.phase, p => p);
    }
    public static PatternStrategy GetPatternStrategyForRole(MusicalPhase phase, MusicalRole role)
    {
        var profile = Get(phase);
        return profile != null ? profile.GetPatternStrategyForRole(role) : PatternStrategy.Arpeggiated;
    }
    public static AudioClip GetRandomClip(MusicalPhase phase)
    {
        var profile = Get(phase);
        if (profile == null || profile.drumClips == null || profile.drumClips.Length == 0) return null;
        return profile.drumClips[Random.Range(0, profile.drumClips.Length)];
    }

    private static void Load()
    {
        _profiles = Resources.LoadAll<MusicalPhaseProfile>("MusicalPhases")
            .ToDictionary(p => p.phase, p => p);
    }
    private static MusicalPhaseProfile Get(MusicalPhase phase)
    {
        if (_profiles == null) Load();
        return _profiles.TryGetValue(phase, out var profile) ? profile : null;
    }

}
