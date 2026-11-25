using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PhaseStarCravingNavigator : MonoBehaviour
{
    [Tooltip("Shared PhaseStar tuning (Resources/Config/PhaseStarConfig). Unassigned = that asset is loaded.")]
    [SerializeField] private PhaseStarConfig config;
    private PhaseStarConfig _cfg;
    private PhaseStarConfig Cfg => _cfg ??= PhaseStarConfig.OrDefault(config, this);
    // Shared tuning (PhaseStarConfig → 4. Targeting)
    private bool retargetOnTargetLost => Cfg.retargetOnTargetLost;
    private float snifferIntervalSeconds => Cfg.snifferIntervalSeconds;
    private int wanderMinHops => Cfg.wanderMinHops;
    // Effective interval: behavior profile (or config fallback) × the maze's replan multiplier. Set in Initialize.
    private float _replanInterval = 0.5f;

    private GameFlowManager _gfm;
    private bool _active;
    private MusicalRole _attunedRole = MusicalRole.None;
    private bool _huntingEnabled;
    private float _replanTimer;
    private float _snifferTimer;

    private bool _hasTarget;
    private Vector2Int _targetCell;
    private Vector2 _targetDir;

    private bool _hasLockOnCell;
    private Vector2Int _lockOnCell;

    private readonly Dictionary<MusicalRole, Vector2> _snifferDirs = new();
    private Vector2 _nearestColoredDustDir = Vector2.zero;
    private readonly List<Vector2Int> _coloredCellsScratch = new(512);
    private readonly HashSet<Vector2Int> _zappedThisCycle = new();
    private readonly HashSet<Vector2Int> _reservedCells = new();

    // Maze-aware navigation (StarNavigationParams on the motif's MazePatternConfig).

    private PhaseStarNavMode _navMode = PhaseStarNavMode.Direct;
    private int _pathLookaheadCells = 2;
    private readonly PhaseStarGridPathfinder _pathfinder = new();
    private bool _pathIsWander;
    private Vector2 _pathDir;
    private Func<Vector2Int, bool> _isHuntTargetPredicate;
    private CosmicDustGenerator _predicateGen;
    private readonly HashSet<Vector2Int> _coloredCellSet = new();

    private bool UsesPathing => StarNavigationParams.UsesPathing(_navMode);

    public void Initialize(
        PhaseStar star,
        PhaseStarBehaviorProfile profile,
        StarNavigationParams nav = null,
        PhaseStarNavMode navMode = PhaseStarNavMode.Direct)
    {
        nav ??= StarNavigationParams.Default;
        float baseInterval = profile != null ? profile.mazeNavReplanInterval : Cfg.replanIntervalSeconds;
        _replanInterval = Mathf.Max(0.1f, baseInterval * nav.replanIntervalMultiplier);

        _navMode = navMode == PhaseStarNavMode.Auto ? PhaseStarNavMode.Direct : navMode;
        _pathLookaheadCells = Mathf.Max(1, nav.pathLookaheadCells);

        // Cached so HuntTick doesn't allocate a closure every replan. The set check runs first:
        // the BFS calls this for every neighbour it touches, and IsZapEligible does a
        // component lookup.
        _isHuntTargetPredicate ??= c => _predicateGen != null &&
            _coloredCellSet.Contains(c) &&
            IsZapEligible(_predicateGen, c, _attunedRole != MusicalRole.None ? _attunedRole : MusicalRole.None);

        _active = true;
        ResetTargetingState(clearSniffer: true);
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (!active)
            ResetTargetingState(clearSniffer: false);
    }

    public void SetAttunedRole(MusicalRole role)
    {
        _attunedRole = role;
    }

    public void SetHuntingEnabled(bool enabled)
    {
        _huntingEnabled = enabled;
        if (!enabled)
        {
            _hasTarget = false;
            _hasLockOnCell = false;
            _targetDir = Vector2.zero;
            ClearPath();
        }
    }

    // ---------------------------------------------------------------------------
    // Tentacle target selection (PhaseStarDustAffect). Flowers are ranked by how much
    // dust a vine would have to break to reach them (0-1 BFS from the star's cell:
    // dust costs 1, Cosmic Blocks are walls), then by straight-line distance — so an
    // open flower always wins over a nearer one behind a wall. The drain lock-on is a
    // star-movement concept and does not gate tentacle targets: every idle tentacle
    // may take its own flower while another is draining.
    // ---------------------------------------------------------------------------
    private readonly DustReachability _targetReach = new DustReachability();
    private readonly List<Vector2Int> _targetReachSeeds = new List<Vector2Int>(1);
    private readonly List<(Vector2Int cell, int cost, float sqDist)> _scoredScratch = new(64);
    private Func<Vector2Int, bool> _reachHard, _reachSoft;
    private CosmicDustGenerator _reachGen;
    private DrumTrack _reachDrum;
    private string _lastTargetLog;

    /// <summary>Fills <paramref name="cells"/> (cleared first) with up to maxCount flowers of
    /// <paramref name="role"/>, best first. Returns false when there are none.</summary>
    public bool TryGetTargetsForRole(
        MusicalRole role,
        int maxCount,
        HashSet<Vector2Int> excludedCells,
        List<Vector2Int> cells)
    {
        cells.Clear();
        if (maxCount <= 0) return false;
        if (_attunedRole != MusicalRole.None && role != _attunedRole) return false;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen = _gfm?.dustGenerator;
        var drum = _gfm?.activeDrumTrack;
        if (gen == null || drum == null) return false;

        // Only this role's flowers (shared once-per-frame scan) — no reach BFS when there are none.
        var flowers = gen.GetFlowerCells(role);
        if (flowers.Count == 0) return false;

        Vector2 starWorld = transform.position;
        var scored = _scoredScratch;
        scored.Clear();

        bool reachRan = RunTargetReach(gen, drum, starWorld);

        for (int i = 0; i < flowers.Count; i++)
        {
            var c = flowers[i];
            if (excludedCells != null && excludedCells.Contains(c)) continue;
            if (_reservedCells.Contains(c)) continue;
            if (!IsZapEligible(gen, c, role, ignoreLockOn: true)) continue;

            int cost = 0;
            if (reachRan && !_targetReach.TryGetBestNear(c, out _, out cost))
                cost = int.MaxValue; // walled in by blocks: last resort (vine will give up and cool down)
            float sqd = ((Vector2)drum.GridToWorldPosition(c) - starWorld).sqrMagnitude;
            scored.Add((c, cost, sqd));
        }

        if (scored.Count == 0) return false;
        scored.Sort((a, b) => a.cost != b.cost ? a.cost.CompareTo(b.cost) : a.sqDist.CompareTo(b.sqDist));

        int targetCount = Mathf.Min(maxCount, scored.Count);
        for (int i = 0; i < targetCount; i++)
            cells.Add(scored[i].cell);

        if (GameFlowManager.VerboseLogging)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < scored.Count && i < 12; i++)
                sb.Append($"{scored[i].cell}:{(scored[i].cost == int.MaxValue ? "walled" : scored[i].cost.ToString())} ");
            string line = $"[STAR:TARGET] candidates role={role} flowers={scored.Count} want={maxCount} ranked(cell:dustCrossings)= {sb}";
            if (line != _lastTargetLog) { _lastTargetLog = line; Debug.Log(line); } // log on change only
        }

        return cells.Count > 0;
    }

    private bool RunTargetReach(CosmicDustGenerator gen, DrumTrack drum, Vector2 starWorld)
    {
        int w = drum.GetSpawnGridWidth();
        int h = drum.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return false;

        Vector2Int from = drum.WorldToGridPosition(starWorld);
        from.x = Mathf.Clamp(from.x, 0, w - 1);
        from.y = Mathf.Clamp(from.y, 0, h - 1);
        _targetReachSeeds.Clear();
        _targetReachSeeds.Add(from);

        _reachGen = gen;
        _reachDrum = drum;
        _reachHard ??= c => _reachGen != null && _reachGen.IsStarImpassable(c);
        _reachSoft ??= c => _reachDrum != null && _reachDrum.HasDustAt(c);
        try
        {
            _targetReach.Run(w, h, 0, _targetReachSeeds, _reachHard, _reachSoft);
        }
        finally
        {
            _reachGen = null;
            _reachDrum = null;
        }
        return true;
    }

    public void NotifyDraining(Vector2Int cell)
    {
        // Lock-on: the tentacle already reached the cell, so steer straight at it.
        if (!_hasTarget || _targetCell != cell) ClearPath();
        _hasLockOnCell = true;
        _lockOnCell = cell;
        _hasTarget = true;
        _targetCell = cell;
        RefreshTargetDir();
        _replanTimer = _replanInterval;
    }

    public void ClearLockOn(Vector2Int cell)
    {
        if (_hasLockOnCell && _lockOnCell == cell)
        {
            _hasLockOnCell = false;
            _replanTimer = 0f;
        }
    }

    public void NotifyCellZappedThisCycle(Vector2Int cell)
    {
        _zappedThisCycle.Add(cell);
        _reservedCells.Remove(cell);
    }

    public bool WasCellZappedThisCycle(Vector2Int cell) => _zappedThisCycle.Contains(cell);
    public void ReserveCell(Vector2Int cell) => _reservedCells.Add(cell);
    public void ReleaseCellReservation(Vector2Int cell) => _reservedCells.Remove(cell);

    public Vector2 GetDensitySteerDir() => (_huntingEnabled && _hasTarget) ? _targetDir : Vector2.zero;
    
    public Vector2 GetDominantSnifferDir()
    {
        if (_huntingEnabled && (_hasLockOnCell || _hasTarget))
            return _hasTarget ? _targetDir : Vector2.zero;

        // Pathing modes: the straight-line sniffer direction points into walls, so drift
        // along the BFS path (to the nearest reachable dust, or a wander cell) instead.
        if (UsesPathing && _pathfinder.HasPath)
            return _pathDir;

        return _nearestColoredDustDir;
    }

    private void Update()
    {
        if (!_active) return;

        float dt = Time.deltaTime;

        RefreshTargetDir();
        ValidateCurrentTarget();

        _replanTimer -= dt;
        if (_replanTimer <= 0f)
        {
            _replanTimer = _replanInterval;
            HuntTick();
        }

        _snifferTimer -= dt;
        if (_snifferTimer <= 0f)
        {
            _snifferTimer = snifferIntervalSeconds;
            SnifferTick();
        }
    }

    private void HuntTick()
    {
        if (_hasLockOnCell) return;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen = _gfm?.dustGenerator;
        var drum = _gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        FillHuntCandidates(gen);

        if (_coloredCellsScratch.Count == 0)
        {
            _hasTarget = false;
            _targetDir = Vector2.zero;
            if (UsesPathing) EnsureWanderPath(drum);
            else ClearPath();
            return;
        }

        if (UsesPathing)
        {
            _predicateGen = gen;
            _coloredCellSet.Clear();
            for (int i = 0; i < _coloredCellsScratch.Count; i++)
                _coloredCellSet.Add(_coloredCellsScratch[i]);

            Vector2Int starCell = drum.WorldToGridPosition(transform.position);
            if (_pathfinder.TryFindPathToAny(drum, starCell, _isHuntTargetPredicate, out var pathTarget))
            {
                _pathIsWander = false;
                _hasTarget = true;
                _targetCell = pathTarget;
                RefreshTargetDir();
                return;
            }
            // Nothing reachable through open cells: fall through to straight-line selection so
            // the star never stalls (it may still scrape a wall, which the unstick impulse handles).
            ClearPath();
        }

        Vector2 starWorld = transform.position;
        Vector2Int bestCell = default;
        float bestEffDist = float.MaxValue;
        bool found = false;

        for (int i = 0; i < _coloredCellsScratch.Count; i++)
        {
            var cell = _coloredCellsScratch[i];
            if (_reservedCells.Contains(cell)) continue;
            var expectedRole = _attunedRole != MusicalRole.None ? _attunedRole : MusicalRole.None;
            if (!IsZapEligible(gen, cell, expectedRole)) continue;

            Vector2 cellWorld = drum.GridToWorldPosition(cell);
            float dist = (cellWorld - starWorld).magnitude;

            if (dist < bestEffDist)
            {
                bestCell = cell;
                bestEffDist = dist;
                found = true;
            }
        }

        if (!found)
        {
            _hasTarget = false;
            _targetDir = Vector2.zero;
            if (UsesPathing) EnsureWanderPath(drum);
            return;
        }

        _hasTarget = true;
        _targetCell = bestCell;
        RefreshTargetDir();
    }

    // Keeps an idle corridor-following wander path alive while nothing is huntable.
    private void EnsureWanderPath(DrumTrack drum)
    {
        if (_pathIsWander && _pathfinder.HasPath && _pathfinder.Waypoints.Count > 0) return;

        Vector2Int starCell = drum.WorldToGridPosition(transform.position);
        _pathIsWander = _pathfinder.TryPickWanderTarget(drum, starCell, wanderMinHops, out _);
        RefreshPathDir(drum);
    }

    private void ClearPath()
    {
        _pathfinder.Clear();
        _pathIsWander = false;
        _pathDir = Vector2.zero;
    }

    // Advances past reached waypoints and aims at the one pathLookaheadCells ahead.
    // Returns false when the path is exhausted.
    private bool RefreshPathDir(DrumTrack drum)
    {
        if (!_pathfinder.HasPath || drum == null) { _pathDir = Vector2.zero; return false; }

        Vector2 starWorld = transform.position;
        _pathfinder.AdvancePast(drum.WorldToGridPosition(starWorld));

        var wps = _pathfinder.Waypoints;
        if (wps.Count == 0) { _pathDir = Vector2.zero; return false; }

        int idx = Mathf.Min(_pathLookaheadCells - 1, wps.Count - 1);
        Vector2 dir = (Vector2)drum.GridToWorldPosition(wps[idx]) - starWorld;
        _pathDir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.zero;
        return true;
    }

    // Hunt/sniff candidates: the attuned role's flowers only (a star never eats other roles);
    // an unattuned star considers every role's flowers.
    private void FillHuntCandidates(CosmicDustGenerator gen)
    {
        if (_attunedRole != MusicalRole.None)
        {
            _coloredCellsScratch.Clear();
            _coloredCellsScratch.AddRange(gen.GetFlowerCells(_attunedRole));
        }
        else
        {
            gen.GetColoredDustCells(_coloredCellsScratch);
        }
    }

    private void SnifferTick()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen = _gfm?.dustGenerator;
        var drum = _gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        _snifferDirs.Clear();
        Vector2 starWorld = transform.position;
        float nearestSqDist = float.MaxValue;
        Vector2Int nearestCell = default;
        bool foundAny = false;

        // Per-role buckets carry the role already — no per-cell component lookup.
        foreach (MusicalRole role in s_allRoles)
        {
            if (role == MusicalRole.None) continue;
            if (_attunedRole != MusicalRole.None && role != _attunedRole) continue;
            var flowers = gen.GetFlowerCells(role);
            float roleBest = float.MaxValue;
            for (int i = 0; i < flowers.Count; i++)
            {
                var cell = flowers[i];
                Vector2 dir = (Vector2)drum.GridToWorldPosition(cell) - starWorld;
                float sqDist = dir.sqrMagnitude;
                if (sqDist < roleBest)
                {
                    roleBest = sqDist;
                    if (sqDist > 0.0001f) _snifferDirs[role] = dir.normalized;
                }
                if (sqDist < nearestSqDist)
                {
                    nearestSqDist = sqDist;
                    nearestCell = cell;
                    foundAny = true;
                }
            }
        }

        if (foundAny)
        {
            Vector2 dir = (Vector2)drum.GridToWorldPosition(nearestCell) - starWorld;
            _nearestColoredDustDir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.zero;
        }
        else
        {
            _nearestColoredDustDir = Vector2.zero;
        }
    }

    private static readonly MusicalRole[] s_allRoles = (MusicalRole[])Enum.GetValues(typeof(MusicalRole));

    private void LateUpdate()
    {
        _zappedThisCycle.Clear();
    }

    private void RefreshTargetDir()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;

        if (!_hasTarget)
        {
            _targetDir = Vector2.zero;
            if (_pathIsWander && !RefreshPathDir(drum)) ClearPath();
            return;
        }

        if (drum == null)
        {
            _targetDir = Vector2.zero;
            return;
        }

        // Pathing modes: follow the BFS route; once it's exhausted the star is adjacent to the
        // target and the straight-line direction below is wall-free.
        if (UsesPathing && !_pathIsWander && RefreshPathDir(drum))
        {
            _targetDir = _pathDir;
            return;
        }

        Vector2 cellWorld = drum.GridToWorldPosition(_targetCell);
        Vector2 dir = cellWorld - (Vector2)transform.position;
        _targetDir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.zero;
    }

    private void ValidateCurrentTarget()
    {
        if (_hasLockOnCell && !IsTargetValid(_lockOnCell))
        {
            _hasLockOnCell = false;
            _hasTarget = false;
            _targetDir = Vector2.zero;
            _replanTimer = 0f;
            if (!_pathIsWander) ClearPath();
            return;
        }

        if (_hasTarget && retargetOnTargetLost && !IsTargetValid(_targetCell))
        {
            _hasTarget = false;
            _targetDir = Vector2.zero;
            _replanTimer = 0f;
            if (!_pathIsWander) ClearPath();
        }
    }

    private void ResetTargetingState(bool clearSniffer)
    {
        _replanTimer = 0f;
        _snifferTimer = 0f;
        _hasTarget = false;
        _targetDir = Vector2.zero;
        _hasLockOnCell = false;
        _huntingEnabled = false;
        ClearPath();

        if (clearSniffer)
        {
            _snifferDirs.Clear();
            _nearestColoredDustDir = Vector2.zero;
        }
    }

    private bool IsTargetValid(Vector2Int cell)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen = _gfm?.dustGenerator;
        if (gen == null) return false;

        var expectedRole = _attunedRole != MusicalRole.None ? _attunedRole : MusicalRole.None;
        return IsZapEligible(gen, cell, expectedRole);
    }

    private bool IsZapEligible(CosmicDustGenerator gen, Vector2Int cell, MusicalRole requiredRole, bool ignoreLockOn = false)
    {
        if (!gen.HasDustAt(cell)) return false;
        var dust = GetDust(gen, cell);
        if (dust == null || dust.Role == MusicalRole.None) return false;
        if (requiredRole != MusicalRole.None && dust.Role != requiredRole) return false;
        if (dust.currentEnergyUnits <= 0) return false;
        if (!ignoreLockOn && _hasLockOnCell && _lockOnCell != cell) return false;
        if (_zappedThisCycle.Contains(cell)) return false;
        if (_reservedCells.Contains(cell)) return false;
        return true;
    }

    private static CosmicDust GetDust(CosmicDustGenerator gen, Vector2Int gp)
    {
        if (!gen.TryGetCellGo(gp, out var go) || go == null) return null;
        go.TryGetComponent<CosmicDust>(out var dust);
        return dust;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (_hasTarget)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(transform.position, _targetDir * 2f);

            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var drum = _gfm?.activeDrumTrack;
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

        if (_pathfinder.HasPath)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var drumForPath = _gfm?.activeDrumTrack;
            if (drumForPath != null)
            {
                Gizmos.color = _pathIsWander ? Color.gray : Color.green;
                Vector3 prev = transform.position;
                var wps = _pathfinder.Waypoints;
                for (int i = 0; i < wps.Count; i++)
                {
                    Vector3 p = drumForPath.GridToWorldPosition(wps[i]);
                    Gizmos.DrawLine(prev, p);
                    prev = p;
                }
            }
        }
    }
#endif
}
