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
    [Tooltip("How far each petal bobs from its resting position while swaying, in world units.")]
    [SerializeField] private float flowerSwayAmplitudeWorld = 0.03f;
    [Tooltip("How quickly the sway wanders -- low, matching the dust particles' own noise drift, for a breeze-like rather than mechanical feel.")]
    [SerializeField] private float flowerSwayFrequency = 0.22f;

    private Color _flowerRoleColor = Color.white;
    private float _flowerBloomTime;
    private bool _flowerSwayBaseCaptured;
    private Vector3 _flowerDiamondABasePos;
    private Vector3 _flowerDiamondBBasePos;

    public bool HasBloomedFlower { get; private set; }

    public void BloomFlower(Color roleColorRgb)
    {
        HasBloomedFlower = true;
        _flowerRoleColor = roleColorRgb;
        _flowerBloomTime = Time.time;
        if (!_flowerSwayBaseCaptured)
        {
            if (flowerDiamondA != null) _flowerDiamondABasePos = flowerDiamondA.transform.localPosition;
            if (flowerDiamondB != null) _flowerDiamondBBasePos = flowerDiamondB.transform.localPosition;
            _flowerSwayBaseCaptured = true;
        }
        if (flowerRoot != null) flowerRoot.SetActive(true);
        ApplyFlowerShimmerColor();
        ApplyFlowerSway();
    }

    public void HideFlower()
    {
        HasBloomedFlower = false;
        if (flowerRoot != null) flowerRoot.SetActive(false);
    }

    // Shimmer/glow tell: smoothly pulse-lerps between the cell's role color and white instead
    // of rotating, so a vehicle-carved regrow cell reads as gently glowing rather than spinning.
    // Also sways/bobs gently in place, echoing the dust particles' own noise-driven drift.
    private void TickFlowerShimmer()
    {
        if (!HasBloomedFlower) return;
        ApplyFlowerShimmerColor();
        ApplyFlowerSway();
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

    // Perlin (not periodic sine) so the bob wanders irregularly like a breeze rather than
    // mechanically oscillating -- closer in character to the particle system's own noise
    // module. Each diamond/axis samples a differently-seeded slice of the noise field so the
    // two petals don't bob in lockstep with each other. Anchored on each diamond's own
    // authored rest position (captured once), never accumulating -- just wanders around it.
    private void ApplyFlowerSway()
    {
        float t = (Time.time - _flowerBloomTime) * flowerSwayFrequency;

        if (flowerDiamondA != null)
            flowerDiamondA.transform.localPosition = _flowerDiamondABasePos + SwayOffset(t, 0f);
        if (flowerDiamondB != null)
            flowerDiamondB.transform.localPosition = _flowerDiamondBBasePos + SwayOffset(t, 40f);
    }

    private Vector3 SwayOffset(float t, float phaseSeed)
    {
        float nx = Mathf.PerlinNoise(t + phaseSeed, phaseSeed) * 2f - 1f;
        float ny = Mathf.PerlinNoise(phaseSeed, t + phaseSeed + 10f) * 2f - 1f;
        return new Vector3(nx, ny, 0f) * flowerSwayAmplitudeWorld;
    }
}
