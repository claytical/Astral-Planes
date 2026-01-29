using System;
using System.Collections.Generic;
using UnityEngine;
[Serializable]
public struct RiffNoteEvent
{
    [Range(0, 15)]
    public int step;          // local step inside the 16-step bin

    [Range(0, 127)]
    public int midiNote;      // authored pitch (in authoredRoot context)

    [Range(1, 16)]
    public int durSteps;      // authored duration (step units)

    [Range(0f, 1f)]
    public float velocity01;  // feeds persistentLoopNotes.velocity
}

public enum RiffOverlapPolicy
{
    AllowOverlap,
    ClampToNextOnset
}

[Serializable]
public struct Riff
{
    public string id;

    [Range(0, 127)]
    public int authoredRootMidi;   // “this riff is a chord rooted at C”
    [Range(16, 32)]
    public int loopSteps;
    public List<RiffNoteEvent> events;

    public RiffOverlapPolicy overlapPolicy;
    public bool clampToTrackRange;
    public int octaveShift;        // applied post-transpose
}