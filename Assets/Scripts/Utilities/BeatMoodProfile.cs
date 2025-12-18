using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drum/groove profile for a given BeatMood:
/// holds BPM, loop structure, and AudioClips.
/// </summary>
[CreateAssetMenu(
    fileName = "BeatMoodProfile",
    menuName = "Astral Planes/Music/Beat Mood Profile")]
public class BeatMoodProfile : ScriptableObject
{
    [Header("Identity")]
    public BeatMood mood;

    [Tooltip("Nominal BPM for this groove family.")]
    public float bpm = 120f;

    [Tooltip("How many steps per full loop (ties into DrumTrack.totalSteps).")]
    public int stepsPerLoop = 16;

    [Tooltip("Candidate drum loops for this mood (same BPM/feel).")]
    public List<AudioClip> loopClips = new List<AudioClip>();
    [Header("Density (NoteSet influence)")]
    [Tooltip("Scales how many step positions are used from the rhythm pattern. < 1 = sparser, > 1 = denser.")]
    [Min(0f)]
    public float stepDensityMultiplier = 1f;

    [Tooltip("Scales how many of those steps actually become notes instead of rests. < 1 = more rests, > 1 = fewer rests.")]
    [Min(0f)]
    public float noteDensityMultiplier = 1f;

    /// <summary>
    /// Returns a random loop clip for this mood, or null if none.
    /// </summary>
    public AudioClip GetRandomLoopClip()
    {
        if (loopClips == null || loopClips.Count == 0)
            return null;

        int idx = Random.Range(0, loopClips.Count);
        return loopClips[idx];
    }
}