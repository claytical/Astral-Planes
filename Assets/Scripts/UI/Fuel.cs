using UnityEngine;
using UnityEngine.UI;

[AddComponentMenu("UI/Vehicle Stat Bars")]
public class Fuel : MonoBehaviour
{
    [Header("Runtime Fuel")]
    public Image fuelFill;

    [Header("Fuel Change FX")]
    [SerializeField] private float changeEpsilon = 0.0005f;       // ignore float-noise no-op updates
    [SerializeField] private float intensityPerFullDelta = 0.05f; // ratio delta that maps to intensity 1
    [SerializeField] private float intensityDecayPerSecond = 3.5f;
    [SerializeField] private float swellScale = 0.18f;    // peak scale offset on gain
    [SerializeField] private float squeezeScale = 0.14f;  // peak scale offset on drain
    [SerializeField] private float shakeAmplitudeDegrees = 6f;
    [SerializeField] private float gainShakeFrequency = 9f;   // slower "absorb" wobble
    [Tooltip("Rotation wobble frequency while draining against near-zero resistance (drainWeight01 = 0): fast, buzzy.")]
    [SerializeField] private float lightDrainShakeFrequency = 20f;
    [Tooltip("Rotation wobble frequency while draining against max resistance (drainWeight01 = 1, e.g. Bass dust): slow, heavy judder.")]
    [SerializeField] private float heavyDrainShakeFrequency = 4f;
    [SerializeField] private Color gainTint = Color.white;
    [SerializeField] private Color drainTint = Color.black;
    [SerializeField, Range(0f, 1f)] private float maxTintLerp = 0.6f;

    [Header("Drain Heaviness (e.g. plowing resistant dust)")]
    [Tooltip("Baseline shake/squeeze intensity while actively draining against near-zero resistance (drainWeight01 = 0).")]
    [SerializeField] private float minResistanceIntensity = 0.02f;
    [Tooltip("Baseline shake/squeeze intensity while actively draining against max resistance (drainWeight01 = 1, e.g. Bass dust).")]
    [SerializeField] private float maxResistanceIntensity = 3f;
    [Tooltip("Rotation wobble peak degrees as a function of drainWeight01 (0=lightest material, 1=heaviest). " +
             "Default keys encode: Rhythm~0.1→~0.1°, Lead~0.275→~0.25°, mid Harmony/Groove~0.6→~1°, Bass~0.815→~2°.")]
    [SerializeField] private AnimationCurve shakeAmplitudeByResistance = new AnimationCurve(
        new Keyframe(0f,     0.1f),
        new Keyframe(0.275f, 0.25f),
        new Keyframe(0.6f,   1f),
        new Keyframe(0.815f, 2f),
        new Keyframe(1f,     2.3f));
    [Tooltip("Fraction of maxTintLerp reached at drainWeight01 = 0; heavier drains darken closer to the full drainTint.")]
    [SerializeField, Range(0f, 1f)] private float minHeavyTintMul = 0.5f;

    private RectTransform _rect;
    private Color _baseFillColor;
    private Vector3 _baseScale;

    private bool _hasLastRatio;
    private float _lastRatio;

    private float _intensity;       // decays over time; unclamped so heavy drains can punch harder than 1
    private bool _gaining;          // direction of the active pulse
    private float _activeDrainWeight; // 0..1 heaviness of the drain currently driving _intensity
    private float _shakePhase;
    private bool _atRestVisual = true;

    // Read by PlayerStats.cs, which owns the rotation wobble on the Player Stats root
    // (composed on top of its own baked rest tilt) — Fuel no longer rotates its own rect.
    public float ShakeDegreesZ { get; private set; }

    private void Awake()
    {
        _rect = transform as RectTransform;
        _baseScale = _rect != null ? _rect.localScale : Vector3.one;
        if (fuelFill != null) _baseFillColor = fuelFill.color;
    }

    // drainWeight01: how resistant/heavy whatever is currently being drained by feels (e.g. a
    // vehicle's live carveResistance01 while plowing dust). 0 = no resistance, 1 = maximum.
    // Meaningless for gains, where it's ignored.
    public void UpdateFuelUI(float energyRatio, float drainWeight01 = 0f)
    {
        float ratio = Mathf.Clamp01(energyRatio);
        if (fuelFill != null)
            fuelFill.fillAmount = ratio;

        // Actively draining against resistance (e.g. plowing dust) drives its own baseline
        // shake/darken straight off the resistance value, and takes sole ownership of _intensity
        // for this tick: the flat ratio-delta trigger below is the same regardless of dust role
        // (the standing boost burn swamps it), so letting it compete via Mathf.Max would flatten
        // exactly the light-resistance roles that are supposed to read as a much smaller shake.
        float weight01 = Mathf.Clamp01(drainWeight01);
        if (weight01 > 0f)
        {
            _gaining = false;
            _activeDrainWeight = Mathf.Max(_activeDrainWeight, weight01);
            _intensity = Mathf.Max(_intensity, Mathf.Lerp(minResistanceIntensity, maxResistanceIntensity, weight01));
        }
        else if (_hasLastRatio)
        {
            float delta = ratio - _lastRatio;
            if (Mathf.Abs(delta) > changeEpsilon)
            {
                _gaining = delta > 0f;
                float triggered = Mathf.Clamp01(Mathf.Abs(delta) / Mathf.Max(0.0001f, intensityPerFullDelta));
                _intensity = Mathf.Max(_intensity, triggered);
            }
        }

        _lastRatio = ratio;
        _hasLastRatio = true;
    }

    private void Update()
    {
        if (_intensity <= 0f)
        {
            if (!_atRestVisual) SnapToRest();
            return;
        }

        _atRestVisual = false;
        _intensity = Mathf.MoveTowards(_intensity, 0f, intensityDecayPerSecond * Time.deltaTime);

        // Heavier resistance judders slower and bigger; lighter resistance buzzes fast and small.
        // Peak wobble degrees are driven directly by _activeDrainWeight (mirroring frequency) so
        // amplitude is explicitly tunable rather than an incidental side effect of _intensity,
        // which is shared with (and calibrated for) the squeeze/tint effect below.
        float freq;
        float shakeDegPeak;
        float shakeEnvelope01;
        if (_gaining)
        {
            freq = gainShakeFrequency;
            shakeDegPeak = shakeAmplitudeDegrees;
            shakeEnvelope01 = Mathf.Clamp01(_intensity);
        }
        else
        {
            freq = Mathf.Lerp(lightDrainShakeFrequency, heavyDrainShakeFrequency, _activeDrainWeight);
            shakeDegPeak = shakeAmplitudeByResistance.Evaluate(_activeDrainWeight);
            // Normalize by this weight's own achievable _intensity peak so the envelope reaches
            // 1 while sustained regardless of material — otherwise light materials (whose
            // _intensity peak sits well under 1) would be doubly suppressed by both a small
            // shakeDegPeak AND a perpetually-partial envelope.
            float peakIntensityForWeight = Mathf.Max(0.0001f, Mathf.Lerp(minResistanceIntensity, maxResistanceIntensity, _activeDrainWeight));
            shakeEnvelope01 = Mathf.Clamp01(_intensity / peakIntensityForWeight);
        }
        _shakePhase += Time.deltaTime * freq * Mathf.PI * 2f;

        ShakeDegreesZ = Mathf.Sin(_shakePhase) * shakeDegPeak * shakeEnvelope01;

        // Scale multiplies directly by _intensity (not Mathf.Lerp'd), so a heavy drain that
        // pushed intensity above 1 genuinely swells/shakes further than a normal-weight event.
        if (_rect != null)
        {
            float scaleOffset = (_gaining ? swellScale : -squeezeScale) * _intensity;
            _rect.localScale = _baseScale * (1f + scaleOffset);
        }

        if (fuelFill != null)
        {
            float tintLerpAmount = _gaining
                ? maxTintLerp
                : maxTintLerp * Mathf.Lerp(minHeavyTintMul, 1f, _activeDrainWeight);
            Color target = Color.Lerp(_baseFillColor, _gaining ? gainTint : drainTint, tintLerpAmount);
            fuelFill.color = Color.Lerp(_baseFillColor, target, Mathf.Clamp01(_intensity));
        }
    }

    private void SnapToRest()
    {
        _atRestVisual = true;
        _activeDrainWeight = 0f;
        ShakeDegreesZ = 0f;
        if (_rect != null)
            _rect.localScale = _baseScale;
        if (fuelFill != null) fuelFill.color = _baseFillColor;
    }

    private void OnDisable()
    {
        _intensity = 0f;
        SnapToRest();
    }
}
