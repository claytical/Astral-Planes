using UnityEngine;

public interface IPhaseStarStateController
{
    bool CanArm(PhaseStarState state, bool entryInProgress, bool hasWaitCoroutine, bool hasActiveNode, bool awaitingCollectableClear, bool burstOffScreen);
    bool CanDisarmForCollectables(bool anyCollectablesInFlight);
}

public sealed class PhaseStarStateController : IPhaseStarStateController
{
    public bool CanArm(PhaseStarState state, bool entryInProgress, bool hasWaitCoroutine, bool hasActiveNode, bool awaitingCollectableClear, bool burstOffScreen)
        => state == PhaseStarState.WaitingForPoke && !entryInProgress && !hasWaitCoroutine && !hasActiveNode && !awaitingCollectableClear && !burstOffScreen;

    public bool CanDisarmForCollectables(bool anyCollectablesInFlight) => anyCollectablesInFlight;
}
