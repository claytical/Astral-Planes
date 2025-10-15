using UnityEngine;

[DisallowMultipleComponent]
public sealed class PhaseStarDustAffect : MonoBehaviour
{
    PhaseStarBehaviorProfile _profile;
    bool _enabled;
    [Header("Detection")]
    [SerializeField] LayerMask dustLayer = ~0;  // set to your Dust layer in the prefab
    [SerializeField] int maxHits = 64;

    // cached from profile
    float _radius;
    float _unitsPerSec;
    AnimationCurve _falloff;
    bool  _feedsDust;
    float _regrowDelayMul;

    // working
    readonly Collider2D[] _hits = null;
    bool _active;

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        // Pull from Behavior Profile
        _radius         = profile ? profile.dustShrinkRadius       : 6f;
        _unitsPerSec    = profile ? profile.dustShrinkUnitsPerSec  : 1.2f;
        _falloff        = profile ? profile.dustFalloff            : AnimationCurve.Linear(0,1,1,0);
        _feedsDust      = profile && profile.feedsDust;
        _regrowDelayMul = profile ? Mathf.Max(0.01f, profile.dustRegrowDelayMul) : 1f;

        // Minimal event wiring from PhaseStar
        star.OnArmed += s => { GameFlowManager.Instance.activeDrumTrack.isPhaseStarActive = true;  };
        star.OnDisarmed += s => { GameFlowManager.Instance.activeDrumTrack.isPhaseStarActive = false; };

    }
    void Update()
    {
        if (!_active) return;

        // Non-alloc overlap
        var count = Physics2D.OverlapCircleNonAlloc(transform.position, _radius, GetBuffer(), dustLayer);
        for (int i = 0; i < count; i++)
        {
            var col = _hits[i];
            if (!col) continue;

            var dust = col.GetComponent<CosmicDust>();
            if (!dust) continue;

            float d   = Vector2.Distance(transform.position, dust.transform.position);
            float t   = Mathf.Clamp01(1f - (d / Mathf.Max(0.0001f, _radius)));
            float mul = _falloff != null ? _falloff.Evaluate(t) : t;

            if (_feedsDust)
            {
                // DarkStar-like behavior: do NOT shrink. Ensure it can regrow (or lingers longer).
                // Nudge its regrow timing characteristics without forcing immediate growth.
                dust.starRemovesWithoutRegrow = false;      // allow generator-led regrow
                dust.regrowDelay = Mathf.Max(0f, dust.regrowDelay * _regrowDelayMul);
                // Optional: you can add a gentle “undo” of shrink if you expose a Grow API.
                // (Current CosmicDust has ShrinkByPhaseStar; if you later add GrowByPhaseStar,
                // call it here with _unitsPerSec * mul.)
            }
            else
            {
                // Regular PhaseStar: prune dust near the star
                dust.starRemovesWithoutRegrow = true;       // fades without waiting for generator
                dust.ShrinkByPhaseStar(_unitsPerSec * mul); // frame-rate safe shrink
            }
        }
    }
    Collider2D[] GetBuffer()
    {
        // Allocate once on first use
        if (_hits != null) return _hits;
        return (Collider2D[])typeof(PhaseStarDustAffect).GetField("_hits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(this) ?? new Collider2D[maxHits];
    }

}