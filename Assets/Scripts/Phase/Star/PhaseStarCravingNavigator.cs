using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives PhaseStar movement toward the highest-density dust patch in the maze,
/// and maintains per-role sniffer directions for the preview ring diamonds.
///
/// Motion rule:
///   - Every replanIntervalSeconds, sample dust density around the star in a spoke
///     pattern. Pick the spoke with the highest count and set that as the drift target.
///   - The result is exposed as GetDensitySteerDir() for PhaseStarMotion2D to blend.
///   - No BFS, no craving concept — the star always drifts toward more dust.
///
/// Sniffer rule:
///   - Every snifferIntervalSeconds, do a lightweight BFS outward from the star.
///   - For each playable role, record the world-space direction to the nearest dust
///     cell of that role.
///   - PhaseStar.Update reads GetSnifferDir(role) to rotate each diamond.
///   - The dominant (highest-charge) shard's direction is also fed back to motion
///     via GetDominantSnifferDir() so the star steers toward what it is draining.
/// </summary>
[DisallowMultipleComponent]
public sealed class PhaseStarCravingNavigator : MonoBehaviour
{
    // ----------------------------------------------------------------
    // Inspector
    // ----------------------------------------------------------------
    [Header("Density Steering")]
    [Tooltip("Seconds between density-spoke replans.")]
    [SerializeField] private float replanIntervalSeconds = 0.4f;

    [Tooltip("How many spoke directions to sample (evenly distributed around 360°).")]
    [SerializeField, Range(4, 16)] private int spokeCount = 8;

    [Tooltip("How far along each spoke to sample (world units). Should be ~half the play area.")]
    [SerializeField] private float spokeLength = 5f;

    [Tooltip("Number of sample points along each spoke.")]
    [SerializeField, Range(2, 10)] private int spokeSamples = 4;

    [Header("Sniffer (per-role diamond facing)")]
    [Tooltip("Seconds between sniffer BFS ticks.")]
    [SerializeField] private float snifferIntervalSeconds = 0.25f;

    [Tooltip("Max grid cells visited per sniffer BFS. Keep low — it runs per-role.")]
    [SerializeField] private int snifferBfsBudget = 80;

    [Header("Hunger-Weighted Steering")]
    [Tooltip("Weight given to dust of a fully-charged role (0 = ignore it entirely, 1 = no hunger bias).")]
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

    // Density steer: world-space direction toward highest-density spoke
    private Vector2 _densitySteerDir = Vector2.zero;
    private bool    _hasDensityDir   = false;

    // Sniffer: per-role direction toward nearest dust of that role
    private readonly Dictionary<MusicalRole, Vector2> _snifferDirs = new();

    // Dominant role for motion blend (set externally by PhaseStar each frame)
    private MusicalRole _dominantRole = MusicalRole.None;

    // BFS reuse buffers
    private readonly Queue<Vector2Int>                _bfsQueue = new(256);
    private readonly HashSet<Vector2Int>              _bfsVisited = new(256);

    private static readonly MusicalRole[] kPlayableRoles =
        { MusicalRole.Lead, MusicalRole.Harmony, MusicalRole.Groove, MusicalRole.Bass };

    private static readonly Vector2Int[] kNeighbours4 =
    {
        new( 1, 0), new(-1, 0), new( 0, 1), new( 0,-1)
    };

    // ----------------------------------------------------------------
    // Public API
    // ----------------------------------------------------------------

    /// <summary>
    /// Call once from PhaseStar.Initialize after motion.Initialize.
    /// Profile fields override inspector defaults when present.
    /// </summary>
    public void Initialize(
        PhaseStar star,
        PhaseStarMotion2D motionComponent,
        MusicalRole ignoredInitialCraving,   // kept for call-site compat; unused
        PhaseStarBehaviorProfile profile)
    {
        if (profile != null)
        {
            replanIntervalSeconds = Mathf.Max(0.1f, profile.mazeNavReplanInterval);
            snifferBfsBudget      = Mathf.Max(10,   profile.mazeNavBfsBudget);
        }

        _star = star;
        _active       = true;
        _replanTimer  = 0f; // replan immediately
        _snifferTimer = 0f;

        if (verbose)
            Debug.Log("[DensityNav] Initialized.");
    }

    /// <summary>
    /// Tell the navigator which role currently has the most charge so it can
    /// blend that shard's sniffer direction into motion. Call from PhaseStar.Update.
    /// </summary>
    public void SetDominantRole(MusicalRole role) => _dominantRole = role;

    /// <summary>
    /// World-space direction toward highest-density dust region.
    /// Used by PhaseStarMotion2D to steer the star body.
    /// Returns Vector2.zero when no data is available yet.
    /// </summary>
    public Vector2 GetDensitySteerDir() => _hasDensityDir ? _densitySteerDir : Vector2.zero;

    /// <summary>
    /// World-space direction the dominant shard's sniffer is pointing.
    /// Blended into motion so the star steers toward what it's draining.
    /// Returns Vector2.zero when no data is available.
    /// </summary>
    public Vector2 GetDominantSnifferDir()
    {
        if (_dominantRole == MusicalRole.None) return Vector2.zero;
        return _snifferDirs.TryGetValue(_dominantRole, out var d) ? d : Vector2.zero;
    }

    // ----------------------------------------------------------------
    // Unity
    // ----------------------------------------------------------------

    private void Update()
    {
        if (!_active) return;

        _replanTimer -= Time.deltaTime;
        if (_replanTimer <= 0f)
        {
            _replanTimer = replanIntervalSeconds;
            DensitySpokeTick();
        }

        _snifferTimer -= Time.deltaTime;
        if (_snifferTimer <= 0f)
        {
            _snifferTimer = snifferIntervalSeconds;
            SnifferTick();
        }
    }

    // ----------------------------------------------------------------
    // Density spoke scan
    // ----------------------------------------------------------------

    /// <summary>
    /// Samples dust density along evenly-spaced spokes. Each dust cell is weighted
    /// by how hungry the star is for that cell's role — starving roles pull harder,
    /// already-charged roles pull less. The star drifts toward variety, not bulk.
    /// </summary>
    private void DensitySpokeTick()
    {
        var gfm  = GameFlowManager.Instance;
        var gen  = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null) { _hasDensityDir = false; return; }

        Vector2 origin = transform.position;
        float   step   = spokeLength / Mathf.Max(1, spokeSamples);
        float   bestScore = -1f;
        Vector2 bestDir   = Vector2.zero;

        for (int s = 0; s < spokeCount; s++)
        {
            float angle = s * (360f / spokeCount) * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

            float score = 0f;
            for (int p = 1; p <= spokeSamples; p++)
            {
                Vector2    sampleWorld = origin + dir * (step * p);
                Vector2Int cell        = drum.WorldToGridPosition(sampleWorld);

                if (!gen.HasDustAt(cell)) continue;

                // Weight by hunger: starving roles score 1.0, fully-charged roles score hungerFloorWeight.
                float weight = 1f;
                if (_star != null && gen.TryGetDustAt(cell, out var dust) && dust != null
                    && dust.Role != MusicalRole.None)
                {
                    float hunger = _star.GetRoleHunger(dust.Role);
                    // Remap: hunger 1 (starving) → weight 1.0, hunger 0 (full) → hungerFloorWeight
                    weight = Mathf.Lerp(hungerFloorWeight, 1f, hunger);
                }

                score += weight;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestDir   = dir;
            }
        }

        if (bestScore > 0f)
        {
            _densitySteerDir = bestDir;
            _hasDensityDir   = true;
        }
        else
        {
            _hasDensityDir = false;
        }

        if (verbose)
            Debug.Log($"[DensityNav] Spoke scan: bestScore={bestScore:F2} dir={_densitySteerDir}");
    }
    // ----------------------------------------------------------------
    // Sniffer: per-role nearest-dust direction
    // ----------------------------------------------------------------

    /// <summary>
    /// BFS outward from the star. For each playable role, find the nearest
    /// solid dust cell of that role and store the world-space direction to it.
    /// Traverses through open cells; dust cells terminate that branch but are
    /// evaluated for their role before stopping.
    /// </summary>
    private void SnifferTick()
    {
        var gfm  = GameFlowManager.Instance;
        var gen  = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        int w = drum.GetSpawnGridWidth();
        int h = drum.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        Vector2Int startCell = drum.WorldToGridPosition(transform.position);
        startCell.x = Mathf.Clamp(startCell.x, 0, w - 1);
        startCell.y = Mathf.Clamp(startCell.y, 0, h - 1);

        var found = new Dictionary<MusicalRole, Vector2Int>(4);

        _bfsQueue.Clear();
        _bfsVisited.Clear();
        _bfsQueue.Enqueue(startCell);
        _bfsVisited.Add(startCell);

        int visited = 0;
        while (_bfsQueue.Count > 0 && visited < snifferBfsBudget && found.Count < kPlayableRoles.Length)
        {
            var cell = _bfsQueue.Dequeue();
            visited++;

            for (int i = 0; i < kNeighbours4.Length; i++)
            {
                var nb = cell + kNeighbours4[i];
                if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;
                if (_bfsVisited.Contains(nb)) continue;

                _bfsVisited.Add(nb);

                if (gen.HasDustAt(nb))
                {
                    // Dust cell — record role, don't traverse further through it
                    MusicalRole role = GetDustRole(gen, nb);
                    if (role != MusicalRole.None && !found.ContainsKey(role))
                        found[role] = nb;
                    // don't enqueue — dust is a boundary
                    continue;
                }

                _bfsQueue.Enqueue(nb);
            }
        }

        // Convert found cells → normalized world-space directions
        Vector2 starWorld = transform.position;
        foreach (var kvp in found)
        {
            Vector2 cellWorld = drum.GridToWorldPosition(kvp.Value);
            Vector2 dir = cellWorld - starWorld;
            if (dir.sqrMagnitude > 0.0001f)
                _snifferDirs[kvp.Key] = dir.normalized;
        }

        if (verbose)
            Debug.Log($"[DensityNav] Sniffer found {found.Count} roles in {visited} cells.");
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static MusicalRole GetDustRole(CosmicDustGenerator gen, Vector2Int gp)
    {
        if (!gen.TryGetCellGo(gp, out var go) || go == null) return MusicalRole.None;
        if (!go.TryGetComponent<CosmicDust>(out var dust)) return MusicalRole.None;
        return dust.Role;
    }

    // ----------------------------------------------------------------
    // Gizmos
    // ----------------------------------------------------------------

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!_hasDensityDir) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(transform.position, _densitySteerDir * 1.5f);

        foreach (var kvp in _snifferDirs)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(transform.position, kvp.Value * 0.8f);
        }
    }
#endif
}
