using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives PhaseStar movement as a committed hunter.
///
/// Every replan interval the navigator scans all colored (role != None) dust cells
/// directly from the grid, picks the nearest one (weighted by the star's hunger for
/// that role), and exposes it as the hunt direction.  No BFS required — the grid
/// query is O(cells) and immune to gray-dust blocking or budget exhaustion.
///
/// Lock-on (NotifyDraining / ClearLockOn) pins the target while a cell is actively
/// being drained so the star does not wander mid-drain.
/// </summary>
[DisallowMultipleComponent]
public sealed class PhaseStarCravingNavigator : MonoBehaviour
{
    // ----------------------------------------------------------------
    // Inspector
    // ----------------------------------------------------------------
    [Header("Hunter Targeting")]
    [Tooltip("Seconds between target re-evaluations when no lock-on is active.")]
    [SerializeField] private float replanIntervalSeconds = 0.3f;

    [Tooltip("If the current target cell loses its colored dust (eaten by vehicle, etc.), " +
             "pick a new target immediately rather than waiting for replan interval.")]
    [SerializeField] private bool retargetOnTargetLost = true;

    [Header("Sniffer (per-role diamond facing)")]
    [Tooltip("Seconds between sniffer ticks.")]
    [SerializeField] private float snifferIntervalSeconds = 0.25f;

    [Header("Hunger-Weighted Targeting")]
    [Tooltip("Weight given to a role the star is already full on (0 = never target it, 1 = no hunger bias).")]
    [SerializeField, Range(0f, 1f)] private float hungerFloorWeight = 0.15f;

    [Header("Debug")]
    [SerializeField] private bool verbose = false;

    // ----------------------------------------------------------------
    // State
    // ----------------------------------------------------------------
    private PhaseStar _star;
    private bool _active;
    private float _replanTimer;
    private float _snifferTimer;

    // Hunt target
    private bool       _hasTarget;
    private Vector2Int _targetCell;
    private Vector2    _targetDir;   // normalized world-space direction; refreshed each frame

    // Lock-on from PhaseStarDustAffect (draining a specific cell)
    private bool       _hasLockOnCell;
    private Vector2Int _lockOnCell;

    // Dominant role for sniffer blend (set externally by PhaseStar each frame)
    private MusicalRole _dominantRole = MusicalRole.None;

    // Sniffer: per-role direction toward nearest dust of that role
    private readonly Dictionary<MusicalRole, Vector2> _snifferDirs = new();
    private Vector2 _nearestColoredDustDir = Vector2.zero;

    // Reusable scratch list for grid queries
    private readonly List<Vector2Int> _coloredCellsScratch = new(512);

    // ----------------------------------------------------------------
    // Public API
    // ----------------------------------------------------------------

    public void Initialize(
        PhaseStar star,
        PhaseStarMotion2D motionComponent,
        MusicalRole ignoredInitialCraving,   // kept for call-site compat; unused
        PhaseStarBehaviorProfile profile)
    {
        if (profile != null)
            replanIntervalSeconds = Mathf.Max(0.1f, profile.mazeNavReplanInterval);

        _star          = star;
        _active        = true;
        _replanTimer   = 0f;   // hunt immediately on first Update
        _snifferTimer  = 0f;
        _hasTarget     = false;
        _hasLockOnCell = false;

        if (verbose) Debug.Log("[HuntNav] Initialized.");
    }


    public void SetActive(bool active)
    {
        _active = active;
        if (!active)
        {
            _hasTarget     = false;
            _hasLockOnCell = false;
        }
    }

    /// <summary>
    /// Called by PhaseStarDustAffect when it begins draining a specific cell.
    /// Pins the hunt target to this cell until it is fully drained.
    /// </summary>
    public void NotifyDraining(Vector2Int cell)
    {
        _hasLockOnCell = true;
        _lockOnCell    = cell;
        _hasTarget     = true;
        _targetCell    = cell;
        _replanTimer   = replanIntervalSeconds; // don't immediately replan over a fresh lock
    }

    /// <summary>
    /// Called by PhaseStarDustAffect when a cell is fully drained.
    /// Only clears if it matches the locked cell; triggers an immediate retarget.
    /// </summary>
    public void ClearLockOn(Vector2Int cell)
    {
        if (_hasLockOnCell && _lockOnCell == cell)
        {
            _hasLockOnCell = false;
            _replanTimer   = 0f; // hunt next cell immediately
        }
    }

    /// <summary>World-space direction toward the current hunt target, or zero if none.</summary>
    public Vector2 GetDensitySteerDir() => _hasTarget ? _targetDir : Vector2.zero;

    /// <summary>Direction toward the dominant shard's nearest dust, for diamond rotation.</summary>
    public Vector2 GetDominantSnifferDir()
    {
        if (_hasLockOnCell || _hasTarget)
            return _hasTarget ? _targetDir : Vector2.zero;

        if (_dominantRole != MusicalRole.None)
            return _snifferDirs.TryGetValue(_dominantRole, out var d) ? d : Vector2.zero;

        return _nearestColoredDustDir;
    }

    private void Update()
    {
        if (!_active) return;

        float dt = Time.deltaTime;

        RefreshTargetDir();

        // If the lock-on cell is no longer valid (e.g. externally carved by a vehicle
        // before the drain probe could call ClearLockOn), release the lock so HuntTick
        // can retarget. Without this, _hasLockOnCell stays true forever and HuntTick
        // always returns early, leaving the star stuck in drift mode.
        if (_hasLockOnCell && !IsTargetValid(_lockOnCell))
        {
            _hasLockOnCell = false;
            _hasTarget     = false;
            _replanTimer   = 0f;
        }
        else if (_hasTarget && retargetOnTargetLost && !IsTargetValid(_targetCell))
        {
            _hasTarget   = false;
            _replanTimer = 0f;
        }

        _replanTimer -= dt;
        if (_replanTimer <= 0f)
        {
            _replanTimer = replanIntervalSeconds;
            HuntTick();
        }

        _snifferTimer -= dt;
        if (_snifferTimer <= 0f)
        {
            _snifferTimer = snifferIntervalSeconds;
            SnifferTick();
        }
    }

    // ----------------------------------------------------------------
    // Hunt: scan all colored cells, pick nearest (hunger-weighted)
    // ----------------------------------------------------------------

    private void HuntTick()
    {
        // Stay committed to a cell that is actively being drained.
        if (_hasLockOnCell) return;

        var gfm  = GameFlowManager.Instance;
        var gen  = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        gen.GetColoredDustCells(_coloredCellsScratch);

        if (_coloredCellsScratch.Count == 0)
        {
            _hasTarget = false;
            if (verbose) Debug.Log("[HuntNav] No colored dust found.");
            return;
        }

        Vector2    starWorld    = transform.position;
        Vector2Int bestCell     = default;
        float      bestEffDist  = float.MaxValue;
        bool       found        = false;

        for (int i = 0; i < _coloredCellsScratch.Count; i++)
        {
            var cell = _coloredCellsScratch[i];
            var dust = GetDust(gen, cell);
            if (dust == null || dust.Role == MusicalRole.None) continue;

            float hunger = _star != null ? _star.GetRoleHunger(dust.Role) : 1f;
            float score  = Mathf.Lerp(hungerFloorWeight, 1f, hunger);

            Vector2 cellWorld = drum.GridToWorldPosition(cell);
            float   dist      = (cellWorld - starWorld).magnitude;
            // Divide distance by score so hungry roles appear effectively closer.
            float   effDist   = dist / score;

            if (effDist < bestEffDist)
            {
                bestCell    = cell;
                bestEffDist = effDist;
                found       = true;
            }
        }

        if (!found)
        {
            _hasTarget = false;
            return;
        }

        _hasTarget  = true;
        _targetCell = bestCell;
        RefreshTargetDir();
    }

    // ----------------------------------------------------------------
    // Sniffer: nearest cell per role (for diamond rotation)
    // ----------------------------------------------------------------

    private void SnifferTick()
    {
        var gfm  = GameFlowManager.Instance;
        var gen  = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        gen.GetColoredDustCells(_coloredCellsScratch);

        _snifferDirs.Clear();

        Vector2    starWorld        = transform.position;
        bool       foundAny         = false;
        float      nearestSqDist    = float.MaxValue;
        Vector2Int nearestCell      = default;

        // Scratch: nearest sqDist per role
        var nearestSqDistPerRole = new Dictionary<MusicalRole, float>(4);

        for (int i = 0; i < _coloredCellsScratch.Count; i++)
        {
            var cell = _coloredCellsScratch[i];
            var dust = GetDust(gen, cell);
            if (dust == null || dust.Role == MusicalRole.None) continue;

            Vector2 cellWorld = drum.GridToWorldPosition(cell);
            float   sqDist    = (cellWorld - starWorld).sqrMagnitude;

            // Per-role nearest
            if (!nearestSqDistPerRole.TryGetValue(dust.Role, out float best) || sqDist < best)
            {
                nearestSqDistPerRole[dust.Role] = sqDist;
                Vector2 dir = cellWorld - starWorld;
                if (dir.sqrMagnitude > 0.0001f)
                    _snifferDirs[dust.Role] = dir.normalized;
            }

            // Overall nearest
            if (sqDist < nearestSqDist)
            {
                nearestSqDist = sqDist;
                nearestCell   = cell;
                foundAny      = true;
            }
        }

        if (foundAny)
        {
            Vector2 cellWorld = drum.GridToWorldPosition(nearestCell);
            Vector2 dir = cellWorld - starWorld;
            if (dir.sqrMagnitude > 0.0001f) _nearestColoredDustDir = dir.normalized;
        }
        else
        {
            _nearestColoredDustDir = Vector2.zero;
        }
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private void RefreshTargetDir()
    {
        if (!_hasTarget) return;

        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum == null) return;

        Vector2 cellWorld = drum.GridToWorldPosition(_targetCell);
        Vector2 dir = cellWorld - (Vector2)transform.position;

        if (dir.sqrMagnitude > 0.0001f)
            _targetDir = dir.normalized;
        // else: star is on top of target; keep last direction until cell clears
    }

    private bool IsTargetValid(Vector2Int cell)
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null) return false;
        var dust = GetDust(gen, cell);
        return dust != null && dust.Role != MusicalRole.None && gen.HasDustAt(cell);
    }

    private static CosmicDust GetDust(CosmicDustGenerator gen, Vector2Int gp)
    {
        if (!gen.TryGetCellGo(gp, out var go) || go == null) return null;
        go.TryGetComponent<CosmicDust>(out var dust);
        return dust;
    }

    // ----------------------------------------------------------------
    // Gizmos
    // ----------------------------------------------------------------

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (_hasTarget)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(transform.position, _targetDir * 2f);

            var drum = GameFlowManager.Instance?.activeDrumTrack;
            if (drum != null)
            {
                Vector2 tw = drum.GridToWorldPosition(_targetCell);
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(tw, 0.25f);
            }
        }

        foreach (var kvp in _snifferDirs)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(transform.position, kvp.Value * 0.8f);
        }
    }
#endif
}
