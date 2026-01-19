using UnityEngine;

[DisallowMultipleComponent]
public sealed class PhaseStarDustAffect : MonoBehaviour
{
    [Header("Star Keep-Clear (Option A: no destruction)")]
    [SerializeField] private float keepClearTick = 0.10f; // seconds between updates (cheap)

    private float _timer;
    private PhaseStarBehaviorProfile _profile;
    private MusicalPhase _lastPhase; // fallback for clearing
    private bool _hasLastPhase;

    private void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < keepClearTick) return;
        _timer = 0f;

        if (_profile == null) return;

        var gfm = GameFlowManager.Instance;
        var gen = gfm != null ? gfm.dustGenerator : null;
        if (gen == null) return;

        if (!_profile.starKeepsDustClear) return;

        // Prefer the active phase from PhaseTransitionManager; cache for disarm cleanup.
        var phaseMgr = gfm.phaseTransitionManager;
        if (phaseMgr != null)
        {
            _lastPhase = phaseMgr.currentPhase;
            _hasLastPhase = true;
        }
// Maintain a maneuvering pocket around the star without eroding the maze.
// NOTE: profile value is in CELLS; SetStarKeepClearWorld expects WORLD units.
        float cellWorld = (gfm.activeDrumTrack != null) ? Mathf.Max(0.001f, gfm.activeDrumTrack.GetCellWorldSize()) : 1f;
        float radiusWorld = _profile.starKeepClearRadiusCells * cellWorld;
        gen.SetStarKeepClearWorld(
            (Vector2)transform.position,
            radiusWorld,
            _hasLastPhase ? _lastPhase : MusicalPhase.Establish
        );
        
    }

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile = profile;

        // Keep the simple drum flag wiring you already had
        star.OnArmed += s =>
        {
            var gfm = GameFlowManager.Instance;
            if (gfm != null && gfm.activeDrumTrack != null) gfm.activeDrumTrack.isPhaseStarActive = true;
        };

        star.OnDisarmed += s =>
        {
            var gfm = GameFlowManager.Instance;
            if (gfm != null && gfm.activeDrumTrack != null) gfm.activeDrumTrack.isPhaseStarActive = false;

            var gen = gfm != null ? gfm.dustGenerator : null;
            if (gen == null) return;

            // Release the keep-clear pocket so those cells can regrow normally.
            gen.ClearStarKeepClear(_hasLastPhase ? _lastPhase : MusicalPhase.Establish);
        };
    }
}
