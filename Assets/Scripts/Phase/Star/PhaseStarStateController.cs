using UnityEngine;

public interface IPhaseStarStateController
{
    bool CanArm(PhaseStarState state, bool entryInProgress, bool hasWaitCoroutine, bool hasActiveNode, bool awaitingCollectableClear, bool burstOffScreen);
    bool ShouldDisarmForGlobalGates(bool anyCollectablesInFlight, bool anyExpansionPending, bool isReadyDisplay);
}

public sealed class PhaseStarStateController : IPhaseStarStateController
{
    public bool CanArm(PhaseStarState state, bool entryInProgress, bool hasWaitCoroutine, bool hasActiveNode, bool awaitingCollectableClear, bool burstOffScreen)
        => state == PhaseStarState.WaitingForPoke && !entryInProgress && !hasWaitCoroutine && !hasActiveNode && !awaitingCollectableClear && !burstOffScreen;

    public bool ShouldDisarmForGlobalGates(bool anyCollectablesInFlight, bool anyExpansionPending, bool isReadyDisplay)
    {
        if (anyCollectablesInFlight) return true;
        if (!anyExpansionPending) return false;
        return !isReadyDisplay;
    }
}
