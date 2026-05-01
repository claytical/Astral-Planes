using UnityEngine;

[DisallowMultipleComponent]
public sealed class CosmicDustVisualController : MonoBehaviour
{
    [SerializeField] private CosmicDust dust;

    private void Awake()
    {
        if (dust == null) dust = GetComponent<CosmicDust>();
    }

    private void OnEnable()
    {
        if (dust == null) return;
        dust.OnChargeChanged += HandleChargeChanged;
        dust.OnRoleChanged += HandleRoleChanged;
        dust.OnCollisionStateChanged += HandleCollisionStateChanged;
    }

    private void OnDisable()
    {
        if (dust == null) return;
        dust.OnChargeChanged -= HandleChargeChanged;
        dust.OnRoleChanged -= HandleRoleChanged;
        dust.OnCollisionStateChanged -= HandleCollisionStateChanged;
    }

    private void HandleChargeChanged(float charge01)
    {
        // State-driven hook for pulse/scale/emission adapters.
    }

    private void HandleRoleChanged(MusicalRole role)
    {
        // State-driven hook for role visual transitions.
    }

    private void HandleCollisionStateChanged(bool collisionEnabled)
    {
        // State-driven hook for dormant/active visual transitions.
    }
}
