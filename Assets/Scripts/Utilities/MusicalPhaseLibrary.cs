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
 
// Call this once during boot (e.g., GFM.Start or PTM.Awake)
    public static void InitializeProfiles(List<MusicalPhaseProfile> profiles)
    {
        if (profiles == null)
        {
            _profiles = new Dictionary<MusicalPhase, MusicalPhaseProfile>();
            return;
        }

        // Filter nulls and dedupe by phase (last one wins if duplicates)
        _profiles = profiles
            .Where(p => p != null)
            .GroupBy(p => p.phase)
            .ToDictionary(g => g.Key, g => g.Last());
    }

// Lazy fallback (only if InitializeProfiles never ran)
    private static void LoadFromResourcesIfEmpty()
    {
        if (_profiles != null && _profiles.Count > 0) return;

        var loaded = Resources.LoadAll<MusicalPhaseProfile>("MusicalPhaseProfiles");
        _profiles = loaded?
                        .Where(p => p != null)
                        .GroupBy(p => p.phase)
                        .ToDictionary(g => g.Key, g => g.Last())
                    ?? new Dictionary<MusicalPhase, MusicalPhaseProfile>();
    }

// Central getter used by all public APIs
    public static MusicalPhaseProfile Get(MusicalPhase phase)
    {
        LoadFromResourcesIfEmpty();
        return (_profiles != null && _profiles.TryGetValue(phase, out var prof)) ? prof : null;
    }

// Example public API stays the same, but now works with injected profiles:
    public static AudioClip GetRandomClip(MusicalPhase phase)
    {
Debug.LogError($"Get Random Clip for phase has been deprecated");
        var prof = Get(phase);
        return null;
    }

    public static PatternStrategy GetPatternStrategyForRole(MusicalPhase phase, MusicalRole role)
    {
        var profile = Get(phase);
        return profile != null ? profile.GetPatternStrategyForRole(role) : PatternStrategy.Arpeggiated;
    }
    
    private static void Load()
    {
        _profiles = Resources.LoadAll<MusicalPhaseProfile>("MusicalPhases")
            .ToDictionary(p => p.phase, p => p);
    }
}
