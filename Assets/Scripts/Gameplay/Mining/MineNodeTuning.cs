using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeTuning : MonoBehaviour
{
    public MineNodeDustInteractor dustInteractor;// optional
    public WiggleMotion wiggle;                  // optional

    public void Apply(PhaseStarBehaviorProfile profile, MusicalRole role, PhasePersonality personality)
    {
        if (!dustInteractor)  dustInteractor = GetComponent<MineNodeDustInteractor>();
        if (!wiggle)          wiggle       = GetComponent<WiggleMotion>();
        if (wiggle) wiggle.enabled = true;     // visual or small signal only
        if (dustInteractor) dustInteractor.enabled = true; // sets multipliers only

        var roleTuning = profile.GetRoleTuning(role);
        
        // --- Dust interaction (node in dust) ---
        if (dustInteractor)
        {
            dustInteractor.speedCapMul   = Mathf.Max(0.1f, profile.dustSpeedCapMul + roleTuning.dustSpeedCapDelta);
            dustInteractor.extraBrake    = Mathf.Max(0f,   profile.dustExtraBrake + roleTuning.dustExtraBrakeDelta);
            dustInteractor.lateralNudgeMul = Mathf.Max(0f, profile.dustLateralMul + roleTuning.dustLateralDelta);
            dustInteractor.turbulenceMul = Mathf.Max(0f,   profile.dustTurbulenceMul + roleTuning.dustTurbulenceDelta);
        }

        // --- Wiggle / life ---
        if (wiggle)
        {
            wiggle.wiggleTorqueStrength = Mathf.Max(0f, profile.wiggleTorqueStrength + roleTuning.wiggleTorqueDelta);
            wiggle.wiggleFrequency      = Mathf.Max(0.1f, profile.wiggleFrequency + roleTuning.wiggleFreqDelta);
            wiggle.enableDrift          = profile.wiggleDrift;
            wiggle.driftAmplitude       = profile.wiggleDriftAmplitude;
            wiggle.driftFrequency       = profile.wiggleDriftFrequency;
        }
    }
}
