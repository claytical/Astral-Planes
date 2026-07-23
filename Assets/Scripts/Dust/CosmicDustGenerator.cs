using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public enum DustInteractionMode
{
    Carve,
    Zap
}

public enum DustClearMode
{
    FadeAndHide,
    HideInstant
}

public readonly struct DustClearRequest
{
    public readonly DustInteractionMode InteractionMode;
    public readonly DustClearMode ClearMode;
    public readonly float FadeSeconds;
    public readonly bool ScheduleRegrow;
    public readonly DustClearSource Source;
    public readonly float RegrowDelaySeconds;
    public readonly bool RunPreExplode;
    public readonly Color? ExplosionTintOverride;
    public readonly Vector2 BurstDirection;          // zero = radial burst
    public readonly float PreExplodeScaleOutSeconds; // <= 0 = DustTimings.clearSpriteScaleOutSeconds

    public DustClearRequest(DustInteractionMode interactionMode, DustClearMode clearMode, float fadeSeconds, bool scheduleRegrow, DustClearSource source, float regrowDelaySeconds, bool runPreExplode, Color? explosionTintOverride = null, Vector2 burstDirection = default, float preExplodeScaleOutSeconds = -1f)
    {
        InteractionMode = interactionMode;
        ClearMode = clearMode;
        FadeSeconds = fadeSeconds;
        ScheduleRegrow = scheduleRegrow;
        Source = source;
        RegrowDelaySeconds = regrowDelaySeconds;
        RunPreExplode = runPreExplode;
        ExplosionTintOverride = explosionTintOverride;
        BurstDirection = burstDirection;
        PreExplodeScaleOutSeconds = preExplodeScaleOutSeconds;
    }
}

public partial class CosmicDustGenerator : MonoBehaviour
{
    private GameFlowManager _gfm;
    private CosmicDustRegrowthController _regrow;
    private bool _isBootstrappingMaze = false;
    public GameObject dustPrefab;
    public Transform activeDustRoot;
    private readonly DustImprintStore _imprints = new DustImprintStore();
    private bool _cellGridReady;
    [Header("Config")]
    [SerializeField] public CosmicDustGeneratorConfig config;
    public bool toroidal => config != null ? config.toroidal : false;
    public float cellClearanceWorld => config != null ? config.cellClearanceWorld : 0f;
    private bool _mazeAlreadyGenerated = false;
    private bool _regrowthSuppressed = false;

    private CosmicDustCellRegistry _registryBacking;
    private CosmicDustCellRegistry _registry => _registryBacking ??= new CosmicDustCellRegistry(
        _gridState,
        () => toroidal,
        () => drums != null ? drums.GetSpawnGridWidth() : 0,
        () => drums != null ? drums.GetSpawnGridHeight() : 0,
        cell => _imprints.TryGetValue(cell, out var imp) ? imp.hiddenRole : MusicalRole.None,
        (cell, becomesSolid) => _roleDensity.TrackRoleDensityChange(cell, becomesSolid));

    private DustResistanceResolver _resistanceBacking;
    private DustResistanceResolver _resistance => _resistanceBacking ??= new DustResistanceResolver(
        _imprints,
        cell => { TryGetDustAt(cell, out var d); return d; });

    private DustRoleDensityTracker _roleDensityBacking;
    private DustRoleDensityTracker _roleDensity => _roleDensityBacking ??= new DustRoleDensityTracker(
        _imprints,
        _gridState,
        gp => HasCellFlag(gp, CellFlags.ForceGrayRegrow),
        gp => TryGetCellState(gp, out var st) && st == DustCellState.Solid,
        cell => { TryGetDustAt(cell, out var d); return d; });

    [Header("Dust Visual Timings")]
    [SerializeField] private DustVisualTimingSettings dustVisualTimingSettings;
    private DustVisualTimings DustTimings => dustVisualTimingSettings != null
        ? dustVisualTimingSettings.Timings
        : DustVisualTimings.Default;
    
    // --- Extracted controllers (refactor targets) ---
    private CosmicDustExclusionMap _exclusions = new CosmicDustExclusionMap();
    private CosmicDustTintDiffusionSystem _tintDiffusionSystem;

    private CosmicDustVehicleReservationController _vehicleReservationBacking;
    private CosmicDustVehicleReservationController _vehicleReservation => _vehicleReservationBacking ??= new CosmicDustVehicleReservationController(
        _exclusions,
        () => drums != null ? drums.GetSpawnGridWidth() : 0,
        () => drums != null ? drums.GetSpawnGridHeight() : 0,
        () => dustClaims,
        cell => _permanentClearCells.Contains(cell),
        cell => RequestRegrowCellAt(cell, -1f, true, false),
        HasDustAt,
        (cell, fade) => CarveDustByVehicle(cell, fade),
        cell => { TryGetCellGo(cell, out var go); return go; },
        (cell, mode, fade, scheduleRegrow) => ClearCell(cell, mode, fade, scheduleRegrow));

    private CosmicDustColliderSuppressionController _colliderSuppressionBacking;
    private CosmicDustColliderSuppressionController _colliderSuppression => _colliderSuppressionBacking ??= new CosmicDustColliderSuppressionController(
        _gridState,
        IsInBounds,
        gp => TryGetCellState(gp, out var st) && st == DustCellState.Solid,
        () => drums != null,
        () => drums != null ? drums.GetCellWorldSize() : 0f,
        gp => drums != null ? drums.GridToWorldPosition(gp) : Vector3.zero,
        () => vehicleMask,
        () => config.regrowVetoBoxMul,
        () => config.regrowVetoMaxHits,
        SetDustCollision);

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

    private MazePatternConfig _activeMazePattern;
    private readonly DustRegrowthScheduler _regrowthScheduler = new();
    private readonly MazeTopologyService _mazeTopologyService = new();

    private HashSet<Vector2Int> _permanentClearCells = new HashSet<Vector2Int>();
    // Cells drained by a PhaseStar whose energy is "held" by a MineNode: no regrow
    // until ReleaseHeldCells (node death) or a maze flush supersedes them.
    private readonly HashSet<Vector2Int> _heldRegrowCells = new HashSet<Vector2Int>();
    private Dictionary<Vector2Int, float> _carveAccumulator = new();
    private Coroutine _spawnRoutine;
    private DrumTrack drums;
    private PhaseTransitionManager phaseTransitionManager;

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

    [SerializeField] private DustClaimManager dustClaims;
    private bool _runtimeVoidOnlyDustCreation;

    // Back-compat hooks (no-op now that composite rebuilds are removed).

    public void HardStopRegrowthForBridge(bool hideTransientDust = true)
    {
        _regrowthSuppressed = true;
        // The regenerated maze supersedes any node-held drain batches.
        _heldRegrowCells.Clear();

        // Stop maze-growth/stagger routines tied to the outgoing motif.
        if (_spawnRoutine != null)
        {
            StopCoroutine(_spawnRoutine);
            _spawnRoutine = null;
        }

        // Stop any pending per-cell regrowth-delay coroutines.
        _regrow?.CancelAllPending();

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

        // Stop any in-flight commit coroutines (past the delay, animating a cell solid).
        if (_regrowthScheduler.CommitRegrowCoroutines != null)
        {
            foreach (var kv in _regrowthScheduler.CommitRegrowCoroutines)
            {
                if (kv.Value != null)
                    StopCoroutine(kv.Value);
            }
            _regrowthScheduler.CommitRegrowCoroutines.Clear();
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

    private void SetDustCollision(CosmicDust dust, bool _enabled)
        {
            if (dust == null) return;
            dust.SetTerrainColliderEnabled(_enabled);
        }

    public void DisableCellCollider(Vector2Int cell) => _colliderSuppression.DisableCellCollider(cell);

    public void SuppressCellColliderForPlow(Vector2Int cell) => _colliderSuppression.SuppressCellColliderForPlow(cell);

    private bool IsKeepClearCell(Vector2Int cell) => dustClaims != null && dustClaims.IsBlocked(cell);

    private bool HasCellFlag(Vector2Int cell, CellFlags flag) => _registry.HasCellFlag(cell, flag);
    private void SetCellFlag(Vector2Int cell, CellFlags flag) => _registry.SetCellFlag(cell, flag);
    private bool ClearCellFlag(Vector2Int cell, CellFlags flag) => _registry.ClearCellFlag(cell, flag);

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

    public void SetVehicleKeepClear(int ownerId, Vector2Int centerCell, int radiusCells, bool forceRemoveExisting, float forceRemoveFadeSeconds = 0.20f) =>
        _vehicleReservation.SetVehicleKeepClear(ownerId, centerCell, radiusCells, forceRemoveExisting, forceRemoveFadeSeconds);

    public void ReleaseVehicleKeepClear(int ownerId) => _vehicleReservation.ReleaseVehicleKeepClear(ownerId);
    private void Start()
    {
        // In some scenes the generator may be instantiated before the GameFlowManager
        // is fully ready. We do a best-effort bind here, and also lazily re-bind elsewhere.
        _imprints.EnsureAllocated();
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
                return _colliderSuppression.IsVehicleOverlappingCellWorld(world, cellWorld);
            },

            tryGetPendingRegrow: gp => TryGetCellState(gp, out var st) && st == DustCellState.PendingRegrow,
            setCellEmpty: gp => SetCellState(gp, DustCellState.Empty),
            setCellPendingRegrow: gp => SetCellState(gp, DustCellState.PendingRegrow),
            startCommitRegrowCell: gp => _regrowthScheduler.CommitRegrowCoroutines[gp] = StartCoroutine(CommitRegrowCell(gp)),

            getRegrowVetoRetryDelaySeconds: () => Mathf.Max(0.05f, config.regrowVetoRetryDelaySeconds),
            getRegrowCellsPerStep: () => Mathf.Max(0, config.regrowCellsPerStep)
        );
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
        if (dustClaims == null) dustClaims = FindAnyObjectByType<DustClaimManager>();
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
        _colliderSuppression.Tick();
        if (_regrowthSuppressed)
            return;
        EnsureRegrowController();
        // Tint diffusion: keep visual seams soft around recent changes.
        ProcessTintDiffusion(Time.deltaTime);

        if (drums == null) return;


        // Regrow step gate: promote PendingRegrow -> Regrowing/Solid rhythmically on drum steps.
        _regrow?.ProcessStepGate(drums.currentStep);
    }

    private bool IsVehicleOverlappingCell(Vector2Int gp) => _colliderSuppression.IsVehicleOverlappingCell(gp);

    private IEnumerator CommitRegrowCell(Vector2Int gp)
    {
        if (_regrowthSuppressed)
        {
            if (TryGetCellGo(gp, out var existing) && existing != null)
                HideCellGO(existing);

            SetCellState(gp, DustCellState.Empty);
            _regrowthScheduler.CommitRegrowCoroutines.Remove(gp);
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
            _regrowthScheduler.CommitRegrowCoroutines.Remove(gp);
            yield break;
        }

        // Density gate: a frontier compensation cell already restored coverage for this
        // erosion event. Suppress the original cell so dust "shifts" rather than doubling.

        var go = GetOrCreateCellGO(gp);
        if (go == null) { _regrowthScheduler.CommitRegrowCoroutines.Remove(gp); yield break; }
        if (!go.activeSelf)
            go.SetActive(true);
        // Visual comes back first.
        SetCellState(gp, DustCellState.Regrowing);
        CosmicDust dust = null;
        MusicalRole regrowRole = MusicalRole.None;
        ClearCellFlag(gp, CellFlags.PlayerCarved);
        if (go.TryGetComponent<CosmicDust>(out dust) && dust != null)
        {
            dust.PrepareForReuse();
            dust.InitializeVisuals(DustTimings);
            dust.SetGrowInDuration(DustTimings.regrowParticleGrowInSeconds);

            regrowRole = ClearCellFlag(gp, CellFlags.ZapForceGray) ? MusicalRole.None : _roleDensity.ResolveRegrowRole(gp);
            ClearCellFlag(gp, CellFlags.ForceGrayRegrow);

            // --- Color from role profile (authoritative source) ---
            var roleProfile = MusicalRoleProfileLibrary.GetProfile(regrowRole);
            Color regrowTint = (roleProfile != null) ? roleProfile.GetRandomVoiceColor() : config.mazeTint;

            // Write / update the imprint so future regrows of this cell remember the role.
            // Preserve hiddenRole (permanent Voronoi ground-truth) across the rewrite.
            _imprints.EnsureAllocated();
            _imprints.TryGetValue(gp, out var existingImp);
            _imprints[gp] = new DustImprint
            {
                color               = regrowTint,
                carveResistance01   = roleProfile != null ? roleProfile.carveResistance01 : 0f,
                drainResistance01   = roleProfile != null ? roleProfile.drainResistance01 : 0f,
                maxEnergyUnits      = roleProfile != null ? roleProfile.maxEnergyUnits : 1,
                role                = regrowRole,
                healDelay           = 0f,
                hiddenRole          = existingImp.hiddenRole,
            };

            var resistance = ResolveResistanceProfile(gp, regrowRole, context: "CommitRegrowCell");
            dust.clearing.drainResistance01 = resistance.drainResistance01;
            int maxUnits = roleProfile != null ? roleProfile.maxEnergyUnits : 1;
            dust.ApplyRoleAndCharge(regrowRole, regrowTint, regrowTint.a, maxUnits);

            if (regrowRole == MusicalRole.None)
                _imprints.ApplyHiddenHintToDust(gp, dust);

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
            _regrowthScheduler.CommitRegrowCoroutines.Remove(gp);
            yield break;
        }

        if (IsKeepClearCell(gp) || IsDustSpawnBlocked(gp) || IsVehicleOverlappingCell(gp))
        {
            SetCellState(gp, DustCellState.PendingRegrow);
            FadeAndHideCellGO(go);              // stays active, but invisible + non-colliding
            EnqueueStepRegrow(gp);
            _regrowthScheduler.CommitRegrowCoroutines.Remove(gp);
            yield break;
        }

        SetCellState(gp, DustCellState.Solid);
        if (dust != null)
        {
            dust.regrowAlphaCapped = false;
            dust.EnsureMinSolidAlpha(0.55f);
            SetDustCollision(dust, true);
        }
        _roleDensity.RemoveExcludedRole(gp);
        _regrowthScheduler.CommitRegrowCoroutines.Remove(gp);
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
    _permanentClearCells ??= new HashSet<Vector2Int>();
    _imprints.EnsureAllocated(256);

    _exclusions ??= new CosmicDustExclusionMap();

    TryEnsureRefs();

    if (!drums)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        drums = _gfm != null ? _gfm.ResolveDrumTrack() : FindAnyObjectByType<DrumTrack>();
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
    _registry.GoToCell.Clear();

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
        _registry.GoToCell[go] = gp;

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
        return _registry.TryGetCellGo(gp, out go);
    }

    public bool TryGetCellState(Vector2Int gp, out DustCellState st)
    {
        EnsureCellGrid();
        return _registry.TryGetCellState(gp, out st);
    }
    private void SetCellState(Vector2Int gp, DustCellState st)
    {
        EnsureCellGrid();
        _registry.SetCellState(gp, st);
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
    public void GetColoredDustCells(List<Vector2Int> results) => _registry.GetColoredDustCells(results);
    private bool IsInBounds(Vector2Int gp) => _registry.IsInBounds(gp);

    /// <summary>
    /// Wraps a grid coordinate toroidally when <see cref="toroidal"/> is enabled.
    /// Returns the input unchanged in non-toroidal mode.
    /// </summary>
    public Vector2Int WrapCell(Vector2Int gp) => _registry.WrapCell(gp);

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

    private DustResistanceProfile ResolveResistanceProfile(Vector2Int cell, MusicalRole fallbackRole, string context) =>
        _resistance.Resolve(cell, fallbackRole, context);

    public float GetLiveCarveResistance01(Vector2Int cell) => _resistance.GetLiveCarveResistance01(cell);

    public bool TryGetDustAt(Vector2Int cell, out CosmicDust dust) {
        dust = null;
        if (!TryGetCellGo(cell, out var go) || go == null) return false;
        return go.TryGetComponent(out dust) && dust != null;
    }

    public Color MazeColor()
    {
        return config.mazeTint;
    }
    public void ApplyActiveRoles(IReadOnlyList<MusicalRole> roles) => _roleDensity.SetActiveRoles(roles);

    public MusicalRole GetZoneRole(Vector2Int cell) => _registry.GetZoneRole(cell);

    public void ApplyProfile(PhaseStarBehaviorProfile profile)
    {
        if (profile == null) return;

        _resistance.ClearLoggedContexts();

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
    /// <summary>
    /// Removes dust topology immediately (opens corridor) but fades visuals and pools afterward.
    /// Boost carving should use this, NOT DespawnDustAt.
    /// </summary>
    public void SetReservedVehicleCells(IReadOnlyList<Vector2Int> cells) => _vehicleReservation.SetReservedVehicleCells(cells);
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

    private void MarkTintDirty(Vector2Int center, int radius)
    {
        if (_tintDiffusionSystem == null) return;
        _tintDiffusionSystem.MarkDirty(center, radius);
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
            carveResistance01  = profile.carveResistance01,
            drainResistance01  = profile.drainResistance01,
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

}
