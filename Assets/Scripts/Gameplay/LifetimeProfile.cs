using UnityEngine;
using System.Collections.Generic;

public enum MinedObjectType
{
    NoteSpawner,
    TrackUtility,
    TrackClear,
    LoopExpansion,
    AntiNoteSpawner,
    NoteModifier, // Future
    ChordChange,
    RootShift,
    NoteBehaviorChange,
    RhythmStyleChange,
    Shoft,
    Note // Add this line
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

    public static readonly Dictionary<MinedObjectType, LifetimeProfile> Profiles = new()
    {
        { MinedObjectType.NoteSpawner, new LifetimeProfile(16f) },
        { MinedObjectType.TrackUtility, new LifetimeProfile(8f) },
        { MinedObjectType.NoteModifier, new LifetimeProfile(24f) },
        { MinedObjectType.Shoft,        new LifetimeProfile(12f, true) },
        { MinedObjectType.Note,         new LifetimeProfile(20f, true) }
    };

    public static LifetimeProfile GetProfile(MinedObjectType type)
    {
        return Profiles.TryGetValue(type, out var profile) ? profile : new LifetimeProfile(12f);
    }
}