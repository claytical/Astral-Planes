using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

[DisallowMultipleComponent]
public sealed class PhaseStarDustAffect : MonoBehaviour
{
    [Header("Star Keep-Clear (Option A: no destruction)")]
    [SerializeField] private float keepClearTick = 0.10f; // seconds between updates (cheap)

    private float _timer;
    private PhaseStarBehaviorProfile _profile;
    private MazeArchetype _lastPhase; // fallback for clearing
    private bool _hasLastPhase;
    [SerializeField] private float drainRatePerSec = 0.60f;     // drain dust alpha (charge) per second in contact
    [SerializeField] private float carveThreshold = 0.05f;      // begin carving when charge <= threshold
    [SerializeField] private float carveHoldSec = 0.50f;        // require sustained contact under threshold
    private PhaseStar _star;
    private PhaseStarCravingNavigator _navigator;
    private readonly Dictionary<Vector2Int, float> _underThresholdTime = new();

// Reused buffers to avoid allocations
    private readonly List<Vector2Int> _touchedCells = new();
    private readonly List<Vector2Int> _staleKeys = new();
    private readonly HashSet<Vector2Int> _touchedSet = new();
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

// … inside Update() after tick gating and after you’ve fetched gfm/gen/drums …

var drums = gfm.activeDrumTrack;
if (drums == null) return;

float cellWorld = Mathf.Max(0.001f, drums.GetCellWorldSize());
int radiusCells = Mathf.Max(0, _profile.starKeepClearRadiusCells);
Vector2Int center = drums.WorldToGridPosition(transform.position);

// 1) Keep-clear CLAIMS ONLY (no forced destruction)
gen.SetStarKeepClear(center, radiusCells, _hasLastPhase ? _lastPhase : MazeArchetype.Establish, forceRemoveExisting: false);

// 2) Drain → Carve
float dt = keepClearTick;                 // deterministic tick time
float drainAmount = drainRatePerSec * dt;

_touchedCells.Clear();
_touchedSet.Clear();

int rSq = radiusCells * radiusCells;

for (int dy = -radiusCells; dy <= radiusCells; dy++)
{
    for (int dx = -radiusCells; dx <= radiusCells; dx++)
    {
        int dSq = dx * dx + dy * dy;
        if (dSq > rSq) continue;

        Vector2Int gp = new Vector2Int(center.x + dx, center.y + dy);

        if (!gen.TryGetCellGo(gp, out var go) || go == null) continue;
        if (!go.TryGetComponent<CosmicDust>(out var dust) || dust == null) continue;

        _touchedCells.Add(gp);
        _touchedSet.Add(gp);

        // Drain alpha-charge
        float taken = dust.DrainCharge(drainAmount);
        if (taken > 0f && _star != null)
        {
            _star.AddCharge(dust.Role, taken);
            // Tell the craving navigator what role was just eaten.
            // If it differs from the current craving, the navigator will pivot.
            _navigator?.NotifyDustEaten(dust.Role);
        }

        // Carve only after sustained depleted contact
        if (dust.Charge01 <= carveThreshold)
        {
            _underThresholdTime.TryGetValue(gp, out float t);
            t += dt;
            _underThresholdTime[gp] = t;

            if (t >= carveHoldSec)
            {
                gen.ClearCell(
                    gp,
                    CosmicDustGenerator.DustClearMode.FadeAndHide,
                    fadeSeconds: 0.20f,
                    scheduleRegrow: true,
                    phase: _hasLastPhase ? _lastPhase : MazeArchetype.Establish
                );
            }
        }
        else
        {
            _underThresholdTime.Remove(gp);
        }
    }
}

// Age out entries we didn’t touch this tick
if (_underThresholdTime.Count > 0)
{
    _staleKeys.Clear();
    foreach (var kvp in _underThresholdTime)
        if (!_touchedSet.Contains(kvp.Key))
            _staleKeys.Add(kvp.Key);

    for (int i = 0; i < _staleKeys.Count; i++)
        _underThresholdTime.Remove(_staleKeys[i]);
}
    }

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star, PhaseStarCravingNavigator navigator = null)
     {
         _profile = profile;
         _star = star;
         _navigator = navigator;


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
            gen.ClearStarKeepClear(_hasLastPhase ? _lastPhase : MazeArchetype.Establish);
        };
    }
}
