using System.Collections.Generic;
using UnityEngine;

public static class MusicalRoleProfileLibrary
{
    private static Dictionary<MusicalRole, MusicalRoleProfile> _roleToProfile;

    public static void LoadProfiles()
    {
        _roleToProfile = new Dictionary<MusicalRole, MusicalRoleProfile>();
        var profiles = Resources.LoadAll<MusicalRoleProfile>("RoleProfiles");
        foreach (var profile in profiles)
        {
            if (!_roleToProfile.ContainsKey(profile.role))
            {
                _roleToProfile.Add(profile.role, profile);
            }
        }

        Debug.Log($"Loaded {_roleToProfile.Count} MusicalRoleProfiles.");
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

        Debug.LogWarning($"No profile found for role: {role}");
        return null;
    }
}