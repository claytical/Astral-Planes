using UnityEngine;
using System.Collections.Generic;

public enum MinedObjectType
{
    NoteSpawner
}      
[System.Serializable]
public class LifetimeProfile
{
    public float lifetime = 12f;
    public bool randomizeLifetime = false;

    public LifetimeProfile(float time, bool randomize = false)
    {
        lifetime = time;
        randomizeLifetime = randomize;
    }

    private static readonly Dictionary<MinedObjectType, LifetimeProfile> Profiles = new()
    {
        { MinedObjectType.NoteSpawner, new LifetimeProfile(160f) }
    };

    public static LifetimeProfile GetProfile(MinedObjectType type)
    {
        return Profiles.TryGetValue(type, out var profile) ? profile : new LifetimeProfile(12f);
    }
}