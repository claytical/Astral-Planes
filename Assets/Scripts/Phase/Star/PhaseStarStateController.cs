using UnityEngine;

public interface IPhaseStarStateController
{
    bool CanArm(in PhaseStarInteractionSnapshot snapshot);
    bool ShouldDisarmForGlobalGates(in PhaseStarInteractionSnapshot snapshot);
}

public sealed class PhaseStarStateController : IPhaseStarStateController
{
    public bool CanArm(in PhaseStarInteractionSnapshot snapshot)
        => snapshot.State == PhaseStarState.WaitingForPoke
           && !snapshot.Interaction.EntryInProgress
           && !snapshot.HasWaitCoroutine
           && !snapshot.HasActiveNode
           && !snapshot.Interaction.AwaitingCollectableClear
           && !snapshot.Interaction.BurstOffScreen;

    public bool ShouldDisarmForGlobalGates(in PhaseStarInteractionSnapshot snapshot)
    {
        if (snapshot.AnyCollectablesInFlight) return true;
        if (!snapshot.AnyExpansionPending) return false;
        return !snapshot.IsReadyDisplay;
    }
}
