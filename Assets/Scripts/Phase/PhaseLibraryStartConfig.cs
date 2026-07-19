/// <summary>
/// Cross-scene bridge that carries a phase + motif start position from the
/// PhaseLibrary browser into the GeneratedTrack session setup.
/// Set before loading TrackSelection or GeneratedTrack; consumed in GameFlowManager STEP 1b.
/// </summary>
public static class PhaseLibraryStartConfig
{
    public static bool   HasPendingStart { get; private set; }
    public static int    PhaseIndex      { get; private set; }
    public static int    MotifIndex      { get; private set; }
    public static string MotifId         { get; private set; }

    public static void RequestStart(int phaseIndex, int motifIndex, string motifId)
    {
        PhaseIndex      = phaseIndex;
        MotifIndex      = motifIndex;
        MotifId         = motifId;
        HasPendingStart = true;
    }

    public static void Consume() => HasPendingStart = false;
}
