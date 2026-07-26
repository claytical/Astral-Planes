using System.Collections.Generic;
using UnityEngine;

public static class MusicalRoleProfileLibrary
{
    private static Dictionary<MusicalRole, MusicalRoleProfile> _roleToProfile;

    private static void LoadProfiles()
    {
        _roleToProfile = new Dictionary<MusicalRole, MusicalRoleProfile>();
        var profiles = Resources.LoadAll<MusicalRoleProfile>("Profiles/Musical Roles");
        System.Array.Sort(profiles, (a, b) => string.CompareOrdinal(a.name, b.name));
        foreach (var profile in profiles)
        {
            if (!_roleToProfile.ContainsKey(profile.role))
            {
                _roleToProfile.Add(profile.role, profile);
            }
            else
            {
                Debug.LogWarning($"[MusicalRoleProfileLibrary] Duplicate profile for role {profile.role}: " +
                    $"'{profile.name}' ignored, '{_roleToProfile[profile.role].name}' already registered.");
            }
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"Loaded {_roleToProfile.Count} MusicalRoleProfiles.");
    }

    public static MusicalRoleProfile GetProfile(MusicalRole role)
    {
        if (_roleToProfile == null)
        {
            LoadProfiles();
        }

        if (_roleToProfile.TryGetValue(role, out var profile))
        {
            return profile;
        }

        return null;
    }
}