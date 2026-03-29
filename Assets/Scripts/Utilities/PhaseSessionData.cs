using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class SerializableNoteEntry
{
    public int step;
    public int note;
    public float velocity;
    public Color trackColor;
    public float commitTime01; // 0 = first collected, 1 = last collected within bin
}

[Serializable]
public class SerializablePhaseSnapshot
{
    public string pattern;
    public Color color;
    public long timestamp;
    public List<SerializableNoteEntry> collectedNotes;
}

[Serializable]
public class SerializablePhaseSession
{
    public List<SerializablePhaseSnapshot> snapshots = new();
    public long sessionTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}