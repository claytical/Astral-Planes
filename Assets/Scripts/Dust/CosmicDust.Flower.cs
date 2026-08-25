using UnityEngine;

public partial class CosmicDust
{
    [Header("Flower Bloom (vehicle-carve regrow tell)")]
    [SerializeField] private GameObject flowerRoot;
    [SerializeField] private SpriteRenderer flowerDiamondA;
    [SerializeField] private SpriteRenderer flowerDiamondB;
    [SerializeField] private float flowerBloomAlpha = 0.75f;
    [Tooltip("Shimmer cycles per second -- how fast the flower pulses between its role color and white.")]
    [SerializeField] private float flowerShimmerSpeed = 1.2f;

    private Color _flowerRoleColor = Color.white;
    private float _flowerBloomTime;

    public bool HasBloomedFlower { get; private set; }

    public void BloomFlower(Color roleColorRgb)
    {
        HasBloomedFlower = true;
        _flowerRoleColor = roleColorRgb;
        _flowerBloomTime = Time.time;
        if (flowerRoot != null) flowerRoot.SetActive(true);
        ApplyFlowerShimmerColor();
    }

    public void HideFlower()
    {
        HasBloomedFlower = false;
        if (flowerRoot != null) flowerRoot.SetActive(false);
    }

    // Shimmer/glow tell: smoothly pulse-lerps between the cell's role color and white instead
    // of rotating, so a vehicle-carved regrow cell reads as gently glowing rather than spinning.
    private void TickFlowerShimmer()
    {
        if (!HasBloomedFlower) return;
        ApplyFlowerShimmerColor();
    }

    private void ApplyFlowerShimmerColor()
    {
        // Phased off this cell's own bloom time, not the shared global clock -- otherwise
        // every flower in the maze pulses in lockstep regardless of when each one bloomed.
        // 1-Cos (not Sin) so elapsed=0 starts exactly at the role color, not mid-pulse.
        float elapsed = Time.time - _flowerBloomTime;
        float t = (1f - Mathf.Cos(elapsed * flowerShimmerSpeed * Mathf.PI * 2f)) * 0.5f;
        Color c = Color.Lerp(_flowerRoleColor, Color.white, t);
        c.a = flowerBloomAlpha;
        if (flowerDiamondA != null) flowerDiamondA.color = c;
        if (flowerDiamondB != null) flowerDiamondB.color = c;
    }
}
