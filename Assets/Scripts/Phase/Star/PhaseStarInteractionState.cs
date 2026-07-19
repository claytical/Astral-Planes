using System;

[Serializable]
public sealed class PhaseStarInteractionState
{
    [Serializable]
    public sealed class InteractionStatus
    {
        public bool BurstOffScreen;
        public bool AwaitingCollectableClear;
        public bool IsArmed;
        public int DisarmReason;

        public override string ToString() =>
            $"armed={IsArmed} burstOff={BurstOffScreen} awaitClr={AwaitingCollectableClear} disarm={(PhaseStarDisarmReason)DisarmReason}";
    }

    [Serializable]
    public sealed class ChargeVisualStatus
    {
        public bool HasReceivedEnergy;
        public float DisplayedCharge01;
        public bool DormantSeedVisualPrimed;

        public override string ToString() =>
            $"hasEnergy={HasReceivedEnergy} shown={DisplayedCharge01:0.00} seedPrimed={DormantSeedVisualPrimed}";
    }

    public InteractionStatus Interaction = new();
    public ChargeVisualStatus ChargeVisual = new();

    public string ToDebugString() => $"{Interaction} | {ChargeVisual}";
}

public enum PhaseStarDisarmReason
{
    None = 0,
    NodeResolving,
    CollectablesInFlight,
    ExpansionPending,
    SiblingActive
}

public readonly struct PhaseStarInteractionSnapshot
{
    public readonly PhaseStarState State;
    public readonly bool HasWaitCoroutine;
    public readonly bool HasActiveNode;
    public readonly bool AnyCollectablesInFlight;
    public readonly bool AnyExpansionPending;
    public readonly bool IsReadyDisplay;
    public readonly PhaseStarInteractionState.InteractionStatus Interaction;

    public PhaseStarInteractionSnapshot(
        PhaseStarState state,
        bool hasWaitCoroutine,
        bool hasActiveNode,
        bool anyCollectablesInFlight,
        bool anyExpansionPending,
        bool isReadyDisplay,
        PhaseStarInteractionState.InteractionStatus interaction)
    {
        State = state;
        HasWaitCoroutine = hasWaitCoroutine;
        HasActiveNode = hasActiveNode;
        AnyCollectablesInFlight = anyCollectablesInFlight;
        AnyExpansionPending = anyExpansionPending;
        IsReadyDisplay = isReadyDisplay;
        Interaction = interaction;
    }
}
