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
    public static List<MusicalPhaseProfile> allPhaseProfiles = new();
    private static Dictionary<MusicalPhase, MusicalPhaseProfile> profileLookup;
    
    public static void Load()
    {
        _profiles = Resources.LoadAll<MusicalPhaseProfile>("MusicalPhases")
            .ToDictionary(p => p.phase, p => p);
    }

    public static void InitializeProfiles(List<MusicalPhaseProfile> profiles)
    {
        allPhaseProfiles = profiles;
        profileLookup = profiles.ToDictionary(p => p.phase, p => p);
    }
    public static int GetGhostLoopCount(MusicalPhase phase)
    {
        switch (phase)
        {
            case MusicalPhase.Establish: return 4;
            case MusicalPhase.Evolve: return 3;
            case MusicalPhase.Intensify: return 2;
            case MusicalPhase.Release: return 6;
            case MusicalPhase.Wildcard: return Random.Range(2, 6);
            case MusicalPhase.Pop: return 3;
            default: return 4;
        }
    }

    public static MusicalPhaseProfile GetProfile(MusicalPhase phase)
    {
        if (profileLookup == null || !profileLookup.ContainsKey(phase))
        {
            Debug.LogWarning($"No MusicalPhaseProfile found for phase {phase}");
            return null;
        }

        return profileLookup[phase];
    }


    public static PatternStrategy GetPatternStrategyForRole(MusicalPhase phase, MusicalRole role)
    {
        var profile = GetProfile(phase);
        return profile != null ? profile.GetPatternStrategyForRole(role) : PatternStrategy.Arpeggiated;
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
