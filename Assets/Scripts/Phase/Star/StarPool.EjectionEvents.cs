using System.Collections;
using UnityEngine;

public sealed partial class StarPool
{
    [Tooltip("How many seconds after ejection before the exiting Star GameObject is destroyed.")]
    [SerializeField, Min(0f)] private float starExitDuration = 0.4f;

    // The most recent ejecting Star — kept alive briefly for its exit animation.
    private PhaseStar _lastEjectingStar;
    // Set to true when the MineNode fires OnResolved (Vehicle destroys it).
    // _mineNodePending only clears once this is true AND the resulting burst is cleared.
    // Prevents pre-ejection expansion bursts from prematurely releasing the gate.
    private bool _mineNodeResolved;
    // Set when the ejected track fires OnCollectableBurstCleared with hadNotes=false BEFORE
    // _mineNodeResolved is true (empty-burst race: SpawnCollectableBurst fires synchronously
    // before TriggerExplosion). Tells OnStarMineNodeResolved it's safe to clear the gate
    // immediately. Without this flag, AnyCollectablesInFlight()=false after the vehicle
    // collects and deactivates notes would incorrectly look like an empty burst.
    private bool _ejectedBurstWasEmpty;

    // ── Star event handlers ───────────────────────────────────────────────────

    private void OnStarEjected(PhaseStar star, MusicalRole role)
    {
        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] OnStarEjected role={role} remainingHarvests={_remainingEjectionsTotal}");

        _lastEjectingStar = star;
        _lastEjectedRole = role;
        _mineNodePending = true;
        _mineNodeResolved = false;
        _ejectedBurstWasEmpty = false;

        // Remove from active dict so the slot can be refilled.
        if (_activeStars.TryGetValue(role, out var active) && active == star)
        {
            _activeStars.Remove(role);
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] _activeStars.Remove({role}) — ejecting star={star.name}");
        }
        else
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] _activeStars.Remove({role}) skipped — active={(active == null ? "null" : active.name)} star={star.name} match={active == star}");
        }

        // Pause stars that would expand their bin on next ejection — same-bin stars stay active.
        PauseExpansionConflicts(star);

        // Destroy the ejecting Star after a short exit animation.
        StartCoroutine(DestroyStarAfterDelay(star));

        // SuperNode with no shard tracks resolves synchronously inside SpawnSuperNodeCommon,
        // firing OnMineNodeResolved BEFORE OnEjected — the clear branch there sees
        // _mineNodePending=false and skips. Clear the gate now, mirroring that branch.
        if (_mineNodePending && !ReferenceEquals(star, null)
            && star.LastNodeWasSuperNode && !star.HasLiveEjectionNode)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log("[StarPool] OnStarEjected: SuperNode already resolved synchronously — clearing gate now.");
            _mineNodePending = false;
            _mineNodeResolved = false;
            ResumeAll();
            CheckBridgeGate();
        }
    }

    private IEnumerator DestroyStarAfterDelay(PhaseStar star)
    {
        if (starExitDuration > 0f)
            yield return new WaitForSeconds(starExitDuration);
        if (star != null) Destroy(star.gameObject);
        if (star == _lastEjectingStar) _lastEjectingStar = null;
    }

    // ── Pause / Resume ────────────────────────────────────────────────────────

    // Only pause stars whose next ejection would expand their track's bin count.
    // Stars that would stay in the same bin are left active so concurrent same-bin
    // MineNodes can coexist for distinct InstrumentTracks.
    private void PauseExpansionConflicts(PhaseStar except)
    {
        foreach (var kvp in _activeStars)
        {
            var star = kvp.Value;
            if (star == null || star == except) continue;

            var track = FindTrackForRole(kvp.Key);
            if (track != null && track.GetBinCursor() < track.loopMultiplier)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] PauseExpansionConflicts: {kvp.Key} star NOT paused — same-bin ejection allowed.");
                continue;
            }

            star.Pause();
            if (!_pausedStars.Contains(star)) _pausedStars.Add(star);
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] PauseExpansionConflicts: {kvp.Key} star paused — would expand bin.");
        }
    }

    private InstrumentTrack FindTrackForRole(MusicalRole role)
    {
        if (_tracks == null) return null;
        foreach (var t in _tracks)
            if (t != null && t.assignedRole == role) return t;
        return null;
    }

    private void ResumeAll()
    {
        foreach (var star in _pausedStars)
        {
            if (star != null) star.Resume();
        }
        _pausedStars.Clear();
        if (GameFlowManager.VerboseLogging) Debug.Log("[StarPool] Resumed all paused Stars.");
    }

    private void OnStarMineNodeResolved(PhaseStar star, MusicalRole role)
    {
        _mineNodeResolved = true;
        // Use ReferenceEquals rather than Unity's == operator: the PhaseStar GameObject may
        // already be destroyed (DestroyStarAfterDelay runs 0.4s after ejection), but the C#
        // object remains alive in memory with valid property values. Unity's == override
        // returns false for destroyed objects, incorrectly hiding all outcome flags.
        bool hasRef       = !ReferenceEquals(star, null);
        bool wasSuperNode = hasRef && star.LastNodeWasSuperNode;
        bool wasExpired   = hasRef && star.LastNodeWasExpired;
        bool wasEscaped   = hasRef && star.LastNodeWasEscaped;
        bool wasCaptured  = hasRef && star.LastNodeWasCaptured;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] MineNode resolved role={role} captured={wasCaptured} escaped={wasEscaped} expired={wasExpired} superNode={wasSuperNode} emptyBurst={_ejectedBurstWasEmpty} CIF={AnyCollectablesInFlight()}");

        if (wasCaptured)
        {
            _nodesCapturedThisMotif++;
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Node captured — capturedThisMotif={_nodesCapturedThisMotif}");
        }

        // Clear the gate immediately for:
        //   empty burst, SuperNode, expired (player ignored it), or escaped (node fled successfully).
        // These outcomes do not count as harvests — _remainingEjectionsTotal is unchanged and
        // Tick() will spawn the next PhaseStar so the player can try again.
        if (_mineNodePending && (_ejectedBurstWasEmpty || wasSuperNode || wasExpired || wasEscaped))
        {
            if (wasExpired || wasEscaped)
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] MineNode {(wasExpired ? "expired" : "escaped")} — no harvest, spawning next star (remainingHarvests={_remainingEjectionsTotal})");

            _mineNodeResolved = false;
            _mineNodePending = false;
            _ejectedBurstWasEmpty = false;
            ResumeAll();
            CheckBridgeGate();
        }
    }

    private void HandleCollectableBurstCleared(InstrumentTrack track, int burstId, bool hadNotes)
    {
        // Only the ejected role's track is authoritative for rollback and the mine-node gate.
        bool isEjectedTrack = track.assignedRole == _lastEjectedRole;

        // Only treat this as the MineNode's burst if OnResolved already fired.
        // Pre-ejection expansion bursts arrive before the MineNode is destroyed, so
        // _mineNodeResolved is still false when they clear.
        bool isMineBurst = isEjectedTrack && _mineNodeResolved;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] HandleCollectableBurstCleared track={track.assignedRole} burstId={burstId} hadNotes={hadNotes} isEjected={isEjectedTrack} mineResolved={_mineNodeResolved} isMineBurst={isMineBurst} unresolved={HasUnresolvedMineNodeSequence()}");

        // Detect the empty-burst race: SpawnCollectableBurst fires OnCollectableBurstCleared
        // synchronously with hadNotes=false BEFORE TriggerExplosion sets _mineNodeResolved.
        // Flag it so OnStarMineNodeResolved knows it's a true empty burst (not notes-in-vehicle).
        if (isEjectedTrack && !_mineNodeResolved && !hadNotes)
            _ejectedBurstWasEmpty = true;

        if (isMineBurst)
        {
            if (hadNotes)
            {
                _remainingEjectionsTotal = Mathf.Max(0, _remainingEjectionsTotal - 1);
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Harvest complete — remainingHarvests={_remainingEjectionsTotal}");

                // Budget is committed at eject time, so stars for every dusty role may still
                // be live when the last harvest lands. CheckBridgeGate requires no live stars —
                // clear them out now that no further ejection could ever spend budget.
                if (_remainingEjectionsTotal == 0)
                    DespawnLeftoverStars();
            }
            else
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Mine burst had no notes — not counted as harvest, spawning next star");
            }
        }

        // Clear the mine-node gate immediately when the burst is placed,
        // without requiring AnyCollectablesInFlight() == false first.
        // Tick() has its own AnyCollectablesInFlight() guard that prevents
        // spawning a new star while notes are still being carried.
        if (isMineBurst)
        {
            _mineNodeResolved = false;
            _mineNodePending = false;
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Mine burst cleared — _mineNodePending=false");
        }

        if (HasUnresolvedMineNodeSequence())
        {
            // Manual-release path fires this event while collectables are still active in the
            // vehicle. Set a flag so Update() calls ResumeAll/CheckBridgeGate once they clear.
            if (isMineBurst) _pendingGateCheck = true;
            return;
        }

        if (!_mineNodePending)
        {
            ResumeAll();
            CheckBridgeGate();
        }
    }

    // Explodes all remaining stars (active + paused) once the harvest budget is spent, so
    // CheckBridgeGate's no-live-stars requirement can pass. Mirrors ExplodeAndClearAll's star
    // teardown but leaves the gate/budget state alone — callers are mid-gate-clear.
    private void DespawnLeftoverStars()
    {
        foreach (var star in _activeStars.Values)
        {
            if (star == null) continue;
            var explode = star.GetComponent<Explode>();
            if (explode != null) explode.Permanent();
            else Destroy(star.gameObject);
        }
        _activeStars.Clear();

        foreach (var star in _pausedStars)
        {
            if (star == null) continue;
            var explode = star.GetComponent<Explode>();
            if (explode != null) explode.Permanent();
            else Destroy(star.gameObject);
        }
        _pausedStars.Clear();
        if (GameFlowManager.VerboseLogging) Debug.Log("[StarPool] DespawnLeftoverStars: budget spent — cleared remaining stars for bridge.");
    }
}
