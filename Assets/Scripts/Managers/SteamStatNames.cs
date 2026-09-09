// Single source of truth for Steam Stats API names. Keep in sync with the
// stats published on the Steamworks backend (see the analytics plan doc).
public static class SteamStatNames
{
    public const string GameplayStarted        = "gameplay_started";
    public const string FirstCommitCompleted    = "first_commit_completed";
    public const string TutorialCompleted       = "tutorial_completed";
    public const string Motif1Started           = "motif_1_started";
    public const string Motif1Completed         = "motif_1_completed";
    public const string Motif2Started           = "motif_2_started";
    public const string Motif2Completed         = "motif_2_completed";
    public const string Motif3Started           = "motif_3_started";
    public const string Motif3Completed         = "motif_3_completed";
    public const string NaturalRunEndReached    = "natural_run_end_reached";
    public const string MotifsCompletedLifetime = "motifs_completed_lifetime";

    public static string MotifStarted(int ordinal) => ordinal switch
    {
        1 => Motif1Started,
        2 => Motif2Started,
        3 => Motif3Started,
        _ => null
    };

    public static string MotifCompleted(int ordinal) => ordinal switch
    {
        1 => Motif1Completed,
        2 => Motif2Completed,
        3 => Motif3Completed,
        _ => null
    };
}
