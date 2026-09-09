using System;
using System.Collections.Generic;

// Data model for the Greater Maze telemetry layer. Separate from Steam Stats and from
// MotifSnapshot (which is UI/preview-oriented and carries fields — commit time, track color,
// fill duration — this feature must not capture). All types here are JsonUtility-safe:
// no Dictionary<> fields, plain [Serializable] classes only.

[Serializable]
public class MotifVisit
{
    public string PhaseId;
    public string MotifId;
    public int VisitOrder; // 0-based order within this run
}

[Serializable]
public class MotifTransition
{
    public string FromPhaseId;
    public string FromMotifId;
    public string ToPhaseId;
    public string ToMotifId;
    public int TransitionOrder; // 0-based order within this run
}

[Serializable]
public class TelemetryNoteEntry
{
    public int StepIndex;
    public int MidiNote;
    public int DurationTicks;
    public float Velocity;
}

[Serializable]
public class TelemetryTrackBin
{
    public int BinIndex;
    public bool IsFilled;
}

[Serializable]
public class TelemetryTrackVoice
{
    public MusicalRole Role;
    public int VoiceIndex;
    public List<TelemetryNoteEntry> Notes = new List<TelemetryNoteEntry>();
    public List<TelemetryTrackBin> Bins = new List<TelemetryTrackBin>();
}

[Serializable]
public class MotifSolution
{
    public string PhaseId;
    public string MotifId;
    public int VisitOrder; // links back to the MotifVisit this solution belongs to
    public List<TelemetryTrackVoice> Voices = new List<TelemetryTrackVoice>();
}

[Serializable]
public class MazeRunRecord
{
    public string RunId;
    public string StartedAtUtc;
    public string EndedAtUtc;
    public string EndReason; // "Completed" | "QuitEarly"
    public int Version = 1;
    public List<MotifVisit> Path = new List<MotifVisit>();
    public List<MotifTransition> Transitions = new List<MotifTransition>();
    public List<MotifSolution> Solutions = new List<MotifSolution>();
}
