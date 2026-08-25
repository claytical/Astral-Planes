using System;
using System.Collections;
using UnityEngine;

public partial class CosmicDust
{
    private Vector3 _dustSpriteBaseVisualScale = Vector3.one;
    private Vector3 _dustSpriteBaseLocalPos;
    private Vector3 _noseVisualOffsetLocal = Vector3.zero;
    private Vector2 _noseCompressDirWorld = Vector2.up;
    private float _noseCompressTarget01 = 0f;
    private float _noseCompressCurrent01 = 0f;
    private float _lastNoseContactTime = -999f;
    private float _noseVisibleUntil = -999f;

    private bool _noseNoiseBaseCaptured;
    private float _noseBaseNoiseStrengthMin;
    private float _noseBaseNoiseStrengthMax;
    private bool _noseCompressWasActive;

    [SerializeField] private NoseCompressionSettings noseCompression = new()
    {
        enabled = true,
        // Dialed back from an earlier pass that over-relied on this transform-level squash --
        // deforming the whole particle system as one rigid shape reads as "one uniform entity"
        // being pushed. Particle Reactivity below now carries more of the "many small things
        // being disturbed" feel via noise/emission, so the shape deformation only needs to
        // communicate roughly where contact is happening, not sell the disturbance by itself.
        compressAmount = 0.28f,
        bulgeAmount = 0.12f,
        maxOffsetWorld = 0.20f,
        probeWorld = 0.55f,
        speedForFull = 10f,
        boostBonus = 0.20f,
        contactGraceSeconds = 0.35f,
        minimumVisibleSeconds = 0.050f,
        attackSharpness = 10f,
        releaseSharpness = 20f,
        noiseBurstMultiplier = 2.2f,
        emissionBurstPeakMultiplier = 2.5f,
        emissionBurstAttackSeconds = 0.06f,
        emissionBurstDecaySeconds = 0.25f
    };

    private void DriveVehicleCompression(Vehicle vehicle, Collision2D collision)
    {
        if (!noseCompression.enabled || vehicle == null || visual.particleSystem == null) return;
        if (_isDespawned) return;
        if (_dustSpriteBaseVisualScale.sqrMagnitude <= 0.000001f) return;

        Vector2 dustCenter = transform.position;
        Vector2 noseWorld = (Vector2)vehicle.transform.position + (Vector2)vehicle.transform.up * noseCompression.probeWorld;

        Vector2 dir = Vector2.zero;
        Vector2 toNose = noseWorld - dustCenter;
        if (toNose.sqrMagnitude > 0.0001f) dir = toNose.normalized;
        else if (collision != null && collision.contactCount > 0)
        {
            Vector2 contact = collision.GetContact(0).point;
            Vector2 toContact = contact - dustCenter;
            if (toContact.sqrMagnitude > 0.0001f) dir = toContact.normalized;
        }

        if (dir.sqrMagnitude > 0.0001f) _noseCompressDirWorld = dir;

        float speed01 = 0f;
        if (vehicle.rb != null)
            speed01 = Mathf.Clamp01(vehicle.rb.linearVelocity.magnitude / Mathf.Max(0.01f, noseCompression.speedForFull));

        float boostBonus = vehicle.boosting ? noseCompression.boostBonus : 0f;
        _noseCompressTarget01 = Mathf.Clamp01(Mathf.Max(0.18f, speed01 + boostBonus));
        _lastNoseContactTime = Time.time;
        _noseVisibleUntil = Mathf.Max(_noseVisibleUntil, Time.time + noseCompression.minimumVisibleSeconds);
    }

    private void SetBaseSpriteScale(Vector3 scale)
    {
        _dustSpriteBaseVisualScale = scale;
        if (visual.particleSystem != null) visual.particleSystem.transform.localScale = scale;
    }

    private void TickVehicleCompression()
    {
        if (!noseCompression.enabled || visual.particleSystem == null) return;

        Transform srt = visual.particleSystem.transform;
        float now = Time.time;
        bool recentlyTouched = (now - _lastNoseContactTime) <= noseCompression.contactGraceSeconds;
        bool stillVisible = now <= _noseVisibleUntil;
        float desired01 = (recentlyTouched || stillVisible) ? _noseCompressTarget01 : 0f;

        float sharpness = (desired01 > _noseCompressCurrent01) ? noseCompression.attackSharpness : noseCompression.releaseSharpness;
        float lerp = 1f - Mathf.Exp(-sharpness * Time.deltaTime);
        _noseCompressCurrent01 = Mathf.Lerp(_noseCompressCurrent01, desired01, lerp);

        if (desired01 <= 0.0001f && _noseCompressCurrent01 <= 0.001f) _noseCompressCurrent01 = 0f;

        TickCollisionParticleReactivity(desired01);

        if (_noseCompressCurrent01 <= 0.0001f)
        {
            _noseVisualOffsetLocal = Vector3.zero;
            srt.localScale = _dustSpriteBaseVisualScale;
            srt.localPosition = _dustSpriteBaseLocalPos;
            return;
        }

        if (_dustSpriteBaseVisualScale.sqrMagnitude <= 0.000001f)
        {
            srt.localScale = _dustSpriteBaseVisualScale;
            srt.localPosition = _dustSpriteBaseLocalPos;
            return;
        }

        Vector2 dirLocal2 = transform.InverseTransformDirection(_noseCompressDirWorld.normalized);
        if (dirLocal2.sqrMagnitude < 0.0001f) dirLocal2 = Vector2.up;
        dirLocal2.Normalize();

        Vector2 perpLocal2 = new Vector2(-dirLocal2.y, dirLocal2.x);
        float squash = noseCompression.compressAmount * _noseCompressCurrent01;
        float bulge = noseCompression.bulgeAmount * _noseCompressCurrent01;

        float sx = 1f - squash * Mathf.Abs(Vector2.Dot(dirLocal2, Vector2.right)) + bulge * Mathf.Abs(Vector2.Dot(perpLocal2, Vector2.right));
        float sy = 1f - squash * Mathf.Abs(Vector2.Dot(dirLocal2, Vector2.up)) + bulge * Mathf.Abs(Vector2.Dot(perpLocal2, Vector2.up));

        Vector3 deformationScale = new Vector3(Mathf.Max(0.1f, sx), Mathf.Max(0.1f, sy), 1f);
        Vector3 worldOffset = (Vector3)(_noseCompressDirWorld.normalized * (noseCompression.maxOffsetWorld * _noseCompressCurrent01));
        _noseVisualOffsetLocal = transform.InverseTransformVector(worldOffset);

        srt.localScale = Vector3.Scale(_dustSpriteBaseVisualScale, deformationScale);
        srt.localPosition = _dustSpriteBaseLocalPos + _noseVisualOffsetLocal;
    }

    // Particle-level contact feedback -- this is what should actually sell "hundreds of
    // individual motes reacting," not the whole-transform squash above (which, alone, reads as
    // one rigid shape being pushed regardless of how many particles it contains). Noise
    // turbulence ramps with the same eased contact envelope driving the squash, and a brief
    // emission-rate spike fires once per new contact so particles visibly scatter at the
    // moment of impact.
    private void TickCollisionParticleReactivity(float desired01)
    {
        // Covers both "targeting active" and "still easing down from active" so noise decays
        // along the same curve as the shape squash, instead of snapping off the instant
        // desired01 returns to 0 (which happens well before _noseCompressCurrent01 catches up).
        bool activeNow = desired01 > 0.01f || _noseCompressCurrent01 > 0.0001f;
        bool risingEdge = activeNow && !_noseCompressWasActive;
        bool fallingEdge = !activeNow && _noseCompressWasActive;
        _noseCompressWasActive = activeNow;

        if (risingEdge)
            TriggerContactEmissionBurst();

        // Skip the native noise.strength write entirely for idle cells (the common case across
        // a maze of hundreds of cells) -- only pay this cost while a cell is actually reacting,
        // plus one final write on the falling edge to reset back to baseline.
        if (!activeNow && !fallingEdge) return;

        var noise = visual.particleSystem.noise;
        if (!noise.enabled) return;

        if (!_noseNoiseBaseCaptured)
        {
            var s = noise.strength;
            bool twoConst = s.mode == ParticleSystemCurveMode.TwoConstants;
            _noseBaseNoiseStrengthMin = twoConst ? s.constantMin : s.constant;
            _noseBaseNoiseStrengthMax = twoConst ? s.constantMax : s.constant;
            _noseNoiseBaseCaptured = true;
        }

        float burstMul = activeNow ? Mathf.Lerp(1f, noseCompression.noiseBurstMultiplier, _noseCompressCurrent01) : 1f;
        noise.strength = new ParticleSystem.MinMaxCurve(_noseBaseNoiseStrengthMin * burstMul, _noseBaseNoiseStrengthMax * burstMul);
    }

    // Shares the same tracked emission-multiplier lane as TriggerCarveEmissionSpike
    // (CosmicDust.Collision.cs) -- the two never fire in the same instant in practice (carve is
    // boost-only and fires from OnCollisionStay2D's boost branch, this fires from the contact
    // envelope that also covers non-boost bumps), and if they did, the later call simply wins.
    private void TriggerContactEmissionBurst()
    {
        if (_emissionMulRoutine != null) { StopCoroutine(_emissionMulRoutine); _emissionMulRoutine = null; }
        _emissionMulRoutine = StartCoroutine(ContactEmissionBurstRoutine());
    }

    private IEnumerator ContactEmissionBurstRoutine()
    {
        yield return _particles.LerpEmissionMultiplier(_particles.CurrentMultiplier, noseCompression.emissionBurstPeakMultiplier, noseCompression.emissionBurstAttackSeconds);
        yield return _particles.LerpEmissionMultiplier(noseCompression.emissionBurstPeakMultiplier, 1f, noseCompression.emissionBurstDecaySeconds);
        _emissionMulRoutine = null;
    }
}
