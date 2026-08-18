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
}
