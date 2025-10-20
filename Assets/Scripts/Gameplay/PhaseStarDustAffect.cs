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
    [SerializeField] private float erodeTick = 0.15f; // seconds
    private float _erodeTimer;

    [SerializeField] private float appetiteMul = 1f;   // can be driven from behaviorProfile

    private void Update()
    {
        _erodeTimer += Time.deltaTime;
        if (_erodeTimer >= erodeTick)
        {
            _erodeTimer = 0f;

            float appetite = appetiteMul;
            // if you have PhaseStarBehaviorProfile fields, blend them here:
            // appetite *= behaviorProfile != null ? behaviorProfile.appetite : 1f;

            GameFlowManager.Instance?.dustGenerator?.ErodeDustDisk(transform.position, appetite);
        }
    }

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
    Collider2D[] GetBuffer()
    {
        // Allocate once on first use
        if (_hits != null) return _hits;
        return (Collider2D[])typeof(PhaseStarDustAffect).GetField("_hits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(this) ?? new Collider2D[maxHits];
    }

}