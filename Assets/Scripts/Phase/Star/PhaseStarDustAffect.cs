using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Handles the PhaseStar's interaction with the cosmic dust field.
///
/// Two independent systems:
///
/// 1) Keep-clear pocket (optional, controlled by profile.starKeepsDustClear):
///    Claims a small radius around the star so regrowth won't fill in
///    the immediate pocket. Does NOT destroy dust — just holds a claim.
///    Runs on keepClearTick interval for cheap steady-state maintenance.
///
/// 2) Drain probe (always active):
///    Every drainTick, samples a probe cell directly ahead of the star's
///    travel direction (plus the cell the star occupies). Drains charge
///    from any dust found and feeds that charge to PhaseStar.AddCharge().
///    The star moves into the dust field unobstructed because we ignore
///    physics collisions per-collider (not per-layer) on Initialize so the
///    vehicle's dust collisions are unaffected.
///    When the immediately-ahead cell is fully drained, the star's motion
///    naturally carries it to the next cell — rummaging behaviour emerges
///    without extra steering logic.
/// </summary>
[DisallowMultipleComponent]
public sealed class PhaseStarDustAffect : MonoBehaviour
{
    // ---------------------------------------------------------------
    // Inspector
    // ---------------------------------------------------------------
    [Header("Keep-Clear Pocket (optional)")]
    [SerializeField] private float keepClearTick = 0.15f;

    [Header("Drain Probe")]
    [Tooltip("How often the forward-probe drain fires (seconds). Lower = faster per-cell drain.")]
    [SerializeField] private float drainTick = 0.08f;

    [Tooltip("Charge drained per second from each contacted dust cell.")]
    [SerializeField] private float drainRatePerSec = 1.8f;

    [Tooltip("How many cells ahead of travel direction to probe for dust. 1 = immediate neighbour only.")]
    [SerializeField, Range(1, 4)] private int probeDepthCells = 2;

    [Tooltip("Layer mask selecting CosmicDust physics colliders. Set this to the Dust layer in the inspector.")]
    [SerializeField] private LayerMask dustLayerMask = 0;

    [Tooltip("Radius around the star used to find nearby dust colliders to ignore on startup (world units). Should be >= half the play area diagonal.")]
    [SerializeField] private float dustIgnoreSearchRadius = 40f;

    [Tooltip("Max dust colliders to ignore in the initial sweep.")]
    [SerializeField] private int dustIgnoreMaxHits = 256;

    // ---------------------------------------------------------------
    // State
    // ---------------------------------------------------------------
    private float _keepClearTimer;
    private float _drainTimer;

    private PhaseStarBehaviorProfile _profile;
    private MazeArchetype _lastPhase;
    private bool _hasLastPhase;

    private PhaseStar _star;
    private Rigidbody2D _rb;
    private Collider2D[] _myColliders;
    private readonly HashSet<int> _ignoredDustColliderIds = new HashSet<int>();
    private readonly Collider2D[] _overlapScratch = new Collider2D[256];

    private Vector2 _lastPos;
    [SerializeField] private float starDrainRegrowDelay = 6f;

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile    = profile;
        _star       = star;
        _rb         = GetComponent<Rigidbody2D>();
        _lastPos    = transform.position;
        _myColliders = GetComponentsInChildren<Collider2D>(true);

        // --- Per-collider dust ignore ---
        // We ignore collision between the star's specific colliders and nearby dust
        // colliders, rather than using IgnoreLayerCollision which would also suppress
        // the vehicle's dust interactions.
        IgnoreNearbyDustColliders();


        star.OnArmed += _ =>
        {
            var gfm = GameFlowManager.Instance;
            if (gfm?.activeDrumTrack != null)
                gfm.activeDrumTrack.isPhaseStarActive = true;
        };

        star.OnDisarmed += _ =>
        {
            var gfm = GameFlowManager.Instance;
            if (gfm?.activeDrumTrack != null)
                gfm.activeDrumTrack.isPhaseStarActive = false;

            var gen = gfm?.dustGenerator;
            if (gen != null)
                gen.ClearStarKeepClear(_hasLastPhase ? _lastPhase : MazeArchetype.Windows);
        };
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        // ---------------------------------------------------------------
        // 1) Keep-clear pocket
        // ---------------------------------------------------------------
        _keepClearTimer += dt;
        if (_keepClearTimer >= keepClearTick)
        {
            _keepClearTimer = 0f;
            TickKeepClear();
        }

        // ---------------------------------------------------------------
        // 2) Drain probe
        // ---------------------------------------------------------------
        _drainTimer += dt;
        if (_drainTimer >= drainTick)
        {
            _drainTimer = 0f;
            TickDrain();
        }
    }

    // ---------------------------------------------------------------
    // Dust collision ignore (per-collider, not per-layer)
    // ---------------------------------------------------------------

    /// <summary>
    /// Overlaps a large circle around the star and calls Physics2D.IgnoreCollision
    /// between each found dust collider and each of the star's own colliders.
    /// Safe to call repeatedly — already-ignored pairs are idempotent.
    /// New dust spawned after Initialize is caught by the keep-clear tick sweep below.
    /// </summary>
    private void IgnoreNearbyDustColliders()
    {
        if (_myColliders == null || _myColliders.Length == 0) return;
        if (dustLayerMask == 0) return;

        int count = Physics2D.OverlapCircleNonAlloc(
            transform.position, dustIgnoreSearchRadius, _overlapScratch, dustLayerMask);

        for (int i = 0; i < count && i < _overlapScratch.Length; i++)
        {
            var dustCol = _overlapScratch[i];
            if (dustCol == null) continue;
            int id = dustCol.GetInstanceID();
            if (_ignoredDustColliderIds.Contains(id)) continue;

            for (int j = 0; j < _myColliders.Length; j++)
            {
                var mine = _myColliders[j];
                if (mine == null || !mine.enabled) continue;
                Physics2D.IgnoreCollision(mine, dustCol, true);
            }
            _ignoredDustColliderIds.Add(id);
        }
    }

    // ---------------------------------------------------------------
    // Keep-clear
    // ---------------------------------------------------------------
    private void TickKeepClear()
    {
        if (_profile == null || !_profile.starKeepsDustClear) return;

        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drums = gfm?.activeDrumTrack;
        if (gen == null || drums == null) return;

        var phaseMgr = gfm.phaseTransitionManager;
        if (phaseMgr != null) { _lastPhase = phaseMgr.currentPhase; _hasLastPhase = true; }

        int radiusCells = Mathf.Max(0, _profile.starKeepClearRadiusCells);
        Vector2Int center = drums.WorldToGridPosition(transform.position);

        gen.SetStarKeepClear(center, radiusCells,
            _hasLastPhase ? _lastPhase : MazeArchetype.Windows,
            forceRemoveExisting: false);

        // Re-sweep for any dust colliders spawned since Initialize
        IgnoreNearbyDustColliders();
    }

    // ---------------------------------------------------------------
    // Drain probe
    // ---------------------------------------------------------------
    private void TickDrain()
    {
        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drums = gfm?.activeDrumTrack;
        if (gen == null || drums == null) return;

        // Pick up any dust colliders that have regrown since last sweep
        IgnoreNearbyDustColliders();

        Vector2 pos = transform.position;

        // Travel direction: prefer Rigidbody velocity, fall back to position delta
        Vector2 travelDir = Vector2.zero;
        if (_rb != null && _rb.linearVelocity.sqrMagnitude > 0.01f)
            travelDir = _rb.linearVelocity.normalized;
        else
        {
            Vector2 delta = pos - _lastPos;
            if (delta.sqrMagnitude > 0.0001f) travelDir = delta.normalized;
        }

        float cellSize    = Mathf.Max(0.001f, drums.GetCellWorldSize());
        float drainAmount = drainRatePerSec * drainTick;

        // Build probe cells: current cell, then step forward along travel direction
        Vector2Int baseCell = drums.WorldToGridPosition(pos);

        for (int depth = 0; depth <= probeDepthCells; depth++)
        {
            Vector2Int probeCell;
            if (depth == 0)
            {
                probeCell = baseCell;
            }
            else if (travelDir.sqrMagnitude > 0.001f)
            {
                Vector2 probeWorld = pos + travelDir * (cellSize * depth);
                probeCell = drums.WorldToGridPosition(probeWorld);
            }
            else
            {
                break; // no travel direction — only drain current cell
            }

            if (!gen.HasDustAt(probeCell)) continue;
            if (!gen.TryGetCellGo(probeCell, out var go) || go == null) continue;
            if (!go.TryGetComponent<CosmicDust>(out var dustCell) || dustCell == null) continue;
            if (dustCell.Charge01 <= 0f) continue;
            float taken = dustCell.DrainCharge(drainAmount);
            if (taken > 0f && _star != null)
                _star.AddCharge(dustCell.Role, taken);

            if (dustCell.Charge01 <= 0f)
            {
                gen.ClearImprintAt(probeCell);
                var phaseMgr = gfm.phaseTransitionManager;
                MazeArchetype phase = phaseMgr != null ? phaseMgr.currentPhase : MazeArchetype.Windows;
                gen.ClearCell(probeCell, CosmicDustGenerator.DustClearMode.FadeAndHide,
                    fadeSeconds: 0.4f,
                    scheduleRegrow: true,
                    phase: phase,
                    regrowDelaySeconds: starDrainRegrowDelay);
            }
            // Keep probing deeper — dense patches should feed the star continuously
            // as it pushes in, not just from the leading edge.
        }

        _lastPos = pos;
    }
    /// <summary>
    /// Re-applies per-collider dust ignore after the star's own colliders have been
    /// re-enabled (e.g. after ArmNext calls EnableColliders). Unity clears IgnoreCollision
    /// pairs when a collider is disabled/re-enabled, so this must be called after any
    /// EnableColliders() call on the star.
    /// </summary>
    public void RefreshDustIgnore()
    {
        _myColliders = GetComponentsInChildren<Collider2D>(true);
        _ignoredDustColliderIds.Clear();
        IgnoreNearbyDustColliders();
    }
}
