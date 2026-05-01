using System;
using UnityEngine;

public partial class CosmicDust
{
    [Serializable]
    public struct NoseCompressionSettings
    {
        [Header("Vehicle Nose Compression")]
        public bool enabled;
        [Range(0f, 0.75f)] public float compressAmount;
        [Range(0f, 0.5f)] public float bulgeAmount;
        [Tooltip("Maximum world-space visual offset while compressed.")]
        [Min(0f)] public float maxOffsetWorld;
        [Tooltip("Distance from vehicle center used to sample nose contact.")]
        [Min(0f)] public float probeWorld;
        [Tooltip("Vehicle speed (world units/sec) that yields full compression target.")]
        [Min(0.01f)] public float speedForFull;
        [Range(0f, 1f)] public float boostBonus;
        [Min(0f)] public float contactGraceSeconds;
        [Min(0f)] public float minimumVisibleSeconds;
        [Tooltip("Lower = slower, more cushioned. Higher = snappier.")]
        [Min(0.01f)] public float attackSharpness;
        [Min(0.01f)] public float releaseSharpness;
    }

    [SerializeField] private NoseCompressionSettings noseCompression = new NoseCompressionSettings
    {
        enabled = true,
        compressAmount = 0.22f,
        bulgeAmount = 0.10f,
        maxOffsetWorld = 0.16f,
        probeWorld = 0.55f,
        speedForFull = 10f,
        boostBonus = 0.20f,
        contactGraceSeconds = 0.075f,
        minimumVisibleSeconds = 0.050f,
        attackSharpness = 10f,
        releaseSharpness = 16f
    };

    private void DriveVehicleCompression(Vehicle vehicle, Collision2D collision)
    {
        if (!noseCompression.enabled || vehicle == null || visual.sprite == null) return;
        if (_isDespawned) return;
        if (_dustSpriteBaseVisualScale.sqrMagnitude <= 0.000001f) return;

        Vector2 dustCenter = visual.sprite.bounds.center;
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
        if (visual.sprite != null) visual.sprite.transform.localScale = scale;
    }

    private void TickVehicleCompression()
    {
        if (!noseCompression.enabled || visual.sprite == null) return;

        Transform srt = visual.sprite.transform;
        float now = Time.time;
        bool recentlyTouched = (now - _lastNoseContactTime) <= noseCompression.contactGraceSeconds;
        bool stillVisible = now <= _noseVisibleUntil;
        float desired01 = (recentlyTouched || stillVisible) ? _noseCompressTarget01 : 0f;

        float sharpness = (desired01 > _noseCompressCurrent01) ? noseCompression.attackSharpness : noseCompression.releaseSharpness;
        float lerp = 1f - Mathf.Exp(-sharpness * Time.deltaTime);
        _noseCompressCurrent01 = Mathf.Lerp(_noseCompressCurrent01, desired01, lerp);

        if (desired01 <= 0.0001f && _noseCompressCurrent01 <= 0.001f) _noseCompressCurrent01 = 0f;
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
}
