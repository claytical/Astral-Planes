using System.Collections;
using UnityEngine;

public partial class CosmicDust
{
    [Header("Flower Bloom (vehicle-carve regrow tell)")]
    [SerializeField] private GameObject flowerRoot;
    [SerializeField] private SpriteRenderer flowerDiamondA;
    [SerializeField] private SpriteRenderer flowerDiamondB;
    [SerializeField] private float flowerBloomAlpha = 0.75f;
    [Tooltip("Seconds for the flower to scale in from 0 when it blooms.")]
    [SerializeField] private float flowerGrowInSeconds = 0.18f;
    [Tooltip("Shimmer cycles per second -- how fast the flower pulses between its role color and white.")]
    [SerializeField] private float flowerShimmerSpeed = 1.2f;
    [Tooltip("How far the whole bud sways from its resting position, in world units. Both diamonds move together as one unit.")]
    [SerializeField] private float flowerSwayAmplitudeWorld = 0.08f;
    [Tooltip("How quickly the bud sway wanders -- low, matching the dust particles' own noise drift, for a breeze-like rather than mechanical feel.")]
    [SerializeField] private float flowerSwayFrequency = 0.3f;
    [Tooltip("Continuous whole-bud rotation speed (degrees/sec), like a pinwheel -- both diamonds turn together as one rigid unit.")]
    [SerializeField] private float flowerSpinDegPerSec = 45f;

    [Tooltip("Fractional pluck-velocity reduction applied per prior bump against this flowered cell -- lets harsh tones relax the more a spot gets leaned on.")]
    [Range(0f, 1f)] [SerializeField] private float flowerVelocitySoftenPerBump = 0.15f;
    [Tooltip("Pluck velocity never softens below this floor.")]
    [Range(1f, 127f)] [SerializeField] private float flowerVelocityFloor127 = 20f;
    [Tooltip("Fractional darkening of the bud's base color applied per prior bump.")]
    [Range(0f, 1f)] [SerializeField] private float flowerBudDarkenPerBump = 0.1f;
    [Tooltip("Cap on how dark the bud can get from repeated bumps -- must never reach 1 (fully black/invisible).")]
    [Range(0f, 1f)] [SerializeField] private float flowerBudMaxDarken01 = 0.65f;

    private Color _flowerRoleColor = Color.white;
    private float _flowerBloomTime;
    private bool _flowerRootTransformCaptured;
    private Vector3 _flowerRootBasePos;
    private Vector3 _flowerRootBaseScale = Vector3.one;
    private Quaternion _flowerRootBaseRot = Quaternion.identity;
    private Coroutine _flowerGrowInRoutine;
    private int _flowerBumpCount;

    public bool HasBloomedFlower { get; private set; }

    public void BloomFlower(Color roleColorRgb)
    {
        HasBloomedFlower = true;
        _flowerRoleColor = roleColorRgb;
        _flowerBloomTime = Time.time;
        _flowerBumpCount = 0;

        if (flowerRoot != null)
        {
            if (!_flowerRootTransformCaptured)
            {
                _flowerRootBasePos = flowerRoot.transform.localPosition;
                _flowerRootBaseScale = flowerRoot.transform.localScale;
                _flowerRootBaseRot = flowerRoot.transform.localRotation;
                _flowerRootTransformCaptured = true;
            }
            flowerRoot.SetActive(true);
            if (_flowerGrowInRoutine != null) StopCoroutine(_flowerGrowInRoutine);
            _flowerGrowInRoutine = StartCoroutine(FlowerGrowInRoutine());
        }

        ApplyFlowerShimmerColor();
        ApplyFlowerSway();
        ApplyFlowerSpin();
    }

    public void HideFlower()
    {
        HasBloomedFlower = false;
        if (_flowerGrowInRoutine != null) { StopCoroutine(_flowerGrowInRoutine); _flowerGrowInRoutine = null; }
        if (flowerRoot != null) flowerRoot.SetActive(false);
    }

    private IEnumerator FlowerGrowInRoutine()
    {
        flowerRoot.transform.localScale = Vector3.zero;
        float t = 0f;
        float duration = Mathf.Max(0.01f, flowerGrowInSeconds);
        while (t < duration)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / duration);
            flowerRoot.transform.localScale = _flowerRootBaseScale * u;
            yield return null;
        }
        flowerRoot.transform.localScale = _flowerRootBaseScale;
        _flowerGrowInRoutine = null;
    }

    // Shimmer/glow tell: smoothly pulse-lerps between the cell's role color and white, sways
    // as one bud, and spins continuously like a pinwheel -- see ApplyFlowerSway and
    // ApplyFlowerSpin below.
    private void TickFlowerShimmer()
    {
        if (!HasBloomedFlower) return;
        ApplyFlowerShimmerColor();
        ApplyFlowerSway();
        ApplyFlowerSpin();
    }

    private void ApplyFlowerShimmerColor()
    {
        // Phased off this cell's own bloom time, not the shared global clock -- otherwise
        // every flower in the maze pulses in lockstep regardless of when each one bloomed.
        // 1-Cos (not Sin) so elapsed=0 starts exactly at the role color, not mid-pulse.
        float elapsed = Time.time - _flowerBloomTime;
        float t = (1f - Mathf.Cos(elapsed * flowerShimmerSpeed * Mathf.PI * 2f)) * 0.5f;
        // Repeated bumps darken the bud's base color (capped short of full black) before the
        // usual role-color<->white shimmer oscillates over it -- a visual echo of the pluck
        // velocity relaxing with each hit, see RegisterFlowerBumpVelocity.
        float darken01 = Mathf.Min(flowerBudMaxDarken01, _flowerBumpCount * flowerBudDarkenPerBump);
        Color baseColor = Color.Lerp(_flowerRoleColor, Color.black, darken01);
        Color c = Color.Lerp(baseColor, Color.white, t);
        c.a = flowerBloomAlpha;
        if (flowerDiamondA != null) flowerDiamondA.color = c;
        if (flowerDiamondB != null) flowerDiamondB.color = c;
    }

    // Called once per throttled non-boost pluck against this flowered cell (CosmicDust.Collision.cs).
    // Uses the bump count from *before* this bump, so the first bump plays at full velocity --
    // the harsh baseline -- and each subsequent one is progressively softer, easing toward the floor.
    public float RegisterFlowerBumpVelocity(float vel127)
    {
        float multiplier = Mathf.Pow(1f - flowerVelocitySoftenPerBump, _flowerBumpCount);
        _flowerBumpCount++;
        return Mathf.Clamp(vel127 * multiplier, flowerVelocityFloor127, 127f);
    }

    // Perlin (not periodic sine) so the sway wanders irregularly like a breeze rather than
    // mechanically oscillating -- closer in character to the particle system's own noise
    // module. Moves flowerRoot itself (not the individual diamonds), so both diamonds
    // translate together as one bud, anchored on the root's own authored rest position
    // (captured once) and never accumulating -- just wanders around it.
    private void ApplyFlowerSway()
    {
        if (flowerRoot == null) return;

        float t = (Time.time - _flowerBloomTime) * flowerSwayFrequency;
        float nx = Mathf.PerlinNoise(t, 0f) * 2f - 1f;
        float ny = Mathf.PerlinNoise(0f, t + 10f) * 2f - 1f;

        flowerRoot.transform.localPosition = _flowerRootBasePos + new Vector3(nx, ny, 0f) * flowerSwayAmplitudeWorld;
    }

    // Continuous whole-bud spin, like a pinwheel -- both diamonds are children of flowerRoot,
    // so rotating the root turns them together as one rigid piece instead of independently.
    // A steady, unmistakably-turning silhouette reads as something growing/spinning amongst
    // the dust rather than a static shape sitting on the grid. Phased off this cell's own
    // bloom time so flowers across the maze aren't all at the same rotation angle at once.
    private void ApplyFlowerSpin()
    {
        if (flowerRoot == null) return;
        float elapsed = Time.time - _flowerBloomTime;
        flowerRoot.transform.localRotation = _flowerRootBaseRot * Quaternion.Euler(0f, 0f, elapsed * flowerSpinDegPerSec);
    }
}
