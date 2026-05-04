using UnityEngine;

/// <summary>
/// Minimal adapter so SuperNode can delegate role-wave visuals without owning dust internals.
/// Concrete dust-overlay implementation should live in CosmicDust/CosmicDustGenerator systems.
/// </summary>
public class SuperNodeCosmicDustWaveDriver : MonoBehaviour
{
    [SerializeField] private CosmicDustGenerator dustGenerator;

    private void Awake()
    {
        if (dustGenerator == null)
            dustGenerator = FindAnyObjectByType<CosmicDustGenerator>();
    }

    public void EmitRoleWave(Vector2 worldPos, MusicalRole role, Color roleColor)
    {
        // Adapter/stub hook.
        // REQUIRED EXTERNAL: implement one of the called APIs in dust systems.
        // Example expected behavior:
        // - radial propagation from worldPos
        // - one overlay tint per cell (overwrite allowed)
        // - no additive blend
        // - persists until ResetSuperNodeOverlays()

        // Suggested API (add in CosmicDustGenerator if missing):
        // dustGenerator.ApplySuperNodeOverlayWave(worldPos, role, roleColor);
    }

    public void ResetSuperNodeOverlays()
    {
        // Adapter/stub hook.
        // Suggested API:
        // dustGenerator.ClearSuperNodeOverlays();
    }
}
