using UnityEngine;
using UnityEngine.UI;

[AddComponentMenu("UI/Vehicle Stat Bars")]
public class Fuel : MonoBehaviour
{
    [Header("Runtime Fuel")]
    public Image fuelFill;

    // All tuning lives on VehicleConfig → "Fuel Bar (HUD)" and "Fuel Status Tells".
    // PlayerStats.SetStats passes the vehicle's config in; until then the Resources default is used.
    private const string DefaultConfigPath = "Profiles/Vehicles/DefaultVehicleConfig";
    private static VehicleConfig s_defaultConfig;
    private VehicleConfig _config;

    private VehicleConfig C
    {
        get
        {
            if (_config != null) return _config;
            if (s_defaultConfig == null)
            {
                s_defaultConfig = Resources.Load<VehicleConfig>(DefaultConfigPath);
                if (s_defaultConfig == null)
                {
                    Debug.LogError($"[Fuel] No VehicleConfig at Resources/{DefaultConfigPath}; using field defaults.");
                    s_defaultConfig = ScriptableObject.CreateInstance<VehicleConfig>();
                }
            }
            return s_defaultConfig;
        }
    }

    public void SetConfig(VehicleConfig config) => _config = config;

    private bool _refueling;

    /// <summary>Set by the vehicle while it is passively refueling (inside a gravity void).</summary>
    public void SetRefueling(bool refueling) => _refueling = refueling;

    private bool IsLowFuel => _hasLastRatio && _lastRatio > 0f && _lastRatio < C.lowFuelThreshold01;
    private bool HasStatus => IsLowFuel || _refueling;

    // Rest color with status tints applied; the change FX lerp away from this instead of the
    // authored base color, so a drain shake still reads on top of the low-fuel pulse.
    private Color StatusRestColor(out float scaleMul)
    {
        Color c = _baseFillColor;
        scaleMul = 1f;
        if (IsLowFuel)
        {
            float urgency = 1f - _lastRatio / C.lowFuelThreshold01;
            float hz = Mathf.Lerp(C.lowFuelPulseHz, C.lowFuelPulseHzEmpty, urgency);
            float p = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * hz * Mathf.PI * 2f);
            c = Color.Lerp(c, C.lowFuelGlowColor, C.fuelBarLowFuelTintMax * p);
            scaleMul *= 1f + C.fuelBarLowFuelScalePulse * p;
        }
        if (_refueling)
        {
            float p = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * C.fuelBarRefuelShimmerHz * Mathf.PI * 2f);
            c = Color.Lerp(c, C.voidRefuelGlowColor, C.fuelBarRefuelTintMax * (0.4f + 0.6f * p));
            scaleMul *= 1f + C.fuelBarRefuelScalePulse * p;
        }
        c.a = _baseFillColor.a;
        return c;
    }

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
            _intensity = Mathf.Max(_intensity, Mathf.Lerp(C.fuelBarMinResistanceIntensity, C.fuelBarMaxResistanceIntensity, weight01));
        }
        else if (_hasLastRatio)
        {
            float delta = ratio - _lastRatio;
            if (Mathf.Abs(delta) > C.fuelBarChangeEpsilon)
            {
                _gaining = delta > 0f;
                float triggered = Mathf.Clamp01(Mathf.Abs(delta) / Mathf.Max(0.0001f, C.fuelBarIntensityPerFullDelta));
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
            if (HasStatus)
            {
                // No change FX running, but a status (low fuel / refueling) owns the rest look.
                _atRestVisual = false;
                ShakeDegreesZ = 0f;
                Color rest = StatusRestColor(out float restScale);
                if (_rect != null) _rect.localScale = _baseScale * restScale;
                if (fuelFill != null) fuelFill.color = rest;
            }
            else if (!_atRestVisual) SnapToRest();
            return;
        }

        _atRestVisual = false;
        _intensity = Mathf.MoveTowards(_intensity, 0f, C.fuelBarIntensityDecayPerSecond * Time.deltaTime);

        // Heavier resistance judders slower and bigger; lighter resistance buzzes fast and small.
        // Peak wobble degrees are driven directly by _activeDrainWeight (mirroring frequency) so
        // amplitude is explicitly tunable rather than an incidental side effect of _intensity,
        // which is shared with (and calibrated for) the squeeze/tint effect below.
        float freq;
        float shakeDegPeak;
        float shakeEnvelope01;
        if (_gaining)
        {
            freq = C.fuelBarGainShakeFrequency;
            shakeDegPeak = C.fuelBarGainShakeDegrees;
            shakeEnvelope01 = Mathf.Clamp01(_intensity);
        }
        else
        {
            freq = Mathf.Lerp(C.fuelBarLightDrainShakeFrequency, C.fuelBarHeavyDrainShakeFrequency, _activeDrainWeight);
            shakeDegPeak = C.fuelBarShakeByResistance.Evaluate(_activeDrainWeight);
            // Normalize by this weight's own achievable _intensity peak so the envelope reaches
            // 1 while sustained regardless of material — otherwise light materials (whose
            // _intensity peak sits well under 1) would be doubly suppressed by both a small
            // shakeDegPeak AND a perpetually-partial envelope.
            float peakIntensityForWeight = Mathf.Max(0.0001f, Mathf.Lerp(C.fuelBarMinResistanceIntensity, C.fuelBarMaxResistanceIntensity, _activeDrainWeight));
            shakeEnvelope01 = Mathf.Clamp01(_intensity / peakIntensityForWeight);
        }
        _shakePhase += Time.deltaTime * freq * Mathf.PI * 2f;

        ShakeDegreesZ = Mathf.Sin(_shakePhase) * shakeDegPeak * shakeEnvelope01;

        // Scale multiplies directly by _intensity (not Mathf.Lerp'd), so a heavy drain that
        // pushed intensity above 1 genuinely swells/shakes further than a normal-weight event.
        Color restColor = StatusRestColor(out float statusScale);
        if (_rect != null)
        {
            float scaleOffset = (_gaining ? C.fuelBarSwellScale : -C.fuelBarSqueezeScale) * _intensity;
            _rect.localScale = _baseScale * (1f + scaleOffset) * statusScale;
        }

        if (fuelFill != null)
        {
            float tintLerpAmount = _gaining
                ? C.fuelBarMaxTintLerp
                : C.fuelBarMaxTintLerp * Mathf.Lerp(C.fuelBarMinHeavyTintMul, 1f, _activeDrainWeight);
            Color target = Color.Lerp(restColor, _gaining ? C.fuelBarGainTint : C.fuelBarDrainTint, tintLerpAmount);
            fuelFill.color = Color.Lerp(restColor, target, Mathf.Clamp01(_intensity));
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
