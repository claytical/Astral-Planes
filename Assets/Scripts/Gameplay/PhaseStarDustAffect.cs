using UnityEngine;

[DisallowMultipleComponent]
public sealed class PhaseStarDustAffect : MonoBehaviour
{
    [SerializeField] private float erodeTick   = 0.15f; // seconds between bites
    [SerializeField] private float appetiteMul = 1f;    // global strength scaler

    private float _erodeTimer;

    private void Update()
    {
        _erodeTimer += Time.deltaTime;
        if (_erodeTimer < erodeTick) return;
        _erodeTimer = 0f;

        var gen = GameFlowManager.Instance?.dustGenerator;
        if (!gen) return;

        gen.ErodeDustDisk(transform.position, appetiteMul);
    }

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        // Optional: tie appetite to ONE profile scalar if you want
        // hue to also imply "hungriness". Otherwise leave it as 1
        // and just tune in the inspector.
        appetiteMul = profile ? profile.dustShrinkUnitsPerSec : 1f;

        // Keep the simple drum flag wiring you already had
        star.OnArmed    += s => { GameFlowManager.Instance.activeDrumTrack.isPhaseStarActive = true;  };
        star.OnDisarmed += s => { GameFlowManager.Instance.activeDrumTrack.isPhaseStarActive = false; };
    }
}