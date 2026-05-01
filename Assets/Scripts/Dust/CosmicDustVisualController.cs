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
        dust.OnTintStateChanged += HandleTintStateChanged;
        dust.OnSpawnVisualRequested += HandleSpawnVisualRequested;
        dust.OnClearVisualRequested += HandleClearVisualRequested;
    }

    private void OnDisable()
    {
        if (dust == null) return;
        dust.OnChargeChanged -= HandleChargeChanged;
        dust.OnRoleChanged -= HandleRoleChanged;
        dust.OnCollisionStateChanged -= HandleCollisionStateChanged;
        dust.OnTintStateChanged -= HandleTintStateChanged;
        dust.OnSpawnVisualRequested -= HandleSpawnVisualRequested;
        dust.OnClearVisualRequested -= HandleClearVisualRequested;
    }

    private void HandleChargeChanged(float charge01)
    {
        HandleTintStateChanged(dust.CurrentTint);
    }

    private void HandleRoleChanged(MusicalRole role)
    {
        HandleTintStateChanged(dust.CurrentTint);
    }

    private void HandleCollisionStateChanged(bool collisionEnabled)
    {
        // State-driven hook for dormant/active visual transitions.
    }

    private void HandleTintStateChanged(Color tint)
    {
        if (dust == null) return;
        dust.ApplyTintVisual(tint);
    }

    private void HandleSpawnVisualRequested()
    {
        if (dust == null) return;
        dust.RunSpawnVisuals(dust.ResolveGrowDurationSeconds());
    }

    private void HandleClearVisualRequested(float fadeSeconds)
    {
        if (dust == null) return;
        dust.RunClearVisuals(fadeSeconds);
    }
}
