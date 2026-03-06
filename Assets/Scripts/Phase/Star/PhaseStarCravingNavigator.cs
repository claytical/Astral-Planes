using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Gives the PhaseStar a craving-driven maze navigation brain.
///
/// Craving rule:
///   - Starts hungry for the phase's dominant role (set via Initialize).
///   - Each time PhaseStarDustAffect drains a dust cell, it calls NotifyDustEaten(role).
///   - If that role differs from the current craving, the craving switches immediately.
///     The star always wants what it just tasted.
///
/// Navigation rule:
///   - Every replanIntervalSeconds, run a BFS through open (non-dust) corridor cells.
///   - Find the nearest open cell that is adjacent to a dust cell of _currentCraving.
///   - The first step on that path becomes _waypointWorld.
///   - PhaseStarMotion2D.ExternalPathStep is wired to steer toward that waypoint,
///     blended against the existing drift/avoidance layers.
///   - If no craving-role dust edge is reachable within bfsBudget cells, fall back to
///     the nearest reachable dust edge of any role (keeps the star purposeful).
/// </summary>
[DisallowMultipleComponent]
public sealed class PhaseStarCravingNavigator : MonoBehaviour
{
    // ----------------------------------------------------------------
    // Inspector tuning (also driven by PhaseStarBehaviorProfile fields)
    // ----------------------------------------------------------------
    [Header("Pathfinding")]
    [Tooltip("Seconds between full BFS replans.")]
    [SerializeField] private float replanIntervalSeconds = 0.5f;

    [Tooltip("Max grid cells visited per BFS. Keep ≤200 for performance.")]
    [SerializeField] private int bfsBudget = 150;

    [Header("Steering")]
    [Tooltip("How strongly the waypoint pull overrides the free-drift direction. 0=ignore waypoint, 1=full pull.")]
    [Range(0f, 1f)]
    [SerializeField] private float waypointPull = 0.85f;

    [Tooltip("Stop steering toward the waypoint when within this many world units.")]
    [SerializeField] private float waypointArrivalRadius = 0.35f;

    [Tooltip("Speed used when moving toward waypoint (world units / sec). Should roughly match profile.starDriftSpeed.")]
    [SerializeField] private float navigationSpeed = 3.5f;

    [Header("Debug")]
    [SerializeField] private bool verbose = false;

    // ----------------------------------------------------------------
    // State
    // ----------------------------------------------------------------
    private MusicalRole _currentCraving = MusicalRole.None;
    private bool _hasWaypoint;
    private Vector2 _waypointWorld;
    private float _replanTimer;
    private bool _navigating; // false until Initialize is called

    // BFS reuse buffers (avoid per-frame alloc)
    private readonly Queue<Vector2Int> _bfsQueue   = new(256);
    private readonly Dictionary<Vector2Int, Vector2Int> _bfsPrev = new(256); // child -> parent
    private readonly List<Vector2Int> _pathScratch = new(32);

    // Sniffer: per-role world-space pointing direction (toward nearest dust of that role)
    private readonly Dictionary<MusicalRole, Vector2> _snifferDirs = new();
    private float _snifferTimer;
    [SerializeField] private float snifferIntervalSeconds = 0.3f;
    [SerializeField] private int   snifferBfsBudget       = 60; // cheap; separate from nav budget

    /// <summary>Fired when the craving changes role. PhaseStar listens to update currentShardIndex.</summary>
    public event Action<MusicalRole> OnCravingChanged;

    // Cardinal + diagonal neighbours (8-connected open-space traversal)
    private static readonly Vector2Int[] kNeighbours =
    {
        new( 1,  0), new(-1,  0), new( 0,  1), new( 0, -1),
        new( 1,  1), new(-1,  1), new( 1, -1), new(-1, -1)
    };

    // ----------------------------------------------------------------
    // Public API
    // ----------------------------------------------------------------

    /// <summary>Current role the star is craving. Read by visuals if desired.</summary>
    public MusicalRole CurrentCraving => _currentCraving;

    /// <summary>
    /// Call once from PhaseStar.Initialize.
    /// Wires the ExternalPathStep hook on PhaseStarMotion2D and starts navigation.
    /// </summary>
    public void Initialize(
        PhaseStar star,
        PhaseStarMotion2D motionComponent,
        MusicalRole initialCraving,
        PhaseStarBehaviorProfile profile)
    {
        _currentCraving = initialCraving;

        // Override tuning from profile if the fields exist there
        if (profile != null)
        {
            replanIntervalSeconds = Mathf.Max(0.1f, profile.mazeNavReplanInterval);
            bfsBudget             = Mathf.Max(10,   profile.mazeNavBfsBudget);
            waypointPull          = Mathf.Clamp01(  profile.mazeNavWaypointPull);
        }

        // Wire the motion hook: called every FixedUpdate when kinematic mode is on,
        // but we expose the waypoint direction even in dynamic mode via the blend below.
        if (motionComponent != null)
            motionComponent.ExternalPathStep = StepTowardWaypoint;

        _navigating = true;
        _replanTimer = 0f; // replan immediately on first update

        if (verbose)
            Debug.Log($"[CravingNav] Initialized. initialCraving={initialCraving}");
    }

    /// <summary>
    /// Called by PhaseStarDustAffect each time it drains charge from a dust cell.
    /// If the eaten role differs from the current craving, the craving switches.
    /// </summary>
    public void NotifyDustEaten(MusicalRole eatenRole)
    {
        if (eatenRole == MusicalRole.None) return;
        if (eatenRole == _currentCraving) return;

        if (verbose)
            Debug.Log($"[CravingNav] Craving switch: {_currentCraving} → {eatenRole}");

        _currentCraving = eatenRole;
        _replanTimer    = 0f; // replan immediately after craving shift
        _hasWaypoint    = false;
        OnCravingChanged?.Invoke(_currentCraving);
    }

    // ----------------------------------------------------------------
    // Unity
    // ----------------------------------------------------------------

    private void Update()
    {
        if (!_navigating) return;

        _replanTimer -= Time.deltaTime;
        if (_replanTimer <= 0f)
        {
            _replanTimer = replanIntervalSeconds;
            Replan();
        }

        _snifferTimer -= Time.deltaTime;
        if (_snifferTimer <= 0f)
        {
            _snifferTimer = snifferIntervalSeconds;
            SnifferTick();
        }
    }

    // ----------------------------------------------------------------
    // ExternalPathStep hook  (signature: (currentPos, dt) -> nextPos)
    // ----------------------------------------------------------------

    /// <summary>
    /// Feeds into PhaseStarMotion2D.ExternalPathStep.
    /// Used in kinematic mode; in dynamic mode the waypoint direction is wired
    /// through the motion component's drift blend instead.
    /// </summary>
    public Vector2 StepTowardWaypoint(Vector2 currentPos, float dt)
    {
        if (!_hasWaypoint)
            return currentPos; // no movement override; drift handles it

        Vector2 toWaypoint = _waypointWorld - currentPos;
        float dist = toWaypoint.magnitude;

        if (dist < waypointArrivalRadius)
        {
            // Arrived — let drift take over until the next replan picks a new waypoint
            _hasWaypoint = false;
            return currentPos;
        }

        Vector2 dir = toWaypoint / dist;
        return currentPos + dir * (navigationSpeed * dt);
    }

    /// <summary>
    /// Returns the current waypoint pull direction for use in dynamic (non-kinematic) steering.
    /// Blend this into the motion component's desired direction externally, weighted by waypointPull.
    /// Returns Vector2.zero when no waypoint is set.
    /// </summary>
    public Vector2 GetWaypointSteerDir(Vector2 currentPos)
    {
        if (!_hasWaypoint) return Vector2.zero;
        Vector2 d = _waypointWorld - currentPos;
        return d.sqrMagnitude > 0.0001f ? d.normalized : Vector2.zero;
    }

    public float WaypointPull => waypointPull;
    public bool HasWaypoint   => _hasWaypoint;

    // ----------------------------------------------------------------
    // Sniffer: per-role pointing direction
    // ----------------------------------------------------------------

    /// <summary>
    /// Returns the world-space direction this star should point for a given role's diamond,
    /// i.e. toward the nearest reachable dust of that role.
    /// Returns Vector2.zero if no data is available (diamond should hold its last angle).
    /// </summary>
    public Vector2 GetSnifferDir(MusicalRole role)
    {
        return _snifferDirs.TryGetValue(role, out var d) ? d : Vector2.zero;
    }

    /// <summary>
    /// For each known role, do a lightweight BFS to find the nearest dust cell of that role
    /// and store the world-space direction to it. Called every snifferIntervalSeconds.
    /// </summary>
    private void SnifferTick()
    {
        var gfm  = GameFlowManager.Instance;
        var gen  = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        Vector2Int startCell = drum.WorldToGridPosition(transform.position);
        int w = drum.GetSpawnGridWidth();
        int h = drum.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        startCell.x = Mathf.Clamp(startCell.x, 0, w - 1);
        startCell.y = Mathf.Clamp(startCell.y, 0, h - 1);

        // Roles we care about: all four playable roles
        MusicalRole[] roles = { MusicalRole.Lead, MusicalRole.Harmony, MusicalRole.Groove, MusicalRole.Bass };

        // Single BFS pass; for each cell we check all four roles so we only traverse once
        var found = new Dictionary<MusicalRole, Vector2Int>();

        _bfsQueue.Clear();
        _bfsPrev.Clear();
        _bfsQueue.Enqueue(startCell);
        _bfsPrev[startCell] = startCell;

        int visited = 0;
        while (_bfsQueue.Count > 0 && visited < snifferBfsBudget && found.Count < roles.Length)
        {
            var cell = _bfsQueue.Dequeue();
            visited++;

            for (int i = 0; i < kNeighbours.Length; i++)
            {
                var nb = cell + kNeighbours[i];
                if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;

                if (gen.HasDustAt(nb))
                {
                    // Dust cell — check its role
                    MusicalRole nbRole = GetDustRole(gen, nb);
                    if (nbRole != MusicalRole.None && !found.ContainsKey(nbRole))
                        found[nbRole] = nb; // BFS guarantees this is nearest of that role
                    continue; // don't traverse through dust
                }

                if (_bfsPrev.ContainsKey(nb)) continue;
                _bfsPrev[nb] = cell;
                _bfsQueue.Enqueue(nb);
            }
        }

        // Convert found cells → world directions from the star's current position
        Vector2 starWorld = transform.position;
        foreach (var kvp in found)
        {
            Vector2 targetWorld = drum.GridToWorldPosition(kvp.Value);
            Vector2 dir = targetWorld - starWorld;
            if (dir.sqrMagnitude > 0.0001f)
                _snifferDirs[kvp.Key] = dir.normalized;
        }
    }

    // ----------------------------------------------------------------
    // BFS planner
    // ----------------------------------------------------------------

    private void Replan()
    {
        var gfm  = GameFlowManager.Instance;
        var gen  = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;

        if (gen == null || drum == null)
        {
            _hasWaypoint = false;
            return;
        }

        Vector2Int startCell = drum.WorldToGridPosition(transform.position);
        int w = drum.GetSpawnGridWidth();
        int h = drum.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        // Clamp start cell to grid
        startCell.x = Mathf.Clamp(startCell.x, 0, w - 1);
        startCell.y = Mathf.Clamp(startCell.y, 0, h - 1);

        // BFS through open (non-dust) cells looking for the edge of _currentCraving territory.
        // "Edge cell" = open cell with at least one dust neighbour of the desired role.
        Vector2Int bestEdgeCell   = new Vector2Int(-1, -1);
        Vector2Int fallbackEdge   = new Vector2Int(-1, -1); // any dust edge if craving not found
        bool foundCraving         = false;

        _bfsQueue.Clear();
        _bfsPrev.Clear();

        // If start cell is inside dust (star just spawned in a carved pocket that
        // may not be registered as clear yet), treat it as open for BFS purposes.
        _bfsQueue.Enqueue(startCell);
        _bfsPrev[startCell] = startCell; // sentinel: parent of root = itself

        int visited = 0;

        while (_bfsQueue.Count > 0 && visited < bfsBudget)
        {
            var cell = _bfsQueue.Dequeue();
            visited++;

            // Check neighbours for dust edges
            for (int i = 0; i < kNeighbours.Length; i++)
            {
                var nb = cell + kNeighbours[i];
                if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;

                if (gen.HasDustAt(nb))
                {
                    // nb is a dust cell — current cell 'cell' is an edge open cell

                    // Any-role fallback
                    if (!foundCraving && fallbackEdge.x < 0)
                        fallbackEdge = cell;

                    // Craving-specific check
                    if (!foundCraving && _currentCraving != MusicalRole.None)
                    {
                        MusicalRole nbRole = GetDustRole(gen, nb);
                        if (nbRole == _currentCraving)
                        {
                            bestEdgeCell = cell;
                            foundCraving = true;
                        }
                    }

                    // Don't enqueue dust cells as traversable
                    continue;
                }

                // Open cell — enqueue if not yet visited
                if (_bfsPrev.ContainsKey(nb)) continue;
                _bfsPrev[nb] = cell;
                _bfsQueue.Enqueue(nb);
            }

            if (foundCraving) break;
        }

        Vector2Int target = foundCraving  ? bestEdgeCell
                          : fallbackEdge.x >= 0 ? fallbackEdge
                          : new Vector2Int(-1, -1);

        if (target.x < 0)
        {
            // Nothing reachable — clear waypoint and let drift take over
            _hasWaypoint = false;
            if (verbose) Debug.Log($"[CravingNav] BFS found no target. cells visited={visited}");
            return;
        }

        // Trace path back from target to start, take the first step
        Vector2Int firstStep = TraceFirstStep(startCell, target);

        _waypointWorld = drum.GridToWorldPosition(firstStep);
        _hasWaypoint   = true;

        if (verbose)
            Debug.Log($"[CravingNav] Replan: craving={_currentCraving} found={foundCraving} " +
                      $"target={target} firstStep={firstStep} visited={visited}");
    }

    /// <summary>
    /// Walks _bfsPrev backwards from target to find the cell one step from startCell.
    /// Returns startCell itself if target == startCell.
    /// </summary>
    private Vector2Int TraceFirstStep(Vector2Int startCell, Vector2Int target)
    {
        _pathScratch.Clear();
        Vector2Int cur = target;

        // Safety: max path length = bfsBudget
        for (int i = 0; i < bfsBudget + 2; i++)
        {
            _pathScratch.Add(cur);
            Vector2Int parent = _bfsPrev.TryGetValue(cur, out var p) ? p : startCell;
            if (parent == startCell || parent == cur) break;
            cur = parent;
        }

        // _pathScratch is target→start; the first step from start is the last element before start.
        if (_pathScratch.Count == 0) return startCell;
        if (_pathScratch.Count == 1) return _pathScratch[0]; // target is adjacent to start

        return _pathScratch[_pathScratch.Count - 1]; // second-to-last = first step from root
    }

    // ----------------------------------------------------------------
    // Dust role query
    // ----------------------------------------------------------------

    /// <summary>
    /// Asks the dust cell at the given grid position for its assigned MusicalRole.
    /// Returns MusicalRole.None if the cell has no dust or no role data.
    /// </summary>
    private static MusicalRole GetDustRole(CosmicDustGenerator gen, Vector2Int gp)
    {
        if (!gen.TryGetCellGo(gp, out var go) || go == null) return MusicalRole.None;
        if (!go.TryGetComponent<CosmicDust>(out var dust)) return MusicalRole.None;
        return dust.Role;
    }

    // ----------------------------------------------------------------
    // Gizmos (editor aid)
    // ----------------------------------------------------------------

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!_hasWaypoint) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawSphere(_waypointWorld, 0.15f);
        Gizmos.DrawLine(transform.position, _waypointWorld);
    }
#endif
}
