using UnityEngine;

// Player-facing poke feedback (LEGIBILITY_AUDIT.md B1/B2).
//
// B1 — every rejected poke answers with one tell: the star's diamonds shake their head
//   ("not now"). Internal guards (same frame, ejection already in flight, bad state, sibling)
//   stay silent. (A "Wait" pointer line to the blocking node/note was tried and removed: it
//   read as the star zapping a differently colored note.)
//
// B2 — while an armed star is blocked by work elsewhere it "sleeps" (desaturated, slowed
// rotation, no ready-spin), so the global one-sequence-at-a-time rule is visible as state
// instead of only surfacing on poke. Paused (SiblingActive) stars always sleep.
public partial class PhaseStar
{
    private const float kPokeFeedbackCooldown = 0.25f;
    private const float kSleepRotationMul = 0.25f;
    private float _lastPokeFeedbackTime = -999f;

    private bool _sleepVisual;
    private float _nextSleepCheck;

    private void RespondToRejectedPoke()
    {
        if (Time.time - _lastPokeFeedbackTime < kPokeFeedbackCooldown) return;
        _lastPokeFeedbackTime = Time.time;
        visuals?.PlayNotReadyShake();
    }

    // True when an armed star can't eject because of work happening elsewhere.
    // isSuperNode:true asks only "is another sequence in flight" — the harvest budget never
    // blocks a live star (stars are despawned the moment it hits zero).
    private bool IsBlockedByWorkElsewhere()
    {
        if (IsPickupLocked()) return true;
        if (AnyExpansionPendingGlobal()) return true;
        if (CanCommitEjection != null && _attunedRole != MusicalRole.None && !CanCommitEjection(_attunedRole, true)) return true;
        return false;
    }

    private void UpdateSleepVisual()
    {
        bool eligible = _isArmed
                        && _state == PhaseStarState.WaitingForPoke
                        && !_burstOffScreen
                        && _disarmReason != PhaseStarDisarmReason.SiblingActive;

        // Paused by StarPool (another role's sequence would expand bins): always asleep. This
        // used to be skipped entirely, which froze the diamonds at their last full-color frame.
        bool paused = _disarmReason == PhaseStarDisarmReason.SiblingActive && !_burstOffScreen;

        bool want = false;
        if (paused)
        {
            want = true;
        }
        else if (eligible)
        {
            if (Time.unscaledTime >= _nextSleepCheck)
            {
                _nextSleepCheck = Time.unscaledTime + 0.1f;
                want = IsBlockedByWorkElsewhere();
            }
            else
            {
                want = _sleepVisual;
            }
        }

        if (want == _sleepVisual) return;
        _sleepVisual = want;
        if (visuals != null) visuals.Sleeping = want;

        if (want)
            SetVisual(VisualMode.Dim, ResolvePreviewColorByReadiness());
        else if (eligible)
            SetVisual(VisualMode.Bright, ResolvePreviewColorByReadiness());
    }
}
