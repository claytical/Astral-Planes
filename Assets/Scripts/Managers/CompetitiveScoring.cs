/// <summary>
/// Competitive-mode scoring rules. Stateless; per-player totals live on PlayerStatsTracking so they
/// survive vehicle respawns. All entry points are no-ops outside competitive mode.
/// </summary>
public static class CompetitiveScoring
{
    private const int DefaultPointsPerNote = 1;
    private const int DefaultMultiplier = 2;

    public static bool IsActive =>
        GameFlowManager.Instance != null &&
        GameFlowManager.Instance.SessionState != null &&
        GameFlowManager.Instance.SessionState.IsCompetitive;

    private static int PointsPerNote =>
        GameFlowManager.Instance?.competitiveScoringConfig != null
            ? GameFlowManager.Instance.competitiveScoringConfig.pointsPerNote
            : DefaultPointsPerNote;

    private static int Multiplier =>
        GameFlowManager.Instance?.competitiveScoringConfig != null
            ? GameFlowManager.Instance.competitiveScoringConfig.discoveryMultiplier
            : DefaultMultiplier;

    /// <summary>True when this depleting hit should own a multiplier burst (competitive mode, owner has a tracker).</summary>
    public static bool CanGrantMultiplier(PlayerStatsTracking owner) => owner != null && IsActive;

    /// <summary>A player's first collected note from a discovery burst opens their round display.</summary>
    public static void JoinRound(PlayerStatsTracking player, bool isMultiplierOwner)
    {
        if (player == null || !IsActive) return;
        player.JoinRound(isMultiplierOwner ? Multiplier : 0);
    }

    /// <summary>The burst cleared (last note placed or discarded): close that burst's round display.</summary>
    public static void LeaveRound(PlayerStatsTracking player, bool wasMultiplierOwner)
    {
        if (player != null) player.LeaveRound(wasMultiplierOwner);
    }

    /// <summary>Awards one placed note. Only the burst's multiplier owner gets the multiplier on Total; the round score counts raw notes.</summary>
    public static void AwardNotePlaced(PlayerStatsTracking placer, PlayerStatsTracking burstOwner)
    {
        if (placer == null || !IsActive) return;
        int basePoints = PointsPerNote;
        placer.AddNotePoints(basePoints * (placer == burstOwner ? Multiplier : 1), basePoints);
    }
}
