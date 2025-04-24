using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class ShipMusicalProfileLoader
{
    private static Dictionary<string, ShipMusicalProfile> _profilesByName;

    public static void Load()
    {
        if (_profilesByName != null) return; // Already loaded

        _profilesByName = Resources
            .LoadAll<ShipMusicalProfile>("ShipProfiles")
            .ToDictionary(profile => profile.shipName, profile => profile);

        Debug.Log($"Loaded {_profilesByName.Count} ShipMusicalProfiles.");
    }

    public static ShipMusicalProfile GetProfile(string shipName)
    {
        Load();

        if (_profilesByName.TryGetValue(shipName, out var profile))
        {
            return profile;
        }

        Debug.LogWarning($"No ShipMusicalProfile found for ship name: {shipName}");
        return null;
    }

    public static List<ShipMusicalProfile> GetProfiles(IEnumerable<string> shipNames)
    {
        return shipNames.Select(GetProfile).Where(p => p != null).ToList();
    }
}