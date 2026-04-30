using UnityEngine;

public interface IPhaseStarBurstCoordinator
{
    bool TryEnterBurstHidden(ref bool burstOffScreen);
}

public sealed class PhaseStarBurstCoordinator : IPhaseStarBurstCoordinator
{
    public bool TryEnterBurstHidden(ref bool burstOffScreen)
    {
        if (burstOffScreen) return false;
        burstOffScreen = true;
        return true;
    }
}
