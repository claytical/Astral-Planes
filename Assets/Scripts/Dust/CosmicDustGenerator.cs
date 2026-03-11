using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class CosmicDustGenerator : MonoBehaviour
{
    private CosmicDustRegrowthController _regrow;
    public GameObject dustPrefab;
    public Transform activeDustRoot;
    Dictionary<Vector2Int, DustImprint> _imprints; 
    private readonly Dictionary<Vector2Int, MusicalRole> _regrowExcludeRoleByCell = new Dictionary<Vector2Int, MusicalRole>(2048);
    private bool _cellGridReady;
    [Header("Maze Collision Shape")]
    [Tooltip("World-units clearance inside each cell. 0 = watertight.")]
    public float cellClearanceWorld = 0f;

    [Header("Hardness")]
    [Tooltip("Baseline dust hardness for non-imprinted maze cells. Imprinted MineNode cells override this via DustImprint.hardness01.")]
    [Range(0f, 1f)]
    [SerializeField] private float defaultMazeHardness01 = 0f;
// Track bin edge so we only fire once per bin boundary
    int lastPlayheadBin = -1;
    float lastPulseTime = -999f; // optional: debouncing / spam guard
    private bool _regrowthSuppressed = false;
    struct DustImprint
    {
        public Color color;
        public float healDelay;
        public float hardness01;
        public MusicalRole role;
    }
    [SerializeField] private CosmicDust.DustVisualTimings dustTimings = new CosmicDust.DustVisualTimings
    {
        spriteScaleInSeconds = 0.20f,
        spriteScaleOutSeconds = 0.20f,
        particleGrowInSeconds = 1.00f,
        fadeOutSeconds = 0.20f
    };
    
    // --- Extracted controllers (refactor targets) ---
    private CosmicDustExclusionMap _exclusions = new CosmicDustExclusionMap();
    private CosmicDustTintDiffusionSystem _tintDiffusionSystem;
    private readonly List<Vector2Int> _tmpReleased = new List<Vector2Int>(512);
    private readonly List<Vector2Int> _tmpClaimed  = new List<Vector2Int>(512);
    [Header("Vehicle erosion (temporary)")]
    [SerializeField] private float vehicleErodeRadius = 1f;   // world units
    [SerializeField] private int vehicleErodePerTick = 2;       // max cells per call
    [Header("MineNode Erosion")]
    [SerializeField] private float mineNodeErodeRadius = 1f;

    [SerializeField] private int mineNodeErodePerTick = 10;
    [Header("Dust Visual Footprint")]
    [Range(0.8f, 1.6f)]
    public float dustFootprintMul = 1.15f;

    [Header("Tile Sizing")]
    [SerializeField] private float tileDiameterWorld = 1f;          // cached from dustfab.hitbox
    public int GridW => _cellW;
    public int GridH => _cellH;
    // ------------------------------------------------------------------
    // Authoritative grid (no pooling). The grid is the traffic cop.
    // ------------------------------------------------------------------
    private enum DustCellState
    {
        Empty = 0,
        PendingRegrow = 1,
        Regrowing = 2,
        Clearing = 3,
        Solid = 4,
    }
    private GameObject[,] _cellGo;
    private CosmicDust[,] _cellDust;
    private DustCellState[,] _cellState;
    private int _cellW = -1, _cellH = -1;
    private Dictionary<GameObject, Vector2Int> _goToCell = new Dictionary<GameObject, Vector2Int>(1024);

    [Header("Regrow Step Gate")]
    [Tooltip("How many cells are allowed to transition from PendingRegrow -> Solid per drum step.")]
    [SerializeField] private int regrowCellsPerStep = 1;
    [Tooltip("Seconds to wait after a cell becomes visible again before enabling its collider.")]
    [SerializeField] private float regrowColliderEnableDelaySeconds = 0.20f;

    private readonly Queue<List<Vector2Int>> _pendingCorridorRegrowth = new Queue<List<Vector2Int>>();
    private Dictionary<Vector2Int, bool> _fillMap = new();
    private Dictionary<Vector2Int, Coroutine> _regrowthCoroutines = new();
    private readonly Dictionary<Vector2Int, Coroutine> _voidGrowCoroutines = new();
    private readonly Dictionary<Vector2Int, Coroutine> _voidSolidifyCoroutines = new();
    // Per-phase regrow pacing (lets PhaseStarBehaviorProfile drive maze closure without refactoring MazeArchetype).
    private readonly Dictionary<MazeArchetype, float> _regrowDelayMulByPhase = new();
    [SerializeField] private Color _mazeTint = new Color(0.7f, 0.7f, 0.7f, .25f);
    private PhaseStarBehaviorProfile _activeProfile;
    [Header("Tint Blending (Neighborhood)")]
    [Tooltip("When MineNodes imprint dust colors, blend the imprint toward nearby cell tints to avoid sharp grid seams.")]
    [Range(0, 3)] [SerializeField] private int imprintBlendRadius = 1;
    [Tooltip("0 = no blending (pure imprint). 1 = fully neighborhood average.")]
    [Range(0f, 1f)] [SerializeField] private float imprintNeighborWeight = 0.55f;
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

    private float GetRegrowDelayMul(MazeArchetype phase)
        => _regrowDelayMulByPhase.TryGetValue(phase, out var m) ? m : 1f;

    private HashSet<Vector2Int> _permanentClearCells = new HashSet<Vector2Int>();
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
    private readonly Dictionary<MusicalRole, int> _solidCountByRole = new Dictionary<MusicalRole, int>
    {
        { MusicalRole.Bass,    0 },
        { MusicalRole.Harmony, 0 },
        { MusicalRole.Lead,    0 },
        { MusicalRole.Groove,  0 },
    };
    public float TileDiameterWorld
    {
        get => tileDiameterWorld;
        set => tileDiameterWorld = value;
    }
    [Header("Dust Terrain Query")]
    [SerializeField] private float dustQueryRadiusWorld = 1.25f;
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

    // Back-compat hooks (no-op now that composite rebuilds are removed).

    public enum DustClearMode
    {
        FadeAndHide,
        HideInstant
    }
    public void ClearCell(Vector2Int gp, DustClearMode mode, float fadeSeconds, bool scheduleRegrow, MazeArchetype phase, float regrowDelaySeconds = -1f) { 
        if (!TryGetCellState(gp, out var st)) return; 
        if (st == DustCellState.Empty || st == DustCellState.Clearing || st == DustCellState.PendingRegrow) { 
            // Optionally refresh regrow timer even if already empty.
            if (scheduleRegrow) 
                RequestRegrowCellAt(gp, phase, regrowDelaySeconds, refreshIfPending: true); 
            return;
        }
        if (!TryGetCellGo(gp, out var go) || go == null) {
            SetCellState(gp, DustCellState.Empty); 
            if (scheduleRegrow) 
                RequestRegrowCellAt(gp, phase, regrowDelaySeconds, refreshIfPending: true);
            return;
        }

        // Immediately stop being terrain.
        SetCellState(gp, DustCellState.Clearing); 
        if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null){ 
           SetDustCollision(dust, false);
        }

         // Visual policy
         if (dust != null) { 
             if (mode == DustClearMode.HideInstant){ 
                 dust.HideVisualsInstant();
                SetCellState(gp, DustCellState.Empty); 
             }
             else {
                dust.DissipateAndHideVisualOnly(dustTimings.spriteScaleOutSeconds);
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
             RequestRegrowCellAt(gp, phase, regrowDelaySeconds, refreshIfPending: true); 
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
        if (_regrowthCoroutines != null)
        {
            foreach (var kv in _regrowthCoroutines)
            {
                if (kv.Value != null)
                    StopCoroutine(kv.Value);
            }
            _regrowthCoroutines.Clear();
        }

        // Stop any void-grow coroutines that might still be animating dust in.
        if (_voidGrowCoroutines != null)
        {
            foreach (var kv in _voidGrowCoroutines)
            {
                if (kv.Value != null)
                    StopCoroutine(kv.Value);
            }
            _voidGrowCoroutines.Clear();
        }

        // Hide/discard transient cells from the outgoing motif so nothing "pops" during coral.
        EnsureCellGrid();
        if (_cellState != null && _cellGo != null)
        {
            for (int x = 0; x < _cellW; x++)
            {
                for (int y = 0; y < _cellH; y++)
                {
                    var gp = new Vector2Int(x, y);
                    var st = _cellState[x, y];

                    if (st == DustCellState.PendingRegrow ||
                        st == DustCellState.Regrowing ||
                        st == DustCellState.Clearing)
                    {
                        _cellState[x, y] = DustCellState.Empty;

                        var go = _cellGo[x, y];
                        if (hideTransientDust && go != null)
                            HideCellGO(go);
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
    public bool TryResolveDustCellFromWorldPoint(Vector2 world, int searchRadiusCells, out Vector2Int resolved)
    {
        resolved = default;
        if (drums == null) return false;

        Vector2Int baseCell = drums.WorldToGridPosition(world);

        // Perfect hit
        if (HasDustAt(baseCell))
        {
            resolved = baseCell;
            return true;
        }

        // Spiral / ring search outward (Manhattan rings)
        for (int r = 1; r <= searchRadiusCells; r++)
        {
            // Top/Bottom rows of ring
            for (int dx = -r; dx <= r; dx++)
            {
                var c1 = new Vector2Int(baseCell.x + dx, baseCell.y + r);
                if (HasDustAt(c1)) { resolved = c1; return true; }

                var c2 = new Vector2Int(baseCell.x + dx, baseCell.y - r);
                if (HasDustAt(c2)) { resolved = c2; return true; }
            }

            // Left/Right cols of ring (skip corners already checked)
            for (int dy = -r + 1; dy <= r - 1; dy++)
            {
                var c1 = new Vector2Int(baseCell.x + r, baseCell.y + dy);
                if (HasDustAt(c1)) { resolved = c1; return true; }

                var c2 = new Vector2Int(baseCell.x - r, baseCell.y + dy);
                if (HasDustAt(c2)) { resolved = c2; return true; }
            }
        }

        return false;
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

            int rSq = vehicleNoSpawnRadiusCells * vehicleNoSpawnRadiusCells;
            for (int i = 0; i < vehicleCells.Count; i++)
            {
                var vc = vehicleCells[i];
                int dx = gp.x - vc.x;
                int dy = gp.y - vc.y;
                if (dx * dx + dy * dy <= rSq) return true;
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
                    existingDust.ApplyRoleAndCharge(imprintRole, c, c.a);
                    existingDust.clearing.hardness01 = imprintHardness01;
                    continue;
                }

    // 2) Only after that, decide whether we’re allowed to SPAWN dust into empty space.
                if (_permanentClearCells.Contains(gp)) continue;
                if (IsKeepClearCell(gp)) continue;
                if (dustClaims != null && dustClaims.IsBlocked(gp)) continue;
                if (IsDustSpawnBlocked(gp)) continue;
                if (NearAnyVehicle(gp)) continue;

    // 3) Spawn/regrow if empty
                if (_voidGrowCoroutines.ContainsKey(gp))
                    continue;

                _voidGrowCoroutines[gp] = StartCoroutine(VoidGrowCellNow(gp, imprintRole, c, imprintHardness01, growInSeconds));
            }
        }

        return processed;
    }
    /// <summary>
    /// Called by CosmicDust.DrainCharge when a cell's visual alpha drops below the
    /// solid-visibility threshold (0.55). The cell is physically drained but was
    /// never explicitly "cleared" by gameplay — this bridges that gap so the
    /// collider doesn't linger as an invisible wall.
    /// </summary>
    public void OnDustDrainedBelowThreshold(CosmicDust dust)
    {
        if (dust == null) return;

        // Resolve grid position.
        Vector2Int gp;
        bool have = _goToCell.TryGetValue(dust.gameObject, out gp);
        if (!have && drums != null)
        {
            gp = drums.WorldToGridPosition(dust.transform.position);
            have = IsInBounds(gp);
        }
        if (!have) return;

        // Only act if the cell is still considered Solid. If it's already
        // Clearing/Empty/PendingRegrow, someone else owns the transition.
        if (!TryGetCellState(gp, out var st) || st != DustCellState.Solid)
            return;

        Debug.Log($"[DUSTGEN] DrainedBelowThreshold grid={gp} alpha={dust.CurrentTint.a:F2}", dust);

        // Kill collision immediately (CosmicDust already did this, belt-and-suspenders).
        SetDustCollision(dust, false);

        // Transition to Clearing → the existing dissipate path handles the
        // visual fade-out and will call OnDustVisualFadedOut to finalize Empty.
        SetCellState(gp, DustCellState.Clearing);
        dust.DissipateAndHideVisualOnly(0.3f);

        // Schedule regrow so the maze heals.
        MazeArchetype phase = GetCurrentPhaseSafe();
        RequestRegrowCellAt(gp, phase, refreshIfPending: true);
    }
    private IEnumerator VoidGrowCellNow(Vector2Int gp, MusicalRole role, Color tintWithAlpha, float hardness01, float growInSeconds)
    {
        if (!IsInBounds(gp)) { _voidGrowCoroutines.Remove(gp); yield break; }

        bool veto0_perm        = _permanentClearCells.Contains(gp);
        bool veto0_spawnBlocked = IsDustSpawnBlocked(gp);
        bool veto0_claim       = (dustClaims != null && dustClaims.IsBlocked(gp));
        bool veto0_keep        = IsKeepClearCell(gp);

        // Permanent/spawn-block/claim are hard vetoes for even showing visuals.
        // Keep-clear is NOT a veto for visuals (it only prevents solid/collision).
        if (veto0_perm || veto0_spawnBlocked || veto0_claim)
        {
            Debug.Log($"[VOID_GROW] ABORT_START gp={gp} perm={veto0_perm} keep={veto0_keep} spawnBlocked={veto0_spawnBlocked} claim={veto0_claim}");
            _voidGrowCoroutines.Remove(gp);
            yield break;
        }

        var go = GetOrCreateCellGO(gp);
        if (go == null)
        {
            Debug.Log($"[VOID_GROW] ABORT no-go gp={gp}");
            _voidGrowCoroutines.Remove(gp);
            yield break;
        }

        Debug.Log($"[VOID_GROW] START gp={gp} growIn={growInSeconds:F2} a={tintWithAlpha.a:F2} role={role} keep={veto0_keep}");

        SetCellState(gp, DustCellState.Regrowing);

        CosmicDust dust = null;
        if (go.TryGetComponent(out dust) && dust != null)
        {
            dust.PrepareForReuse();
            dust.SetVisualTimings(dustTimings);

            // void uses remaining-bin time
            dust.SetGrowInDuration(growInSeconds);

            dust.clearing.hardness01 = hardness01;
            dust.ApplyRoleAndCharge(role, tintWithAlpha, tintWithAlpha.a);
            dust.SetFeedbackColors(Color.white, Color.darkGray);
            dust.Begin();

            // Always non-colliding during grow
            SetDustCollision(dust, false);
        }
        if (dust != null)
        {
            var sr = dust.GetComponentInChildren<SpriteRenderer>(true);
            if (sr != null)
                Debug.Log($"[VOID_SPR] enabled={sr.enabled} color={sr.color} scale={sr.transform.localScale}");
        }
        float enableDelay = Mathf.Max(regrowColliderEnableDelaySeconds, growInSeconds * 0.85f);
        yield return new WaitForSeconds(enableDelay);

        if (!IsInBounds(gp) || _permanentClearCells.Contains(gp))
        {
            SetCellState(gp, DustCellState.Empty);
            FadeAndHideCellGO(go);
            _voidGrowCoroutines.Remove(gp);
            yield break;
        }

        bool veto1_spawnBlocked = IsDustSpawnBlocked(gp);
        bool veto1_vehicle      = IsVehicleOverlappingCell(gp);
        bool veto1_claim        = (dustClaims != null && dustClaims.IsBlocked(gp));
        bool veto1_keep         = IsKeepClearCell(gp);

        // Hard vetoes at end: don't keep visuals if spawn is blocked, vehicle overlaps, or claim blocks.
        if (veto1_spawnBlocked || veto1_vehicle || veto1_claim)
        {
            Debug.Log($"[VOID_GROW] ABORT_END gp={gp} keep={veto1_keep} spawnBlocked={veto1_spawnBlocked} vehicle={veto1_vehicle} claim={veto1_claim}");
            SetCellState(gp, DustCellState.PendingRegrow);
            FadeAndHideCellGO(go);
            EnqueueStepRegrow(gp);
            _voidGrowCoroutines.Remove(gp);
            yield break;
        }

        // Keep-clear at end: allow visuals, but never become solid/colliding.
        if (veto1_keep)
        {
            Debug.Log($"[VOID_GROW] VISUAL_ONLY gp={gp} (keep-clear)");
            SetCellState(gp, DustCellState.Regrowing); // or define a VisualOnly state later
            if (dust != null) SetDustCollision(dust, false);
            _voidGrowCoroutines.Remove(gp);
            yield break;
        }

        // Otherwise: become solid.
        SetCellState(gp, DustCellState.Solid);
        if (dust != null) SetDustCollision(dust, true);

        Debug.Log($"[VOID_GROW] SOLID gp={gp}");
        _voidGrowCoroutines.Remove(gp);
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
    public bool IsKeepClearCell(Vector2Int cell) => dustClaims != null && dustClaims.IsBlocked(cell);
    public void SetVehicleKeepClear(int ownerId, Vector2Int centerCell, int radiusCells, MazeArchetype phase, bool forceRemoveExisting, float forceRemoveFadeSeconds = 0.20f)
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
        RequestRegrowCellAt(cell, phase, delaySeconds: -1f, refreshIfPending: true, clearImprintOnRefresh: false);
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
                CarveDustAt(cell, fade);
            }

            // Optional: remove any persistent imprint so the pocket reads clean.
            _imprints?.Remove(cell);
            _fillMap[cell] = false;

            // Ensure a regrow attempt exists (it will self-delay while the keep-clear claim is active).
            RequestRegrowCellAt(cell, phase, delaySeconds: -1f, refreshIfPending: true, clearImprintOnRefresh: false);
        }
    }
}
    public void ReleaseVehicleKeepClear(int ownerId, MazeArchetype phase = MazeArchetype.Establish)
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
        RequestRegrowCellAt(cell, phase, delaySeconds: -1f, refreshIfPending: true, clearImprintOnRefresh: false);
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
                cell => { TryGetDustAt(cell, out var d); return d; },
                GetCellVisualColor);
        
    }

    /// <summary>
    /// Repositions and rescales all existing dust cell GOs to match the current play area.
    /// Call this after changing grid offset or cell size at runtime (e.g. from DrumTrack.SyncTileWithScreen).
    /// </summary>
    public void ResyncAllCellPositions()
    {
        if (drums == null || _cellGo == null) return;

        float cellSize = Mathf.Max(0.001f, drums.GetCellWorldSize());

        for (int x = 0; x < _cellW; x++)
        {
            for (int y = 0; y < _cellH; y++)
            {
                var go = _cellGo[x, y];
                if (go == null) continue;

                var gp = new Vector2Int(x, y);
                go.transform.position = (Vector3)drums.GridToWorldPosition(gp);

                var dust = _cellDust[x, y];
                if (dust != null)
                    dust.SetCellSizeDrivenScale(cellSize, dustFootprintMul, cellClearanceWorld);
            }
        }
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
            _cellGridReady = (_cellGo != null); // or a stronger condition
            if (!_cellGridReady) return;
        }

        if (!drums) return;
        if (_regrowthSuppressed)
            return;
        EnsureRegrowController();
        // Tint diffusion: keep visual seams soft around recent changes.
        ProcessTintDiffusion(Time.deltaTime);

        if (drums == null) return;


        // Regrow step gate: promote PendingRegrow -> Regrowing/Solid rhythmically on drum steps.
        _regrow?.ProcessStepGate(drums.currentStep);
    }

    private bool IsVehicleOverlappingCell(Vector2Int gp)
    {        _goToCell ??= new Dictionary<GameObject, Vector2Int>(1024);
        _permanentClearCells ??= new HashSet<Vector2Int>();
        // Conservative, simple overlap check: if we cannot query, assume false.
        if (drums == null) return false;
        if (vehicleMask.value == 0) return false;

        float cellWorld = Mathf.Max(0.001f, drums.GetCellWorldSize());
        Vector2 center = drums.GridToWorldPosition(gp);
        Vector2 size = Vector2.one * (cellWorld * regrowVetoBoxMul);

        // Use a small non-alloc overlap.
        if (_vehicleVetoHits == null || _vehicleVetoHits.Length != regrowVetoMaxHits)
            _vehicleVetoHits = new Collider2D[regrowVetoMaxHits];

        int hits = Physics2D.OverlapBoxNonAlloc(center, size, 0f, _vehicleVetoHits, vehicleMask);
        return hits > 0;
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

        var go = GetOrCreateCellGO(gp);
        if (go == null) yield break;
        if (!go.activeSelf)
            go.SetActive(true);
        // Visual comes back first.
        SetCellState(gp, DustCellState.Regrowing);
        CosmicDust dust = null;
        if (go.TryGetComponent<CosmicDust>(out dust) && dust != null)
        {
            dust.PrepareForReuse();
            dust.SetVisualTimings(dustTimings);
            dust.SetGrowInDuration(dustTimings.particleGrowInSeconds);

            // --- Role resolution ---
            // Priority 1: cell has a live imprint with a real role (Voronoi, MineNode, void).
            // Priority 2: no imprint (vehicle-carved or PhaseStar-drained) → least-dense role,
            //             so the maze self-balances toward equal representation of all four roles.
            MusicalRole excludedRole = MusicalRole.None;
            if (_regrowExcludeRoleByCell.TryGetValue(gp, out var ex))
                excludedRole = ex;

            MusicalRole regrowRole = GetLeastDenseRoleExcluding(excludedRole);
            // --- Color from role profile (authoritative source) ---
            var roleProfile = MusicalRoleProfileLibrary.GetProfile(regrowRole);
            Color regrowTint = (roleProfile != null) ? roleProfile.GetBaseColor() : _mazeTint;

            // Write / update the imprint so future regrows of this cell remember the role.
            _imprints ??= new Dictionary<Vector2Int, DustImprint>();
            _imprints[gp] = new DustImprint
            {
                color      = regrowTint,
                hardness01 = roleProfile != null ? roleProfile.GetDustHardness01() : defaultMazeHardness01,
                role       = regrowRole,
                healDelay  = 0f
            };

            dust.clearing.hardness01 = _imprints[gp].hardness01;
            dust.ApplyRoleAndCharge(regrowRole, regrowTint, regrowTint.a);

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
    }

    private void EnqueueStepRegrow(Vector2Int gp)
    {
        EnsureRegrowController();
        _regrow?.EnqueueStepRegrow(gp);
    }
    
    
    
    private List<(Vector2Int grid, Vector3 world)> BuildMazeGrowthForPhase(
        MazeArchetype phase,
        Vector2Int starCell,
        HashSet<Vector2Int> reservedCells)
    {
        if (drums == null) return new List<(Vector2Int, Vector3)>();

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return new List<(Vector2Int, Vector3)>();

        // Delegates required by CosmicDustMazePatterns
        Func<int, List<Vector2Int>> getDirsByRow = row => GetHexDirections(row);
        Func<Vector2Int, Vector3> gridToWorld = cell => drums.GridToWorldPosition(cell);
        Func<int, int, bool> isCellAvailable = (x, y) => drums.IsSpawnCellAvailable(x, y);

        // World veto: keep your existing screen cull.
        Func<Vector3, bool> includeWorld = world => true;

        // Grid veto for BFS-based patterns (ring chokepoints)
        Func<Vector2Int, bool> includeCellForBfs = cell =>
        {
            if ((uint)cell.x >= (uint)w || (uint)cell.y >= (uint)h) return false;

            // IMPORTANT: allow the BFS seed (starCell) even though it's reserved.
            // We will still filter it out from emitted growth later.
            if (cell == starCell) return true;

            if (!drums.IsSpawnCellAvailable(cell.x, cell.y)) return false;
            if (reservedCells != null && reservedCells.Contains(cell)) return false;
            if (_permanentClearCells != null && _permanentClearCells.Contains(cell)) return false;
            if (IsKeepClearCell(cell)) return false;
            return true;
        };


        // IMPORTANT: Patterns return the *cells to place dust*.
        // We apply a final reserved/permanent/keep-clear filter afterward as defense-in-depth.
        List<(Vector2Int cell, Vector3 world)> growth = null;

        switch (phase)
        {
            case MazeArchetype.Establish:
            {
                // Legacy behavior: fill everything except reserved.
                var rect = new List<(Vector2Int, Vector3)>(w * h);
                for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    if (!drums.IsSpawnCellAvailable(x, y)) continue;
                    var gp = new Vector2Int(x, y);
                    if (reservedCells != null && reservedCells.Contains(gp)) continue;
                    if (_permanentClearCells != null && _permanentClearCells.Contains(gp)) continue;
                    if (IsKeepClearCell(gp)) continue;

                    var world = drums.GridToWorldPosition(gp);
                    if (!IsWorldPositionInsideScreen(world)) continue;
                    rect.Add((gp, world));
                }
                growth = rect;
                break;
            }

            case MazeArchetype.Evolve:
            {
                // Moderate CA
                float fillChance = Mathf.Clamp01(GetFillProbability(phase));
                growth = CosmicDustMazePatterns.BuildCA(
                    width: w,
                    height: h,
                    fillChance: fillChance,
                    iterations: 3,
                    getHexDirectionsByRow: getDirsByRow,
                    gridToWorld: gridToWorld,
                    isCellAvailable: isCellAvailable,
                    includeWorld: includeWorld);
                break;
            }

            case MazeArchetype.Intensify:
            {
                // Denser CA (slightly higher fill chance + same iteration count)
                float fillChance = Mathf.Clamp01(GetFillProbability(phase) + 0.15f);
                growth = CosmicDustMazePatterns.BuildCA(
                    width: w,
                    height: h,
                    fillChance: fillChance,
                    iterations: 3,
                    getHexDirectionsByRow: getDirsByRow,
                    gridToWorld: gridToWorld,
                    isCellAvailable: isCellAvailable,
                    includeWorld: includeWorld);
                break;
            }

            case MazeArchetype.Release:
            {
                // “Breath / breakdown”: ring chokepoints reads like structure-with-air.
                // Tunables: spacing larger = fewer rings; thickness smaller = thinner walls.
                int spacing = 6;
                int thickness = 1;
                float jitter = 0.35f;

                growth = CosmicDustMazePatterns.BuildRingChokepoints(
                    center: starCell,
                    width: w,
                    height: h,
                    ringSpacing: spacing,
                    ringThickness: thickness,
                    jitter: jitter,
                    getHexDirectionsByRow: getDirsByRow,
                    gridToWorld: gridToWorld,
                    isCellAvailable: isCellAvailable,
                    includeCellForBfs: includeCellForBfs,
                    includeWorld: includeWorld);
                break;
            }

            case MazeArchetype.Wildcard:
            {
                // Glitchy scribbles
                growth = CosmicDustMazePatterns.BuildDrunkenStrokes(
                    width: w,
                    height: h,
                    strokes: 10,
                    maxLen: 18,
                    stepJitter: 0.35f,
                    dilate: 0.30f,
                    getHexDirectionsByRow: getDirsByRow,
                    gridToWorld: gridToWorld,
                    isCellAvailable: isCellAvailable,
                    includeWorld: includeWorld);
                break;
            }

            case MazeArchetype.Pop:
            {
                // Polka dots / hooky grid mask
                growth = CosmicDustMazePatterns.BuildPopDots(
                    width: w,
                    height: h,
                    step: 4,
                    phaseOffset: Random.Range(0, 8),
                    gridToWorld: gridToWorld,
                    isCellAvailable: isCellAvailable,
                    includeWorld: includeWorld);
                break;
            }

            default:
            {
                // Safe fallback: treat as Establish rectangle fill
                var rect = new List<(Vector2Int, Vector3)>(w * h);
                for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    if (!drums.IsSpawnCellAvailable(x, y)) continue;
                    var gp = new Vector2Int(x, y);
                    if (reservedCells != null && reservedCells.Contains(gp)) continue;
                    if (_permanentClearCells != null && _permanentClearCells.Contains(gp)) continue;
                    if (IsKeepClearCell(gp)) continue;

                    var world = drums.GridToWorldPosition(gp);
                    if (!IsWorldPositionInsideScreen(world)) continue;
                    rect.Add((gp, world));
                }
                growth = rect;
                break;
            }
        }

        if (growth == null)
            growth = new List<(Vector2Int, Vector3)>();

        // Final filter: enforce reserved/permanent/keep-clear *even if a pattern emitted them*.
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
        MazeArchetype phase,
        Vector2Int starCell,
        List<(Vector2Int cell, Vector3 world)> cells)
    {
        if (cells == null || cells.Count == 0) return;
        if (drums == null) return;

        _imprints ??= new Dictionary<Vector2Int, DustImprint>(cells.Count * 2);

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        // Resolve dominant role from the active profile; fall back to Bass.
        MusicalRole dominant = (_activeProfile != null)
            ? _activeProfile.dominantRole
            : MusicalRole.Bass;

        // --- Role assignment: currently Voronoi for all archetypes.
        // Future: switch on phase to use archetype-specific distributions
        // (e.g. radial wedges for Release, striped for Pop, noise blobs for Wildcard).
        var roleMap = CosmicDustMazePatterns.AssignRolesVoronoi(cells, starCell, w, h, dominant, phase);

        // --- Write imprints ---
        for (int i = 0; i < cells.Count; i++)
        {
            var gp   = cells[i].cell;
            if (!roleMap.TryGetValue(gp, out var role)) continue;

            var roleProfile = MusicalRoleProfileLibrary.GetProfile(role);
            if (roleProfile == null) continue;

            // Base color carries alpha = baseAlpha from the role profile.
            // This makes hardness directly readable: Bass cells are more opaque than Lead cells.
            Color baseColor = roleProfile.GetBaseColor();

            _imprints[gp] = new DustImprint
            {
                color      = baseColor,
                hardness01 = roleProfile.GetDustHardness01(),
                role       = role,
                healDelay  = 0f   // maze cells use phase-default regrow delay
            };
        }

        Debug.Log($"[MAZE] BuildMazeRoleImprints: phase={phase} dominant={dominant} cells={cells.Count} imprinted={roleMap.Count}");
    }

    public IEnumerator GenerateMazeForPhaseWithPaths(MazeArchetype phase, Vector2Int starCell, IReadOnlyList<Vector2Int> vehicleCells, float totalSpawnDuration = 1.0f)
    {
        if (drums == null)
        {
            Debug.LogError("[MAZE] No DrumTrack available; cannot build maze.");
            yield break;
        }

        Debug.Log($"[MAZE] GenerateMazeForPhaseWithPaths START phase={phase} starCell={starCell} vehicleCount={(vehicleCells != null ? vehicleCells.Count : 0)} permCount(before grid wait)={_permanentClearCells.Count}");

        yield return new WaitUntil(() =>
            drums.HasSpawnGrid() &&
            drums.GetSpawnGridWidth()  > 0 &&
            drums.GetSpawnGridHeight() > 0 &&
            Camera.main != null);

        // 1) Clear any existing dust
        ClearMaze();
        drums.SyncTileWithScreen();
        EnsureCellGrid();
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        Debug.Log($"[MAZE] Maze grid size: {w}x{h}");

        // Build a mask of reserved cells (star + vehicles)
        var reserved = new HashSet<Vector2Int> { starCell };
        _permanentClearCells.Add(starCell);

        if (vehicleCells != null)
        {
            for (int i = 0; i < vehicleCells.Count; i++)
            {
                var v = vehicleCells[i];
                reserved.Add(v);
                _permanentClearCells.Add(v);
            }
        }

// Pattern-driven growth list
        var cellsToFill = BuildMazeGrowthForPhase(phase, starCell, reserved);
        Debug.Log($"[MAZE] cellsToFill(pattern) count={cellsToFill.Count}");

        // --- Voronoi role imprint pass ---
        // Write a DustImprint for every cell before StaggeredGrowthFitDuration spawns them.
        // GetOrCreateCellGO reads GetCellHardness01(gp) and GetCellVisualColor(gp) which
        // consult _imprints, so each cell spawns with the correct role color + hardness
        // without any further per-cell logic in the spawn loop.
        BuildMazeRoleImprints(phase, starCell, cellsToFill);

        float spawnDuration = Mathf.Clamp(totalSpawnDuration, 0.05f, 3.0f);
        Debug.Log($"[MAZE] StaggeredGrowthFitDuration with spawnDuration={spawnDuration}");
        yield return StartCoroutine(StaggeredGrowthFitDuration(cellsToFill, spawnDuration));

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
        OnMazeReady?.Invoke(starCell);
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
    public bool IsEffectivelyOpenCell(Vector2Int gp)
    {
        // “Open” means: the cell is currently not solid dust terrain (i.e., no collider wall),
        // OR it is permanently cleared.
        if (_permanentClearCells.Contains(gp)) return true;

        // Authoritative: if we have dust (Solid), it is not open.
        return !HasDustAt(gp);
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
    if (_cellGo != null && _cellW == w && _cellH == h) return;

    _cellW = w; _cellH = h;
    _cellGo = new GameObject[w, h];
    _cellDust = new CosmicDust[w, h];
    _cellState = new DustCellState[w, h];
    _goToCell.Clear();

    // Explicitly initialize state to Empty (avoid enum-default pitfalls)
    for (int x = 0; x < w; x++)
    for (int y = 0; y < h; y++)
        _cellState[x, y] = DustCellState.Empty;

    var root = (activeDustRoot != null) ? activeDustRoot : transform;
    var existing = root.GetComponentsInChildren<CosmicDust>(true);

    for (int i = 0; i < existing.Length; i++)
    {
        var d = existing[i];
        if (d == null) continue;

        var go = d.gameObject;
        Vector2Int gp = drums.WorldToGridPosition(go.transform.position);
        if ((uint)gp.x >= (uint)w || (uint)gp.y >= (uint)h) continue;

        if (_cellGo[gp.x, gp.y] != null && _cellGo[gp.x, gp.y] != go)
        {
            FadeAndHideCellGO(go);
            continue;
        }

        _cellGo[gp.x, gp.y] = go;
        _cellDust[gp.x, gp.y] = d;
        _goToCell[go] = gp;

        go.transform.position = drums.GridToWorldPosition(gp);
        d.SetCellSizeDrivenScale(Mathf.Max(0.001f, drums.GetCellWorldSize()), dustFootprintMul, cellClearanceWorld);

        bool blocks =
            d.terrainCollider != null &&
            d.terrainCollider.enabled &&
            !d.terrainCollider.isTrigger &&
            go.activeInHierarchy;

        _cellState[gp.x, gp.y] = blocks ? DustCellState.Solid : DustCellState.Empty;
    }
}

    public bool TryGetCellGo(Vector2Int gp, out GameObject go)
    {
        EnsureCellGrid();
        go = null;
        if (_cellGo == null) return false;
        if ((uint)gp.x >= (uint)_cellW || (uint)gp.y >= (uint)_cellH) return false;
        go = _cellGo[gp.x, gp.y];
        return go != null;
    }
    private bool TryGetCellState(Vector2Int gp, out DustCellState st)
    {
        EnsureCellGrid();
        st = DustCellState.Empty;
        if (_cellState == null) return false;
        if ((uint)gp.x >= (uint)_cellW || (uint)gp.y >= (uint)_cellH) return false;
        st = _cellState[gp.x, gp.y];
        return true;
    }
    private void SetCellState(Vector2Int gp, DustCellState st)
    {
        EnsureCellGrid();
        if (_cellState == null) return;
        if ((uint)gp.x >= (uint)_cellW || (uint)gp.y >= (uint)_cellH) return;

        // Track role density when a cell enters or leaves Solid state.
        DustCellState prev = _cellState[gp.x, gp.y];
        _cellState[gp.x, gp.y] = st;

        if (prev != st)
        {
            if (st == DustCellState.Solid)
                TrackRoleDensityChange(gp, becomesSolid: true);
            else if (prev == DustCellState.Solid)
                TrackRoleDensityChange(gp, becomesSolid: false);
        }
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

        var existing = _cellGo[gp.x, gp.y];
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
            dust.PrepareForReuse();
            dust.SetGrowInDuration(hexGrowInSeconds);
            dust.clearing.hardness01 = GetCellHardness01(gp);
            dust.SetTint(GetCellVisualColor(gp));

            Color denyColorGo = Color.darkGray;
            if (_imprints != null && _imprints.TryGetValue(gp, out var impGo) && impGo.role != MusicalRole.None)
            {
                var rpGo = MusicalRoleProfileLibrary.GetProfile(impGo.role);
                if (rpGo != null)
                {
                    var shadow = rpGo.dustColors.shadowColor;
                    denyColorGo = (shadow != Color.clear && shadow != Color.magenta)
                        ? shadow
                        : Color.darkGray;
                }
            }
            dust.SetFeedbackColors(Color.white, denyColorGo);
            dust.Begin();
            SetDustCollision(dust, false);
        }

        _cellGo[gp.x, gp.y] = go;
        _cellDust[gp.x, gp.y] = dust;
        _goToCell[go] = gp;
        SetCellState(gp, DustCellState.Empty);

        return go;
    }
    public bool HasDustAt(Vector2Int gridPos)
    {
        return TryGetCellState(gridPos, out var st) && st == DustCellState.Solid;
    } 
    public void CreateJailCenterForCollectable(
        Vector2Int gpCenter,
        MazeArchetype phase,
        float holdSeconds,
        int ownerId,
        DustClearMode mode = DustClearMode.HideInstant,
        float fadeSeconds = 0.10f,
        float regrowDelaySeconds = -1f)
    {
        // 1) Clear ONLY the center cell (the "jail cell" the note occupies).
        ClearCell(gpCenter, mode, fadeSeconds, scheduleRegrow: true, phase: phase, regrowDelaySeconds: regrowDelaySeconds);

        // 2) Claim ONLY the center so it stays empty while jailed.
        if (dustClaims != null && holdSeconds > 0f)
        {
            string owner = $"Collectable#{ownerId}";
            dustClaims.ClaimCell(owner, gpCenter, DustClaimType.TemporaryCarve, seconds: holdSeconds, refresh: true);
        }
    }

    
    public float SampleDensity01(Vector3 worldPos)
    {
        if (drums == null) return 0f;

        Vector2Int cell = drums.WorldToGridPosition(worldPos);
        return HasDustAt(cell) ? 1f : 0f;
    }

    private void RequestRegrowCellAt(Vector2Int gridPos, MazeArchetype phase, float delaySeconds = -1f, bool refreshIfPending = false, bool clearImprintOnRefresh = false)
    {
        if (_regrowthSuppressed)
            return;
        if (!IsInBounds(gridPos)) { 

            if (_regrowthCoroutines != null && _regrowthCoroutines.TryGetValue(gridPos, out var pending))
            { 
                if (pending != null) StopCoroutine(pending);
                _regrowthCoroutines.Remove(gridPos);

            } 
            return;
        }
        
        bool shouldSchedule = !_permanentClearCells.Contains(gridPos);
        
        // If dust already exists here, no need to schedule.
        if (shouldSchedule && HasDustAt(gridPos))
            shouldSchedule = false;
        

        if (!shouldSchedule)
            return;

        // Compute delay (phase default unless explicitly overridden).
        float delay = delaySeconds >= 0f ? delaySeconds : phase switch
        {
            MazeArchetype.Establish  => 4f,
            MazeArchetype.Evolve     => 12f,
            MazeArchetype.Intensify  => 8f,
            MazeArchetype.Release    => 32f,
            MazeArchetype.Wildcard   => 16f,
            MazeArchetype.Pop        => 24f,
            _ => 32f
        };

        delay *= GetRegrowDelayMul(phase);
       
        EnsureRegrowController();
        _regrow?.RequestRegrowCellAt(gridPos, delay, refreshIfPending);
    }
    
    private bool IsInBounds(Vector2Int gp) { 
        if (drums == null) return false; 
        int w = drums.GetSpawnGridWidth(); 
        int h = drums.GetSpawnGridHeight(); 
        if (w <= 0 || h <= 0) return false; 
        return gp.x >= 0 && gp.y >= 0 && gp.x < w && gp.y < h;
    }
     public void ClaimTemporaryDiskForCollectable(
        Vector3 centerWorld,
        float radiusWorld,
        MazeArchetype phase,
        float holdSeconds,
        int ownerId,
        int priority = 50)
    {
        if (drums == null) return;

        // 1) Actually carve/hold the pocket (this despawns dust and schedules regrow).
        // Pass ownerId so the PeekHole claim uses the same owner string as the TemporaryCarve
        // claim below — both will be released together by ReleaseOwner("Collectable#{ownerId}").
        CarveTemporaryDiskFromCollectable(centerWorld, radiusWorld, phase, holdSeconds, ownerId);

        // 2) Also claim cells in DustClaimManager so regrow attempts self-delay while the pocket is maintained
        //    (DustPocketRoutine refreshes this periodically, so expiry behaves as a "keep alive".)
        if (dustClaims == null) return;

        float cellSize = Mathf.Max(0.001f, drums.GetCellWorldSize());
        int rCells = Mathf.CeilToInt(Mathf.Max(0.1f, radiusWorld) / cellSize);

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        Vector2Int c = drums.WorldToGridPosition(centerWorld);
        c.x = Mathf.Clamp(c.x, 0, w - 1);
        c.y = Mathf.Clamp(c.y, 0, h - 1);

        string owner = $"Collectable#{ownerId}";

        for (int gx = c.x - rCells; gx <= c.x + rCells; gx++)
        for (int gy = c.y - rCells; gy <= c.y + rCells; gy++)
        {
            if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;

            int dx = gx - c.x;
            int dy = gy - c.y;
            if ((dx * dx + dy * dy) > (rCells * rCells)) continue;

            var gp = new Vector2Int(gx, gy);

            // Claim the ACTUAL cell (gp), not the center (c).
            // Use an expiry so it regrows if the collectable stops refreshing.
            dustClaims.ClaimCell(owner, gp, DustClaimType.TemporaryCarve, seconds: holdSeconds, refresh: true);
        }
    }
     public void CarveTemporaryDiskFromCollectable(
        Vector3 centerWorld,
        float radiusWorld,
        MazeArchetype phase,
        float holdSeconds,
        int ownerId = 0)
    {
        if (drums == null) return;

        float cellSize = Mathf.Max(0.001f, drums.GetCellWorldSize());
        float rWorld   = Mathf.Max(0.1f, radiusWorld);
        int rCells     = Mathf.CeilToInt(rWorld / cellSize);

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        Vector2Int c = drums.WorldToGridPosition(centerWorld);

        // Visibility pocket: we only clear a small budget each tick to avoid spikes,
        // but we still want cleared cells to stay clear for a short time before regrowing.
        int despawnBudget = Mathf.Max(6, Mathf.RoundToInt(mineNodeErodePerTick * 0.35f));
        int despawned = 0;

        float hold = Mathf.Max(0.05f, holdSeconds);
        // Use the same per-instance owner string as ClaimTemporaryDiskForCollectable so that
        // ReleaseOwner("Collectable#{id}") clears BOTH the PeekHole and TemporaryCarve claims.
        string owner = ownerId != 0 ? $"Collectable#{ownerId}" : "CollectablePocket";

        for (int gx = c.x - rCells; gx <= c.x + rCells; gx++)
        {
            for (int gy = c.y - rCells; gy <= c.y + rCells; gy++)
            {
                if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;

                var gp = new Vector2Int(gx, gy);

                if (_permanentClearCells.Contains(gp)) continue;
                if (IsKeepClearCell(gp)) continue;

                // Only clear dust up to budget; holding/regrow is only meaningful for cells we actually cleared.
                if (despawned < despawnBudget && HasDustAt(gp))
                {
                    DespawnDustAt(gp);
                    despawned++;

                    // Authoritative: claims gate regrow.
                    if (dustClaims != null)
                        dustClaims.ClaimCell(owner, gp, DustClaimType.PeekHole, seconds: hold, refresh: true);

                    RequestRegrowCellAt(gp, phase, hold, refreshIfPending: true);
                }
            }
        }
    }
    public void SetStarKeepClear(Vector2Int centerCell, int radiusCells, MazeArchetype phase, bool forceRemoveExisting)
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
            RequestRegrowCellAt(cell, phase, delaySeconds: -1f, refreshIfPending: true, clearImprintOnRefresh: false);
        }

        // CLAIM -> keep empty; optionally clear any existing dust; DO NOT schedule regrow
        if (forceRemoveExisting)
        {
            for (int i = 0; i < _tmpClaimed.Count; i++)
            {
                var cell = _tmpClaimed[i];
                if (TryGetCellGo(cell, out var go) && go != null)
                    ClearCell(cell, DustClearMode.FadeAndHide, fadeSeconds: 2f, scheduleRegrow: false, phase: phase);
            }
        }
    }
    public void ClearStarKeepClear(MazeArchetype phase)
    {
        _exclusions.ClearStarPocket(_tmpReleased);
        for (int i = 0; i < _tmpReleased.Count; i++)
            RequestRegrowCellAt(_tmpReleased[i], phase, delaySeconds: -1f, refreshIfPending: true, clearImprintOnRefresh: false);
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
    public void RegrowPreviousCorridorOnNewNodeSpawn(MazeArchetype phase)
    {
        if (_pendingCorridorRegrowth.Count == 0) return;
        var path = _pendingCorridorRegrowth.Dequeue();
        BeginCorridorRegrowthReverse(path, phase);
    }
    private void BeginCorridorRegrowthReverse(List<Vector2Int> carvedPath, MazeArchetype phase)
    {
        if (carvedPath == null || carvedPath.Count == 0) return;
        
        if (drums == null) return;

        var cellsToGrow = new List<(Vector2Int, Vector3)>(carvedPath.Count);

        for (int i = carvedPath.Count - 1; i >= 0; i--)
        {
            var cell = carvedPath[i];
            if (_permanentClearCells.Contains(cell)) continue;
            if (IsKeepClearCell(cell)) continue;
            if (HasDustAt(cell)) continue;
            if (!drums.IsSpawnCellAvailable(cell.x, cell.y)) continue;

            cellsToGrow.Add((cell, drums.GridToWorldPosition(cell)));
        }

        if (cellsToGrow.Count == 0) return;

        // Reuse your existing “staggered growth” feel.
        BeginStaggeredMazeRegrowth(cellsToGrow);
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
        // Stable iteration order; tie-break randomly so all roles are equally likely.
        var roles = new[] { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove };
        // Shuffle order for tie-breaking
        for (int i = roles.Length - 1; i > 0; i--)
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
        MusicalRole best = MusicalRole.Bass;
        int bestCount = int.MaxValue;
    
        foreach (var kv in _solidCountByRole)
        {
            if (kv.Key == excluded) continue;
            if (kv.Value < bestCount)
            {
                bestCount = kv.Value;
                best = kv.Key;
            }
            else if (kv.Value == bestCount && Random.value > 0.5f)
            {
                best = kv.Key; // random tie-break
            }
        }
    
        return best;
    }
    private Color BlendImprintWithNeighbors(Vector2Int cell, Color target, int radius, float neighborWeight)
    {
        radius = Mathf.Max(0, radius);
        neighborWeight = Mathf.Clamp01(neighborWeight);
        if (radius == 0 || neighborWeight <= 0f)
            return target;

        int count = 0;
        float r = 0f, g = 0f, b = 0f, a = 0f;

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var c = GetCellVisualColor(new Vector2Int(cell.x + dx, cell.y + dy));
                r += c.r; g += c.g; b += c.b; a += c.a;
                count++;
            }
        }

        if (count <= 0)
            return target;

        Color avg = new Color(r / count, g / count, b / count, a / count);
        return Color.Lerp(target, avg, neighborWeight);
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
    public void ApplyProfile(PhaseStarBehaviorProfile profile)
    {
        if (profile == null) return;

        _activeProfile = profile;

        // Authoritative default: phase-authored maze tint.
        _mazeTint = profile.mazeColor;

        // If dust already exists (e.g., generator persists between phases), immediately
        // nudge visuals to match the new profile so we don't leave any tiles at prefab/default.
        RetintExisting(seconds: 0.20f);
    }
    public void BeginStaggeredMazeRegrowth(List<(Vector2Int, Vector3)> cellsToGrow)
    {
        if (_spawnRoutine != null)
            StopCoroutine(_spawnRoutine); 
        float fit = Mathf.Clamp(drums.GetLoopLengthInSeconds()*0.25f, 0.08f, drums.GetLoopLengthInSeconds()*0.5f);
        _spawnRoutine = StartCoroutine(StaggeredGrowthFitDuration(cellsToGrow, fit));
    }
    public void RetintExisting(float seconds = 0.35f) {
        if (!isActiveAndEnabled) return;
        // If this generator is active but its GameObject is not in hierarchy (parent disabled), also bail.
        if (!gameObject.activeInHierarchy) return;

        // Primary: iterate the authoritative grid.
        if (_cellGridReady && _cellGo != null)
        {
            for (int x = 0; x < _cellW; x++)
            {
                for (int y = 0; y < _cellH; y++)
                {
                    var go = _cellGo[x, y];
                    if (!go) continue;
                    if (!go.activeInHierarchy) continue;

                    var d = _cellDust != null ? _cellDust[x, y] : go.GetComponent<CosmicDust>();
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
            for (int x = 0; x < _cellW; x++)
            {
                for (int y = 0; y < _cellH; y++)
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
    }
    /// <summary>
    /// Removes dust topology immediately (opens corridor) but fades visuals and pools afterward.
    /// Boost carving should use this, NOT DespawnDustAt.
    /// </summary>
    public void CarveDustAt(Vector2Int gridPos, float fadeSeconds)
    {
        // IMPORTANT: "Permanent clear" was originally used to keep an authored tunnel open.
        // In the refactor, we still want *pockets* and dynamic clearing. If a cell is marked
        // permanent-clear but somehow still contains Solid dust (e.g., legacy init path),
        // we treat that as stale state: remove the flag and allow carving.
        if (_permanentClearCells.Contains(gridPos))
        {
            if (TryGetCellState(gridPos, out var st0) && st0 == DustCellState.Solid)
            {
                _permanentClearCells.Remove(gridPos);
                Debug.Log($"[DUST:CARVE] Stale permanent-clear on {gridPos}; removed flag and carving.");
            }
            else
            {
                Debug.Log($"[DUST:CARVE] Reject {gridPos}: permanent clear");
                return;
            }
        }
        if (!TryGetCellState(gridPos, out var st) || st != DustCellState.Solid) return;
        if (!TryGetCellGo(gridPos, out var go) || go == null) return;

        // Logical authority: the moment we carve, the cell stops being solid and must be removed
        // from legacy maps so queries cannot treat it as terrain.
        SetCellState(gridPos, DustCellState.Clearing);

        // Stop contributing collisions/topology immediately.
        if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null){ 
            SetDustCollision(dust, false); 
        }
        // Visual: allow dust to fade out; when the fade completes the tile will call
        // back into OnDustVisualFadedOut(), which finalizes the Empty state.
        if (go.TryGetComponent<CosmicDust>(out var d) && d != null)
        {
            // Optional: immediate "impact" read.
            //d.PulseCharge(1f, fadeSeconds: Mathf.Max(0.01f, fadeSeconds), stickyUntilDestroyed: true);
            d.DissipateAndHideVisualOnly(Mathf.Max(0.01f, dustTimings.fadeOutSeconds));
        }
        else
        {
            FadeAndHideCellGO(go);
            SetCellState(gridPos, DustCellState.Empty);
        }

        // Schedule regrow (held off by keep-clear, claims, vehicle veto, collectables)
        RequestRegrowCellAt(gridPos, GetCurrentPhaseSafe(), refreshIfPending: true);
    }
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

            MazeArchetype phaseNow = GetCurrentPhaseSafe();

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
                    if (IsDustSpawnBlocked(grid)) continue;          // includes permanent + keepclear + claims/holds
                    if (!Collectable.IsCellFreeStatic(grid)) continue; // never grow dust on top of a collectable
                    // ---------------------------
                    // SPAWN + REGISTER (VISUAL FIRST)
                    // ---------------------------
                    var hex = GetOrCreateCellGO(grid);

                    if (hex.TryGetComponent<CosmicDust>(out var dust))
                    {
                        dust.SetTrackBundle(this, drums);
                        dust.SetCellSizeDrivenScale(cellWorldSize, dustFootprintMul, cellClearanceWorld);

                        dust.PrepareForReuse();
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
                            dust.SetTint(cellColor);
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

// At this point, it is legitimately solid terrain.
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
        Debug.Log($"[DUSTGEN] RemoveActiveAt grid={grid} hexMapHasAfter={HasDustAt(grid)} go={(go ? go.name : "null")}", this);
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
            _cellGo[gridPos.x, gridPos.y] = hex;
            var d = hex != null ? hex.GetComponent<CosmicDust>() : null;
            _cellDust[gridPos.x, gridPos.y] = d;
            if (hex != null) _goToCell[hex] = gridPos;
            SetCellState(gridPos, DustCellState.Solid);
        }

        // No composite collider rebuild (per-cell terrain only).
    }
     private static Vector2Int Any(HashSet<Vector2Int> set) {
        foreach (var v in set) return v;             // returns the first enumerated element
        return new Vector2Int(-1, -1);
    } 
    private MazeArchetype GetCurrentPhaseSafe()
    {
        TryEnsureRefs();
        return (phaseTransitionManager != null) ? phaseTransitionManager.currentPhase : MazeArchetype.Establish;
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
        RequestRegrowCellAt(gridPos, GetCurrentPhaseSafe(), refreshIfPending: true);
    }
    private int CountFilledNeighbors(Vector2Int cell)
    {
        int count = 0;
        foreach (var dir in GetHexDirections(cell.y))
        {
            Vector2Int neighbor = cell + dir;
            if (_fillMap.TryGetValue(neighbor, out bool filled) && filled)
                count++;
        }
        return count;
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
    private MusicalRole GetRandomRoleExcluding(MusicalRole excludedRole)
    {
         MusicalRole[] roles =
        {
            MusicalRole.Bass,
            MusicalRole.Harmony,
            MusicalRole.Lead,
            MusicalRole.Groove
        };

        int validCount = 0;
        for (int i = 0; i < roles.Length; i++)
            if (roles[i] != excludedRole)
                validCount++;

        if (validCount <= 0)
            return MusicalRole.Bass;

        int pick = Random.Range(0, validCount);
        for (int i = 0; i < roles.Length; i++)
        {
            if (roles[i] == excludedRole) continue;
            if (pick == 0) return roles[i];
            pick--;
        }

        return MusicalRole.Bass;
    }
    public int CarveTemporaryCellFromMineNode(
        Vector3 centerWorld,
        MazeArchetype phase,
        float regrowDelaySeconds,
        MusicalRole removedRoleOverride,
        int resolveRadiusCells,
        float appetiteMul = 1f) {
        if (drums == null) return 0;

        EnsureImprints();

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        Vector2Int c = drums.WorldToGridPosition(centerWorld);

        // Footprint is cell-based, so 0 = 1 cell, 1 = 3x3, etc.
        int rCells = Mathf.Max(0, resolveRadiusCells);

        // Budget is "how many *present* dust cells we may remove this tick".
        // We still refresh regrow timers across the footprint.
        int removed = 0;
        int budget  = Mathf.RoundToInt(mineNodeErodePerTick * Mathf.Clamp(appetiteMul, 0.4f, 2f));
        
        for (int gx = c.x - rCells; gx <= c.x + rCells; gx++)
        {
            for (int gy = c.y - rCells; gy <= c.y + rCells; gy++)
            {
                if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;

                Vector2Int gp = new Vector2Int(gx, gy);

                // Never touch permanent-clear or keep-clear cells.
                if (_permanentClearCells.Contains(gp)) continue;
                if (IsKeepClearCell(gp)) continue;

                MusicalRole removedRole = removedRoleOverride;

                if (removedRole == MusicalRole.None &&
                    TryGetDustAt(gp, out var existingDust) &&
                    existingDust != null)
                {
                    removedRole = existingDust.Role;
                }

                if (removedRole != MusicalRole.None)
                    _regrowExcludeRoleByCell[gp] = removedRole;
                _imprints?.Remove(gp);
                // Clear if present, respecting budget.
                if (HasDustAt(gp))
                {
                    if (removed < budget)
                    {
                        ClearCell(gp,DustClearMode.FadeAndHide, fadeSeconds: 0.20f, scheduleRegrow: true, phase: phase,regrowDelaySeconds: regrowDelaySeconds);
                        removed++;
                    }
                }

                // Always refresh regrow timer (even if already empty).
                RequestRegrowCellAt(gp, phase, regrowDelaySeconds, refreshIfPending: true);
            }
        }

        return removed;
    }
    /// <summary>
    /// Removes the role imprint for a cell so its next regrowth is assigned
    /// the least-dense role rather than its original Voronoi role.
    /// Called by PhaseStarDustAffect when the star fully drains a cell.
    /// </summary>
    public void ClearImprintAt(Vector2Int cell)
    {
        _imprints?.Remove(cell);
    }
    public void CarveTemporaryCellFromVehicle(
        Vector3 worldPos,
        MazeArchetype phase,
        float healDelaySeconds,
        int resolveRadiusCells = 0)
    {
        if (drums == null) return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        Vector2Int cell;
        if (resolveRadiusCells > 0)
        {
            if (!TryResolveDustCellFromWorldPoint(worldPos, resolveRadiusCells, out cell))
                cell = drums.WorldToGridPosition(worldPos);
        }
        else
        {
            cell = drums.WorldToGridPosition(worldPos);
        }

        if (cell.x < 0 || cell.y < 0 || cell.x >= w || cell.y >= h)
            return;

        // Open corridor immediately
        if (HasDustAt(cell))
            ClearCell(cell, DustClearMode.FadeAndHide, fadeSeconds: 0.20f, scheduleRegrow: true, phase: phase, regrowDelaySeconds: healDelaySeconds);

        // Vehicle carving does NOT own the role — it's purely a physical interaction.
        // If a Voronoi or MineNode role imprint exists, preserve the role so the cell
        // remembers its musical identity when it regrows.
        // If there is no imprint (or no role), CommitRegrowCell will assign the
        // least-dense role automatically, keeping the maze balanced.
        if (_imprints != null && _imprints.TryGetValue(cell, out var existingImp)
            && existingImp.role != MusicalRole.None)
        {
            // Keep the imprint role intact; just refresh the heal delay.
            _imprints[cell] = new DustImprint
            {
                color      = existingImp.color,
                hardness01 = existingImp.hardness01,
                role       = existingImp.role,
                healDelay  = healDelaySeconds
            };
        }
        // else: no imprint → CommitRegrowCell will use least-dense role on regrowth.

        // Schedule normal dust regrow
        RequestRegrowCellAt(
            cell,
            phase,
            healDelaySeconds,
            refreshIfPending: true
        );
    }

    private float GetFillProbability(MazeArchetype phase)
    {
        return phase switch
        {
            MazeArchetype.Establish => 0.90f,
            MazeArchetype.Evolve => 0.30f,
            MazeArchetype.Intensify => 0.45f,
            MazeArchetype.Release => 0.15f,
            MazeArchetype.Wildcard => 0.40f + Random.Range(-0.1f, 0.1f),
            _ => 0.35f
        };
    }
    // Add to CosmicDustGenerator
    [ContextMenu("Debug: Find Phantom Colliders")]
    public void DebugFindPhantomColliders()
    {
        if (_cellState == null || _cellGo == null) return;
        int phantoms = 0;
        for (int x = 0; x < _cellW; x++)
        for (int y = 0; y < _cellH; y++)
        {
            var st = _cellState[x, y];
            var go = _cellGo[x, y];
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