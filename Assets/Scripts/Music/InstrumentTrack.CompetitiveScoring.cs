using System.Collections.Generic;

// Competitive-mode burst bookkeeping: which player holds the discovery multiplier for a burst,
// which players have collected from it (their round display is open), and the single exit point
// that closes both when the burst clears.
public partial class InstrumentTrack
{
    private readonly Dictionary<int, PlayerStatsTracking> _burstMultiplierOwners = new();
    private readonly Dictionary<int, HashSet<PlayerStatsTracking>> _burstParticipants = new();

    // Called right after SpawnBurst returns. An empty/aborted burst never registers a BurstState,
    // so it never gets an owner (its clear event already fired synchronously).
    private void RegisterBurstMultiplierOwner(PlayerStatsTracking owner)
    {
        if (!_bursts.ContainsKey(currentBurstId) || !CompetitiveScoring.CanGrantMultiplier(owner)) return;
        _burstMultiplierOwners[currentBurstId] = owner;
        _burstParticipants[currentBurstId] = new HashSet<PlayerStatsTracking>();
    }

    private PlayerStatsTracking BurstMultiplierOwner(int burstId) =>
        _burstMultiplierOwners.TryGetValue(burstId, out var owner) ? owner : null;

    // A player's first pickup from a discovery burst opens their round display (score 0; the
    // multiplier shows too if they hold it). Runs at pickup, before any placement scores.
    private void NoteCollectedForRound(Vehicle collector, int burstId)
    {
        var player = collector != null ? collector.playerStats : null;
        if (player == null || !_burstParticipants.TryGetValue(burstId, out var participants)) return;
        if (!participants.Add(player)) return;
        CompetitiveScoring.JoinRound(player, player == BurstMultiplierOwner(burstId));
    }

    // Awards one placed note. Must run BEFORE the burst's remaining counter is decremented,
    // since the final decrement clears the burst (and closes the round displays).
    private void AwardCompetitiveNote(Vehicle placer, int burstId)
    {
        if (placer == null) return;
        CompetitiveScoring.AwardNotePlaced(placer.playerStats, BurstMultiplierOwner(burstId));
    }

    private void RaiseBurstCleared(int burstId, bool hadNotes)
    {
        _burstMultiplierOwners.TryGetValue(burstId, out var owner);
        if (_burstParticipants.TryGetValue(burstId, out var participants))
        {
            foreach (var player in participants)
                CompetitiveScoring.LeaveRound(player, player == owner);
            _burstParticipants.Remove(burstId);
        }
        _burstMultiplierOwners.Remove(burstId);
        OnCollectableBurstCleared?.Invoke(this, burstId, hadNotes);
    }
}
