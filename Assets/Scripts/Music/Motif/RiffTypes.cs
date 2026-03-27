using System;
using System.Collections.Generic;
using UnityEngine;
[Serializable]
public struct RiffNoteEvent
{
    public int step;          // absolute step index within the riff loop (0..loopSteps-1)

    [Range(0, 127)]
    public int midiNote;      // authored pitch (in authoredRoot context)

    public int durSteps;      // authored duration in steps (1..loopSteps)

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
    public int loopSteps;
    public List<RiffNoteEvent> events;

    public RiffOverlapPolicy overlapPolicy;
    public bool clampToTrackRange;
    public int octaveShift;        // applied post-transpose
}