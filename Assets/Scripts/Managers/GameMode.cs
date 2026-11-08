public enum GameMode
{
    Coop,
    /// <summary>Competitive. No respawn; the match ends when 1 player is left alive.</summary>
    CompetitiveElimination,
    /// <summary>Competitive. No respawn; the match ends when the motif completes or every player is dead.</summary>
    CompetitiveMotif
}

public enum MatchEndReason
{
    LastShipStanding,
    MotifComplete,
    AllEliminated
}
