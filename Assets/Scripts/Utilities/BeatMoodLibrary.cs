using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lookup for BeatMoodProfile assets, with helpers to get profiles and clips.
/// </summary>
[CreateAssetMenu(
    fileName = "BeatMoodLibrary",
    menuName = "Astral Planes/Music/Beat Mood Library")]
public class BeatMoodLibrary : ScriptableObject
{
    public List<BeatMoodProfile> profiles = new List<BeatMoodProfile>();

    public BeatMoodProfile GetProfile(BeatMood mood)
    {
        if (profiles == null) return null;
        for (int i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            if (p != null && p.mood == mood)
                return p;
        }
        return null;
    }

    public AudioClip GetRandomClip(BeatMood mood)
    {
        var profile = GetProfile(mood);
        return profile != null ? profile.GetRandomLoopClip() : null;
    }
}