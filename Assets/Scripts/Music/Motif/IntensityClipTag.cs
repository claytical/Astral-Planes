using System;
using UnityEngine;

// Pairs a drum clip with its target position in (depth, breadth, pressure) space.
// Clip selection picks whichever tag is nearest the live-computed axis point.
[Serializable]
public struct IntensityClipTag
{
    public AudioClip clip;
    [Range(0f, 1f)] public float depth01;
    [Range(0f, 1f)] public float breadth01;
    [Range(0f, 1f)] public float pressure01;

    // 0 = no cap. Once this clip has been continuously audible for this many seconds,
    // it's excluded from selection until a different clip takes over. If multiple tags
    // reference the same physical clip, only the first matching tag's value is consulted
    // (DrumTrack.MotifApplication.GetMaxConsecutiveLoopSeconds) — author them consistently.
    [Min(0f), Tooltip("0 = no cap. Once this clip has been continuously audible for this many " +
        "seconds, it's excluded from selection until a different clip takes over.")]
    public float maxConsecutiveLoopSeconds;
}
