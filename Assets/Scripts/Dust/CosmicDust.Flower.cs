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
    [Tooltip("Peak random rotation (degrees), applied independently to each diamond so they flutter " +
             "slightly differently from each other -- like two leaves attached to the same swaying bud.")]
    [SerializeField] private float flowerLeafRotationDegrees = 8f;
    [Tooltip("How quickly each leaf's independent rotation wanders.")]
    [SerializeField] private float flowerLeafRotationFrequency = 0.4f;

    private Color _flowerRoleColor = Color.white;
    private float _flowerBloomTime;
    private bool _flowerRootTransformCaptured;
    private Vector3 _flowerRootBasePos;
    private Vector3 _flowerRootBaseScale = Vector3.one;
    private bool _flowerLeafRotBaseCaptured;
    private Quaternion _flowerDiamondABaseRot = Quaternion.identity;
    private Quaternion _flowerDiamondBBaseRot = Quaternion.identity;
    private Coroutine _flowerGrowInRoutine;

    public bool HasBloomedFlower { get; private set; }

    public void BloomFlower(Color roleColorRgb)
    {
        HasBloomedFlower = true;
        _flowerRoleColor = roleColorRgb;
        _flowerBloomTime = Time.time;

        if (!_flowerLeafRotBaseCaptured)
        {
            if (flowerDiamondA != null) _flowerDiamondABaseRot = flowerDiamondA.transform.localRotation;
            if (flowerDiamondB != null) _flowerDiamondBBaseRot = flowerDiamondB.transform.localRotation;
            _flowerLeafRotBaseCaptured = true;
        }

        if (flowerRoot != null)
        {
            if (!_flowerRootTransformCaptured)
            {
                _flowerRootBasePos = flowerRoot.transform.localPosition;
                _flowerRootBaseScale = flowerRoot.transform.localScale;
                _flowerRootTransformCaptured = true;
            }
            flowerRoot.SetActive(true);
            if (_flowerGrowInRoutine != null) StopCoroutine(_flowerGrowInRoutine);
            _flowerGrowInRoutine = StartCoroutine(FlowerGrowInRoutine());
        }

        ApplyFlowerShimmerColor();
        ApplyFlowerSway();
        ApplyFlowerLeafRotation();
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
    // as one bud, and wobbles each diamond/leaf independently -- see ApplyFlowerSway and
    // ApplyFlowerLeafRotation below.
    private void TickFlowerShimmer()
    {
        if (!HasBloomedFlower) return;
        ApplyFlowerShimmerColor();
        ApplyFlowerSway();
        ApplyFlowerLeafRotation();
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

    // The "variation" half of the sway: each diamond/leaf gets its own small, independently-
    // seeded random rotation on top of the shared bud translation above, so the two leaves
    // flutter slightly differently from each other rather than staying rigidly parallel.
    private void ApplyFlowerLeafRotation()
    {
        float t = (Time.time - _flowerBloomTime) * flowerLeafRotationFrequency;

        if (flowerDiamondA != null)
        {
            float angleA = (Mathf.PerlinNoise(t, 5f) * 2f - 1f) * flowerLeafRotationDegrees;
            flowerDiamondA.transform.localRotation = _flowerDiamondABaseRot * Quaternion.Euler(0f, 0f, angleA);
        }
        if (flowerDiamondB != null)
        {
            float angleB = (Mathf.PerlinNoise(t + 70f, 5f) * 2f - 1f) * flowerLeafRotationDegrees;
            flowerDiamondB.transform.localRotation = _flowerDiamondBBaseRot * Quaternion.Euler(0f, 0f, angleB);
        }
    }
}
