using UnityEngine;

public enum StartKind
{
    /// <summary>Nothing chosen yet (or back on the title menu).</summary>
    None,
    /// <summary>START: solo or co-op, before the tutorial is done. Sandbox, then the tutorial motif.</summary>
    Start,
    /// <summary>CONTINUE: solo or co-op with a carousel pick. Multiplayer runs the sandbox first.</summary>
    Continue,
    /// <summary>VERSUS: 2+ players, Survival or Score. Sandbox, then the picked motif.</summary>
    Versus,
}

/// <summary>
/// What the player chose on the title menu (MainMenu), carried across TrackSelection into the
/// run. It decides the session's GameMode: co-op unless VERSUS, so ship select no longer offers
/// a mode choice. The motif itself still travels in PhaseLibraryStartConfig.
/// </summary>
public static class StartIntent
{
    public static StartKind Kind { get; private set; }
    /// <summary>VERSUS only: CompetitiveElimination ("Survival") or CompetitiveMotif ("Score").</summary>
    public static GameMode VersusMode { get; private set; } = GameMode.CompetitiveElimination;

    public static bool IsVersus => Kind == StartKind.Versus;
    public static GameMode SessionMode => IsVersus ? VersusMode : GameMode.Coop;

    /// <summary>Players a VERSUS session needs before it can launch.</summary>
    public const int VersusMinPlayers = 2;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Kind = StartKind.None;
        VersusMode = GameMode.CompetitiveElimination;
    }

    public static void Set(StartKind kind, GameMode versusMode = GameMode.CompetitiveElimination)
    {
        Kind = kind;
        VersusMode = versusMode;
        ApplyMode();
    }

    public static void Clear() => Set(StartKind.None);

    /// <summary>Pushes <see cref="SessionMode"/> into the live session (also re-applied on TrackSelection load).</summary>
    public static void ApplyMode()
    {
        var session = GameFlowManager.Instance != null ? GameFlowManager.Instance.SessionState : null;
        session?.SetMode(SessionMode);
    }

    /// <summary>The tutorial counts as done once any ring is saved: the tutorial's own, or one
    /// beyond it, which can't be reached otherwise. Same rule as the sandbox gate and MotifMazeGraph.</summary>
    public static bool TutorialComplete => RingSessionStore.LoadAllRingsFromDisk().Count > 0;
}
