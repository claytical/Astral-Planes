using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

public class CosmicDustGenerator : MonoBehaviour
{
    private CosmicDustRegrowthController _regrow;
    private bool _isBootstrappingMaze = false;
    public GameObject dustPrefab;
    public Transform activeDustRoot;
    Dictionary<Vector2Int, DustImprint> _imprints;
    private Dictionary<Vector2Int, MusicalRole> _hiddenImprints = new();
    private readonly Dictionary<Vector2Int, MusicalRole> _regrowExcludeRoleByCell = new Dictionary<Vector2Int, MusicalRole>(2048);
    private bool _cellGridReady;
    [Header("Maze Collision Shape")]
    [Tooltip("World-units clearance inside each cell. 0 = watertight.")]
    public float cellClearanceWorld = 0f;
    private bool _mazeAlreadyGenerated = false;
    [Header("Hardness")]
    [Tooltip("Baseline dust hardness for non-imprinted maze cells. Imprinted MineNode cells override this via DustImprint.hardness01.")]
    [Range(0f, 1f)]
    [SerializeField] private float defaultMazeHardness01 = 0f;
    private bool _regrowthSuppressed = false;
    struct DustImprint
    {
        public Color color;
        public float healDelay;
        public float hardness01;          // Legacy — kept for migration.
        public float carveResistance01;
        public float drainResistance01;
        public int   maxEnergyUnits;
        public MusicalRole role;
    }
    [Header("Dust Visual Timings")]
    [SerializeField] private DustVisualTimingSettings dustVisualTimingSettings;
    private CosmicDust.DustVisualTimings DustTimings => dustVisualTimingSettings != null
        ? dustVisualTimingSettings.Timings
        : CosmicDust.DustVisualTimings.Default;
    
    // --- Extracted controllers (refactor targets) ---
    private CosmicDustExclusionMap _exclusions = new CosmicDustExclusionMap();
    private CosmicDustTintDiffusionSystem _tintDiffusionSystem;
    private readonly List<Vector2Int> _tmpReleased = new List<Vector2Int>(512);
    private readonly List<Vector2Int> _tmpClaimed  = new List<Vector2Int>(512);
    [Header("MineNode Erosion")]
    [SerializeField] private int mineNodeErodePerTick = 10;
    [Header("Dust Visual Footprint")]
    [Range(0.8f, 1.6f)]
    public float dustFootprintMul = 1.15f;

    [Header("Tile Sizing")]
    [SerializeField] private float tileDiameterWorld = 1f;          // cached from dustfab.hitbox

    // ------------------------------------------------------------------
    // Authoritative grid (no pooling). The grid is the traffic cop.
    // ------------------------------------------------------------------
    private readonly DustGridState _gridState = new();
    private Dictionary<GameObject, Vector2Int> _goToCell = new Dictionary<GameObject, Vector2Int>(1024);

    [Header("Regrow Step Gate")]
    [Tooltip("How many cells are allowed to transition from PendingRegrow -> Solid per drum step.")]
    [SerializeField] private int regrowCellsPerStep = 1;
    [Tooltip("Seconds to wait after a cell becomes visible again before enabling its collider.")]
    [SerializeField] private float regrowColliderEnableDelaySeconds = 0.20f;

    private Dictionary<Vector2Int, bool> _fillMap = new();
    private MazePatternConfig _activeMazePattern;
    private readonly DustRegrowthScheduler _regrowthScheduler = new();
    private readonly MazeTopologyService _mazeTopologyService = new();
    [SerializeField] private Color _mazeTint = new Color(0.7f, 0.7f, 0.7f, .25f);
    [Header("Topology")]
    [Tooltip("When enabled, the dust grid wraps toroidally — cells at one edge connect to the opposite edge.")]
    [SerializeField] public bool toroidal = false;
    private PhaseStarBehaviorProfile _activeProfile;
    [Header("Tint Diffusion (Option 2)")]
    [Tooltip("If enabled, recently modified dust cells will gradually blend toward their neighbors over time (local diffusion).")]
    [SerializeField] private bool enableTintDiffusion = true;
    [Tooltip("Seconds between diffusion passes. Lower = smoother but more CPU.")]
    [Range(0.02f, 0.5f)] [SerializeField] private float tintDiffusionInterval = 0.12f;
    [Tooltip("Maximum number of dirty cells processed per diffusion pass (prevents spikes).")]
    [Range(16, 2048)] [SerializeField] private int tintDiffusionMaxCellsPerTick = 256;
    [Tooltip("Neighborhood radius used for diffusion averaging (1 = 8-neighborhood).")]
    [Range(0, 3)] [SerializeField] private int tintDiffusionRadius = 1;
    [Tooltip("How strongly each pass nudges a cell toward the neighborhood average (0–1).")]
    [Range(0f, 1f)] [SerializeField] private float tintDiffusionStrength = 0.25f;
    [Tooltip("When a cell changes materially due to diffusion, enqueue its immediate neighbors to propagate blending.")]
    [SerializeField] private bool tintDiffusionPropagateOnChange = true;
    [Tooltip("Minimum per-channel delta required to apply a diffusion step (skips tiny changes).")]
    [Range(0f, 0.05f)] [SerializeField] private float tintDiffusionMinDelta = 0.0025f;
    [Tooltip("How far out to mark cells dirty when a tint-affecting event occurs (imprint, regrow, removal).")]
    [Range(0, 3)] [SerializeField] private int tintDirtyMarkRadius = 1;

    private List<Vector2Int> _reservedVehicleCells = new List<Vector2Int>(64);

    private HashSet<Vector2Int> _permanentClearCells = new HashSet<Vector2Int>();
    // Cells spawned by GrowVoidDustDiskFromGrid that should not regrow when carved by a vehicle.
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
    [SerializeField] private float maxSpawnMillisPerFrame = 1.2f; // tune for target HW
    public event Action<Vector2Int?> OnMazeReady;

    [SerializeField] private float hexGrowInSeconds = 0.45f;        // visual “grow in” time per hex
    private readonly HashSet<Vector2Int> _starClearCells = new HashSet<Vector2Int>();

    // ---------------------------------------------------------------------------
    // Role density tracking
    // Counts how many Solid cells are currently imprinted with each MusicalRole.
    // Updated in SetCellState when a cell becomes or leaves Solid.
    // Used by CommitRegrowCell to pick the least-represented role when a cell
    // has no role imprint (e.g. vehicle-carved cells whose imprint was removed).
    // ---------------------------------------------------------------------------
    private List<MusicalRole> _activeRoles; // set from motif at phase start via ApplyActiveRoles
    private readonly Dictionary<MusicalRole, int> _solidCountByRole = new Dictionary<MusicalRole, int>
    {
        { MusicalRole.Bass,    0 },
        { MusicalRole.Harmony, 0 },
        { MusicalRole.Lead,    0 },
        { MusicalRole.Groove,  0 },
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
    [SerializeField] private float regrowVetoRetryDelaySeconds = 0.5f;
    [Tooltip("Overlap box size as a fraction of cellWorldSize.")] 
    [Range(0.25f, 1.25f)] 
    [SerializeField] private float regrowVetoBoxMul = 0.85f; 
    [SerializeField] private int regrowVetoMaxHits = 8; 
    
    private Collider2D[] _vehicleVetoHits;    

    [SerializeField] private DustClaimManager dustClaims;
    private bool _runtimeVoidOnlyDustCreation;

    // Back-compat hooks (no-op now that composite rebuilds are removed).

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
                 var explode = go.GetComponent<Explode>();
                 if (explode != null)
                 {
                     var tint = dust.CurrentTint;
                     tint.a = 1f;
                     explode.SetTint(tint);
                     Debug.Log($"[CLEARCELL] preexplode tint={dust.CurrentTint} forcedVisible={(new Color(dust.CurrentTint.r, dust.CurrentTint.g, dust.CurrentTint.b, 1f))}");
                     explode.PreExplode();
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
                    dust.DissipateAndHideVisualOnly(DustTimings.clearSpriteScaleOutSeconds);
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
             // Density conservation: immediately fill a frontier cell so total coverage
             // is maintained. The eroded cell's own regrow will be suppressed in
             // CommitRegrowCell once the compensation cell is committed.
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
        float imprintHardness01,
        float energyAtCenter01,
        float falloffExp,
        float growInSeconds,
        int fillWedges01To4,
        List<Vector2Int> vehicleCells,
        int vehicleNoSpawnRadiusCells,
        int maxCellsThisCall = -1,
        int innerRadiusCellsExclusive = -1)
    {
        EnsureCellGrid();
        _imprints ??= new Dictionary<Vector2Int, DustImprint>(2048);

        if (outerRadiusCells <= 0) return 0;

        imprintHardness01 = Mathf.Clamp01(imprintHardness01);
        energyAtCenter01 = Mathf.Clamp01(energyAtCenter01);
        falloffExp = Mathf.Max(0.01f, falloffExp);
        growInSeconds = Mathf.Max(0.01f, growInSeconds);
        fillWedges01To4 = Mathf.Clamp(fillWedges01To4, 1, 4);

        int processed = 0;
        int rOuterSq = outerRadiusCells * outerRadiusCells;
        int rInnerSq = innerRadiusCellsExclusive >= 0
            ? innerRadiusCellsExclusive * innerRadiusCellsExclusive
            : -1;

        bool NearAnyVehicle(Vector2Int gp)
        {
            if (vehicleNoSpawnRadiusCells <= 0) return false;
            if (vehicleCells == null || vehicleCells.Count == 0) return false;

            for (int i = 0; i < vehicleCells.Count; i++)
            {
                var vc = vehicleCells[i];
                int dx = Mathf.Abs(gp.x - vc.x);
                int dy = Mathf.Abs(gp.y - vc.y);
                if (dx <= vehicleNoSpawnRadiusCells && dy <= vehicleNoSpawnRadiusCells) return true;
            }
            return false;
        }

        bool InFilledWedge(int dx, int dy)
        {
            // Quadrant order: 0=NE, 1=NW, 2=SW, 3=SE
            int quad;
            if (dy >= 0)
                quad = (dx >= 0) ? 0 : 1;
            else
                quad = (dx < 0) ? 2 : 3;

            return quad < fillWedges01To4;
        }

        for (int dy = -outerRadiusCells; dy <= outerRadiusCells; dy++)
        {
            for (int dx = -outerRadiusCells; dx <= outerRadiusCells; dx++)
            {
                int dSq = dx * dx + dy * dy;
                if (dSq > rOuterSq) continue;
                if (rInnerSq >= 0 && dSq <= rInnerSq) continue;
                if (!InFilledWedge(dx, dy)) continue;

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
                    Debug.Log($"[VOID_RING] rIn={innerRadiusCellsExclusive} rOut={outerRadiusCells} d={d:F2} u={u:F2} a={c.a:F2}");
                }            
                // Vehicle pocket is a hard exclusion — no imprint, no spawn, no visual update.
                if (NearAnyVehicle(gp)) continue;

                // Always write persistent imprint (so regrow picks it up later)
                _imprints[gp] = new DustImprint
                {
                    color = c,
                    hardness01 = imprintHardness01,
                    role = imprintRole
                };
                processed++;

    // 1) If dust already exists, ALWAYS refresh visuals (even if keep-clear/blocked/etc).
                if (TryGetCellGo(gp, out var existingGo) && existingGo != null &&
                    existingGo.TryGetComponent<CosmicDust>(out var existingDust) && existingDust != null &&
                    HasDustAt(gp))
                {
                    existingDust.ApplyRoleAndCharge(MusicalRole.None, _mazeTint, c.a);
                    existingDust.clearing.hardness01 = imprintHardness01;
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
                _regrowthScheduler.VoidGrowCoroutines[gp] = StartCoroutine(VoidGrowCellNow(gp, imprintRole, c, imprintHardness01, growInSeconds));
            }
        }

        return processed;
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
    /// Called by CosmicDust.DrainCharge when a cell's visual alpha drops below the
    /// solid-visibility threshold (0.55). The cell is physically drained but was
    /// never explicitly "cleared" by gameplay — this bridges that gap so the
    /// collider doesn't linger as an invisible wall.
    /// </summary>
    private IEnumerator VoidGrowCellNow(Vector2Int gp, MusicalRole role, Color tintWithAlpha, float hardness01, float growInSeconds)
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

            // void uses remaining-bin time
            dust.SetGrowInDuration(growInSeconds);

            dust.clearing.hardness01 = hardness01;
            dust.ApplyRoleAndCharge(MusicalRole.None, _mazeTint, tintWithAlpha.a);
            dust.SetFeedbackColors(Color.white, Color.darkGray);
            dust.Begin();

            // Always non-colliding during grow
            SetDustCollision(dust, false);
        }
        if (dust != null)
        {
            var sr = dust.GetComponentInChildren<SpriteRenderer>(true);
        }
        float enableDelay = Mathf.Max(regrowColliderEnableDelaySeconds, growInSeconds * 0.85f);
        yield return new WaitForSeconds(enableDelay);

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
            Debug.Log($"[VOID_GROW] ABORT_END gp={gp} keep={veto1_keep} spawnBlocked={veto1_spawnBlocked} vehicle={veto1_vehicle} claim={veto1_claim}");
            SetCellState(gp, DustCellState.Empty);
            FadeAndHideCellGO(go);
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        // Keep-clear at end: allow visuals, but never become solid/colliding.
        if (veto1_keep)
        {
            Debug.Log($"[VOID_GROW] VISUAL_ONLY gp={gp} (keep-clear)");
            SetCellState(gp, DustCellState.Regrowing); // or define a VisualOnly state later
            if (dust != null) SetDustCollision(dust, false);
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        // Otherwise: become solid.
        SetCellState(gp, DustCellState.Solid);
        if (dust != null) SetDustCollision(dust, true);

//        Debug.Log($"[VOID_GROW] SOLID gp={gp}");
        _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
    }
    private void SetDustCollision(CosmicDust dust, bool _enabled)
        {
            if (dust == null) return;
            dust.SetTerrainColliderEnabled(_enabled);
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
    for (int dx = -r; dx <= r; dx++)
    for (int dy = -r; dy <= r; dy++)
    {
        if (dx * dx + dy * dy > r * r) continue;
        int x = centerCell.x + dx;
        int y = centerCell.y + dy;
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
        next.Add(new Vector2Int(x, y));
    }

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
        if (_vehicleVetoHits == null || _vehicleVetoHits.Length != regrowVetoMaxHits) 
            _vehicleVetoHits = new Collider2D[Mathf.Max(1, regrowVetoMaxHits)];
        // In some scenes the generator may be instantiated before the GameFlowManager
        // is fully ready. We do a best-effort bind here, and also lazily re-bind elsewhere.
        EnsureImprints();
        TryEnsureRefs();
        EnsureCellGrid();
        _regrowthScheduler.Initialize(regrowCellsPerStep, regrowColliderEnableDelaySeconds);
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

            getRegrowVetoRetryDelaySeconds: () => Mathf.Max(0.05f, regrowVetoRetryDelaySeconds),
            getRegrowCellsPerStep: () => Mathf.Max(0, regrowCellsPerStep)
        );
    }
    private bool IsVehicleOverlappingCellWorld(Vector3 cellWorld, float cellWorldSize)
    {
        Vector2 size = Vector2.one * Mathf.Max(0.001f, cellWorldSize * regrowVetoBoxMul);

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

        var gfm = GameFlowManager.Instance;
        if (gfm == null) return;

        if (drums == null) drums = gfm.activeDrumTrack;
        if (phaseTransitionManager == null) phaseTransitionManager = gfm.phaseTransitionManager;
    }
    public void ManualStart()
    {
        var gfm = GameFlowManager.Instance;
        drums = gfm.activeDrumTrack;
        TryEnsureRefs();
        EnsureRegrowController();
        if (dustClaims == null) dustClaims = FindObjectOfType<DustClaimManager>();
        if (_tintDiffusionSystem == null)
            _tintDiffusionSystem = new CosmicDustTintDiffusionSystem(
                cell =>
                {
                    TryGetDustAt(cell, out var d); 
                    Debug.Log($"[DUST] {d.name} with {GetCellVisualColor(cell)}");
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
        Vector2 size = Vector2.one * (cellWorld * regrowVetoBoxMul);

        if (_vehicleVetoHits == null || _vehicleVetoHits.Length != regrowVetoMaxHits)
            _vehicleVetoHits = new Collider2D[Mathf.Max(1, regrowVetoMaxHits)];

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
        bool playerCarved = _playerCarvedCells.Remove(gp);
        if (go.TryGetComponent<CosmicDust>(out dust) && dust != null)
        {
            dust.PrepareForReuse();
            dust.InitializeVisuals(DustTimings);
            dust.SetGrowInDuration(DustTimings.regrowParticleGrowInSeconds);

            // --- Role resolution ---
            // Priority 1: cell has a live imprint with a real role (Voronoi, MineNode, void).
            // Priority 2: no imprint → prefer the plurality role among solid imprinted neighbors
            //             (maintains spatial cohesion with the Voronoi layout). Fall back to
            //             least-dense when neighbors are split or absent (global balance).
            MusicalRole excludedRole = MusicalRole.None;
            if (_regrowExcludeRoleByCell.TryGetValue(gp, out var ex))
                excludedRole = ex;

            if (_imprints != null && _imprints.TryGetValue(gp, out var existingImp))
            {
                if (existingImp.role == MusicalRole.None)
                {
                    // Maze cells (gray start) stay gray on regrowth.
                    // Roles are only earned through GravityVoid or MineNode, not regrowth.
                    regrowRole = MusicalRole.None;
                }
                else if (IsRoleActive(existingImp.role))
                {
                    regrowRole = existingImp.role;
                }
                else
                {
                    var neighborRole = GetPluralityNeighborRole(gp, excludedRole);
                    regrowRole = neighborRole != MusicalRole.None
                        ? neighborRole
                        : GetLeastDenseRoleExcluding(excludedRole);
                }
            }
            else
            {
                var neighborRole = GetPluralityNeighborRole(gp, excludedRole);
                regrowRole = neighborRole != MusicalRole.None
                    ? neighborRole
                    : GetLeastDenseRoleExcluding(excludedRole);
            }

            // --- Ripeness gate (applied before visual coloring) ---
            // Only player-carved cells earn their role color on regrowth.
            // All other regrowth (initial maze spawn, tentacle drain, bridge reset, etc.)
            // must stay gray so no role-color flash occurs during the grow-in animation.
            if (!playerCarved && regrowRole != MusicalRole.None)
                regrowRole = MusicalRole.None;

            // --- Color from role profile (authoritative source) ---
            var roleProfile = MusicalRoleProfileLibrary.GetProfile(regrowRole);
            Color regrowTint = (roleProfile != null) ? roleProfile.GetBaseColor() : _mazeTint;

            // Write / update the imprint so future regrows of this cell remember the role.
            _imprints ??= new Dictionary<Vector2Int, DustImprint>();
            _imprints[gp] = new DustImprint
            {
                color               = regrowTint,
                hardness01          = roleProfile != null ? roleProfile.GetDustHardness01() : defaultMazeHardness01,
                carveResistance01   = roleProfile != null ? roleProfile.GetCarveResistance01() : 0f,
                drainResistance01   = roleProfile != null ? roleProfile.GetDrainResistance01() : 0f,
                maxEnergyUnits      = roleProfile != null ? roleProfile.maxEnergyUnits : 1,
                role                = regrowRole,
                healDelay           = 0f
            };

            dust.clearing.hardness01        = _imprints[gp].hardness01;
            dust.clearing.carveResistance01 = roleProfile != null ? roleProfile.GetCarveResistance01() : 0f;
            dust.clearing.drainResistance01 = roleProfile != null ? roleProfile.GetDrainResistance01() : 0f;
            int maxUnits = roleProfile != null ? roleProfile.maxEnergyUnits : 1;
            dust.ApplyRoleAndCharge(regrowRole, regrowTint, regrowTint.a, maxUnits);

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
        if (regrowColliderEnableDelaySeconds > 0f)
            yield return new WaitForSeconds(regrowColliderEnableDelaySeconds);

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

        // Ripeness: player-carved cells that regrew with a real role start fully ripe.
        // Non-player-carved cells already have regrowRole = None (forced above) so no
        // late override is needed — the cell is already gray from ApplyRoleAndCharge.
        if (playerCarved && regrowRole != MusicalRole.None)
            _ripenessByCell[gp] = 1f;
    }

    private void EnqueueStepRegrow(Vector2Int gp)
    {
        EnsureRegrowController();
        _regrow?.EnqueueStepRegrow(gp);
    }
    
    private List<(Vector2Int grid, Vector3 world)> BuildMazeGrowthFromConfig(
        MazePatternConfig config,
        Vector2Int starCell,
        HashSet<Vector2Int> reservedCells)
    {
        config?.Validate();
        if (drums == null) return new List<(Vector2Int, Vector3)>();

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return new List<(Vector2Int, Vector3)>();

        Func<int, List<Vector2Int>> getDirsByRow = row => GetHexDirections(row);
        Func<int, int, bool> isCellAvailable = (x, y) => drums.IsSpawnCellAvailable(x, y);
        var context = new MazeTopologyService.Context
        {
            Width = w,
            Height = h,
            StarCell = starCell,
            GetHexDirectionsByRow = getDirsByRow,
            IsCellAvailable = isCellAvailable,
            IsBlocked = cell =>
            {
                if ((uint)cell.x >= (uint)w || (uint)cell.y >= (uint)h) return true;
                if (cell == starCell) return true;
                if (!drums.IsSpawnCellAvailable(cell.x, cell.y)) return true;
                if (reservedCells != null && reservedCells.Contains(cell)) return true;
                if (_permanentClearCells != null && _permanentClearCells.Contains(cell)) return true;
                if (IsKeepClearCell(cell)) return true;
                return false;
            },
            NormalizeCell = toroidal ? WrapCell : null
        };

        var solidCells = _mazeTopologyService.BuildSolidCells(config, context);
        var growth = new List<(Vector2Int cell, Vector3 world)>(solidCells.Count);
        foreach (var gp in solidCells)
        {
            var world = drums.GridToWorldPosition(gp);
            if (!IsWorldPositionInsideScreen(world)) continue;
            growth.Add((gp, world));
        }

        // Final filter: enforce reserved/permanent/keep-clear even if a pattern emitted them.
        var filtered = new List<(Vector2Int, Vector3)>(growth.Count);
        for (int i = 0; i < growth.Count; i++)
        {
            var gp = growth[i].cell;
            if (reservedCells != null && reservedCells.Contains(gp)) continue;
            if (_permanentClearCells != null && _permanentClearCells.Contains(gp)) continue;
            if (IsKeepClearCell(gp)) continue;
            filtered.Add((growth[i].cell, growth[i].world));
        }

        return filtered;
    }

    /// <summary>
    /// Assigns a MusicalRole to every cell in the growth list via Voronoi (or a future
    /// archetype-specific layout), then writes a DustImprint for each cell so that
    /// GetCellHardness01 and GetCellVisualColor return per-cell role values during spawn.
    /// </summary>
private void BuildMazeRoleImprints(
    Vector2Int starCell,
    List<(Vector2Int cell, Vector3 world)> cells)
{
    if (cells == null || cells.Count == 0) return;
    if (drums == null) return;

    _imprints ??= new Dictionary<Vector2Int, DustImprint>(cells.Count * 2);

    // Resolve active roles from motif; fall back to all 4 roles.
    IReadOnlyList<MusicalRole> roles = (_activeRoles != null && _activeRoles.Count > 0)
        ? _activeRoles
        : new List<MusicalRole>
        {
            MusicalRole.Bass,
            MusicalRole.Harmony,
            MusicalRole.Lead,
            MusicalRole.Groove
        };

    var rolesList = roles is List<MusicalRole> rl ? rl : new List<MusicalRole>(roles);
    if (rolesList.Count == 0)
        return;

    // All dust starts gray (MusicalRole.None); hidden Voronoi roles are revealed later.
    foreach (var (gp, _) in cells)
    {
        _imprints[gp] = new DustImprint
        {
            role               = MusicalRole.None,
            color              = _mazeTint,
            hardness01         = defaultMazeHardness01,
            carveResistance01  = 0f,
            drainResistance01  = 0f,
            maxEnergyUnits     = 1,
            healDelay          = 0f
        };
    }

    _hiddenImprints.Clear();

    // Use only the ACTUAL occupied maze cells as the Voronoi domain.
    var occupied = new List<Vector2Int>(cells.Count);
    for (int i = 0; i < cells.Count; i++)
        occupied.Add(cells[i].cell);

    // Single-role motif: trivial assignment.
    if (rolesList.Count == 1)
    {
        MusicalRole onlyRole = rolesList[0];
        for (int i = 0; i < occupied.Count; i++)
            _hiddenImprints[occupied[i]] = onlyRole;

        Debug.Log($"[MAZE] BuildMazeRoleImprints: gray start, cells={cells.Count}, hidden single role={onlyRole}");
        return;
    }

    // ------------------------------------------------------------
    // Seed selection over occupied cells only
    // ------------------------------------------------------------
    //
    // Strategy:
    // 1) Seed 0 (dominant role) = occupied cell farthest from the star.
    //    This preserves the idea that the first motif role has the strongest territory,
    //    but only within the actual maze footprint.
    // 2) Remaining seeds use farthest-point sampling over occupied cells so roles
    //    spread across the real pattern instead of empty grid space.
    //
    // This fixes patterns like blobs, strokes, tunnels, and rings where the occupied
    // region is only a subset of the full spawn grid.
    // ------------------------------------------------------------

    int seedCount = Mathf.Min(rolesList.Count, occupied.Count);
    var seedCells = new Vector2Int[seedCount];
    var seedRoles = new MusicalRole[seedCount];

    // Seed 0: farthest occupied cell from the star.
    int firstSeedIdx = 0;
    float bestStarDist = float.MinValue;
    for (int i = 0; i < occupied.Count; i++)
    {
        float d = (occupied[i] - starCell).sqrMagnitude;
        if (d > bestStarDist)
        {
            bestStarDist = d;
            firstSeedIdx = i;
        }
    }

    seedCells[0] = occupied[firstSeedIdx];
    seedRoles[0] = rolesList[0];

    // Remaining seeds: farthest-point sampling from already chosen seeds.
    var chosen = new HashSet<Vector2Int> { seedCells[0] };

    for (int s = 1; s < seedCount; s++)
    {
        int bestIdx = -1;
        float bestMinDist = float.MinValue;

        for (int i = 0; i < occupied.Count; i++)
        {
            Vector2Int candidate = occupied[i];
            if (chosen.Contains(candidate)) continue;

            float minDistToChosen = float.MaxValue;
            for (int c = 0; c < s; c++)
            {
                float d = (candidate - seedCells[c]).sqrMagnitude;
                if (d < minDistToChosen)
                    minDistToChosen = d;
            }

            if (minDistToChosen > bestMinDist)
            {
                bestMinDist = minDistToChosen;
                bestIdx = i;
            }
        }

        if (bestIdx < 0)
            break;

        seedCells[s] = occupied[bestIdx];
        seedRoles[s] = rolesList[s];
        chosen.Add(seedCells[s]);
    }

    // If there are more roles than distinct available seed positions, assign only the seeds
    // we could actually place. This is defensive and should be rare.
    int actualSeedCount = chosen.Count;

    // Assign each occupied cell to the nearest chosen seed.
    for (int i = 0; i < occupied.Count; i++)
    {
        Vector2Int gp = occupied[i];

        float best = float.MaxValue;
        int bestSeed = 0;

        for (int s = 0; s < actualSeedCount; s++)
        {
            float d = (gp - seedCells[s]).sqrMagnitude;
            if (d < best)
            {
                best = d;
                bestSeed = s;
            }
        }

        _hiddenImprints[gp] = seedRoles[bestSeed];
    }

    // Helpful distribution log for validation.
    var counts = new Dictionary<MusicalRole, int>();
    for (int i = 0; i < actualSeedCount; i++)
        counts[seedRoles[i]] = 0;

    foreach (var kv in _hiddenImprints)
    {
        if (!counts.ContainsKey(kv.Value))
            counts[kv.Value] = 0;
        counts[kv.Value]++;
    }

    string summary = string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));
    Debug.Log($"[MAZE] BuildMazeRoleImprints: gray start, cells={cells.Count}, hidden Voronoi roles={_hiddenImprints.Count}, seeds={actualSeedCount}, distribution=({summary})");
}
    public void ResetMazeGenerationFlag()
    {
        _mazeAlreadyGenerated = false;
    }
    public IEnumerator GenerateMazeForPhaseWithPaths(Vector2Int starCell, IReadOnlyList<Vector2Int> vehicleCells, float totalSpawnDuration = 1.0f)
    {
        _isBootstrappingMaze = true;
        if (_mazeAlreadyGenerated)
        {
            Debug.Log($"[MAZE] Maze is already generated, skipping.");
            yield break;
        }

        _mazeAlreadyGenerated = true;
        if (drums == null)
        {
            Debug.LogError("[MAZE] No DrumTrack available; cannot build maze.");
            _isBootstrappingMaze = false;
            yield break;
        }

        yield return new WaitUntil(() =>
            drums.HasSpawnGrid() &&
            drums.GetSpawnGridWidth()  > 0 &&
            drums.GetSpawnGridHeight() > 0 &&
            Camera.main != null);

        // Ensure the dust root is active before clearing so that HideVisualsInstant
        // can reliably call StopCoroutine on active MonoBehaviours.
        // If the root was deactivated by SetBridgeCinematicMode, coroutines on child
        // objects are suspended (not cancelled). StopCoroutine on inactive objects is
        // unreliable in Unity — suspended scale/emission ramps can resume when the root
        // is later re-enabled, producing "sprite enabled, emission off" artifacts.
        if (activeDustRoot != null && !activeDustRoot.gameObject.activeSelf)
            activeDustRoot.gameObject.SetActive(true);

        // 1) Clear any existing dust
        ClearMaze();
        drums.SyncTileWithScreen();
        EnsureCellGrid();
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        Debug.Log($"[MAZE] Maze grid size: {w}x{h}");


        // Resolve pattern config from the active motif; null falls back to FullFill inside the method.
        _activeMazePattern = phaseTransitionManager?.currentMotif?.mazePattern;
        _runtimeVoidOnlyDustCreation = false;
        var reserved = new HashSet<Vector2Int> { starCell }; 
        // Pattern-driven growth list
        var cellsToFill = BuildMazeGrowthFromConfig(_activeMazePattern, starCell, reserved);
        Debug.Log($"[MAZE] cellsToFill(pattern) count={cellsToFill.Count}"); 
        _permanentClearCells.Add(starCell);
        const int startupVehicleReserveRadiusCells = 2;
        if (vehicleCells != null) { 
            for (int i = 0; i < vehicleCells.Count; i++) { 
                var v = vehicleCells[i]; 
                for (int dx = -startupVehicleReserveRadiusCells; dx <= startupVehicleReserveRadiusCells; dx++) { 
                    for (int dy = -startupVehicleReserveRadiusCells; dy <= startupVehicleReserveRadiusCells; dy++) { 
                        if (dx * dx + dy * dy > startupVehicleReserveRadiusCells * startupVehicleReserveRadiusCells) 
                            continue;
                        var gp = new Vector2Int(v.x + dx, v.y + dy); 
                        if (!IsInBounds(gp)) continue;
                        reserved.Add(gp); 
                        _permanentClearCells.Add(gp);
                    }
                }
            }
        }
        // Cache pattern oracle so frontier compensation can prefer original wall cells.
        _mazePatternCells = new HashSet<Vector2Int>(cellsToFill.Count);
        foreach (var (cell, _) in cellsToFill)
            _mazePatternCells.Add(cell);

        // --- Voronoi role imprint pass ---
        // Write a DustImprint for every cell before StaggeredGrowthFitDuration spawns them.
        // GetOrCreateCellGO reads GetCellHardness01(gp) and GetCellVisualColor(gp) which
        // consult _imprints, so each cell spawns with the correct role color + hardness
        // without any further per-cell logic in the spawn loop.
        BuildMazeRoleImprints(starCell, cellsToFill);

        float spawnDuration = Mathf.Clamp(totalSpawnDuration, 0.05f, 3.0f);
        Debug.Log($"[MAZE] StaggeredGrowthFitDuration with spawnDuration={spawnDuration}");
        if (_spawnRoutine != null) { 
            StopCoroutine(_spawnRoutine); 
            _spawnRoutine = null;
        } 
        _spawnRoutine = StartCoroutine(StaggeredGrowthFitDuration(cellsToFill, spawnDuration)); 
        yield return _spawnRoutine; 
        EnterRuntimeVoidOnlyDustCreationMode();        
        _spawnRoutine = null;
        // 2) Carve generous holes around the star and each vehicle
        const int starHoleRadiusCells    = 3; // was 1 — make the star sit in a clearly open pocket
        const int vehicleHoleRadiusCells = 2; // local pocket around each ship

        // Star pocket
        
        CarvePermanentDisk(starCell, starHoleRadiusCells);
        BuildStarPocketSet(starCell, starHoleRadiusCells);
        // Vehicle pockets + corridors
        if (vehicleCells != null)
        {
            foreach (var vehCell in vehicleCells)
            {
                // Local clearing so the ship is never visually "in" the dust
                CarvePermanentDisk(vehCell, vehicleHoleRadiusCells);
            }
        }
        // Capture density baseline after all permanent clears are carved.
        // Compensation will maintain this exact solid count throughout the phase.
        _targetSolidCount = TotalSolidCount();
        OnMazeReady?.Invoke(starCell);
        _isBootstrappingMaze = false;
    }
    private void BuildStarPocketSet(Vector2Int starCell, int radiusCells)
    {
        _starClearCells.Clear();

        if (drums == null) return;
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            for (int dy = -radiusCells; dy <= radiusCells; dy++)
            {
                var gp = new Vector2Int(starCell.x + dx, starCell.y + dy);

                if ((uint)gp.x >= (uint)w || (uint)gp.y >= (uint)h)
                    continue;

                if (dx * dx + dy * dy > radiusCells * radiusCells)
                    continue;

                _starClearCells.Add(gp);
            }
        }
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
        d.SetCellSizeDrivenScale(Mathf.Max(0.001f, drums.GetCellWorldSize()), dustFootprintMul, cellClearanceWorld);

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
    private bool TryGetCellState(Vector2Int gp, out DustCellState st)
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
    public void OnDustVisualFadedOut(CosmicDust dust)
    {
        if (dust == null) return;

        // Stop contributing collisions/topology.
        SetDustCollision(dust, false);
        Vector2Int gp = default;
        bool have = _goToCell.TryGetValue(dust.gameObject, out gp);
        if (!have && drums != null)
        {
            gp = drums.WorldToGridPosition(dust.transform.position);
            have = IsInBounds(gp);
        }

        if (have)
        {
            // Only transition Clearing -> Empty. If something else owns the state,
            // leave it alone (defensive).
            if (TryGetCellState(gp, out var st) && st == DustCellState.Clearing)
                SetCellState(gp, DustCellState.Empty);        }
        dust.FinalizeClearedVisuals();
    }

    private GameObject GetOrCreateCellGO(Vector2Int gp)
    {
        EnsureCellGrid();
        if (!IsInBounds(gp)) return null;
        Transform root = activeDustRoot != null ? activeDustRoot : transform;

        // If the active dust container was hidden earlier, bring it back before reuse/instantiate.
        if (root != null && !root.gameObject.activeSelf)
            root.gameObject.SetActive(true);

        var existing = _gridState.CellGo[gp.x, gp.y];
        if (existing != null)
        {
            if (!existing.activeSelf)
                existing.SetActive(true);

            return existing;
        }
        if (dustPrefab == null) return null;

        var go = Instantiate(dustPrefab, drums != null ? drums.GridToWorldPosition(gp) : Vector3.zero, Quaternion.identity, root);
        go.name = $"Cosmic Dust ({gp.x},{gp.y})";

        var dust = go.GetComponent<CosmicDust>();
        if (dust != null)
        {
            dust.SetTrackBundle(this, drums);
            dust.SetCellSizeDrivenScale(Mathf.Max(0.001f, drums.GetCellWorldSize()), dustFootprintMul, cellClearanceWorld);
            // Clear the Awake-state particle burst (ApplyEmissionMultiplierImmediate(100f)) and hide
            // visuals so this new cell enters the same "clean hidden" state as reused cells that
            // came through HideVisualsInstant(). The caller (StaggeredGrowthFitDuration, CommitRegrowCell,
            // VoidGrowCellNow) handles PrepareForReuse + role setup + Begin().
            dust.HideVisualsInstant();
            SetDustCollision(dust, false);
        }

        _gridState.CellGo[gp.x, gp.y] = go;
        _gridState.CellDust[gp.x, gp.y] = dust;
        _goToCell[go] = gp;
        SetCellState(gp, DustCellState.Empty);

        return go;
    }
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
            if (dust != null && dust.Role != MusicalRole.None)
                results.Add(new Vector2Int(x, y));
        }
    }
    public void CarveDustByVehicle(Vector2Int cell, float fadeSeconds)
    {
        if (!IsInBounds(cell)) return;
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        _imprints ??= new Dictionary<Vector2Int, DustImprint>();
        // Revert any MineNode paint — cell regrows as its original Voronoi color.
        // Falls back to keeping the existing imprint if no Voronoi assignment exists.
        if (!RestoreVoronoiImprint(cell))
            PromoteHiddenRole(cell);

        _playerCarvedCells.Add(cell);

        var cellGo = _gridState.CellGo?[cell.x, cell.y];

        bool isVoidCell = _voidGrowCells.Remove(cell);
        if (isVoidCell)
            _imprints?.Remove(cell); // no imprint = no future regrow triggers for this cell
        var explode = cellGo.GetComponentInChildren<Explode>(true);
        Debug.Log($"[DUST-CLEAR] explode={(explode != null ? explode.name : "NULL")} cell={cell} go={cellGo.name}");
        ClearCell(
            cell,
            DustClearMode.FadeAndHide,
            fadeSeconds,
            scheduleRegrow: !isVoidCell,
            runPreExplode: true
        );

        if (!isVoidCell && _hiddenImprints != null && _hiddenImprints.TryGetValue(cell, out var hiddenRole))
        {
            var roleProfile = MusicalRoleProfileLibrary.GetProfile(hiddenRole);
            if (roleProfile != null && roleProfile.regrowthDelay >= 0f)
                RequestRegrowCellAt(cell, roleProfile.regrowthDelay, refreshIfPending: true);
        }
    }
    // Incremental plow carve: removes energyAmount units from the cell, scaled by carveResistance01.
    // Full removal triggers ClearCell + regrowth + ripeness — same as CarveDustByVehicle.
    // Partial chips leave the cell solid at reduced energy.
    public void ChipDustByVehicle(Vector2Int cell, int energyAmount, float fadeSeconds)
    {
        if (!IsInBounds(cell)) return;
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        var cellGo = _gridState.CellGo?[cell.x, cell.y];
        if (cellGo == null) return;
        if (!cellGo.TryGetComponent<CosmicDust>(out var dust) || dust == null) return;

        // Scale chip amount by carve resistance: high resistance = fewer units removed per tick.
        float resistMul = 1f - dust.clearing.carveResistance01;
        int effectiveChip = Mathf.Max(1, Mathf.RoundToInt(energyAmount * resistMul));

        int removed = dust.ChipEnergy(effectiveChip);
        if (removed <= 0) return;

        // Units hit 0: physically clear the cell and handle ripeness + imprint bookkeeping.
        // ChipEnergy has already disabled the collider; we now handle the generator-level teardown.
        if (dust.currentEnergyUnits <= 0)
        {
            _imprints ??= new Dictionary<Vector2Int, DustImprint>();
            if (!RestoreVoronoiImprint(cell))
                PromoteHiddenRole(cell);

            _playerCarvedCells.Add(cell);

            bool isVoidCell = _voidGrowCells.Remove(cell);
            if (isVoidCell) _imprints?.Remove(cell);

            ClearCell(cell, DustClearMode.FadeAndHide, fadeSeconds, scheduleRegrow: !isVoidCell, runPreExplode: true);

            if (!isVoidCell && _hiddenImprints != null && _hiddenImprints.TryGetValue(cell, out var hiddenRole))
            {
                var roleProfile = MusicalRoleProfileLibrary.GetProfile(hiddenRole);
                if (roleProfile != null && roleProfile.regrowthDelay >= 0f)
                    RequestRegrowCellAt(cell, roleProfile.regrowthDelay, refreshIfPending: true);
            }
        }
    }
    
    public void CreateJailCenterForCollectable(
        Vector2Int gpCenter,
        float holdSeconds,
        int ownerId,
        DustClearMode mode = DustClearMode.HideInstant,
        float fadeSeconds = 0.10f,
        float regrowDelaySeconds = -1f)
    {
        // 1) Clear center cell (the "jail cell" the note occupies).
        ClearCell(gpCenter, mode, fadeSeconds, scheduleRegrow: true, regrowDelaySeconds: regrowDelaySeconds);

        // 2) Claim center so it stays empty while jailed.
        if (dustClaims != null && holdSeconds > 0f)
        {
            string owner = $"Collectable#{ownerId}";
            dustClaims.ClaimCell(owner, gpCenter, DustClaimType.TemporaryCarve, seconds: holdSeconds, refresh: true);
        }

        // 3) Also clear the 4 cardinal neighbors so collectables get a visible pocket, not a trapped spawn.
        float neighborRegrow = regrowDelaySeconds >= 0f ? regrowDelaySeconds : holdSeconds;
        var neighbors = new Vector2Int[] { new(1,0), new(-1,0), new(0,1), new(0,-1) };
        foreach (var n in neighbors)
        {
            var np = gpCenter + n;
            if (!IsInBounds(np)) continue;
            if (!HasDustAt(np)) continue;
            ClearCell(np, mode, fadeSeconds, scheduleRegrow: true, regrowDelaySeconds: neighborRegrow);
        }
    }

    /// <summary>
    /// Gradually dissolves all solid dust cells over <paramref name="durationSeconds"/>,
    /// with staggered start delays so they fade out as a wave rather than all at once.
    /// No regrowth is scheduled — cells go permanently empty until the next motif reset.
    /// </summary>
    public void BeginSlowFadeAllDust(float durationSeconds)
    {
        if (_gridState.CellState == null || _gridState.CellGo == null || _gridState.Width <= 0 || _gridState.Height <= 0) return;

        for (int x = 0; x < _gridState.Width; x++)
        for (int y = 0; y < _gridState.Height; y++)
        {
            if (_gridState.CellState[x, y] != DustCellState.Solid) continue;
            var go = _gridState.CellGo[x, y];
            if (go == null) continue;
            float delay = Random.Range(0f, durationSeconds * 0.75f);
            StartCoroutine(DelayedFadeCell(new Vector2Int(x, y), delay));
        }
    }

    private IEnumerator DelayedFadeCell(Vector2Int gp, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        if (_gridState.CellState == null || _gridState.CellState[gp.x, gp.y] != DustCellState.Solid) yield break;
        ClearCell(gp, DustClearMode.FadeAndHide, fadeSeconds: 0.5f, scheduleRegrow: false);
    }
    
    private void RequestRegrowCellAt(Vector2Int gridPos, float delaySeconds = -1f, bool refreshIfPending = false, bool clearImprintOnRefresh = false)
    {
        if (_regrowthSuppressed)
            return;
        if (!IsInBounds(gridPos)) { 

            if (_regrowthScheduler.RegrowthCoroutines != null && _regrowthScheduler.RegrowthCoroutines.TryGetValue(gridPos, out var pending))
            { 
                if (pending != null) StopCoroutine(pending);
                _regrowthScheduler.RegrowthCoroutines.Remove(gridPos);

            } 
            return;
        }
        
        bool shouldSchedule = !_permanentClearCells.Contains(gridPos);

        // If dust already exists here, no need to schedule.
        if (shouldSchedule && HasDustAt(gridPos))
            shouldSchedule = false;

        // Only regrow cells that were originally part of the maze (have an imprint).
        // Cells with no imprint were never maze cells; only GravityVoid should add new positions.
        if (shouldSchedule && (_imprints == null || !_imprints.ContainsKey(gridPos)))
            shouldSchedule = false;

        if (!shouldSchedule)
            return;

        // Compute delay from the active motif's maze config, or fall back to a safe default.
        float delay = delaySeconds >= 0f ? delaySeconds : (_activeMazePattern != null ? _activeMazePattern.dustTiming.regrowDelay : 8f);
       
        EnsureRegrowController();
        _regrow?.RequestRegrowCellAt(gridPos, delay, refreshIfPending);
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
    
    public void SetStarKeepClear(Vector2Int centerCell, int radiusCells, bool forceRemoveExisting)
    {
        if (drums == null) return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        radiusCells = Mathf.Max(0, radiusCells);

        // Build desired star pocket.
        var next = new HashSet<Vector2Int>();
        for (int dy = -radiusCells; dy <= radiusCells; dy++)
        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            if ((dx * dx + dy * dy) > radiusCells * radiusCells) continue;
            var gp = new Vector2Int(centerCell.x + dx, centerCell.y + dy);
            if (gp.x < 0 || gp.y < 0 || gp.x >= w || gp.y >= h) continue;
            next.Add(gp);
        }

        _exclusions.UpdateStarPocket(next, _tmpReleased, _tmpClaimed);

        // DustClaimManager is the authority for keep-clear vetoes.
        const string claimOwner = "PhaseStarPocket";
        if (dustClaims != null)
        {
            for (int i = 0; i < _tmpReleased.Count; i++)
                dustClaims.ReleaseCell(claimOwner, _tmpReleased[i], DustClaimType.KeepClear);

            for (int i = 0; i < _tmpClaimed.Count; i++)
                dustClaims.ClaimCell(claimOwner, _tmpClaimed[i], DustClaimType.KeepClear, seconds: -1f, refresh: true);
        }

        // RELEASE -> allow regrow
        for (int i = 0; i < _tmpReleased.Count; i++)
        {
            var cell = _tmpReleased[i];
            RequestRegrowCellAt(cell, delaySeconds: -1f, refreshIfPending: true, clearImprintOnRefresh: false);
        }

        // CLAIM -> keep empty; optionally clear any existing dust; DO NOT schedule regrow
        if (forceRemoveExisting)
        {
            for (int i = 0; i < _tmpClaimed.Count; i++)
            {
                var cell = _tmpClaimed[i];
                if (TryGetCellGo(cell, out var go) && go != null)
                    ClearCell(cell, DustClearMode.FadeAndHide, fadeSeconds: 2f, scheduleRegrow: false);
            }
        }
    }

    private void CarvePermanentDisk(Vector2Int center, int radiusCells)
    {
        if (drums == null)
            return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            for (int dy = -radiusCells; dy <= radiusCells; dy++)
            {
                var gp = new Vector2Int(center.x + dx, center.y + dy);

                // Bounds check
                if (gp.x < 0 || gp.y < 0 || gp.x >= w || gp.y >= h)
                    continue;

                // Optionally use a circular mask instead of square:
                if (dx * dx + dy * dy > radiusCells * radiusCells)
                    continue;

                DespawnDustAtAndMarkPermanent(gp);
            }
        }
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
        return _mazeTint;
    }

    private float GetCellHardness01(Vector2Int cell)
    {
        if (_imprints != null && _imprints.TryGetValue(cell, out var imp))
            return Mathf.Clamp01(imp.hardness01);

        return Mathf.Clamp01(defaultMazeHardness01);
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
            : new List<MusicalRole> { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove };

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
            : new[] { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove };

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
    
    /// <summary>
    /// Returns the role held by the plurality of solid imprinted neighbors within 1 cell.
    /// Ties broken by global density (least-dense wins). Returns None when no imprinted
    /// solid neighbors exist.
    /// </summary>
    private MusicalRole GetPluralityNeighborRole(Vector2Int cell, MusicalRole excluded)
    {
        if (_imprints == null) return MusicalRole.None;

        int bassC = 0, harmC = 0, leadC = 0, grooveC = 0;
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
                case MusicalRole.Bass:    bassC++;   break;
                case MusicalRole.Harmony: harmC++;   break;
                case MusicalRole.Lead:    leadC++;   break;
                case MusicalRole.Groove:  grooveC++; break;
            }
        }

        int max = Mathf.Max(bassC, Mathf.Max(harmC, Mathf.Max(leadC, grooveC)));
        if (max == 0) return MusicalRole.None;

        // Among tied leaders, prefer the globally least-dense (secondary balance signal).
        MusicalRole best = MusicalRole.None;
        int bestDensity = int.MaxValue;
        UpdatePluralityBest(MusicalRole.Bass,    bassC,   max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Harmony, harmC,   max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Lead,    leadC,   max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Groove,  grooveC, max, ref best, ref bestDensity);
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
        return _mazeTint;
    }
    public void ApplyActiveRoles(IReadOnlyList<MusicalRole> roles)
    {
        _activeRoles = roles != null && roles.Count > 0
            ? new List<MusicalRole>(roles)
            : new List<MusicalRole> { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove };
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

        // Authoritative default: phase-authored maze tint.
//        _mazeTint = profile.mazeColor;

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
                    // _mazeTint (a flat gray) would erase the 4-color Voronoi layout.
                    var gp = new Vector2Int(x, y);
                    if (_imprints != null && _imprints.TryGetValue(gp, out var imp)
                        && imp.role != MusicalRole.None)
                        continue;

                    StartCoroutine(d.RetintOver(seconds, _mazeTint));
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

                StartCoroutine(d.RetintOver(seconds, _mazeTint));
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
                float frameBudget = Mathf.Max(0f, maxSpawnMillisPerFrame) / 1000f;

                while (i < cells.Count && (Time.realtimeSinceStartup - frameStart) < frameBudget)
                {
                    var (grid, pos) = cells[i++];

                    // ---------------------------
                    // GATING
                    // ---------------------------
                    if (_permanentClearCells.Contains(grid)) continue; 
                    if (IsKeepClearCell(grid)) continue; 
                    if (HasDustAt(grid)) continue; 
                    if (IsDustSpawnBlocked(grid)) continue;           
                    // includes permanent + keepclear + claims/holds// if (!Collectable.IsCellFreeStatic(grid)) continue; // never grow dust on top of a collectable
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
                        dust.SetCellSizeDrivenScale(cellWorldSize, dustFootprintMul, cellClearanceWorld);

                        dust.PrepareForReuse();
                        dust.InitializeVisuals(DustTimings);
                        dust.SetGrowInDuration(hexGrowInSeconds);

                        // GetCellVisualColor reads from _imprints if available, otherwise _mazeTint.
                        // GetCellHardness01 reads hardness01 from _imprints if available.
                        Color cellColor = GetCellVisualColor(grid);
                        dust.clearing.hardness01 = GetCellHardness01(grid);

                        // Apply role AND color together so dust.Role is set from birth.
                        // SetTint alone leaves dust.Role = None, which means RetintExisting
                        // cannot distinguish role-colored cells from plain maze cells and
                        // would overwrite them with the flat _mazeTint (gray).
                        if (_imprints != null && _imprints.TryGetValue(grid, out var spawnImprint)
                            && spawnImprint.role != MusicalRole.None)
                        {
                            dust.ApplyRoleAndCharge(spawnImprint.role, cellColor, cellColor.a);
                        }
                        else
                        {
                            // Gray start: initial cells spawn with no role (MusicalRole.None) and maze tint.
                            // Roles are earned dynamically through vehicle carving + regrowth.
                            // dust.Role must be None here so TickDrain's Role guard treats these as
                            // inert gray cells and the star cannot drain them while dormant.
                            dust.ApplyRoleAndCharge(MusicalRole.None, cellColor, cellColor.a);
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
                        SetDustCollision(dust, false);
                        dust.regrowAlphaCapped = true;
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
                float frameBudget = Mathf.Max(0f, maxSpawnMillisPerFrame) / 1000f;

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
                    d.regrowAlphaCapped = false;
                    d.EnsureMinSolidAlpha(0.55f);
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
                dust.ApplyRoleAndCharge(MusicalRole.None, _mazeTint, dust.Charge01);
                _ripenessByCell.Remove(gp);
            }
            else
            {
                _ripenessByCell[gp] = ripeness;
                // Mid-decay: lerp color only (Role stays set so PhaseStar can still drain).
                Color roleColor = profile != null ? profile.GetBaseColor() : Color.white;
                Color full = new Color(roleColor.r, roleColor.g, roleColor.b, dust.Charge01);
                Color gray = new Color(_mazeTint.r, _mazeTint.g, _mazeTint.b, dust.Charge01);
                float effectiveRipeness = Mathf.Max(ripeness, dust.Charge01);
                dust.SetTint(Color.Lerp(gray, full, effectiveRipeness));
            }
        }
    }

    private void ProcessTintDiffusion(float dt)
    {
        if (!enableTintDiffusion) return;
        if (_tintDiffusionSystem == null) return;
        _tintDiffusionSystem.Tick(
            dt: dt,
            enabled: enableTintDiffusion,
            maxCellsPerTick: tintDiffusionMaxCellsPerTick,
            neighborRadius: tintDiffusionRadius,
            strength: tintDiffusionStrength,
            minDelta: tintDiffusionMinDelta,
            propagateOnChange: tintDiffusionPropagateOnChange,
            intervalSeconds: tintDiffusionInterval);
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
        MarkTintDirty(grid, tintDirtyMarkRadius);

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
            if (dust != null && dust.Role != MusicalRole.None) return true;
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
            if (dust != null && dust.Role == role) return true;
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
            hardness01         = profile.GetDustHardness01(),
            carveResistance01  = profile.GetCarveResistance01(),
            drainResistance01  = profile.GetDrainResistance01(),
            maxEnergyUnits     = maxUnits
        };

        if (TryGetDustAt(cell, out var dust))
        {
            dust.clearing.carveResistance01 = profile.GetCarveResistance01();
            dust.clearing.drainResistance01 = profile.GetDrainResistance01();
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
        Debug.Log($"[PHANTOM] Scan complete. Found {phantoms} phantom colliders.");
    }
}
