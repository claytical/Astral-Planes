using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public partial class CosmicDustGenerator : MonoBehaviour
{
    private GameFlowManager _gfm;
    private CosmicDustRegrowthController _regrow;
    private bool _isBootstrappingMaze = false;
    public GameObject dustPrefab;
    public Transform activeDustRoot;
    Dictionary<Vector2Int, DustImprint> _imprints;
    private Dictionary<Vector2Int, MusicalRole> _hiddenImprints = new();
    private readonly Dictionary<Vector2Int, MusicalRole> _regrowExcludeRoleByCell = new Dictionary<Vector2Int, MusicalRole>(2048);
    // Cells carved by non-vehicle objects (e.g. SuperNodeTrackNode) that must regrow as MusicalRole.None
    // (gray) without revealing their hidden imprint. Cleared in CommitRegrowCell after role is resolved.
    private readonly HashSet<Vector2Int> _forceGrayRegrow = new();
    private bool _cellGridReady;
    [Header("Config")]
    [SerializeField] public CosmicDustGeneratorConfig config;
    public bool toroidal => config != null ? config.toroidal : false;
    public float cellClearanceWorld => config != null ? config.cellClearanceWorld : 0f;
    private bool _mazeAlreadyGenerated = false;
    private bool _regrowthSuppressed = false;
    struct DustImprint
    {
        public Color color;
        public float healDelay;
        public float carveResistance01;
        public float drainResistance01;
        public int   maxEnergyUnits;
        public MusicalRole role;
    }
    private struct DustResistanceProfile
    {
        public float carveResistance01;
        public float drainResistance01;
    }
    private readonly HashSet<string> _loggedInvalidResistanceContexts = new HashSet<string>();
    [Header("Dust Visual Timings")]
    [SerializeField] private DustVisualTimingSettings dustVisualTimingSettings;
    private DustVisualTimings DustTimings => dustVisualTimingSettings != null
        ? dustVisualTimingSettings.Timings
        : DustVisualTimings.Default;
    
    // --- Extracted controllers (refactor targets) ---
    private CosmicDustExclusionMap _exclusions = new CosmicDustExclusionMap();
    private CosmicDustTintDiffusionSystem _tintDiffusionSystem;
    private readonly List<Vector2Int> _tmpReleased = new List<Vector2Int>(512);
    private readonly List<Vector2Int> _tmpClaimed  = new List<Vector2Int>(512);

    [Header("Tile Sizing")]
    [SerializeField] private float tileDiameterWorld = 1f;          // cached from dustfab.hitbox

    // ------------------------------------------------------------------
    // Authoritative grid (no pooling). The grid is the traffic cop.
    // Ownership contract:
    // CosmicDustGenerator owns: grid cell state (DustGridState), imprint dictionary, regrowth schedule.
    // CosmicDust owns: visual fields (_currentTint, energy units, sprite scale, collision state).
    // Generator drives CosmicDust via public API; CosmicDust never queries Generator directly.
    // When recycling a cell: call PrepareForReuse() on the CosmicDust component AND update the
    // grid via SetCellState() — both sides must agree or queries to either will diverge.
    // ------------------------------------------------------------------
    private readonly DustGridState _gridState = new();
    private Dictionary<GameObject, Vector2Int> _goToCell = new Dictionary<GameObject, Vector2Int>(1024);


    private Dictionary<Vector2Int, bool> _fillMap = new();
    private MazePatternConfig _activeMazePattern;
    private readonly DustRegrowthScheduler _regrowthScheduler = new();
    private readonly MazeTopologyService _mazeTopologyService = new();
    private PhaseStarBehaviorProfile _activeProfile;

    private List<Vector2Int> _reservedVehicleCells = new List<Vector2Int>(64);

    private HashSet<Vector2Int> _permanentClearCells = new HashSet<Vector2Int>();
    private Dictionary<Vector2Int, float> _carveAccumulator = new();
    // Cells spawned by GrowVoidDustDiskFromGrid.
    // IMPORTANT: This set only affects vehicle carve behavior. Zap/drain clear paths can
    // still regrow these cells when their tuning says they should, so do not reuse this
    // set as a global "never regrow" rule.
    private readonly HashSet<Vector2Int> _voidGrowCells = new HashSet<Vector2Int>();
    // Ripeness: cells currently showing their true role color after a player carve.
    private readonly Dictionary<Vector2Int, float> _ripenessByCell = new();
    // Cells carved by the vehicle plow — only these start ripe on regrowth.
    private readonly HashSet<Vector2Int> _playerCarvedCells = new();
    // Reusable snapshot buffer for TickRipeness to avoid per-frame allocation.
    private List<Vector2Int> _ripenessKeys;
    private Coroutine _spawnRoutine;
    private DrumTrack drums;
    private PhaseTransitionManager phaseTransitionManager;
    public event Action<Vector2Int?> OnMazeReady;

    private readonly HashSet<Vector2Int> _starClearCells = new HashSet<Vector2Int>();

    // ---------------------------------------------------------------------------
    // Role density tracking
    // Counts how many Solid cells are currently imprinted with each MusicalRole.
    // Updated in SetCellState when a cell becomes or leaves Solid.
    // Used by CommitRegrowCell to pick the least-represented role when a cell
    // has no role imprint (e.g. vehicle-carved cells whose imprint was removed).
    // ---------------------------------------------------------------------------
    private List<MusicalRole> _activeRoles; // set from motif at phase start via ApplyActiveRoles
    private List<MazeRoleGeoConfig> _roleGeoConfigs;
    private MazePatternType _activePatternType = MazePatternType.FullFill;
    private readonly Dictionary<MusicalRole, int> _solidCountByRole = new Dictionary<MusicalRole, int>
    {
        { MusicalRole.Bass,    0 },
        { MusicalRole.Harmony, 0 },
        { MusicalRole.Lead,    0 },
        { MusicalRole.Groove,  0 },
        { MusicalRole.Rhythm,  0 },
    };
    // Counts ALL solid cells regardless of role (including MusicalRole.None).
    // _solidCountByRole excludes None-role cells, so TotalSolidCount() uses _gridState.AllSolidCount.
    // Density conservation: target solid count set at maze init; -1 = inactive.
    private int _targetSolidCount = -1;
    // Pattern oracle: wall cells intended by the current archetype. Null when no maze is active.
    private HashSet<Vector2Int> _mazePatternCells;
    // Cells currently transitioning in (Regrowing state). Added to Solid count
    // for committed-count checks so original cells are suppressed before
    // compensation cells finish growing in. Stored in _gridState.RegrowingCount.
    public float TileDiameterWorld
    {
        get => tileDiameterWorld;
        set => tileDiameterWorld = value;
    }
    [Header("Regrow Veto (Vehicle Overlap)")]
    [Tooltip("If a vehicle overlaps a cell, regrow/spawn is deferred to prevent collider penetration shoves.")]
    [SerializeField] private LayerMask vehicleMask;
    
    private Collider2D[] _vehicleVetoHits;    

    [SerializeField] private DustClaimManager dustClaims;
    private bool _runtimeVoidOnlyDustCreation;

    // Back-compat hooks (no-op now that composite rebuilds are removed).

    public enum DustInteractionMode
    {
        Carve,
        Zap
    }

    private readonly struct DustClearRequest
    {
        public readonly DustInteractionMode InteractionMode;
        public readonly DustClearMode ClearMode;
        public readonly float FadeSeconds;
        public readonly bool ScheduleRegrow;
        public readonly float RegrowDelaySeconds;
        public readonly bool RunPreExplode;

        public DustClearRequest(DustInteractionMode interactionMode, DustClearMode clearMode, float fadeSeconds, bool scheduleRegrow, float regrowDelaySeconds, bool runPreExplode)
        {
            InteractionMode = interactionMode;
            ClearMode = clearMode;
            FadeSeconds = fadeSeconds;
            ScheduleRegrow = scheduleRegrow;
            RegrowDelaySeconds = regrowDelaySeconds;
            RunPreExplode = runPreExplode;
        }
    }

    public enum DustClearMode
    {
        FadeAndHide,
        HideInstant
    }
    public void ClearCell(Vector2Int gp, DustClearMode mode, float fadeSeconds, bool scheduleRegrow, float regrowDelaySeconds = -1f, bool runPreExplode = false) {
        if (!TryGetCellState(gp, out var st)) return;
        if (st == DustCellState.Empty || st == DustCellState.Clearing || st == DustCellState.PendingRegrow) {
            // Optionally refresh regrow timer even if already empty.
            if (scheduleRegrow)
                RequestRegrowCellAt(gp, regrowDelaySeconds, refreshIfPending: true);
            return;
        }
        if (!TryGetCellGo(gp, out var go) || go == null) {
            SetCellState(gp, DustCellState.Empty);
            if (scheduleRegrow)
                RequestRegrowCellAt(gp, regrowDelaySeconds, refreshIfPending: true);
            return;
        }

        // Immediately stop being terrain.
        SetCellState(gp, DustCellState.Clearing); 
        if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null){ 
           SetDustCollision(dust, false);
        }

         // Visual policy
         if (dust != null) {
             if (runPreExplode)
             {
                 var explode = go.GetComponentInChildren<Explode>(true);
                 if (explode != null)
                 {
                     var tint = dust.CurrentTint;
                     tint.a = 1f;
                     explode.SetTint(tint);
                     explode.ZapExplode();
                 }
             }

             if (mode == DustClearMode.HideInstant){
                 if (runPreExplode)
                     StartCoroutine(DeferredHideAfterPreExplode(gp, dust));
                 else
                 {
                     dust.HideVisualsInstant();
                     SetCellState(gp, DustCellState.Empty);
                 }
             }
             else {
                if (runPreExplode)
                    StartCoroutine(DeferredDissipateAfterPreExplode(dust, DustTimings.clearSpriteScaleOutSeconds));
                else
                    dust.DissipateAndHideVisualOnly(Mathf.Max(0.01f, fadeSeconds));
                // OnDustVisualFadedOut will finalize Empty + hide visuals
             }
         }
         else { // Fallback: no CosmicDust component
             var col = go.GetComponent<Collider2D>();
         if (col) col.enabled = false;
         FadeAndHideCellGO(go);
         SetCellState(gp, DustCellState.Empty); 
         }

         if (scheduleRegrow)
         {
             RequestRegrowCellAt(gp, regrowDelaySeconds, refreshIfPending: true);
             // TODO: density conservation — fill a frontier cell when one is eroded so total
             // coverage stays constant. Deferred: TryQueueFrontierCompensation() interfered with
             // role-assignment logic when multiple stars were active. Re-enable only after
             // role-aware frontier selection is in place.
//             TryQueueFrontierCompensation();
         }
    }
    private IEnumerator DeferredDissipateAfterPreExplode(CosmicDust dust, float fadeSeconds)
    {
        yield return null;
        if (dust == null) yield break;
        dust.DissipateAndHideVisualOnly(fadeSeconds);
    }

    private IEnumerator DeferredHideAfterPreExplode(Vector2Int gp, CosmicDust dust)
    {
        yield return null;
        if (dust == null) yield break;
        dust.HideVisualsInstant();
        SetCellState(gp, DustCellState.Empty);
    }

    public void HardStopRegrowthForBridge(bool hideTransientDust = true)
    {
        _regrowthSuppressed = true;

        // Stop maze-growth/stagger routines tied to the outgoing motif.
        if (_spawnRoutine != null)
        {
            StopCoroutine(_spawnRoutine);
            _spawnRoutine = null;
        }

        // Stop any legacy per-cell regrowth coroutines if they still exist.
        if (_regrowthScheduler.RegrowthCoroutines != null)
        {
            foreach (var kv in _regrowthScheduler.RegrowthCoroutines)
            {
                if (kv.Value != null)
                    StopCoroutine(kv.Value);
            }
            _regrowthScheduler.RegrowthCoroutines.Clear();
        }

        // Stop any void-grow coroutines that might still be animating dust in.
        if (_regrowthScheduler.VoidGrowCoroutines != null)
        {
            foreach (var kv in _regrowthScheduler.VoidGrowCoroutines)
            {
                if (kv.Value != null)
                    StopCoroutine(kv.Value);
            }
            _regrowthScheduler.VoidGrowCoroutines.Clear();
        }

        // Dismiss transient cells from the outgoing motif using the same dissipation visual as
        // vehicle carving — no abrupt pop/cut.
        EnsureCellGrid();
        if (_gridState.CellState != null && _gridState.CellGo != null)
        {
            for (int x = 0; x < _gridState.Width; x++)
            {
                for (int y = 0; y < _gridState.Height; y++)
                {
                    var st = _gridState.CellState[x, y];
                    if (st == DustCellState.PendingRegrow)
                    {
                        // Not yet visible — instant hide is fine.
                        _gridState.CellState[x, y] = DustCellState.Empty;
                        var go = _gridState.CellGo[x, y];
                        if (hideTransientDust && go != null)
                            HideCellGO(go);
                    }
                    else if (st == DustCellState.Regrowing)
                    {
                        // Was growing in — fade out instead of cutting off.
                        _gridState.CellState[x, y] = DustCellState.Clearing;
                        var go = _gridState.CellGo[x, y];
                        if (hideTransientDust && go != null)
                            FadeAndHideCellGO(go);
                    }
                    else if (st == DustCellState.Clearing)
                    {
                        // DissipateAndHideVisualOnly is already running — let it complete.
                        // OnDustVisualFadedOut will transition the state to Empty.
                    }
                }
            }
        }

        // IMPORTANT:
        // Drop the controller entirely so any internal pending queue/timers are discarded.
        _regrow = null;
    }

     public void ResumeRegrowthAfterBridge()
    {
        _regrowthSuppressed = false;
        EnsureRegrowController();
    }

     public int GrowVoidDustDiskFromGrid(
        Vector2Int centerGP,
        int outerRadiusCells,
        MusicalRole imprintRole,
        Color hueRgb,
        float energyAtCenter01,
        float falloffExp,
        float growInSeconds,
        int fillWedges01To4,
        List<Vector2Int> vehicleCells,
        int vehicleNoSpawnRadiusCells,
        int maxCellsThisCall = -1,
        int innerRadiusCellsExclusive = -1,
        bool hideRole = false)
    {
        EnsureCellGrid();
        _imprints ??= new Dictionary<Vector2Int, DustImprint>(2048);

        if (outerRadiusCells <= 0) return 0;

        energyAtCenter01 = Mathf.Clamp01(energyAtCenter01);
        falloffExp = Mathf.Max(0.01f, falloffExp);
        growInSeconds = Mathf.Max(0.01f, growInSeconds);
        fillWedges01To4 = Mathf.Clamp(fillWedges01To4, 1, 4);

        int processed = 0;
        int rOuterSq = outerRadiusCells * outerRadiusCells;
        int rInnerSq = innerRadiusCellsExclusive >= 0
            ? innerRadiusCellsExclusive * innerRadiusCellsExclusive
            : -1;

        for (int dy = -outerRadiusCells; dy <= outerRadiusCells; dy++)
        {
            for (int dx = -outerRadiusCells; dx <= outerRadiusCells; dx++)
            {
                int dSq = dx * dx + dy * dy;
                if (dSq > rOuterSq) continue;
                if (rInnerSq >= 0 && dSq <= rInnerSq) continue;
                if (!IsInFilledWedge(dx, dy, fillWedges01To4)) continue;

                Vector2Int gp = new Vector2Int(centerGP.x + dx, centerGP.y + dy);
                if (!IsInBounds(gp)) continue;

                // Budget
                if (maxCellsThisCall >= 0 && processed >= maxCellsThisCall)
                    return processed;
    // -------------------------------
    // Radiating ring alpha (annulus-local)
    // Strong at inner edge of NEW ring, fades toward outer edge.
    // This matches incremental growth using innerRadiusCellsExclusive.
    // -------------------------------
                float d = Mathf.Sqrt(dSq);

                float inner = (innerRadiusCellsExclusive >= 0) ? innerRadiusCellsExclusive : 0f;
                float outer = Mathf.Max(1f, outerRadiusCells);
                float span  = Mathf.Max(0.0001f, outer - inner);

    // u=0 at inner edge of this ring, u=1 at outer edge
                float u = Mathf.Clamp01((d - inner) / span);

    // Energy: bright at u=0, fades toward u=1
                float energy01 = Mathf.Clamp01(energyAtCenter01 * Mathf.Pow(1f - u, falloffExp));

    // IMPORTANT: avoid "invisible SOLID" tiles (especially when logging with F2).
    // This is a *visual* floor; it also prevents sprite alpha=0 tiles that are physically present.
    // Set high enough that the role color reads clearly even at the outer edge of a burst.
                const float kMinVisibleAlpha = 0.55f;
                float visibleAlpha = Mathf.Max(energy01, kMinVisibleAlpha);

                Color c = hueRgb;
                c.a = visibleAlpha;
                if (dx == 0 && dy == outerRadiusCells) // pick a consistent sample
                {
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[VOID_RING] rIn={innerRadiusCellsExclusive} rOut={outerRadiusCells} d={d:F2} u={u:F2} a={c.a:F2}");
                }            
                // Vehicle pocket is a hard exclusion — no imprint, no spawn, no visual update.
                if (IsNearAnyVehicle(gp, vehicleCells, vehicleNoSpawnRadiusCells)) continue;

                // When hideRole is true, store role in _hiddenImprints and spawn gray so
                // PhaseStar cannot detect the cell until the vehicle reveals it by carving.
                MusicalRole spawnRole = imprintRole;
                Color spawnColor = c;
                if (hideRole && imprintRole != MusicalRole.None)
                {
                    _hiddenImprints ??= new Dictionary<Vector2Int, MusicalRole>();
                    _hiddenImprints[gp] = imprintRole;
                    spawnRole = MusicalRole.None;
                    spawnColor = config.mazeTint;
                    spawnColor.a = c.a;
                }

                // Always write persistent imprint (so regrow picks it up later)
                _imprints[gp] = new DustImprint
                {
                    color = spawnColor,
                    role = spawnRole
                };
                processed++;

    // 1) If dust already exists, ALWAYS refresh visuals (even if keep-clear/blocked/etc).
                if (TryGetCellGo(gp, out var existingGo) && existingGo != null &&
                    existingGo.TryGetComponent<CosmicDust>(out var existingDust) && existingDust != null &&
                    HasDustAt(gp))
                {
                    existingDust.ApplyRoleAndCharge(MusicalRole.None, config.mazeTint, c.a);
                    ApplyHiddenHintToDust(gp, existingDust);
                    var resistance = ResolveResistanceProfile(gp, imprintRole, context: "GrowVoidDustDisk:existing");
                    existingDust.clearing.drainResistance01 = resistance.drainResistance01;
                    continue;
                }

    // 2) Only after that, decide whether we’re allowed to SPAWN dust into empty space.
                if (_permanentClearCells.Contains(gp)) continue;
                if (IsKeepClearCell(gp)) continue;
                if (dustClaims != null && dustClaims.IsBlocked(gp)) continue;
                if (IsDustSpawnBlocked(gp)) continue;

    // 3) Spawn/regrow if empty
                if (_regrowthScheduler.VoidGrowCoroutines.ContainsKey(gp))
                    continue;

                _voidGrowCells.Add(gp);
                _regrowthScheduler.VoidGrowCoroutines[gp] = StartCoroutine(VoidGrowCellNow(gp, spawnRole, spawnColor, growInSeconds));
            }
        }

        return processed;
    }

    private static bool IsNearAnyVehicle(Vector2Int gp, System.Collections.Generic.List<Vector2Int> vehicleCells, int radiusCells)
    {
        if (radiusCells <= 0) return false;
        if (vehicleCells == null || vehicleCells.Count == 0) return false;

        for (int i = 0; i < vehicleCells.Count; i++)
        {
            var vc = vehicleCells[i];
            int dx = Mathf.Abs(gp.x - vc.x);
            int dy = Mathf.Abs(gp.y - vc.y);
            if (dx <= radiusCells && dy <= radiusCells) return true;
        }
        return false;
    }

    private static bool IsInFilledWedge(int dx, int dy, int fillWedges01To4)
    {
        // Quadrant order: 0=NE, 1=NW, 2=SW, 3=SE
        int quad;
        if (dy >= 0)
            quad = (dx >= 0) ? 0 : 1;
        else
            quad = (dx < 0) ? 2 : 3;

        return quad < fillWedges01To4;
    }

    // Appends trap ring/disk cells directly into the maze stagger list so they grow in
    // alongside every other cell. Call from the onBeforeGrowth callback inside
    // GenerateMazeForPhaseWithPaths — _imprints and _hiddenImprints are already initialised at
    // that point and will not be cleared again before the stagger runs.
    public void InjectTrapCellsIntoStagger(
        List<(Vector2Int, Vector3)> cellsToFill,
        Vector2Int centerGP,
        int outerRadiusCells,
        int innerRadiusCellsExclusive,
        MusicalRole hiddenRole)
    {
        if (drums == null || cellsToFill == null || outerRadiusCells <= 0) return;
        EnsureCellGrid();
        _imprints      ??= new Dictionary<Vector2Int, DustImprint>(2048);
        _hiddenImprints ??= new Dictionary<Vector2Int, MusicalRole>();

        int rOuterSq = outerRadiusCells * outerRadiusCells;
        int rInnerSq = innerRadiusCellsExclusive >= 0 ? innerRadiusCellsExclusive * innerRadiusCellsExclusive : -1;

        for (int dy = -outerRadiusCells; dy <= outerRadiusCells; dy++)
        {
            for (int dx = -outerRadiusCells; dx <= outerRadiusCells; dx++)
            {
                int dSq = dx * dx + dy * dy;
                if (dSq > rOuterSq) continue;
                if (rInnerSq >= 0 && dSq <= rInnerSq) continue;

                var gp = new Vector2Int(centerGP.x + dx, centerGP.y + dy);
                if (!IsInBounds(gp)) continue;
                if (_permanentClearCells.Contains(gp)) continue;

                // Hidden role — revealed only when the vehicle carves this cell.
                if (hiddenRole != MusicalRole.None)
                    _hiddenImprints[gp] = hiddenRole;

                _imprints[gp] = new DustImprint
                {
                    role              = MusicalRole.None,
                    color             = config.mazeTint,
                    carveResistance01 = 0f,
                    drainResistance01 = 0f,
                    maxEnergyUnits    = 1,
                    healDelay         = 0f,
                };

                Vector3 worldPos = drums.GridToWorldPosition(gp);
                cellsToFill.Add((gp, worldPos));
            }
        }
    }

    public void InjectTrapCellsFromList(
        List<(Vector2Int, Vector3)> cellsToFill,
        IEnumerable<Vector2Int> trapCells,
        MusicalRole hiddenRole)
    {
        if (drums == null || cellsToFill == null || trapCells == null) return;
        EnsureCellGrid();
        _imprints      ??= new Dictionary<Vector2Int, DustImprint>(256);
        _hiddenImprints ??= new Dictionary<Vector2Int, MusicalRole>();

        foreach (var gp in trapCells)
        {
            if (!IsInBounds(gp)) continue;
            if (_permanentClearCells.Contains(gp)) continue;

            if (hiddenRole != MusicalRole.None)
                _hiddenImprints[gp] = hiddenRole;

            _imprints[gp] = new DustImprint
            {
                role              = MusicalRole.None,
                color             = config.mazeTint,
                carveResistance01 = 0f,
                drainResistance01 = 0f,
                maxEnergyUnits    = 1,
                healDelay         = 0f,
            };

            Vector3 worldPos = drums.GridToWorldPosition(gp);
            cellsToFill.Add((gp, worldPos));
        }
    }

    public void SpawnDustAtCells(
        IReadOnlyList<Vector2Int> cells,
        MusicalRole role, Color hue, float energy01, float growInSeconds,
        bool hideRole = false)
    {
        if (cells == null || cells.Count == 0) return;
        EnsureCellGrid();
        _imprints ??= new Dictionary<Vector2Int, DustImprint>(256);
        const float kMinVisibleAlpha = 0.55f;
        Color c = hue;
        c.a = Mathf.Max(energy01, kMinVisibleAlpha);

        for (int i = 0; i < cells.Count; i++)
        {
            var gp = cells[i];
            if (!IsInBounds(gp)) continue;

            MusicalRole spawnRole = role;
            Color spawnColor = c;
            if (hideRole && role != MusicalRole.None)
            {
                _hiddenImprints ??= new Dictionary<Vector2Int, MusicalRole>();
                _hiddenImprints[gp] = role;
                spawnRole = MusicalRole.None;
                spawnColor = config.mazeTint;
                spawnColor.a = c.a;
            }

            _imprints[gp] = new DustImprint { color = spawnColor, role = spawnRole };

            if (TryGetCellGo(gp, out var existingGo) && existingGo != null &&
                existingGo.TryGetComponent<CosmicDust>(out var existingDust) &&
                existingDust != null && HasDustAt(gp))
            {
                existingDust.ApplyRoleAndCharge(MusicalRole.None, config.mazeTint, c.a);
                ApplyHiddenHintToDust(gp, existingDust);
                var res = ResolveResistanceProfile(gp, role, context: "SpawnDustAtCells:existing");
                existingDust.clearing.drainResistance01 = res.drainResistance01;
                continue;
            }

            if (_permanentClearCells.Contains(gp)) continue;
            if (IsKeepClearCell(gp)) continue;
            if (dustClaims != null && dustClaims.IsBlocked(gp)) continue;
            if (IsDustSpawnBlocked(gp)) continue;
            if (_regrowthScheduler.VoidGrowCoroutines.ContainsKey(gp)) continue;

            _voidGrowCells.Add(gp);
            _regrowthScheduler.VoidGrowCoroutines[gp] =
                StartCoroutine(VoidGrowCellNow(gp, spawnRole, spawnColor, growInSeconds));
        }
    }

    private void StopActiveStaggeredGrowth()
    {
        if (_spawnRoutine != null)
        {
            StopCoroutine(_spawnRoutine);
            _spawnRoutine = null;
        }
    }

    private void EnterRuntimeVoidOnlyDustCreationMode() { 
        _runtimeVoidOnlyDustCreation = true; 
        StopActiveStaggeredGrowth();
    }

    /// <summary>
    /// Erodes all Solid cells within <paramref name="radiusCells"/> of <paramref name="centerGP"/>
    /// so the gravity-void safety bubble zone is visually clear when the void begins.
    /// Cells may regrow normally after the void ends.
    /// </summary>
    public void ClearBubbleZone(Vector2Int centerGP, int radiusCells)
    {
        if (radiusCells <= 0 || _gridState.CellState == null) return;
        EnsureCellGrid();
        int rSq = radiusCells * radiusCells;
        for (int dy = -radiusCells; dy <= radiusCells; dy++)
        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            if (dx * dx + dy * dy > rSq) continue;
            var gp = new Vector2Int(centerGP.x + dx, centerGP.y + dy);
            if (!IsInBounds(gp)) continue;
            if (TryGetCellState(gp, out var st) && st == DustCellState.Solid)
                ClearCell(gp, DustClearMode.FadeAndHide, DustTimings.clearSpriteScaleOutSeconds, scheduleRegrow: true);
        }
    }

    /// <summary>
    /// Promotes hidden Solid cells within <paramref name="radiusCells"/> of <paramref name="centerGP"/>
    /// whose Voronoi role matches <paramref name="role"/> to their true role color,
    /// firing OnRoleChanged so PhaseStar can target them (player retry path after MineNode expiry).
    /// </summary>
    public void RevealHiddenDustByRole(Vector2Int centerGP, int radiusCells, MusicalRole role)
    {
        if (radiusCells <= 0 || _gridState.CellState == null || _hiddenImprints == null) return;

        var profile = MusicalRoleProfileLibrary.GetProfile(role);
        if (profile == null) return;
        Color roleColor = profile.GetBaseColor();

        int rSq = radiusCells * radiusCells;
        for (int dy = -radiusCells; dy <= radiusCells; dy++)
        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            if (dx * dx + dy * dy > rSq) continue;
            var gp = new Vector2Int(centerGP.x + dx, centerGP.y + dy);
            if (!IsInBounds(gp)) continue;
            if (!_hiddenImprints.TryGetValue(gp, out var hiddenRole) || hiddenRole != role) continue;
            if (!TryGetCellState(gp, out var st) || st != DustCellState.Solid) continue;
            if (!TryGetDustAt(gp, out var dust) || dust.Role != MusicalRole.None) continue;

            if (!PromoteHiddenRole(gp)) continue;
            dust.ApplyRoleAndCharge(role, roleColor, dust.Charge01);
        }
    }

    /// <summary>
    /// Called by CosmicDust.DrainCharge when a cell's visual alpha drops below the
    /// solid-visibility threshold (0.55). The cell is physically drained but was
    /// never explicitly "cleared" by gameplay — this bridges that gap so the
    /// collider doesn't linger as an invisible wall.
    /// </summary>
    private IEnumerator VoidGrowCellNow(Vector2Int gp, MusicalRole role, Color tintWithAlpha, float growInSeconds)
    {
        if (!IsInBounds(gp)) { _regrowthScheduler.VoidGrowCoroutines.Remove(gp); yield break; }

        bool veto0_perm        = _permanentClearCells.Contains(gp);
        bool veto0_spawnBlocked = IsDustSpawnBlocked(gp);
        bool veto0_claim       = (dustClaims != null && dustClaims.IsBlocked(gp));
        bool veto0_keep        = IsKeepClearCell(gp);

        // Permanent/spawn-block/claim are hard vetoes for even showing visuals.
        // Keep-clear is NOT a veto for visuals (it only prevents solid/collision).
        if (veto0_perm || veto0_spawnBlocked || veto0_claim)
        {
//            Debug.Log($"[VOID_GROW] ABORT_START gp={gp} perm={veto0_perm} keep={veto0_keep} spawnBlocked={veto0_spawnBlocked} claim={veto0_claim}");
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        var go = GetOrCreateCellGO(gp);
        if (go == null)
        {
//            Debug.Log($"[VOID_GROW] ABORT no-go gp={gp}");
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

//        Debug.Log($"[VOID_GROW] START gp={gp} growIn={growInSeconds:F2} a={tintWithAlpha.a:F2} role={role} keep={veto0_keep}");

        SetCellState(gp, DustCellState.Regrowing);

        CosmicDust dust = null;
        if (go.TryGetComponent(out dust) && dust != null)
        {
            dust.PrepareForReuse();
            dust.InitializeVisuals(DustTimings);

            dust.SetGrowInDuration(config.voidDustGrowInSeconds);
            var resistance = ResolveResistanceProfile(gp, role, context: "VoidGrowCellNow");
            dust.clearing.drainResistance01 = resistance.drainResistance01;
            dust.ApplyRoleAndCharge(role, tintWithAlpha, tintWithAlpha.a);
            if (role == MusicalRole.None) ApplyHiddenHintToDust(gp, dust);
            dust.SetFeedbackColors(Color.white, Color.darkGray);
            dust.regrowAlphaCapped = true;
            dust.Begin();
            EnsureDustSpriteRendererEnabled(dust);

            // Organic grow-in: start at maze gray and fade to role color over the full visual duration.
            Color dormantStart = config.mazeTint;
            dormantStart.a = tintWithAlpha.a;
            dust.ApplyTintVisual(dormantStart);
            dust.StartCoroutine(dust.TintFadeIn(config.voidDustGrowInSeconds, dormantStart, tintWithAlpha));

            // Always non-colliding during grow
            SetDustCollision(dust, false);
        }
        float enableDelay = Mathf.Max(config.regrowColliderEnableDelaySeconds, growInSeconds * 0.85f);
        yield return new WaitForSeconds(enableDelay);

        if (dust != null)
            EnsureDustSpriteRendererEnabled(dust);

        if (!IsInBounds(gp) || _permanentClearCells.Contains(gp))
        {
            SetCellState(gp, DustCellState.Empty);
            FadeAndHideCellGO(go);
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        bool veto1_spawnBlocked = IsDustSpawnBlocked(gp);
        bool veto1_vehicle      = IsVehicleOverlappingCell(gp);
        bool veto1_claim        = (dustClaims != null && dustClaims.IsBlocked(gp));
        bool veto1_keep         = IsKeepClearCell(gp);

        if (veto1_spawnBlocked || veto1_vehicle || veto1_claim)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[VOID_GROW] ABORT_END gp={gp} keep={veto1_keep} spawnBlocked={veto1_spawnBlocked} vehicle={veto1_vehicle} claim={veto1_claim}");
            SetCellState(gp, DustCellState.Empty);
            FadeAndHideCellGO(go);
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        // Keep-clear at end: allow visuals, but never become solid/colliding.
        if (veto1_keep)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[VOID_GROW] VISUAL_ONLY gp={gp} (keep-clear)");
            SetCellState(gp, DustCellState.Regrowing); // or define a VisualOnly state later
            if (dust != null) SetDustCollision(dust, false);
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        // Otherwise: become solid.
        SetCellState(gp, DustCellState.Solid);
        if (dust != null)
        {
            dust.regrowAlphaCapped = false;
            dust.EnsureMinSolidAlpha(0.55f);
            EnsureDustSpriteRendererEnabled(dust);
            SetDustCollision(dust, true);
        }

//        Debug.Log($"[VOID_GROW] SOLID gp={gp}");
        _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
    }

    private static void EnsureDustSpriteRendererEnabled(CosmicDust dust)
    {
        if (dust == null) return;
        var spriteRenderer = dust.GetComponentInChildren<SpriteRenderer>(true);
        if (spriteRenderer != null)
            spriteRenderer.enabled = true;
    }
    private void SetDustCollision(CosmicDust dust, bool _enabled)
        {
            if (dust == null) return;
            dust.SetTerrainColliderEnabled(_enabled);
        }

    public void DisableCellCollider(Vector2Int cell)
    {
        if (!IsInBounds(cell)) return;
        var dust = _gridState.CellDust?[cell.x, cell.y];
        if (dust != null) SetDustCollision(dust, false);
    }
    private void EnsureImprints()
    {
        if (_imprints == null)
            _imprints = new Dictionary<Vector2Int, DustImprint>();
    }

    /// <summary>
    /// If <paramref name="gp"/> is a gray (None-role) cell and has a hidden Voronoi role,
    /// promotes that role into the active imprint so regrowth uses it.
    /// Returns true when a promotion occurred.
    /// </summary>
    private void ApplyHiddenHintToDust(Vector2Int gp, CosmicDust dust)
    {
        if (_hiddenImprints == null || !_hiddenImprints.TryGetValue(gp, out var role) || role == MusicalRole.None)
        {
            dust.SetHiddenHintColor(Color.clear);
            return;
        }
        var profile = MusicalRoleProfileLibrary.GetProfile(role);
        dust.SetHiddenHintColor(profile != null ? profile.GetBaseColor() : Color.clear);
    }

    private bool PromoteHiddenRole(Vector2Int gp)
    {
        if (_hiddenImprints == null || !_hiddenImprints.TryGetValue(gp, out var hiddenRole)) return false;
        if (_imprints == null || !_imprints.TryGetValue(gp, out var imp)) return false;
        if (imp.role != MusicalRole.None) return false; // already colored — no-op

        imp.role = hiddenRole;
        _imprints[gp] = imp;
        // _hiddenImprints entry is kept as permanent Voronoi ground-truth for the motif lifetime.
        // RestoreVoronoiImprint() uses it to revert MineNode paint when a vehicle carves the cell.
        return true;
    }

    /// <summary>
    /// Clears any MineNode paint on a cell and re-promotes its permanent Voronoi role.
    /// Use this instead of PromoteHiddenRole when carving should revert to Voronoi regardless
    /// of whether a MineNode has already painted the cell.
    /// Returns true if a Voronoi assignment existed and was applied, false otherwise.
    /// </summary>
    private bool RestoreVoronoiImprint(Vector2Int gp)
    {
        if (_hiddenImprints == null || !_hiddenImprints.ContainsKey(gp)) return false;

        // Clear any MineNode paint so PromoteHiddenRole can re-apply the Voronoi role.
        if (_imprints != null && _imprints.TryGetValue(gp, out var imp) && imp.role != MusicalRole.None)
        {
            imp.role = MusicalRole.None;
            _imprints[gp] = imp;
        }
        PromoteHiddenRole(gp);
        return true;
    }

    private bool IsKeepClearCell(Vector2Int cell) => dustClaims != null && dustClaims.IsBlocked(cell);

    private static void FillDisk(HashSet<Vector2Int> result, Vector2Int center, int r, int w, int h)
    {
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        {
            if (dx * dx + dy * dy > r * r) continue;
            int x = center.x + dx; int y = center.y + dy;
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
            result.Add(new Vector2Int(x, y));
        }
    }

    public void SetVehicleKeepClear(int ownerId, Vector2Int centerCell, int radiusCells, bool forceRemoveExisting, float forceRemoveFadeSeconds = 0.20f)
{
    if (drums == null) return;

    int w = drums.GetSpawnGridWidth();
    int h = drums.GetSpawnGridHeight();
    if (w <= 0 || h <= 0) return;

    centerCell.x = Mathf.Clamp(centerCell.x, 0, w - 1);
    centerCell.y = Mathf.Clamp(centerCell.y, 0, h - 1);

    int r = Mathf.Max(0, radiusCells);

    // Build new footprint (disk).
    var next = new HashSet<Vector2Int>();
    FillDisk(next, centerCell, r, w, h);

    _tmpReleased.Clear();
    _tmpClaimed.Clear();
    _exclusions.UpdateVehicleFootprint(ownerId, next, _tmpReleased, _tmpClaimed);

    // DustClaimManager is the authority for keep-clear vetoes.
    string claimOwner = $"Vehicle#{ownerId}";
    if (dustClaims != null)
    {
        // Released cells: remove our keep-clear claim.
        for (int i = 0; i < _tmpReleased.Count; i++)
            dustClaims.ReleaseCell(claimOwner, _tmpReleased[i], DustClaimType.KeepClear);

        // Claimed cells: add/refresh keep-clear claim.
        for (int i = 0; i < _tmpClaimed.Count; i++)
            dustClaims.ClaimCell(claimOwner, _tmpClaimed[i], DustClaimType.KeepClear, seconds: -1f, refresh: true);
    }


    // Released: allow regrow.
    for (int i = 0; i < _tmpReleased.Count; i++)
    {
        var cell = _tmpReleased[i];
        if (_permanentClearCells.Contains(cell)) continue;
        RequestRegrowCellAt(cell, delaySeconds: -1f, refreshIfPending: true, clearImprintOnRefresh: false);
    }

    // Claimed: optionally force-remove dust for legibility (boosting behavior).
    if (forceRemoveExisting)
    {
        float fade = Mathf.Max(0.01f, forceRemoveFadeSeconds);

        for (int i = 0; i < _tmpClaimed.Count; i++)
        {
            var cell = _tmpClaimed[i];
            if (_permanentClearCells.Contains(cell)) continue;

            if (HasDustAt(cell))
            {
                // This path handles visual fade + authoritative state.
                CarveDustByVehicle(cell, fade);
            }

            // Optional: remove any persistent imprint so the pocket reads clean.
//            _imprints?.Remove(cell);
            _fillMap[cell] = false;

            // Ensure a regrow attempt exists (it will self-delay while the keep-clear claim is active).
            RequestRegrowCellAt(cell, delaySeconds: -1f, refreshIfPending: true, clearImprintOnRefresh: false);
        }
    }
}
    public void ReleaseVehicleKeepClear(int ownerId)
{
    _tmpReleased.Clear();
    _exclusions.ReleaseVehicleFootprint(ownerId, _tmpReleased);

    // DustClaimManager is the authority for keep-clear vetoes.
    string claimOwner = $"Vehicle#{ownerId}";
    if (dustClaims != null)
    {
        for (int i = 0; i < _tmpReleased.Count; i++)
            dustClaims.ReleaseCell(claimOwner, _tmpReleased[i], DustClaimType.KeepClear);
    }


    for (int i = 0; i < _tmpReleased.Count; i++)
    {
        var cell = _tmpReleased[i];
        if (_permanentClearCells.Contains(cell)) continue;
        RequestRegrowCellAt(cell, delaySeconds: -1f, refreshIfPending: true, clearImprintOnRefresh: false);
    }
}
    private void Start()
    {
        if (_vehicleVetoHits == null || _vehicleVetoHits.Length != config.regrowVetoMaxHits) 
            _vehicleVetoHits = new Collider2D[Mathf.Max(1, config.regrowVetoMaxHits)];
        // In some scenes the generator may be instantiated before the GameFlowManager
        // is fully ready. We do a best-effort bind here, and also lazily re-bind elsewhere.
        EnsureImprints();
        TryEnsureRefs();
        EnsureCellGrid();
        _regrowthScheduler.Initialize(config.regrowCellsPerStep, config.regrowColliderEnableDelaySeconds);
        EnsureRegrowController();
        // Init extracted systems (no pooling/legacy queues required).
        if (_tintDiffusionSystem == null)
            _tintDiffusionSystem = new CosmicDustTintDiffusionSystem(
                cell => { // If a MineNode has imprinted this cell, keep that tint stable until something else changes it.
                    if (_imprints != null && _imprints.ContainsKey(cell)) 
                        return null; 
                    TryGetDustAt(cell, out var d); 
                    return d;
                },
                GetCellVisualColor);

        // Flow field sizing is lazy and handled in Update once drums is valid.
    }
    private void EnsureRegrowController()
    {
     
        if (_regrow != null) return;
        if (drums == null) return;

        _regrow = new CosmicDustRegrowthController(
            host: this,

            isInBounds: IsInBounds,
            isPermanentClear: gp => _permanentClearCells.Contains(gp),
            hasDustAt: HasDustAt,
            isKeepClearCell: IsKeepClearCell,
            isDustSpawnBlocked: IsDustSpawnBlocked,
            isCollectableCellFree: gp => Collectable.IsCellFreeStatic(gp),
            isSpawnCellAvailable: gp => drums != null && drums.IsSpawnCellAvailable(gp.x, gp.y),
            isVehicleOverlappingCell: gp =>
            {
                if (drums == null) return false;
                float cellWorld = Mathf.Max(0.001f, drums.GetCellWorldSize());
                Vector3 world = drums.GridToWorldPosition(gp);
                return IsVehicleOverlappingCellWorld(world, cellWorld);
            },

            tryGetPendingRegrow: gp => TryGetCellState(gp, out var st) && st == DustCellState.PendingRegrow,
            setCellEmpty: gp => SetCellState(gp, DustCellState.Empty),
            setCellPendingRegrow: gp => SetCellState(gp, DustCellState.PendingRegrow),
            commitRegrowCell: gp => CommitRegrowCell(gp),

            getRegrowVetoRetryDelaySeconds: () => Mathf.Max(0.05f, config.regrowVetoRetryDelaySeconds),
            getRegrowCellsPerStep: () => Mathf.Max(0, config.regrowCellsPerStep)
        );
    }
    private bool IsVehicleOverlappingCellWorld(Vector3 cellWorld, float cellWorldSize)
    {
        Vector2 size = Vector2.one * Mathf.Max(0.001f, cellWorldSize * config.regrowVetoBoxMul);

        // NOTE: even if vehicleMask is broad/misconfigured, we only veto if a Vehicle is present.
        int hits = Physics2D.OverlapBoxNonAlloc(cellWorld, size, 0f, _vehicleVetoHits, vehicleMask);
        if (hits <= 0) return false;

        for (int i = 0; i < hits; i++)
        {
            var col = _vehicleVetoHits[i];
            if (col == null) continue;

            if (col.GetComponentInParent<Vehicle>() != null)
                return true;
        }

        return false;
    }
    private void TryEnsureRefs()
    {
        if (drums != null && phaseTransitionManager != null) return;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        if (_gfm == null) return;

        if (drums == null) drums = _gfm.activeDrumTrack;
        if (phaseTransitionManager == null) phaseTransitionManager = _gfm.phaseTransitionManager;
    }
    public void ManualStart()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        drums = _gfm.activeDrumTrack;
        TryEnsureRefs();
        EnsureRegrowController();
        if (dustClaims == null) dustClaims = FindObjectOfType<DustClaimManager>();
        if (_tintDiffusionSystem == null)
            _tintDiffusionSystem = new CosmicDustTintDiffusionSystem(
                cell =>
                {
                    TryGetDustAt(cell, out var d); 
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[DUST] {d.name} with {GetCellVisualColor(cell)}");
                    return d;
                },
                GetCellVisualColor);
        
    }
    
    private bool IsDustSpawnBlocked(Vector2Int cell)
    {
        return dustClaims != null && dustClaims.IsBlocked(cell);
    }
    void Update()
    {
        TryEnsureRefs();

        if (!_cellGridReady)
        {
            EnsureCellGrid();
            _cellGridReady = (_gridState.CellGo != null); // or a stronger condition
            if (!_cellGridReady) return;
        }

        if (!drums) return;
        if (_regrowthSuppressed)
            return;
        EnsureRegrowController();
        // Tint diffusion: keep visual seams soft around recent changes.
        ProcessTintDiffusion(Time.deltaTime);
        TickRipeness(Time.deltaTime);

        if (drums == null) return;


        // Regrow step gate: promote PendingRegrow -> Regrowing/Solid rhythmically on drum steps.
        _regrow?.ProcessStepGate(drums.currentStep);
    }

    private bool IsVehicleOverlappingCell(Vector2Int gp)
    {
        _goToCell ??= new Dictionary<GameObject, Vector2Int>(1024);
        _permanentClearCells ??= new HashSet<Vector2Int>();

        if (drums == null) return false;
        if (vehicleMask.value == 0) return false;

        float cellWorld = Mathf.Max(0.001f, drums.GetCellWorldSize());
        Vector2 center = drums.GridToWorldPosition(gp);
        Vector2 size = Vector2.one * (cellWorld * config.regrowVetoBoxMul);

        if (_vehicleVetoHits == null || _vehicleVetoHits.Length != config.regrowVetoMaxHits)
            _vehicleVetoHits = new Collider2D[Mathf.Max(1, config.regrowVetoMaxHits)];

        int hits = Physics2D.OverlapBoxNonAlloc(center, size, 0f, _vehicleVetoHits, vehicleMask);
        if (hits <= 0) return false;

        for (int i = 0; i < hits; i++)
        {
            var col = _vehicleVetoHits[i];
            if (col == null) continue;

            if (col.GetComponentInParent<Vehicle>() != null)
                return true;
        }

        return false;
    }
    private IEnumerator CommitRegrowCell(Vector2Int gp)
    {
        if (_regrowthSuppressed)
        {
            if (TryGetCellGo(gp, out var existing) && existing != null)
                HideCellGO(existing);

            SetCellState(gp, DustCellState.Empty);
            yield break;
        }

        // ---------------------------------------------------------------
        // VEHICLE GATE: never begin grow-in while a vehicle sits on the cell.
        // Re-enqueue as PendingRegrow so the step gate retries later.
        // ---------------------------------------------------------------
        if (IsVehicleOverlappingCell(gp))
        {
            SetCellState(gp, DustCellState.PendingRegrow);
            EnqueueStepRegrow(gp);
            yield break;
        }

        // Density gate: a frontier compensation cell already restored coverage for this
        // erosion event. Suppress the original cell so dust "shifts" rather than doubling.
        
        var go = GetOrCreateCellGO(gp);
        if (go == null) yield break;
        if (!go.activeSelf)
            go.SetActive(true);
        // Visual comes back first.
        SetCellState(gp, DustCellState.Regrowing);
        CosmicDust dust = null;
        MusicalRole regrowRole = MusicalRole.None;
        _playerCarvedCells.Remove(gp);
        if (go.TryGetComponent<CosmicDust>(out dust) && dust != null)
        {
            dust.PrepareForReuse();
            dust.InitializeVisuals(DustTimings);
            dust.SetGrowInDuration(DustTimings.regrowParticleGrowInSeconds);

            regrowRole = ResolveRegrowRole(gp);
            _forceGrayRegrow.Remove(gp);

            // --- Color from role profile (authoritative source) ---
            var roleProfile = MusicalRoleProfileLibrary.GetProfile(regrowRole);
            Color regrowTint = (roleProfile != null) ? roleProfile.GetRandomVoiceColor() : config.mazeTint;

            // Write / update the imprint so future regrows of this cell remember the role.
            _imprints ??= new Dictionary<Vector2Int, DustImprint>();
            _imprints[gp] = new DustImprint
            {
                color               = regrowTint,
                carveResistance01   = roleProfile != null ? roleProfile.GetCarveResistance01() : 0f,
                drainResistance01   = roleProfile != null ? roleProfile.GetDrainResistance01() : 0f,
                maxEnergyUnits      = roleProfile != null ? roleProfile.maxEnergyUnits : 1,
                role                = regrowRole,
                healDelay           = 0f
            };

            var resistance = ResolveResistanceProfile(gp, regrowRole, context: "CommitRegrowCell");
            dust.clearing.drainResistance01 = resistance.drainResistance01;
            int maxUnits = roleProfile != null ? roleProfile.maxEnergyUnits : 1;
            dust.ApplyRoleAndCharge(regrowRole, regrowTint, regrowTint.a, maxUnits);

            if (regrowRole == MusicalRole.None)
                ApplyHiddenHintToDust(gp, dust);

            Color denyColor = Color.darkGray;
            if (roleProfile != null)
            {
                var shadow = roleProfile.dustColors.shadowColor;
                denyColor = (shadow != Color.clear && shadow != Color.magenta) ? shadow : Color.darkGray;
            }
            dust.SetFeedbackColors(Color.white, denyColor);
            dust.regrowAlphaCapped = true;
            dust.Begin();
            SetDustCollision(dust, false);
        }

        // Let the visual settle before collisions are reintroduced.
        if (config.regrowColliderEnableDelaySeconds > 0f)
            yield return new WaitForSeconds(config.regrowColliderEnableDelaySeconds);

        // Abort if conditions changed.
        if (_permanentClearCells.Contains(gp))
        {
            SetCellState(gp, DustCellState.Empty);
            FadeAndHideCellGO(go);
            yield break;
        }

        if (IsKeepClearCell(gp) || IsDustSpawnBlocked(gp) || IsVehicleOverlappingCell(gp))
        {
            SetCellState(gp, DustCellState.PendingRegrow);
            FadeAndHideCellGO(go);              // stays active, but invisible + non-colliding
            EnqueueStepRegrow(gp);
            yield break;
        }

        SetCellState(gp, DustCellState.Solid);
        if (dust != null)
        {
            dust.regrowAlphaCapped = false;
            dust.EnsureMinSolidAlpha(0.55f);
            SetDustCollision(dust, true);
        }
        _regrowExcludeRoleByCell.Remove(gp);

        if (regrowRole != MusicalRole.None)
            _ripenessByCell[gp] = 1f;
    }

    private void EnqueueStepRegrow(Vector2Int gp)
    {
        EnsureRegrowController();
        _regrow?.EnqueueStepRegrow(gp);
    }


    // ------------------------------------------------------------------
    // Authoritative cell grid helpers
    // ------------------------------------------------------------------
    private void EnsureCellGrid()
{
    _goToCell ??= new Dictionary<GameObject, Vector2Int>(1024);
    _permanentClearCells ??= new HashSet<Vector2Int>();
    _imprints ??= new Dictionary<Vector2Int, DustImprint>(256);

    _reservedVehicleCells ??= new List<Vector2Int>(64);

    _exclusions ??= new CosmicDustExclusionMap();

    TryEnsureRefs();

    if (!drums)
    {
        drums = FindObjectOfType<DrumTrack>();
        if (!drums) return;
    }

    int w, h;
    try
    {
        w = drums.GetSpawnGridWidth();
        h = drums.GetSpawnGridHeight();
    }
    catch (NullReferenceException)
    {
        return;
    }

    if (w <= 0 || h <= 0) return;
    if (_gridState.CellGo != null && _gridState.Width == w && _gridState.Height == h) return;

    _gridState.Allocate(w, h);
    _goToCell.Clear();

    var root = (activeDustRoot != null) ? activeDustRoot : transform;
    var existing = root.GetComponentsInChildren<CosmicDust>(true);

    for (int i = 0; i < existing.Length; i++)
    {
        var d = existing[i];
        if (d == null) continue;

        var go = d.gameObject;
        Vector2Int gp = drums.WorldToGridPosition(go.transform.position);
        if ((uint)gp.x >= (uint)w || (uint)gp.y >= (uint)h) continue;

        if (_gridState.CellGo[gp.x, gp.y] != null && _gridState.CellGo[gp.x, gp.y] != go)
        {
            FadeAndHideCellGO(go);
            continue;
        }

        _gridState.CellGo[gp.x, gp.y] = go;
        _gridState.CellDust[gp.x, gp.y] = d;
        _goToCell[go] = gp;

        go.transform.position = drums.GridToWorldPosition(gp);
        d.SetCellSizeDrivenScale(Mathf.Max(0.001f, drums.GetCellWorldSize()), config.dustFootprintMul, cellClearanceWorld);

        bool blocks =
            d.terrainCollider != null &&
            d.terrainCollider.enabled &&
            !d.terrainCollider.isTrigger &&
            go.activeInHierarchy;

        _gridState.CellState[gp.x, gp.y] = blocks ? DustCellState.Solid : DustCellState.Empty;
    }
}

    public bool TryGetCellGo(Vector2Int gp, out GameObject go)
    {
        EnsureCellGrid();
        go = null;
        if (_gridState.CellGo == null) return false;
        if (toroidal) gp = WrapCell(gp);
        if ((uint)gp.x >= (uint)_gridState.Width || (uint)gp.y >= (uint)_gridState.Height) return false;
        go = _gridState.CellGo[gp.x, gp.y];
        return go != null;
    }

    public bool TryGetCellState(Vector2Int gp, out DustCellState st)
    {
        EnsureCellGrid();
        return _gridState.TryGetCellState(gp, toroidal, WrapCell, out st);
    }
    private void SetCellState(Vector2Int gp, DustCellState st)
    {
        EnsureCellGrid();
        _gridState.SetCellState(gp, st, toroidal, WrapCell, (cell, becomesSolid) => TrackRoleDensityChange(cell, becomesSolid));
    }
    /// <summary>
    /// Called by CosmicDust when its fade-out completes. Finalizes the
    /// authoritative cell as Empty and hides the GO. This replaces pooling.
    /// </summary>
    public bool HasDustAt(Vector2Int gridPos)
    {
        return TryGetCellState(gridPos, out var st) && st == DustCellState.Solid;
    }

    /// <summary>
    /// Populates <paramref name="results"/> with every Solid cell whose CosmicDust role is not None.
    /// Clears the list before filling.
    /// </summary>
    public void GetColoredDustCells(List<Vector2Int> results)
    {
        results.Clear();
        if (_gridState.CellState == null || _gridState.CellDust == null) return;
        for (int y = 0; y < _gridState.Height; y++)
        for (int x = 0; x < _gridState.Width; x++)
        {
            if (_gridState.CellState[x, y] != DustCellState.Solid) continue;
            var dust = _gridState.CellDust[x, y];
            if (dust != null && dust.Role != MusicalRole.None && dust.currentEnergyUnits > 0 && dust.IsVisuallyPresentForTargeting())
                results.Add(new Vector2Int(x, y));
        }
    }
    // Carve mode (Vehicle/MineNode):
    // - Role imprint changes are allowed before clearing (e.g. MineNode paint restore/promote).
    // - Regrow is normally scheduled, except for permanent clear systems that explicitly disable it.
    // - Void-grown exception applies: vehicle carve removes void-grow imprint so regrow can re-resolve role.
    // - Visual fade duration is caller-provided (resistance/tuning aware).
    public void CarveCellPreserveGray(Vector2Int cell, float fadeSeconds, float regrowDelaySeconds = 2f)
    {
        if (!IsInBounds(cell)) return;
        _forceGrayRegrow.Add(cell);
        CarveCell(cell, fadeSeconds, scheduleRegrow: true, regrowDelaySeconds: regrowDelaySeconds, runPreExplode: false);
    }

    public void CarveCell(Vector2Int cell, float fadeSeconds, bool scheduleRegrow = true, float regrowDelaySeconds = -1f, bool runPreExplode = true)
    {
        var req = new DustClearRequest(DustInteractionMode.Carve, DustClearMode.FadeAndHide, fadeSeconds, scheduleRegrow, regrowDelaySeconds, runPreExplode);
        ClearCellByInteraction(cell, req);
    }

    // Zap mode (PhaseStar tentacles):
    // - Role imprint is not mutated on clear; stars consume current state discretely.
    // - Regrow uses zap tuning (delay can be explicit or role/default when -1).
    // - Void-grown exception does NOT apply: zap can regrow void-grown cells when scheduling allows.
    // - Visual fade duration comes from config.zapFadeSeconds tuning.
    public void ZapCell(Vector2Int cell)
    {
        var req = new DustClearRequest(DustInteractionMode.Zap, DustClearMode.FadeAndHide, config.zapFadeSeconds, true, config.zapRegrowDelaySeconds, true);
        ClearCellByInteraction(cell, req);
    }

    private void ClearCellByInteraction(Vector2Int cell, in DustClearRequest request)
    {
        if (!IsInBounds(cell)) return;
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        ClearCell(
            cell,
            request.ClearMode,
            request.FadeSeconds,
            request.ScheduleRegrow,
            regrowDelaySeconds: request.RegrowDelaySeconds,
            runPreExplode: request.RunPreExplode);
    }

    public void ZapClearCell(Vector2Int cell)
    {
        if (!IsInBounds(cell)) return;
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        ZapCell(cell);
    }

    
    private bool IsInBounds(Vector2Int gp) {
        if (drums == null) return false;
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return false;
        if (toroidal) return true; // every coordinate is valid after wrapping
        return gp.x >= 0 && gp.y >= 0 && gp.x < w && gp.y < h;
    }

    /// <summary>
    /// Wraps a grid coordinate toroidally when <see cref="toroidal"/> is enabled.
    /// Returns the input unchanged in non-toroidal mode.
    /// </summary>
    public Vector2Int WrapCell(Vector2Int gp)
    {
        if (!toroidal || _gridState.Width <= 0 || _gridState.Height <= 0) return gp;
        return new Vector2Int(
            ((gp.x % _gridState.Width) + _gridState.Width) % _gridState.Width,
            ((gp.y % _gridState.Height) + _gridState.Height) % _gridState.Height);
    }
    
    public bool IsPermanentlyClearCell(Vector2Int gridPos)
    {
        return _permanentClearCells.Contains(gridPos);
    }
    private void DespawnDustAtAndMarkPermanent(Vector2Int gridPos)
    {
        bool wasAlreadyPermanent = _permanentClearCells.Contains(gridPos);
        _permanentClearCells.Add(gridPos);
        
        DespawnDustAt(gridPos);
        _regrow?.CancelRegrow(gridPos);
    }
    private Color GetCellVisualColor(Vector2Int cell)
    {
        // MineNode imprint should override everything while it's present.
        if (_imprints != null && _imprints.TryGetValue(cell, out var imp))
            return imp.color;

        // Only treat live dust tint as authoritative when the cell is actually Solid.
        // (During PendingRegrow/Regrowing/Clearing, the dust GO may exist but its tint/alpha
        // is in flux and should not override maze tint.)
        if (TryGetCellState(cell, out var st) && st == DustCellState.Solid)
        {
            if (TryGetDustAt(cell, out var dust) && dust != null)
                return dust.CurrentTint;
        }

        // Default: current phase maze tint (PhaseStarBehaviorProfile.mazeColor).
        return config.mazeTint;
    }

    private DustResistanceProfile ResolveResistanceProfile(Vector2Int cell, MusicalRole fallbackRole, string context)
    {
        // Determine the most authoritative role for this cell, from most to least specific:
        //   hidden imprint (pre-reveal true identity) > revealed imprint role > caller fallback
        MusicalRole role = fallbackRole;

        if (_imprints != null && _imprints.TryGetValue(cell, out var imp) && imp.role != MusicalRole.None)
            role = imp.role;

        if (_hiddenImprints != null && _hiddenImprints.TryGetValue(cell, out var hiddenRole) && hiddenRole != MusicalRole.None)
            role = hiddenRole;

        // Live profile is always authoritative when a role is known.
        if (role != MusicalRole.None)
        {
            var roleProfile = MusicalRoleProfileLibrary.GetProfile(role);
            if (roleProfile != null)
                return ValidateResistanceProfile(new DustResistanceProfile
                {
                    carveResistance01 = roleProfile.GetCarveResistance01(),
                    drainResistance01 = roleProfile.GetDrainResistance01()
                }, $"{context}:live:{role}");
        }

        // Fallback: baked imprint values (None-role cells, trap cells with explicit overrides).
        if (_imprints != null && _imprints.TryGetValue(cell, out var baked))
            return ValidateResistanceProfile(new DustResistanceProfile
            {
                carveResistance01 = baked.carveResistance01,
                drainResistance01 = baked.drainResistance01
            }, $"{context}:baked:{cell.x},{cell.y}");

        return ValidateResistanceProfile(new DustResistanceProfile(), $"{context}:default");
    }

    private DustResistanceProfile ValidateResistanceProfile(DustResistanceProfile profile, string context)
    {
        float carve = Mathf.Clamp01(profile.carveResistance01);
        float drain = Mathf.Clamp01(profile.drainResistance01);
        if ((carve != profile.carveResistance01 || drain != profile.drainResistance01)
            && _loggedInvalidResistanceContexts.Add(context))
        {
            Debug.LogWarning($"[DustResistance] Clamped invalid resistance data at {context}. carve={profile.carveResistance01:F3}->{carve:F3}, drain={profile.drainResistance01:F3}->{drain:F3}");
        }
        profile.carveResistance01 = carve;
        profile.drainResistance01 = drain;
        return profile;
    }
    public float GetLiveCarveResistance01(Vector2Int cell)
    {
        if (!TryGetDustAt(cell, out var dust) || dust == null) return 0f;
        return ResolveResistanceProfile(cell, dust.Role, "VelocityDrain").carveResistance01;
    }

    // ---------------------------------------------------------------------------
    // Role density helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Call when a cell's Solid state changes so role density stays current.
    /// Pass the cell's imprinted role (MusicalRole.None = untracked).
    /// </summary>
    private void TrackRoleDensityChange(Vector2Int gp, bool becomesSolid)
    {
        MusicalRole role = MusicalRole.None;
        if (_imprints != null && _imprints.TryGetValue(gp, out var imp))
            role = imp.role;
        // Also check the live dust object in case the imprint was removed but Role was set.
        if (role == MusicalRole.None && TryGetDustAt(gp, out var d) && d != null)
            role = d.Role;

        if (role == MusicalRole.None) return;
        if (!_solidCountByRole.ContainsKey(role)) return;

        if (becomesSolid)
            _solidCountByRole[role] = Mathf.Max(0, _solidCountByRole[role] + 1);
        else
            _solidCountByRole[role] = Mathf.Max(0, _solidCountByRole[role] - 1);
    }

    /// <summary>
    /// Returns the playable role with the fewest solid cells.
    /// Falls back to a random role if all counts are equal (avoids deterministic bias).
    /// </summary>
    private MusicalRole GetLeastDenseRole()
    {
        MusicalRole best = MusicalRole.Bass;
        int bestCount = int.MaxValue;

        // Use only motif-active roles so regrowth doesn't introduce roles the motif doesn't use.
        var roles = (_activeRoles != null && _activeRoles.Count > 0)
            ? new List<MusicalRole>(_activeRoles)
            : new List<MusicalRole> { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm };

        // Shuffle order for tie-breaking
        for (int i = roles.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (roles[i], roles[j]) = (roles[j], roles[i]);
        }
        foreach (var r in roles)
        {
            int cnt = _solidCountByRole.TryGetValue(r, out var c) ? c : 0;
            if (cnt < bestCount) { bestCount = cnt; best = r; }
        }
        return best;
    }

    private MusicalRole GetLeastDenseRoleExcluding(MusicalRole excluded)
    {
        MusicalRole best = MusicalRole.None;
        int bestCount = int.MaxValue;

        // Use only motif-active roles so regrowth doesn't introduce roles the motif doesn't use.
        var roles = (_activeRoles != null && _activeRoles.Count > 0)
            ? (IReadOnlyList<MusicalRole>)_activeRoles
            : new[] { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm };

        foreach (var r in roles)
        {
            if (r == excluded) continue;
            int cnt = _solidCountByRole.TryGetValue(r, out var c) ? c : 0;
            if (cnt < bestCount)
            {
                bestCount = cnt;
                best = r;
            }
            else if (cnt == bestCount && Random.value > 0.5f)
            {
                best = r; // random tie-break
            }
        }

        // If excluded was the only active role, ignore the exclusion rather than
        // returning None or a role outside the motif's active set.
        if (best == MusicalRole.None)
            best = GetLeastDenseRole();

        return best;
    }
    private int TotalSolidCount()
    {
        // _gridState.AllSolidCount tracks ALL solid cells including MusicalRole.None.
        // _solidCountByRole only tracks non-None roles, so it can't be used here
        // after the gray-start change where all initial cells have role=None.
        return _gridState.AllSolidCount;
    }
    
    private MusicalRole ResolveRegrowRole(Vector2Int gp)
    {
        MusicalRole excludedRole = MusicalRole.None;
        if (_regrowExcludeRoleByCell.TryGetValue(gp, out var ex))
            excludedRole = ex;

        if (_imprints != null && _imprints.TryGetValue(gp, out var existingImp))
        {
            if (existingImp.role == MusicalRole.None)
            {
                // Gray-start cell: consult hidden imprint before giving up,
                // unless a non-vehicle carve has requested the cell stay gray.
                if (!_forceGrayRegrow.Contains(gp))
                {
                    if (_hiddenImprints != null && _hiddenImprints.TryGetValue(gp, out var hidden)
                        && hidden != MusicalRole.None && IsRoleActive(hidden))
                        return hidden;
                }
                // No active hidden imprint (or force-gray suppressed it) — fall through to neighbor/density logic.
            }
            else if (IsRoleActive(existingImp.role))
                return existingImp.role;
        }

        // No imprint or imprint role is inactive: fall back to neighbor plurality / least dense.
        var neighborRole = GetPluralityNeighborRole(gp, excludedRole);
        return neighborRole != MusicalRole.None ? neighborRole : GetLeastDenseRoleExcluding(excludedRole);
    }

    /// <summary>
    /// Returns the role held by the plurality of solid imprinted neighbors within 1 cell.
    /// Ties broken by global density (least-dense wins). Returns None when no imprinted
    /// solid neighbors exist.
    /// </summary>
    private MusicalRole GetPluralityNeighborRole(Vector2Int cell, MusicalRole excluded)
    {
        if (_imprints == null) return MusicalRole.None;

        int bassC = 0, harmC = 0, leadC = 0, grooveC = 0, rhythmC = 0;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var n = new Vector2Int(cell.x + dx, cell.y + dy);
            if (!TryGetCellState(n, out var st) || st != DustCellState.Solid) continue;
            if (!_imprints.TryGetValue(n, out var imp)
                || imp.role == MusicalRole.None || imp.role == excluded
                || !IsRoleActive(imp.role)) continue;
            switch (imp.role)
            {
                case MusicalRole.Bass:    bassC++;    break;
                case MusicalRole.Harmony: harmC++;    break;
                case MusicalRole.Lead:    leadC++;    break;
                case MusicalRole.Groove:  grooveC++;  break;
                case MusicalRole.Rhythm:  rhythmC++;  break;
            }
        }

        int max = Mathf.Max(bassC, Mathf.Max(harmC, Mathf.Max(leadC, Mathf.Max(grooveC, rhythmC))));
        if (max == 0) return MusicalRole.None;

        // Among tied leaders, prefer the globally least-dense (secondary balance signal).
        MusicalRole best = MusicalRole.None;
        int bestDensity = int.MaxValue;
        UpdatePluralityBest(MusicalRole.Bass,    bassC,    max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Harmony, harmC,    max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Lead,    leadC,    max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Groove,  grooveC,  max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Rhythm,  rhythmC,  max, ref best, ref bestDensity);
        return best;
    }

    private void UpdatePluralityBest(
        MusicalRole role, int count, int max,
        ref MusicalRole best, ref int bestDensity)
    {
        if (count != max) return;
        int d = _solidCountByRole.TryGetValue(role, out int cv) ? cv : 0;
        if (d < bestDensity) { bestDensity = d; best = role; }
    }
    public bool TryGetDustAt(Vector2Int cell, out CosmicDust dust) {
        dust = null;
        if (!TryGetCellGo(cell, out var go) || go == null) return false;
        return go.TryGetComponent(out dust) && dust != null;
    }

    public Color MazeColor()
    {
        return config.mazeTint;
    }
    public void ApplyActiveRoles(IReadOnlyList<MusicalRole> roles)
    {
        _activeRoles = roles != null && roles.Count > 0
            ? new List<MusicalRole>(roles)
            : new List<MusicalRole> { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm };
    }

    public void ApplyMotifGeoConfig(MotifProfile motif)
    {
        _roleGeoConfigs = motif?.roleGeoConfigs != null && motif.roleGeoConfigs.Count > 0
            ? new List<MazeRoleGeoConfig>(motif.roleGeoConfigs)
            : null;
        _activePatternType = motif?.mazePattern?.patternType ?? MazePatternType.FullFill;
    }

    public MusicalRole GetZoneRole(Vector2Int cell)
    {
        if (_hiddenImprints != null && _hiddenImprints.TryGetValue(cell, out var r))
            return r;
        return MusicalRole.None;
    }

    private bool TryGetGeoConfig(MusicalRole role, out MazeRoleGeoConfig config)
    {
        config = null;
        if (_roleGeoConfigs == null) return false;
        for (int i = 0; i < _roleGeoConfigs.Count; i++)
            if (_roleGeoConfigs[i].role == role) { config = _roleGeoConfigs[i]; return true; }
        return false;
    }

    private MazeGeoFeature ResolveGeoFeature(MusicalRole role, int roleIndex)
    {
        if (TryGetGeoConfig(role, out var cfg)) return cfg.feature;

        return _activePatternType switch
        {
            MazePatternType.RingChokepoints => MazeGeoFeature.Rings,
            MazePatternType.DrunkenStrokes  => MazeGeoFeature.Archipelago,
            MazePatternType.DiagonalLanes   => MazeGeoFeature.Archipelago,
            MazePatternType.ClearBoxes      => roleIndex == 0 ? MazeGeoFeature.Glade : MazeGeoFeature.Continent,
            MazePatternType.Tunnels         => roleIndex == 0 ? MazeGeoFeature.Ridge : MazeGeoFeature.Continent,
            _                               => MazeGeoFeature.Continent,
        };
    }

    private MazeRoleGeoConfig ResolveGeoConfig(MusicalRole role)
    {
        TryGetGeoConfig(role, out var cfg);
        return cfg;
    }

    // Returns true if the role is present in the current motif's active role list.
    // When no active roles are set (fallback), all roles are considered active.
    private bool IsRoleActive(MusicalRole role)
    {
        if (_activeRoles == null || _activeRoles.Count == 0) return true;
        return _activeRoles.Contains(role);
    }

    public void ApplyProfile(PhaseStarBehaviorProfile profile)
    {
        if (profile == null) return;

        _activeProfile = profile;
        _loggedInvalidResistanceContexts.Clear();

        // Authoritative default: phase-authored maze tint.
//        config.mazeTint = profile.mazeColor;

        // If dust already exists (e.g., generator persists between phases), immediately
        // nudge visuals to match the new profile so we don't leave any tiles at prefab/default.
        RetintExisting(seconds: 0.20f);
    }
    
    public void RetintExisting(float seconds = 0.35f) {
        if (!isActiveAndEnabled) return;
        // If this generator is active but its GameObject is not in hierarchy (parent disabled), also bail.
        if (!gameObject.activeInHierarchy) return;

        // Primary: iterate the authoritative grid.
        if (_cellGridReady && _gridState.CellGo != null)
        {
            for (int x = 0; x < _gridState.Width; x++)
            {
                for (int y = 0; y < _gridState.Height; y++)
                {
                    var go = _gridState.CellGo[x, y];
                    if (!go) continue;
                    if (!go.activeInHierarchy) continue;

                    var d = _gridState.CellDust != null ? _gridState.CellDust[x, y] : go.GetComponent<CosmicDust>();
                    if (d == null) continue;
                    if (!d.isActiveAndEnabled) continue;

                    // Skip cells that carry a role imprint — their color is authoritative
                    // (set by BuildMazeRoleImprints / MineNode carve). Overwriting them with
                    // config.mazeTint (a flat gray) would erase the 4-color Voronoi layout.
                    var gp = new Vector2Int(x, y);
                    if (_imprints != null && _imprints.TryGetValue(gp, out var imp)
                        && imp.role != MusicalRole.None)
                        continue;

                    StartCoroutine(d.RetintOver(seconds, config.mazeTint));
                }
            }
            return;
        }

        // Fallback: if the grid isn't ready yet, scan children under the dust root.
        // This preserves behavior during early initialization / phase transitions.
        if (activeDustRoot != null)
        {
            var dusts = activeDustRoot.GetComponentsInChildren<CosmicDust>(includeInactive: false);
            for (int i = 0; i < dusts.Length; i++)
            {
                var d = dusts[i];
                if (d == null) continue;
                if (!d.isActiveAndEnabled) continue;

                // In the fallback path we don't have grid coords, but we can check dust.Role.
                if (d.Role != MusicalRole.None) continue;

                StartCoroutine(d.RetintOver(seconds, config.mazeTint));
            }
        }
    }
    private void ClearMaze()
    {
        try
        {
            for (int x = 0; x < _gridState.Width; x++)
            {
                for (int y = 0; y < _gridState.Height; y++)
                {
                    var gp = new Vector2Int(x, y);
                    if (TryGetCellGo(gp, out var go) && go != null)
                        RemoveActiveAt(gp, go);
                }
            }
            _permanentClearCells.Clear();
        }
        finally
        {
        }
        _imprints?.Clear();
        _hiddenImprints?.Clear();
        _voidGrowCells.Clear();
        _ripenessByCell.Clear();
        _playerCarvedCells.Clear();
        _gridState.AllSolidCount    = 0;
        _targetSolidCount = -1;
        _gridState.RegrowingCount   = 0;
        _mazePatternCells = null;
    }
    /// <summary>
    /// Removes dust topology immediately (opens corridor) but fades visuals and pools afterward.
    /// Boost carving should use this, NOT DespawnDustAt.
    /// </summary>
    public void SetReservedVehicleCells(IReadOnlyList<Vector2Int> cells)
    {
        _reservedVehicleCells ??= new List<Vector2Int>(64);
        _reservedVehicleCells.Clear();

        if (cells == null) return;
        for (int i = 0; i < cells.Count; i++)
            _reservedVehicleCells.Add(cells[i]);
    }
    private bool IsWorldPositionInsideScreen(Vector3 worldPos)
    {
        var cam = Camera.main;
        if (!cam) return true; // don't cull if camera not ready

        Vector3 viewport = cam.WorldToViewportPoint(worldPos);

        // NEW: if point is behind/at camera plane, don't trust x/y.
        if (viewport.z <= 0.001f) return false;

        return viewport.x >= 0f && viewport.x <= 1f &&
               viewport.y >= 0f && viewport.y <= 1f;
    }

    private IEnumerator StaggeredGrowthFitDuration(List<(Vector2Int grid, Vector3 pos)> cells, float totalDuration) {

        // Keep pacing similar, but enforce a per-frame millisecond budget
        float deadlineStep = Mathf.Max(0.0f, totalDuration / Mathf.Max(1, cells.Count));
        try
        {
            if (drums == null) drums = FindObjectOfType<DrumTrack>();
            if (drums == null || dustPrefab == null) yield break;

            drums.SyncTileWithScreen();
            float cellWorldSize = Mathf.Max(0.001f, drums.GetCellWorldSize());

            float lastPacedAt = Time.realtimeSinceStartup;
            int i = 0;

            // Track which dust we spawned so we can re-enable colliders in a controlled way.
            // Track which dust we spawned so we can re-enable colliders in a controlled way.
            var spawnedDust = new List<CosmicDust>(cells.Count);

            while (i < cells.Count)
            {
                float frameStart  = Time.realtimeSinceStartup;
                float frameBudget = Mathf.Max(0f, config.maxSpawnMillisPerFrame) / 1000f;

                while (i < cells.Count && (Time.realtimeSinceStartup - frameStart) < frameBudget)
                {
                    var (grid, pos) = cells[i++];

                    // ---------------------------
                    // GATING
                    // ---------------------------
                    if (_permanentClearCells.Contains(grid)) continue;
                    if (IsKeepClearCell(grid)) continue;
                    // Skip cells already queued for void-growth (e.g. vehicle trap ring spawned
                    // via onBeforeGrowth callback). _voidGrowCells is populated synchronously so
                    // this check is race-free regardless of coroutine scheduling order.
                    if (_voidGrowCells.Contains(grid)) continue;
                    if (TryGetCellState(grid, out var existSt) &&
                        (existSt == DustCellState.Solid || existSt == DustCellState.Regrowing)) continue;
                    if (IsDustSpawnBlocked(grid)) continue;
                    // IMPORTANT
                    // // During startup maze bootstrap, cells blocked by a vehicle should remain
                    // unspawned/neutral, not be pushed into the generic regrow pipeline.
                    // Otherwise they later come back through CommitRegrowCell() and get assigned
                    // a musical role (plurality / least-dense), which is the wrong behavior for
                    // initial maze fill.
                    if (IsVehicleOverlappingCell(grid)) { 
                        if (_isBootstrappingMaze) 
                            continue; 
                        RequestRegrowCellAt(grid, refreshIfPending: true); 
                        continue;
                    }                    // ---------------------------
                    // SPAWN + REGISTER (VISUAL FIRST)
                    // ---------------------------
                    var hex = GetOrCreateCellGO(grid);
                    if (hex.TryGetComponent<CosmicDust>(out var dust))
                    {
                        dust.SetTrackBundle(this, drums);
                        dust.SetCellSizeDrivenScale(cellWorldSize, config.dustFootprintMul, cellClearanceWorld);

                        dust.PrepareForReuse();
                        dust.InitializeVisuals(DustTimings);
                        dust.SetGrowInDuration(config.hexGrowInSeconds);

                        // GetCellVisualColor reads from _imprints if available, otherwise config.mazeTint.
                        Color cellColor = GetCellVisualColor(grid);
                        var resistance = ResolveResistanceProfile(grid, MusicalRole.None, context: "SpawnDust");
                        dust.clearing.drainResistance01 = resistance.drainResistance01;

                        // Apply role AND color together so dust.Role is set from birth.
                        // SetTint alone leaves dust.Role = None, which means RetintExisting
                        // cannot distinguish role-colored cells from plain maze cells and
                        // would overwrite them with the flat config.mazeTint (gray).
                        if (_imprints != null && _imprints.TryGetValue(grid, out var spawnImprint)
                            && spawnImprint.role != MusicalRole.None)
                        {
                            dust.ApplyRoleAndCharge(spawnImprint.role, cellColor, 1f);
                        }
                        else
                        {
                            // Gray start: initial cells spawn with no role (MusicalRole.None) and maze tint.
                            // Roles are earned dynamically through vehicle carving + regrowth.
                            // dust.Role must be None here so TickDrain's Role guard treats these as
                            // inert gray cells and the star cannot drain them while dormant.
                            // Full charge (1f) — plow energy system requires non-zero units to chip.
                            dust.ApplyRoleAndCharge(MusicalRole.None, cellColor, 1f);
                            ApplyHiddenHintToDust(grid, dust);
                        }

                        // Use the role's shadow color as the deny feedback color.
                        // dustColors.denyColor may be unset on assets; shadowColor is the
                        // authored "darkened role memory" hue and is more reliably set.
                        Color denyColor = Color.darkGray;
                        if (_imprints != null && _imprints.TryGetValue(grid, out var imp) && imp.role != MusicalRole.None)
                        {
                            var rp = MusicalRoleProfileLibrary.GetProfile(imp.role);
                            if (rp != null)
                            {
                                var shadow = rp.dustColors.shadowColor;
                                // Only use shadow if it's meaningfully dark (not unset black or magenta).
                                denyColor = (shadow != Color.clear && shadow != Color.magenta)
                                    ? shadow
                                    : Color.darkGray;
                            }
                        }
                        dust.SetFeedbackColors(Color.white, denyColor);
                        dust.Begin();

                        // Critical: keep collider OFF during bulk topology changes.
                        // PrepareForReuse already disables the collider; this is a safety guard for
                        // edge cases. regrowAlphaCapped is intentionally NOT set here — Begin() already
                        // set sprite alpha to _currentTint.a, and the physics phase restores that same
                        // value, so no cap is needed (and setting it would cause a visible pop on lift).
                        SetDustCollision(dust, false);
                        spawnedDust.Add(dust);
                    }

                    // Register into the authoritative grid (during bulk) without forcing per-cell composite rebuilds.
                    RegisterHex(grid, hex);
                }

                // Pacing
                float elapsedSinceLast = Time.realtimeSinceStartup - lastPacedAt;
                if (elapsedSinceLast < deadlineStep)
                    yield return new WaitForSeconds(deadlineStep - elapsedSinceLast);
                else
                    yield return null;

                lastPacedAt = Time.realtimeSinceStartup;
            }

            // --------------------------------------------------------------------
            // PHYSICS PHASE: re-enable colliders gradually
            // --------------------------------------------------------------------
            int j = 0;
            while (j < spawnedDust.Count)
            {
                float frameStart  = Time.realtimeSinceStartup;
                float frameBudget = Mathf.Max(0f, config.maxSpawnMillisPerFrame) / 1000f;

                while (j < spawnedDust.Count && (Time.realtimeSinceStartup - frameStart) < frameBudget)
                {
                    var d = spawnedDust[j++];
                    if (d == null) continue;

// Resolve which cell this dust belongs to (authoritative mapping).
                    if (!_goToCell.TryGetValue(d.gameObject, out var gp))
                        continue;

// Only enable collision if the generator still considers this cell solid.
                    if (!TryGetCellState(gp, out var st) || st != DustCellState.Solid)
                        continue;

// Respect DustClaimManager / exclusion vetoes (PhaseStar pocket, etc.)
                    if (IsKeepClearCell(gp) || IsDustSpawnBlocked(gp))
                        continue;

// Never enable a collider on top of a vehicle — hand off to the step-gate retry path.
                    if (IsVehicleOverlappingCell(gp))
                    {
                        SetCellState(gp, DustCellState.PendingRegrow);
                        FadeAndHideCellGO(d.gameObject);
                        EnqueueStepRegrow(gp);
                        continue;
                    }

// At this point, it is legitimately solid terrain.
                    // SetTerrainColliderEnabled(true) now restores _currentTint.a directly, so
                    // regrowAlphaCapped and EnsureMinSolidAlpha are not needed here.
                    SetDustCollision(d, true);
                }

                yield return null;
            }

            // No composite collider to rebuild.
        }
        finally
        {
            
        }
    }
    private void MarkTintDirty(Vector2Int center, int radius)
    {
        if (_tintDiffusionSystem == null) return;
        _tintDiffusionSystem.MarkDirty(center, radius);
    }
    private void TickRipeness(float dt)
    {
        if (_ripenessByCell.Count == 0) return;

        _ripenessKeys ??= new List<Vector2Int>(64);
        _ripenessKeys.Clear();
        _ripenessKeys.AddRange(_ripenessByCell.Keys);

        for (int i = 0; i < _ripenessKeys.Count; i++)
        {
            var gp = _ripenessKeys[i];

            if (!TryGetCellState(gp, out var st) || st != DustCellState.Solid
                || !TryGetDustAt(gp, out var dust) || dust == null)
            {
                _ripenessByCell.Remove(gp);
                continue;
            }

            if (_hiddenImprints == null || !_hiddenImprints.TryGetValue(gp, out var trueRole)
                || trueRole == MusicalRole.None)
            {
                _ripenessByCell.Remove(gp);
                continue;
            }

            var profile = MusicalRoleProfileLibrary.GetProfile(trueRole);
            float duration = profile != null ? Mathf.Max(0.01f, profile.ripeDuration) : 8f;
            float ripeness = _ripenessByCell[gp] - (1f / duration) * dt;

            if (ripeness <= 0f)
            {
                // Fully decayed: revert to gray, role becomes None for all external queries.
                dust.ApplyRoleAndCharge(MusicalRole.None, config.mazeTint, dust.Charge01);
                ApplyHiddenHintToDust(gp, dust);
                _ripenessByCell.Remove(gp);
            }
            else
            {
                _ripenessByCell[gp] = ripeness;
                // Mid-decay: lerp color only (Role stays set so PhaseStar can still drain).
                // Use the per-cell imprint color so random voice colors are preserved through decay.
                Color roleColor = Color.white;
                if (_imprints != null && _imprints.TryGetValue(gp, out var imp))
                    roleColor = new Color(imp.color.r, imp.color.g, imp.color.b, 1f);
                else if (profile != null)
                    roleColor = profile.GetBaseColor();
                Color full = new Color(roleColor.r, roleColor.g, roleColor.b, dust.Charge01);
                Color gray = new Color(config.mazeTint.r, config.mazeTint.g, config.mazeTint.b, dust.Charge01);
                float effectiveRipeness = Mathf.Max(ripeness, dust.Charge01);
                dust.SetTint(Color.Lerp(gray, full, effectiveRipeness));
            }
        }
    }

    private void ProcessTintDiffusion(float dt)
    {
        if (!config.enableTintDiffusion) return;
        if (_tintDiffusionSystem == null) return;
        _tintDiffusionSystem.Tick(
            dt: dt,
            enabled: config.enableTintDiffusion,
            maxCellsPerTick: config.tintDiffusionMaxCellsPerTick,
            neighborRadius: config.tintDiffusionRadius,
            strength: config.tintDiffusionStrength,
            minDelta: config.tintDiffusionMinDelta,
            propagateOnChange: config.tintDiffusionPropagateOnChange,
            intervalSeconds: config.tintDiffusionInterval);
    }
    private void RemoveActiveAt(Vector2Int grid, GameObject go) {
        // Logical authority: the moment a cell is cleared, it stops contributing to the maze.
        SetCellState(grid, DustCellState.Empty);

        // Drop from the reverse-lookup map.
        if (go != null)
            _goToCell.Remove(go);

        // No pooling: hide immediately. Any fade-out behavior should be handled by CosmicDust.
        if (go != null)
        {
            if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null)
                SetDustCollision(dust, false);
            HideCellGO(go);
        }

        // Diffusion prep: removing dust can expose the base tint and create hard seams.
        MarkTintDirty(grid, config.tintDirtyMarkRadius);

        // (no composite collider rebuild)
    }
    private void HideCellGO(GameObject go)
    {
        if (!go) return;

        if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null)
        {
            SetDustCollision(dust, false);
            dust.HideVisualsInstant();
        }
    }
    private void FadeAndHideCellGO(GameObject go)
    {
        if (!go) return;

        if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null)
        {
            SetDustCollision(dust, false);
            dust.DissipateAndHideVisualOnly(.5f);
        }
        else
        {
            HideCellGO(go);
        }
    }
    private void RegisterHex(Vector2Int gridPos, GameObject hex)
    {
        // Ensure the authoritative grid is ready.
        EnsureCellGrid();

        // If something already occupies this cell, hide it (no pooling).
        if (TryGetCellGo(gridPos, out var existing) && existing != null && existing != hex)
        {
            _goToCell.Remove(existing);
            if (existing.TryGetComponent<CosmicDust>(out var exDust) && exDust != null)
                SetDustCollision(exDust, false);

            HideCellGO(existing);
        }

        // Register in the authoritative grid.
        if (IsInBounds(gridPos))
        {
            _gridState.CellGo[gridPos.x, gridPos.y] = hex;
            var d = hex != null ? hex.GetComponent<CosmicDust>() : null;
            _gridState.CellDust[gridPos.x, gridPos.y] = d;
            if (hex != null) _goToCell[hex] = gridPos;
            SetCellState(gridPos, DustCellState.Solid);
        }

        // No composite collider rebuild (per-cell terrain only).
    }

    private void DespawnDustAt(Vector2Int gridPos)
    {
        if (_permanentClearCells.Contains(gridPos)) return;

        // Immediate logical clear (no fade). This is used for hard removals
        // (eg. collectable pockets) where we want topology open immediately.
        if (TryGetCellGo(gridPos, out var go) && go != null)
        {
            if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null)
                SetDustCollision(dust, false);
            HideCellGO(go);
        }

        SetCellState(gridPos, DustCellState.Empty);

        // CRITICAL: schedule regrow.
        RequestRegrowCellAt(gridPos, refreshIfPending: true);
    }
    private List<Vector2Int> GetHexDirections(int row)
    {
        // Even-q offset coordinates
        return row % 2 == 0 ? new List<Vector2Int>
        {
            new(1, 0), new(0, 1), new(-1, 1),
            new(-1, 0), new(-1, -1), new(0, -1)
        } : new List<Vector2Int>
        {
            new(1, 0), new(1, 1), new(0, 1),
            new(-1, 0), new(0, -1), new(1, -1)
        };
    }
    
    /// <summary>
    /// Returns true if any solid dust cell's component Role is non-None.
    /// Consistent with GetColoredDustCells so the star arms only when actual colored
    /// (vehicle-carved) cells exist — not just when imprints have been promoted.
    /// </summary>
    public bool HasAnyDustWithRole()
    {
        if (_gridState.CellState == null || _gridState.CellDust == null) return false;
        for (int y = 0; y < _gridState.Height; y++)
        for (int x = 0; x < _gridState.Width; x++)
        {
            if (_gridState.CellState[x, y] != DustCellState.Solid) continue;
            var dust = _gridState.CellDust[x, y];
            if (dust != null && dust.Role != MusicalRole.None && dust.currentEnergyUnits > 0 && dust.IsVisuallyPresentForTargeting()) return true;
        }
        return false;
    }

    public bool HasAnyDustWithRole(MusicalRole role)
    {
        if (role == MusicalRole.None || _gridState.CellState == null || _gridState.CellDust == null) return false;
        for (int y = 0; y < _gridState.Height; y++)
        for (int x = 0; x < _gridState.Width; x++)
        {
            if (_gridState.CellState[x, y] != DustCellState.Solid) continue;
            var dust = _gridState.CellDust[x, y];
            if (dust != null && dust.Role == role && dust.currentEnergyUnits > 0 && dust.IsVisuallyPresentForTargeting()) return true;
        }
        return false;
    }

    // Paints existing dust with a role at reduced energy (MineNode exhaust trail).
    // Does NOT create new dust — only paints cells that already have solid terrain.
    // energyFraction: 0-1, fraction of the role's maxEnergyUnits to assign (e.g. 0.4 = 40% charge).
    public void PaintDustExhaust(Vector2Int cell, MusicalRole role, float energyFraction = 0.4f)
    {
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;
        var profile = MusicalRoleProfileLibrary.GetProfile(role);
        if (profile == null) return;

        Color color = profile.GetBaseColor();
        int maxUnits = profile.maxEnergyUnits;
        int exhaustUnits = Mathf.Max(1, Mathf.RoundToInt(maxUnits * Mathf.Clamp01(energyFraction)));

        // Update imprint so regrowth remembers the role assignment.
        _imprints[cell] = new DustImprint
        {
            role               = role,
            color              = color,
            carveResistance01  = profile.GetCarveResistance01(),
            drainResistance01  = profile.GetDrainResistance01(),
            maxEnergyUnits     = maxUnits
        };

        if (TryGetDustAt(cell, out var dust))
        {
            var resistance = ResolveResistanceProfile(cell, role, context: "PaintDustExhaust");
            dust.clearing.drainResistance01 = resistance.drainResistance01;
            dust.ApplyRoleAndCharge(role, color, (float)exhaustUnits / maxUnits, maxUnits);
            dust.SyncParticleColor();
        }
    }

    [ContextMenu("Debug: Find Phantom Colliders")]
    public void DebugFindPhantomColliders()
    {
        if (_gridState.CellState == null || _gridState.CellGo == null) return;
        int phantoms = 0;
        for (int x = 0; x < _gridState.Width; x++)
        for (int y = 0; y < _gridState.Height; y++)
        {
            var st = _gridState.CellState[x, y];
            var go = _gridState.CellGo[x, y];
            if (go == null) continue;
        
            if (st != DustCellState.Solid)
            {
                // Cell isn't solid, but does it have an enabled collider?
                if (go.TryGetComponent<CosmicDust>(out var d) && d.terrainCollider != null 
                                                              && d.terrainCollider.enabled)
                {
                    Debug.LogWarning($"[PHANTOM] Cell ({x},{y}) state={st} but collider ENABLED on {go.name}", go);
                    phantoms++;
                }
            }
        }
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PHANTOM] Scan complete. Found {phantoms} phantom colliders.");
    }

    [ContextMenu("Debug: Audit Colored Dust vs Star Visibility")]
    public void DebugAuditColoredDustStarVisibility()
    {
        if (_gridState.CellState == null || _gridState.CellDust == null || _gridState.CellGo == null) return;

        int roleZeroEnergy = 0;
        int colliderOnSpriteOff = 0;
        int roleNoneVisuallyTinted = 0;

        for (int x = 0; x < _gridState.Width; x++)
        for (int y = 0; y < _gridState.Height; y++)
        {
            var st  = _gridState.CellState[x, y];
            var go  = _gridState.CellGo[x, y];
            var gp  = new Vector2Int(x, y);
            if (go == null) continue;
            if (!go.TryGetComponent<CosmicDust>(out var d) || d == null) continue;

            // Case 1: Solid cell with a role but 0 energy → stars can never drain it.
            if (st == DustCellState.Solid && d.Role != MusicalRole.None && d.currentEnergyUnits <= 0)
            {
                Debug.LogWarning($"[DUST-AUDIT] ({x},{y}) ROLE={d.Role} energy=0 → invisible to star tentacles (trapped dormant)", go);
                roleZeroEnergy++;
            }

            // Case 2: Collider enabled but sprite renderer disabled → invisible wall.
            if (d.visual.sprite != null && !d.visual.sprite.enabled)
            {
                bool colEnabled = false;
                var cols = go.GetComponentsInChildren<Collider2D>(true);
                for (int i = 0; i < cols.Length; i++) if (cols[i] != null && cols[i].enabled) { colEnabled = true; break; }
                if (d.terrainCollider != null && d.terrainCollider.enabled) colEnabled = true;
                if (colEnabled)
                {
                    Debug.LogWarning($"[DUST-AUDIT] ({x},{y}) state={st} collider=ON sprite=OFF → invisible wall", go);
                    colliderOnSpriteOff++;
                }
            }

            // Case 3: Gray (None-role) cell that appears tinted (RGB far from mazeTint) → diffusion artifact.
            if (st == DustCellState.Solid && d.Role == MusicalRole.None)
            {
                Color cur = d.CurrentTint;
                Color gray = config.mazeTint;
                float delta = Mathf.Max(Mathf.Abs(cur.r - gray.r), Mathf.Max(Mathf.Abs(cur.g - gray.g), Mathf.Abs(cur.b - gray.b)));
                if (delta > 0.15f)
                {
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[DUST-AUDIT] ({x},{y}) Role=None but tint delta={delta:F2} from mazeTint — likely diffusion bleed", go);
                    roleNoneVisuallyTinted++;
                }
            }
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DUST-AUDIT] Done. role+zeroEnergy={roleZeroEnergy}  collider+spriteOff={colliderOnSpriteOff}  diffusionBleed={roleNoneVisuallyTinted}");
    }
}
