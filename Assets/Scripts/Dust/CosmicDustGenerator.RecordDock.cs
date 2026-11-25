using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  Record dock + sealed vault (record recovery): where the record sits for the motif.
//
//          ##########
//        ##############          # = vault shell: Solid dust in Cosmic Block form, bright,
//       ####        ####             `thickness` cells deep
//       ###   (rec)  ###
//       ####        ####         thickness = ceil(nodesPerStar / 4), clamped 1..4
//        ##############          (4 → 1, 8 → 2, 12 → 3, 16 → 4): a long motif is buried deeper.
//          ##########
//
//  The vault sits at a random spot along the edge of the field: the shell's outer ring touches the
//  grid boundary, so it reads as fused to the edge. There is no way in: the record is sealed until
//  the motif completes.
//
//  The shell starts in the inert-block colour (CosmicDustGeneratorConfig.inertBlockTint: dust with
//  no musical role that won't budge); each recorded ring re-tints it toward that ring's role colour
//  (muted by recordVaultRoleTint01, so the record's own lines stay the brightest thing in the
//  chamber), rippling outward (RetintRecordVault). When
//  the motif completes the shell goes up in a chain reaction (ExplodeRecordVault): the block where
//  the record last struck ignites, and each detonation sets off its neighbours a beat later, so the
//  blast crackles around the ring and out through its thickness. Then the record can be reached.
//
//  Trapped ship (CheckShipsInsideVault, every repair tick): if a ship ever ends up inside the sealed
//  vault, a ship-wide hatch opens in the shell on its side and closes again once every ship is out,
//  so a motif can never soft-lock with a player shut in. "Inside" means the chamber or buried in the
//  shell — never a ship scraping the outer layer. Each time, the shell cells around the ship are
//  logged ([VAULT] SHIP INSIDE) to show how it got in.
//
//  Standing vault cells refuse every clear path (ClearCell, DespawnDustAt, zaps, role sweeps) and
//  RepairVaultLoop regrows any that come up empty. They are in block form, so they are never star
//  food. Vault cells (chamber, shell) never take spawns: IsRecordVaultCell is checked by
//  DrumTrackGridMapper.IsSpawnCellAvailable (stars) and the note spawner. Everything is forgotten
//  with the maze (ClearMaze → ResetRecordVault).
// =========================================================================
public partial class CosmicDustGenerator
{
    private readonly HashSet<Vector2Int> _vaultInterior   = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> _vaultWalls      = new HashSet<Vector2Int>(); // the shell
    private readonly HashSet<Vector2Int> _vaultGuarded    = new HashSet<Vector2Int>(); // standing shell cells
    private readonly List<Vector2Int>    _vaultInnerShell = new List<Vector2Int>();    // shell cells touching the chamber
    private readonly List<Vector2Int>    _vaultScratch    = new List<Vector2Int>();
    private readonly List<Vector2Int>    _vaultRepairScratch = new List<Vector2Int>(); // RepairVaultCells (0.25 s tick)
    private readonly List<Vector2Int>    _vaultApproach   = new List<Vector2Int>();    // just outside the shell, field-facing first
    private Color     _vaultTint;
    private Coroutine _vaultRepairRoutine;
    private Coroutine _vaultRetintRoutine;
    private Coroutine _vaultExplodeRoutine;
    private readonly HashSet<Vector2Int> _vaultHatch = new HashSet<Vector2Int>(); // shell cells opened to let a ship out
    private float _vaultHatchCloseAt;
    private const float kVaultHatchCloseDelay = 1.5f; // seconds every ship must be out before the hatch reseals
    private VaultLayout _plannedVault; // chosen before the maze is laid out, built before it grows

    [Tooltip("Particle size multiplier of the record vault's shell (Cosmic Block form).")]
    [SerializeField] private float recordVaultParticleSizeMul = 3f;
    [Tooltip("How far a recorded ring pulls the vault's shell from the inert-block colour toward its role colour " +
             "(1 = full role colour, which competes with the record's own lines).")]
    [SerializeField, Range(0f, 1f)] private float recordVaultRoleTint01 = 0.45f;
    [Tooltip("How far the shell's flashes (retint ripple, the record's bumps) lift the shell colour toward white.")]
    [SerializeField, Range(0f, 1f)] private float recordVaultFlashLift01 = 0.35f;

    /// <summary>The flash colour for the vault's shell right now (its tint, lifted toward white).</summary>
    public Color RecordVaultFlashColor => Color.Lerp(_vaultTint, Color.white, recordVaultFlashLift01);

    /// <summary>True once this maze's vault has been built (its centre is <see cref="RecordVaultCenter"/>).</summary>
    public bool HasRecordVault { get; private set; }

    /// <summary>The record's cell inside the vault (valid while <see cref="HasRecordVault"/>).</summary>
    public Vector2Int RecordVaultCenter { get; private set; }

    /// <summary>Shell cells touching the record's chamber (the record's drift bumps flash these).</summary>
    public IReadOnlyList<Vector2Int> RecordVaultInnerShell => _vaultInnerShell;

    private const float kVaultRepairInterval   = 0.25f;
    private const float kVaultLayerStagger     = 0.12f; // retint / explosion ripple, per shell layer
    private const float kVaultExplodeSeconds   = 0.45f;
    private const float kVaultRetintFlash      = 0.35f;
    private const float kVaultChainFuseMin     = 0.05f; // explosion chain: delay before a block sets off a neighbour
    private const float kVaultChainFuseMax     = 0.09f;
    private const float kVaultChainFuseFlash   = 0.08f; // each block flashes this long before it pops (its fuse)
    private const float kVaultChainMaxSeconds  = 1.8f;  // a big shell's chain is compressed to fit this

    /// <summary>True for the record's chamber and its shell (no spawns there).</summary>
    public bool IsRecordVaultCell(Vector2Int cell)
    {
        if (_vaultInterior.Contains(cell) || _vaultWalls.Contains(cell)) return true;
        return _plannedVault != null && _plannedVault.All.Contains(cell);
    }

    /// <summary>True while any shell cell is standing.</summary>
    public bool IsRecordVaultSealed => _vaultGuarded.Count > 0;

    // A standing vault cell: every clear path refuses it, like a Cosmic Block.
    private bool IsSealedVaultShell(Vector2Int cell) => _vaultGuarded.Contains(cell);

    /// <summary>Forgets the vault (new maze). Called from ClearMaze before the grid is emptied.</summary>
    private void ResetRecordVault()
    {
        StopVaultRepair();
        StopVaultExplode();
        if (_vaultRetintRoutine != null) { StopCoroutine(_vaultRetintRoutine); _vaultRetintRoutine = null; }
        _plannedVault = null;
        HasRecordVault = false;
        _vaultInterior.Clear();
        _vaultWalls.Clear();
        _vaultGuarded.Clear();
        _vaultInnerShell.Clear();
        _vaultApproach.Clear();
        _vaultHatch.Clear();
        _vaultHoleTicks.Clear();
    }

    /// <summary>
    /// Reach target for the role-wall watchdog (GatherReachSeedsAndTargets): while the vault stands,
    /// a cell just outside its shell must stay reachable, so a Cosmic Block wall can never cut the
    /// record off once its shell explodes (CarveDoorways leaves a doorway; EnsureTargetsReachable opens one).
    /// </summary>
    private void AddRecordVaultReachTargets(List<Vector2Int> targets)
    {
        if (!HasRecordVault || !IsRecordVaultSealed || _vaultApproach.Count == 0) return;
        targets.Add(_vaultApproach[0]);
    }

    private void StopVaultRepair()
    {
        if (_vaultRepairRoutine != null) { StopCoroutine(_vaultRepairRoutine); _vaultRepairRoutine = null; }
    }

    private void StopVaultExplode()
    {
        if (_vaultExplodeRoutine != null) { StopCoroutine(_vaultExplodeRoutine); _vaultExplodeRoutine = null; }
    }

    // ── Placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// Chooses the record's dock cell and permanently clears a chamber of <paramref name="radiusCells"/>
    /// around it. With <paramref name="thickness"/> &gt; 0 it also grows a sealed shell that many cells
    /// deep around the chamber. Returns false if no cell qualifies. Post-maze fallback: the vault takes
    /// the maze's shape but is always edge-placed (the pattern's layout is no longer to hand).
    /// </summary>
    public bool TryClearRecordDock(int radiusCells, int minShipDistanceCells, out Vector2Int dockCell,
                                   int thickness = 0)
    {
        dockCell = default;
        if (drums == null) return false;
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return false;
        radiusCells = Mathf.Max(1, radiusCells);
        thickness   = Mathf.Max(0, thickness);
        _vaultTint  = config.inertBlockTint;

        var shipCells = new List<Vector2Int>();
        var vehicles = GameFlowManager.Instance != null ? GameFlowManager.Instance.GetVehicles() : null;
        if (vehicles != null)
            foreach (var v in vehicles)
                if (v != null && v.isActiveAndEnabled)
                    shipCells.Add(drums.WorldToGridPosition(v.transform.position));

        // One ship-wide reach search; unbreakable dust is the only wall.
        bool haveReach = GatherReachSeedsAndTargets();
        if (haveReach)
            _reach.Run(_gridState.Width, _gridState.Height, ComputeShipClearanceCells(), _reachSeeds, HardWallForShips);
        _reachCacheFrame = -1; // this run isn't the per-frame spawn cache

        // A plain clearing (thickness 0, the last-resort dock) stays round.
        var template = thickness > 0 ? BuildVaultTemplate(radiusCells, thickness, out _)
                                     : BuildVaultTemplateFor(VaultShape.Ring, radiusCells, 0);
        var layout = new VaultLayout();
        var req = new VaultPickRequest
        {
            Template = template, MinShipDist = minShipDistanceCells, ShipCells = shipCells,
            HaveReach = haveReach, Mode = VaultPlacement.Edge,
        };
        if (!TryPickVaultWithFallbacks(req, layout))
            return false;

        ResetRecordVault();
        dockCell = layout.Center;

        foreach (var c in layout.Interior) DespawnDustAtAndMarkPermanent(c);
        _vaultInterior.UnionWith(layout.Interior);
        if (layout.Walls.Count > 0) BuildVault(layout);
        HasRecordVault    = true;
        RecordVaultCenter = layout.Center;

        Debug.Log($"[VAULT] Built (post-maze) {template.Shape} at {layout.Center} r={radiusCells} thickness={thickness} " +
                  $"walls={_vaultWalls.Count}", this);
        return true;
    }

    // ── Built with the maze ──────────────────────────────────────────────────
    // GenerateMazeForPhaseWithPaths asks for the vault while laying the maze out:
    //  • Edge / Interior placement: before the pattern runs, so the pattern grows around the
    //    reserved cells (IsSpawnCellAvailable refuses them).
    //  • PatternAnchor placement: after the pattern has computed its walls but before anything
    //    grows, so the spot can be scored against the pattern itself (a room in Tunnels, a clearing
    //    in ClearBoxes, an outer ring wall in Rings, the gap between lanes, the rock in CA).
    // Either way BuildPlannedRecordVault grows the shell and docks the record
    // (GameFlowManager.OnRecordVaultBuilt) just before the maze's own growth starts.

    /// <summary>The vault placement this maze asks for (Edge when there is no pattern).</summary>
    private VaultPlacement ResolveVaultPlacement()
    {
        var p = _activeMazePattern;
        return p != null && p.recordVault != null ? p.recordVault.ResolvePlacement(p.patternType) : VaultPlacement.Edge;
    }

    /// <param name="patternSolid">The pattern's wall cells, for PatternAnchor scoring (null otherwise).</param>
    private bool PlanRecordVaultForMaze(Vector2Int starCell, IReadOnlyList<Vector2Int> vehicleCells,
                                        HashSet<Vector2Int> reserved, int starHoleRadius, int vehicleHoleRadius,
                                        HashSet<Vector2Int> patternSolid = null)
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || drums == null) return false;
        if (!gfm.TryGetRecordVaultSpec(out int r, out int minShipDist, out int thickness)) return false;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return false;
        r = Mathf.Max(1, r);
        _vaultTint = config.inertBlockTint;

        // Keep off the star's and the ships' clearings (carved after the maze grows) and the
        // cells the maze has already reserved.
        var avoid = new HashSet<Vector2Int>(reserved);
        DustGridMath.FillDisk(avoid, starCell, starHoleRadius + 1, w, h);
        var shipCells = new List<Vector2Int>();
        if (vehicleCells != null)
            foreach (var v in vehicleCells)
            {
                shipCells.Add(v);
                DustGridMath.FillDisk(avoid, v, vehicleHoleRadius + 1, w, h);
            }

        var template = BuildVaultTemplate(r, thickness, out var prefs);
        var mode = patternSolid != null ? VaultPlacement.PatternAnchor : ResolveVaultPlacement();
        var layout = new VaultLayout();
        // No reach search: nothing is built yet, and ordinary maze dust is carveable.
        var req = new VaultPickRequest
        {
            Template = template, MinShipDist = minShipDist, ShipCells = shipCells, HaveReach = false,
            Mode = mode, Avoid = avoid, PatternSolid = patternSolid, Prefs = prefs, StarCell = starCell,
        };
        if (!TryPickVaultWithFallbacks(req, layout))
        {
            Debug.LogWarning($"[RECORD] No room for the {template.Shape} vault (r={r}, thickness={thickness}, {mode}) " +
                             "in this maze; the record will dock after it grows.", this);
            return false;
        }

        _plannedVault = layout;
        reserved.UnionWith(layout.All);
        Debug.Log($"[VAULT] Planned {template.Shape} vault ({mode}{(req.UsedFallback ? ", fallback" : "")}) at {layout.Center} " +
                  $"thickness={thickness}{(req.Mode == VaultPlacement.PatternAnchor ? $" score={req.BestScore:F2}" : "")}", this);
        return true;
    }

    private void BuildPlannedRecordVault()
    {
        var layout = _plannedVault;
        if (layout == null) return;
        _plannedVault = null;

        foreach (var c in layout.Interior) DespawnDustAtAndMarkPermanent(c);
        _vaultInterior.UnionWith(layout.Interior);
        if (layout.Walls.Count > 0) BuildVault(layout);
        HasRecordVault    = true;
        RecordVaultCenter = layout.Center;

        Debug.Log($"[VAULT] Built with the maze at {layout.Center} thickness={layout.Thickness} " +
                  $"walls={_vaultWalls.Count}", this);
        GameFlowManager.Instance?.OnRecordVaultBuilt(layout.Center);
    }

    private sealed class VaultLayout
    {
        public Vector2Int Center;
        public int        Thickness;
        public readonly HashSet<Vector2Int> Interior = new HashSet<Vector2Int>();
        public readonly HashSet<Vector2Int> Walls    = new HashSet<Vector2Int>();
        public readonly List<Vector2Int>    Approach = new List<Vector2Int>(); // field-facing first
        public readonly HashSet<Vector2Int> All      = new HashSet<Vector2Int>();

        public void Clear()
        {
            Interior.Clear(); Walls.Clear(); Approach.Clear(); All.Clear();
        }
    }

    // ── Shape templates ──────────────────────────────────────────────────────
    // A vault's shape doesn't depend on where it goes, so it is built once as offsets from the
    // record's cell and then slid across the grid. The chamber is the shape; the shell is every
    // cell within `thickness` (Euclidean) of the chamber, plus every 8-neighbour of the chamber, so
    // no chamber cell ever touches the outside diagonally (a 1-cell round shell used to meet itself
    // only at corners there). The record itself always drifts in a round chamber of radius r
    // (GameFlowManager.DockRadiusWorld), which every shape contains.

    private sealed class VaultTemplate
    {
        public VaultShape Shape;
        public int Thickness;
        public readonly List<Vector2Int> Interior = new List<Vector2Int>();
        public readonly List<Vector2Int> Walls    = new List<Vector2Int>();
        public readonly List<Vector2Int> Approach = new List<Vector2Int>();
        public int MinX, MaxX, MinY, MaxY; // bounding box of Interior ∪ Walls
    }

    /// <summary>PatternAnchor scoring weights (each 0..1, normalised by their sum).</summary>
    private struct VaultAnchorPrefs
    {
        public float InteriorOpen;  // the chamber sits where the pattern left open space (a room)
        public float InteriorSolid; // the chamber is carved out of the pattern's rock
        public float WallsSolid;    // the shell lands on the pattern's own walls
        public float FarFromStar;   // out at the rim (outer ring of a Rings maze)
    }

    private static VaultAnchorPrefs AnchorPrefsFor(MazePatternType t) => t switch
    {
        MazePatternType.Tunnels          => new VaultAnchorPrefs { InteriorOpen = 1f, WallsSolid = 1f },
        MazePatternType.ClearBoxes       => new VaultAnchorPrefs { InteriorOpen = 1f, WallsSolid = 1f },
        MazePatternType.RingChokepoints  => new VaultAnchorPrefs { WallsSolid = 1f, FarFromStar = 0.5f },
        MazePatternType.DiagonalLanes    => new VaultAnchorPrefs { InteriorOpen = 1f, WallsSolid = 0.25f },
        MazePatternType.CellularAutomata => new VaultAnchorPrefs { InteriorSolid = 1f, WallsSolid = 1f },
        _                                => new VaultAnchorPrefs { InteriorOpen = 0.5f, WallsSolid = 0.5f },
    };

    // The template for this maze's vault shape.
    private VaultTemplate BuildVaultTemplate(int r, int thickness, out VaultAnchorPrefs prefs)
    {
        var p = _activeMazePattern;
        var type = p != null ? p.patternType : MazePatternType.FullFill;
        prefs = AnchorPrefsFor(type);
        var shape = p != null && p.recordVault != null ? p.recordVault.ResolveShape(type) : VaultShape.Ring;
        return BuildVaultTemplateFor(shape, r, thickness);
    }

    private VaultTemplate BuildVaultTemplateFor(VaultShape shape, int r, int thickness)
    {
        var p  = _activeMazePattern;
        var vp = p != null ? p.recordVault : null;
        var t  = new VaultTemplate { Shape = shape == VaultShape.Auto ? VaultShape.Ring : shape, Thickness = thickness };
        r = Mathf.Max(1, r);

        // Chamber test per shape, as offsets (dx, dy) from the record's cell; `ext` bounds the search.
        System.Func<int, int, bool> inChamber;
        int ext;
        switch (t.Shape)
        {
            case VaultShape.Box:
            {
                // Same outline as the maze's clearings, same aspect (never narrower than the record).
                var cb = p != null ? p.clearBoxes : null;
                float aspect = cb != null ? cb.clearBoxWidth / (float)Mathf.Max(1, cb.clearBoxHeight) : 1f;
                int rx = Mathf.RoundToInt(r * Mathf.Clamp(aspect, 1f, 1.6f));
                int ry = Mathf.RoundToInt(r * Mathf.Clamp(1f / Mathf.Max(0.01f, aspect), 1f, 1.6f));
                var outline = cb != null ? cb.shape : ClearBoxShape.Rectangle;
                int arm = Mathf.CeilToInt(r * 0.5f);
                inChamber = (dx, dy) =>
                {
                    float u = Mathf.Abs(dx) / (rx + 0.5f), v = Mathf.Abs(dy) / (ry + 0.5f);
                    bool core = u * u + v * v <= 1f; // the record's round room is always inside
                    return core || outline switch
                    {
                        ClearBoxShape.Ellipse => false,
                        ClearBoxShape.Diamond => u + v <= 1.15f,
                        // A square room with stubby arms (the pattern's thin cross is too small for the record).
                        ClearBoxShape.Plus    => (Mathf.Abs(dx) <= rx && Mathf.Abs(dy) <= ry)
                                                 || (Mathf.Abs(dx) <= arm && Mathf.Abs(dy) <= ry + arm)
                                                 || (Mathf.Abs(dy) <= arm && Mathf.Abs(dx) <= rx + arm),
                        _                     => Mathf.Abs(dx) <= rx && Mathf.Abs(dy) <= ry,
                    };
                };
                ext = Mathf.Max(rx, ry) + arm + 1;
                break;
            }
            case VaultShape.RoomCell:
                inChamber = (dx, dy) => Mathf.Abs(dx) <= r && Mathf.Abs(dy) <= r;
                ext = r + 1;
                break;
            case VaultShape.Capsule:
            {
                // Long axis along the lanes: lanes are lines of constant slopeX*x + slopeY*y.
                var dl = p != null ? p.diagonalLanes : null;
                Vector2 axis = dl != null ? new Vector2(dl.slopeY, -dl.slopeX) : Vector2.right;
                if (axis.sqrMagnitude < 0.0001f) axis = Vector2.right;
                axis.Normalize();
                float half = r * Mathf.Max(0f, (vp != null ? vp.capsuleLength : 1.8f) - 1f);
                inChamber = (dx, dy) =>
                {
                    var q = new Vector2(dx, dy);
                    float along = Mathf.Clamp(Vector2.Dot(q, axis), -half, half);
                    return (q - axis * along).sqrMagnitude <= (r + 0.25f) * (r + 0.25f);
                };
                ext = Mathf.CeilToInt(r + half) + 1;
                break;
            }
            case VaultShape.Geode:
            {
                // Radius wobbles with angle (a few random harmonics) but never dips under r.
                float rough = vp != null ? vp.geodeRoughness01 : 0.45f;
                var amp = new float[3]; var phase = new float[3];
                for (int i = 0; i < 3; i++) { amp[i] = UnityEngine.Random.Range(0.3f, 1f); phase[i] = UnityEngine.Random.Range(0f, Mathf.PI * 2f); }
                float ampSum = amp[0] + amp[1] + amp[2];
                inChamber = (dx, dy) =>
                {
                    float a = Mathf.Atan2(dy, dx), n = 0f;
                    for (int i = 0; i < 3; i++) n += amp[i] * Mathf.Sin((i + 2) * a + phase[i]);
                    float rr = r * (1f + rough * 0.5f * (1f + n / ampSum)); // r .. r*(1+rough)
                    return dx * dx + dy * dy <= rr * rr + 0.25f;
                };
                ext = Mathf.CeilToInt(r * (1f + rough)) + 1;
                break;
            }
            default: // Ring
                inChamber = (dx, dy) => dx * dx + dy * dy <= r * r;
                ext = r;
                break;
        }

        var interior = new HashSet<Vector2Int>();
        for (int dy = -ext; dy <= ext; dy++)
        for (int dx = -ext; dx <= ext; dx++)
            if (inChamber(dx, dy)) interior.Add(new Vector2Int(dx, dy));
        t.Interior.AddRange(interior);

        var walls = new HashSet<Vector2Int>();
        if (thickness > 0)
        {
            int outer = ext + thickness + 1;
            float tSq = thickness * thickness + 0.01f;
            for (int dy = -outer; dy <= outer; dy++)
            for (int dx = -outer; dx <= outer; dx++)
            {
                var c = new Vector2Int(dx, dy);
                if (interior.Contains(c)) continue;
                foreach (var i in interior)
                    if ((c - i).sqrMagnitude <= tSq) { walls.Add(c); break; }
            }
            // Close diagonal joints: no chamber cell may touch the outside, even at a corner.
            foreach (var i in interior)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var n = new Vector2Int(i.x + dx, i.y + dy);
                    if (!interior.Contains(n)) walls.Add(n);
                }
            t.Walls.AddRange(walls);

            // Just outside the shell (8-neighbours of the shell).
            var approach = new HashSet<Vector2Int>();
            foreach (var c in walls)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var n = new Vector2Int(c.x + dx, c.y + dy);
                    if (!walls.Contains(n) && !interior.Contains(n)) approach.Add(n);
                }
            t.Approach.AddRange(approach);
        }

        t.MinX = t.MinY = int.MaxValue; t.MaxX = t.MaxY = int.MinValue;
        foreach (var c in t.Interior) Grow(t, c);
        foreach (var c in t.Walls) Grow(t, c);
        return t;

        static void Grow(VaultTemplate tt, Vector2Int c)
        {
            if (c.x < tt.MinX) tt.MinX = c.x; if (c.x > tt.MaxX) tt.MaxX = c.x;
            if (c.y < tt.MinY) tt.MinY = c.y; if (c.y > tt.MaxY) tt.MaxY = c.y;
        }
    }

    // Lays the template out at `center` (approach clipped to the grid). False if any chamber or
    // shell cell falls outside the visible field.
    private static bool PlaceVaultTemplate(VaultTemplate t, Vector2Int center, RectInt visible, int w, int h, VaultLayout k)
    {
        k.Clear();
        if (center.x + t.MinX < visible.xMin || center.x + t.MaxX > visible.xMax ||
            center.y + t.MinY < visible.yMin || center.y + t.MaxY > visible.yMax)
            return false;
        k.Center = center; k.Thickness = t.Thickness;
        foreach (var o in t.Interior) k.Interior.Add(center + o);
        foreach (var o in t.Walls)    k.Walls.Add(center + o);
        var fieldCentre = new Vector2((w - 1) * 0.5f, (h - 1) * 0.5f);
        foreach (var o in t.Approach)
        {
            var c = center + o;
            if ((uint)c.x < (uint)w && (uint)c.y < (uint)h) k.Approach.Add(c);
        }
        // Nearest the field's centre first.
        k.Approach.Sort((a, b) => ((Vector2)a - fieldCentre).sqrMagnitude.CompareTo(((Vector2)b - fieldCentre).sqrMagnitude));
        k.All.UnionWith(k.Interior);
        k.All.UnionWith(k.Walls);
        return true;
    }

    // The part of the grid that is actually on screen (inclusive cell bounds, as RectInt min/max).
    // Maze cells outside the gameplay frame are never grown (BuildMazeGrowthFromConfig), so a vault
    // reaching past it would be partly invisible — and "fused to the edge" means the visible edge.
    private RectInt VisibleGridBounds(int w, int h)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!IsWorldPositionInsideScreen(drums.GridToWorldPosition(new Vector2Int(x, y)))) continue;
            if (x < x0) x0 = x; if (x > x1) x1 = x;
            if (y < y0) y0 = y; if (y > y1) y1 = y;
        }
        if (x0 > x1 || y0 > y1) return new RectInt(0, 0, w - 1, h - 1); // no camera: whole grid
        return new RectInt(x0, y0, x1 - x0, y1 - y0); // xMax/yMax are the last visible cells
    }

    private sealed class VaultPickRequest
    {
        public VaultTemplate Template;
        public int MinShipDist;
        public List<Vector2Int> ShipCells;
        public bool HaveReach;
        public VaultPlacement Mode;
        public HashSet<Vector2Int> Avoid;
        public HashSet<Vector2Int> PatternSolid; // PatternAnchor only
        public VaultAnchorPrefs Prefs;
        public Vector2Int StarCell;
        // Out:
        public bool  UsedFallback;
        public float BestScore;
    }

    // The requested placement, then (relaxed) fallbacks: ship distance dropped, then edge
    // placement, then anywhere.
    private bool TryPickVaultWithFallbacks(VaultPickRequest req, VaultLayout best)
    {
        int minShip = req.MinShipDist;
        var mode = req.Mode == VaultPlacement.Auto ? VaultPlacement.Edge : req.Mode;
        if (TryPickVault(req, mode, minShip, best)) return true;
        req.UsedFallback = true;
        if (TryPickVault(req, mode, 0, best)) return true;
        if (mode != VaultPlacement.Edge && TryPickVault(req, VaultPlacement.Edge, 0, best)) return true;
        return TryPickVault(req, VaultPlacement.Interior, 0, best, anywhere: true);
    }

    // Picks a random qualifying spot for the template.
    //  Edge: the shell touches the top, left or right edge of the visible field (never the
    //        bottom: the motif maze's porous border doesn't wall the bottom row, so a vault there
    //        isn't fused to anything — MAZE_VAULT_AUDIT.md B2).
    //  Interior: clear of every edge (anywhere: no edge rule at all).
    //  PatternAnchor: every spot is scored against the pattern; a random pick among the best.
    private bool TryPickVault(VaultPickRequest req, VaultPlacement mode, int minShipDist, VaultLayout best,
                              bool anywhere = false)
    {
        var t = req.Template;
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        var vis = VisibleGridBounds(w, h);
        int minShipDistSq = minShipDist * minShipDist;
        var k = new VaultLayout();
        var candidates = new List<Vector2Int>();
        var scores = new List<float>();
        float maxDim = Mathf.Max(1f, new Vector2(w, h).magnitude * 0.5f);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var gp = new Vector2Int(x, y);

            if (!anywhere)
            {
                bool touchesEdge = x + t.MinX == vis.xMin || x + t.MaxX == vis.xMax || y + t.MaxY == vis.yMax;
                if (mode == VaultPlacement.Edge && !touchesEdge) continue;
                if (mode == VaultPlacement.Interior && touchesEdge) continue;
            }

            bool nearShip = false;
            foreach (var s in req.ShipCells)
                if ((s - gp).sqrMagnitude < minShipDistSq) { nearShip = true; break; }
            if (nearShip) continue;

            if (!PlaceVaultTemplate(t, gp, vis, w, h, k)) continue;

            // Not on top of other walls, claims (corridors, star pocket), or a ship.
            bool blocked = false;
            foreach (var c in k.All)
                if (HasCellFlag(c, CellFlags.Solid) || IsKeepClearCell(c) || (req.Avoid != null && req.Avoid.Contains(c)))
                { blocked = true; break; }
            foreach (var s in req.ShipCells)
            {
                if (blocked) break;
                for (int dy = -1; dy <= 1 && !blocked; dy++)
                for (int dx = -1; dx <= 1 && !blocked; dx++)
                    if (k.All.Contains(s + new Vector2Int(dx, dy))) blocked = true;
            }
            if (blocked) continue;

            // A ship has to be able to reach the vault's outside (or the record, with no shell).
            if (req.HaveReach)
            {
                bool reachable = false;
                if (k.Approach.Count > 0) { foreach (var c in k.Approach) if (_reach.IsReachableNear(c)) { reachable = true; break; } }
                else reachable = _reach.IsReachableNear(gp);
                if (!reachable) continue;
            }

            candidates.Add(gp);
            scores.Add(mode == VaultPlacement.PatternAnchor && req.PatternSolid != null
                ? ScoreVaultAnchor(k, req, maxDim) : 0f);
        }

        if (candidates.Count == 0) return false;

        // Random among the near-best (PatternAnchor), or among all (other modes: scores are all 0).
        float top = float.NegativeInfinity;
        foreach (var s in scores) top = Mathf.Max(top, s);
        var pool = new List<Vector2Int>();
        for (int i = 0; i < candidates.Count; i++) if (scores[i] >= top - 0.08f) pool.Add(candidates[i]);
        var pick = pool[UnityEngine.Random.Range(0, pool.Count)];
        req.BestScore = top;
        return PlaceVaultTemplate(t, pick, vis, w, h, best);
    }

    // 0..1: how well the vault at this spot agrees with the pattern (see VaultAnchorPrefs).
    private static float ScoreVaultAnchor(VaultLayout k, VaultPickRequest req, float maxDim)
    {
        var p = req.Prefs;
        var solid = req.PatternSolid;
        int inSolid = 0, wallSolid = 0;
        foreach (var c in k.Interior) if (solid.Contains(c)) inSolid++;
        foreach (var c in k.Walls)    if (solid.Contains(c)) wallSolid++;
        float inSolid01   = k.Interior.Count > 0 ? inSolid / (float)k.Interior.Count : 0f;
        float wallSolid01 = k.Walls.Count > 0 ? wallSolid / (float)k.Walls.Count : 0f;
        float far01       = Mathf.Clamp01((k.Center - req.StarCell).magnitude / maxDim);

        float sum = p.InteriorOpen + p.InteriorSolid + p.WallsSolid + p.FarFromStar;
        if (sum <= 0f) return 0f;
        return (p.InteriorOpen * (1f - inSolid01) + p.InteriorSolid * inSolid01 +
                p.WallsSolid * wallSolid01 + p.FarFromStar * far01) / sum;
    }

    // ── Building, repairing, re-tinting, exploding ──────────────────────────

    private void BuildVault(VaultLayout k)
    {
        _vaultWalls.UnionWith(k.Walls);
        _vaultApproach.Clear();
        _vaultApproach.AddRange(k.Approach);

        _vaultInnerShell.Clear();
        foreach (var c in _vaultWalls)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if ((dx != 0 || dy != 0) && _vaultInterior.Contains(c + new Vector2Int(dx, dy)))
                {
                    if (!_vaultInnerShell.Contains(c)) _vaultInnerShell.Add(c);
                    dx = 2; dy = 2; // next cell
                }

        _vaultGuarded.UnionWith(_vaultWalls);
        foreach (var c in _vaultGuarded)
        {
            // A permanent-clear cell (e.g. the maze's star-anchor disk) would refuse the wall.
            _permanentClearCells.Remove(c);
            _regrow?.CancelRegrow(c);
            _heldRegrowCells.Remove(c);
        }

        GrowVaultCells(_vaultWalls, _vaultTint, 0.4f);

        StopVaultRepair();
        _vaultRepairRoutine = StartCoroutine(RepairVaultLoop());
    }

    // Grows Solid dust in `cells` and gives dust already there the vault look (SpawnDustAtCells
    // recolours existing dust with the maze tint, which would hide the vault in plain sight).
    private void GrowVaultCells(IEnumerable<Vector2Int> cells, Color tint, float growSeconds)
    {
        _vaultScratch.Clear();
        _vaultScratch.AddRange(cells);
        if (_vaultScratch.Count == 0) return;
        SpawnDustAtCells(_vaultScratch, MusicalRole.None, tint, 1f, growSeconds, solid: true);
        foreach (var c in _vaultScratch) ApplyVaultLook(c, tint);
    }

    // Role None (never darkened by the legibility rule, so it stays bright), the vault tint, and
    // Cosmic Block form (big particles, never star food, no bud).
    private void ApplyVaultLook(Vector2Int c, Color tint)
    {
        if (!HasDustAt(c) || !TryGetCellGo(c, out var go) || go == null) return;
        if (!go.TryGetComponent<CosmicDust>(out var dust) || dust == null) return;
        var cur = dust.RestTintRaw;
        if (dust.Role != MusicalRole.None ||
            Mathf.Abs(cur.r - tint.r) + Mathf.Abs(cur.g - tint.g) + Mathf.Abs(cur.b - tint.b) >= 0.02f)
        {
            dust.ApplyRoleAndCharge(MusicalRole.None, tint, 1f);
            dust.HideFlower();
        }
        if (!dust.IsBlockForm) dust.SetBlockForm(true, recordVaultParticleSizeMul);
    }

    // Keeps the vault whole while it stands. A vault cell can come up empty when SpawnDustAtCells
    // skipped it (a ship's keep-clear claim, a cell mid-regrow, maze growth still running) or when
    // something cleared it; empty cells regrow Solid and stray looks are restored.
    private IEnumerator RepairVaultLoop()
    {
        var wait = new WaitForSeconds(kVaultRepairInterval);
        while (_vaultGuarded.Count > 0)
        {
            yield return wait;
            CheckShipsInsideVault();
            // While a retint ripple runs it owns the tint; repair only fills holes.
            RepairVaultCells(_vaultWalls, _vaultTint, restyle: _vaultRetintRoutine == null);
        }
        _vaultRepairRoutine = null;
    }

    private void RepairVaultCells(IEnumerable<Vector2Int> cells, Color tint, bool restyle)
    {
        var missing = _vaultRepairScratch;
        missing.Clear();
        foreach (var c in cells)
        {
            if (!_vaultGuarded.Contains(c)) continue;
            SetCellFlag(c, CellFlags.Solid);
            if (HasDustAt(c)) { if (restyle) ApplyVaultLook(c, tint); continue; }
            if (!TryGetCellState(c, out var st)) continue;
            if (st == DustCellState.Clearing) continue; // finishing a fade; repaired next tick
            if (_regrowthScheduler.VoidGrowCoroutines.ContainsKey(c)) continue; // already growing in
            _permanentClearCells.Remove(c);
            _regrow?.CancelRegrow(c);
            _heldRegrowCells.Remove(c);
            missing.Add(c);
        }
        if (missing.Count > 0)
            SpawnDustAtCells(missing, MusicalRole.None, tint, 1f, 0.3f, solid: true);
        // Holes = guarded cells without solid dust, whether or not a grow-in is under way (a
        // grow-in that keeps aborting would otherwise reset the count every other tick).
        _vaultHoleScratch.Clear();
        foreach (var c in cells)
            if (_vaultGuarded.Contains(c) && !HasDustAt(c)) _vaultHoleScratch.Add(c);
        TrackVaultHoles(_vaultHoleScratch);
    }

    private readonly List<Vector2Int> _vaultHoleScratch = new List<Vector2Int>();

    // Diagnostic: a guarded shell cell that stays empty across repair ticks is a hole a ship could
    // use. Warn once per cell (after ~2 s) with whatever is stopping it from growing.
    private readonly Dictionary<Vector2Int, int> _vaultHoleTicks = new Dictionary<Vector2Int, int>();
    private const int kVaultHoleWarnTicks = 8;

    private void TrackVaultHoles(List<Vector2Int> holes)
    {
        if (holes.Count == 0) { _vaultHoleTicks.Clear(); return; }
        var stale = new List<Vector2Int>();
        foreach (var kv in _vaultHoleTicks) if (!holes.Contains(kv.Key)) stale.Add(kv.Key);
        foreach (var c in stale) _vaultHoleTicks.Remove(c);
        foreach (var c in holes)
        {
            _vaultHoleTicks.TryGetValue(c, out int n);
            _vaultHoleTicks[c] = ++n;
            if (n != kVaultHoleWarnTicks) continue;
            TryGetCellState(c, out var st);
            Debug.LogWarning($"[VAULT] Shell cell {c} still empty after {n} repair ticks: state={st} " +
                             $"permanentClear={_permanentClearCells.Contains(c)} " +
                             $"claimed={(dustClaims != null && dustClaims.IsBlocked(c))} " +
                             $"shipOnCell={IsVehicleOverlappingCell(c)} " +
                             $"voidGrowing={_regrowthScheduler.VoidGrowCoroutines.ContainsKey(c)}", this);
        }
    }

    // ── Trapped ship: hatch ──────────────────────────────────────────────────

    private void CheckShipsInsideVault()
    {
        var gfm = GameFlowManager.Instance;
        var vehicles = gfm != null ? gfm.GetVehicles() : null;
        if (vehicles == null || drums == null) return;

        bool anyInside = false;
        foreach (var v in vehicles)
        {
            if (v == null || !v.isActiveAndEnabled) continue;
            var cell = drums.WorldToGridPosition(v.transform.position);
            // Trapped = in the chamber, or buried in the shell (no outside neighbour). A ship whose
            // centre merely rounds onto an OUTER shell cell is scraping the outside: counting that
            // used to open a hatch straight into the chamber and hold it open while the ship sat in
            // it, so players could drive into a sealed vault.
            bool trapped = _vaultInterior.Contains(cell) || IsBuriedVaultWall(cell);
            // While a hatch is open, a ship still in it counts as on its way out.
            bool leaving = _vaultHatch.Count > 0 && _vaultHatch.Contains(cell);
            if (!trapped && !leaving) continue;
            anyInside = true;
            if (trapped && _vaultHatch.Count == 0) OpenVaultHatch(v, cell);
        }

        if (_vaultHatch.Count == 0) return;
        if (anyInside) { _vaultHatchCloseAt = Time.time + kVaultHatchCloseDelay; return; }
        if (Time.time < _vaultHatchCloseAt) return;

        // Everyone is out: the hatch reseals (RepairVaultCells regrows the cells next tick).
        foreach (var c in _vaultHatch)
            if (_vaultWalls.Contains(c)) _vaultGuarded.Add(c);
        Debug.Log($"[VAULT] Hatch resealed ({_vaultHatch.Count} cells)", this);
        _vaultHatch.Clear();
    }

    // A shell cell with no 8-neighbour outside the vault: a ship centred there is inside the wall.
    private bool IsBuriedVaultWall(Vector2Int cell)
    {
        if (!_vaultWalls.Contains(cell)) return false;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var n = new Vector2Int(cell.x + dx, cell.y + dy);
            if (!_vaultWalls.Contains(n) && !_vaultInterior.Contains(n)) return false;
        }
        return true;
    }

    // Opens a ship-wide channel through the shell from the ship's side of the chamber outward.
    private void OpenVaultHatch(Vehicle ship, Vector2Int shipCell)
    {
        LogShipInsideVault(ship, shipCell);

        Vector2 center = RecordVaultCenter;
        Vector2 dir = (Vector2)shipCell - center;
        if (dir.sqrMagnitude < 0.01f)
            dir = _vaultApproach.Count > 0 ? (Vector2)_vaultApproach[0] - center : Vector2.right;
        dir.Normalize();

        float outer = 0f;
        foreach (var c in _vaultWalls) outer = Mathf.Max(outer, ((Vector2)c - center).magnitude);
        int clearance = Mathf.Max(1, ComputeShipClearanceCells());

        for (float t = 0f; t <= outer + 1f; t += 0.5f)
        {
            var p = center + dir * t;
            var pc = new Vector2Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
            for (int dy = -clearance; dy <= clearance; dy++)
            for (int dx = -clearance; dx <= clearance; dx++)
            {
                if (dx * dx + dy * dy > clearance * clearance) continue;
                var c = pc + new Vector2Int(dx, dy);
                if (_vaultWalls.Contains(c)) _vaultHatch.Add(c);
            }
        }

        var flash = RecordVaultFlashColor;
        foreach (var c in _vaultHatch)
        {
            _vaultGuarded.Remove(c);
            ClearCellFlag(c, CellFlags.Solid);
            ClearCell(c, DustClearMode.FadeAndHide, 0.3f, scheduleRegrow: false,
                      runPreExplode: true, explosionTintOverride: flash);
        }
        _vaultHatchCloseAt = Time.time + kVaultHatchCloseDelay;
        Debug.LogWarning($"[VAULT] Ship {(ship != null ? ship.name : "?")} inside the sealed vault at {shipCell} — " +
                         $"opened a {_vaultHatch.Count}-cell hatch toward {dir}.", this);
    }

    // How did it get in? The shell cells around the ship: dust present, Solid flag, collider, state.
    private void LogShipInsideVault(Vehicle ship, Vector2Int shipCell)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"[VAULT] SHIP INSIDE: {(ship != null ? ship.name : "?")} at cell {shipCell}, " +
                  $"pos {(ship != null ? (Vector2)ship.transform.position : Vector2.zero)}, " +
                  $"vel {(ship != null && ship.TryGetComponent<Rigidbody2D>(out var rb) ? rb.linearVelocity : Vector2.zero)}. Shell near it:");
        foreach (var c in _vaultWalls)
        {
            if ((c - shipCell).sqrMagnitude > 9) continue;
            TryGetCellState(c, out var st);
            bool hasDust = TryGetDustAt(c, out var d) && d != null;
            bool col = hasDust && d.terrainCollider != null && d.terrainCollider.enabled;
            sb.Append($" {c}[{st} solid={HasCellFlag(c, CellFlags.Solid)} guarded={_vaultGuarded.Contains(c)} " +
                      $"dust={hasDust} collider={col}]");
        }
        Debug.LogWarning(sb.ToString(), this);
    }

    // Shell cells grouped by depth (0 = touching the chamber), for outward ripples. Depth is the
    // 8-neighbour step count from the chamber through the shell, so it works for every vault
    // shape; cells cut off from the chamber (an open hatch) go in the last layer.
    private List<List<Vector2Int>> VaultLayersOutward(IEnumerable<Vector2Int> cells)
    {
        var set = new HashSet<Vector2Int>(cells);
        var depth = new Dictionary<Vector2Int, int>(set.Count);
        var queue = new Queue<Vector2Int>();
        foreach (var c in set)
            if (_vaultInnerShell.Contains(c)) { depth[c] = 0; queue.Enqueue(c); }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var n = new Vector2Int(c.x + dx, c.y + dy);
                if (!set.Contains(n) || depth.ContainsKey(n)) continue;
                depth[n] = depth[c] + 1;
                queue.Enqueue(n);
            }
        }
        int maxD = 0;
        foreach (var d in depth.Values) maxD = Mathf.Max(maxD, d);
        var layers = new List<List<Vector2Int>>();
        foreach (var c in set)
        {
            int d = depth.TryGetValue(c, out var dd) ? dd : maxD + 1;
            while (layers.Count <= d) layers.Add(new List<Vector2Int>());
            layers[d].Add(c);
        }
        layers.RemoveAll(l => l.Count == 0);
        return layers;
    }

    /// <summary>
    /// A ring was recorded: the whole shell turns toward <paramref name="roleColor"/> (muted by
    /// recordVaultRoleTint01), rippling outward from the chamber, each cell flashing as it turns.
    /// </summary>
    /// <returns>Seconds the ripple takes (0 when there is no standing shell).</returns>
    public float RetintRecordVault(Color roleColor)
    {
        if (!IsRecordVaultSealed) return 0f;
        var color = Color.Lerp(config.inertBlockTint, roleColor, recordVaultRoleTint01);
        color.a = 1f;
        _vaultTint = color;
        if (_vaultRetintRoutine != null) StopCoroutine(_vaultRetintRoutine);
        var layers = VaultLayersOutward(new List<Vector2Int>(_vaultGuarded));
        _vaultRetintRoutine = StartCoroutine(RetintRoutine(color, layers));
        Debug.Log($"[VAULT] Retint → {color}", this);
        return layers.Count * kVaultLayerStagger;
    }

    private IEnumerator RetintRoutine(Color color, List<List<Vector2Int>> layers)
    {
        foreach (var layer in layers)
        {
            foreach (var c in layer)
            {
                if (!_vaultGuarded.Contains(c)) continue;
                ApplyVaultLook(c, color);
                TryFlashSolidCell(c, RecordVaultFlashColor, kVaultRetintFlash);
            }
            yield return new WaitForSeconds(kVaultLayerStagger);
        }
        _vaultRetintRoutine = null;
    }

    /// <summary>
    /// Motif complete: the shell goes up in a chain reaction, in <paramref name="tint"/>. It ignites at
    /// the inner-shell block facing <paramref name="igniteDir"/> (where the record last struck; random
    /// when zero), and each block sets off its neighbours after a short, jittered fuse.
    /// Returns the seconds until the last block has burst.
    /// </summary>
    public float ExplodeRecordVault(Color tint, Vector2 igniteDir = default)
    {
        StopVaultRepair();
        StopVaultExplode();
        if (_vaultRetintRoutine != null) { StopCoroutine(_vaultRetintRoutine); _vaultRetintRoutine = null; }
        _vaultHatch.Clear();
        var cells = new HashSet<Vector2Int>(_vaultGuarded);
        if (cells.Count == 0) return 0f;

        var seed = PickVaultIgnition(cells, igniteDir);
        var schedule = BuildVaultChain(cells, seed);
        float last = schedule[schedule.Count - 1].time;
        _vaultExplodeRoutine = StartCoroutine(ExplodeChainRoutine(schedule, tint));
        float seconds = last + kVaultExplodeSeconds;
        Debug.Log($"[VAULT] Explode chain ({cells.Count} cells, seed {seed} " +
                  $"{(igniteDir.sqrMagnitude > 0.0001f ? $"at bump {igniteDir}" : "random")}, chain {last:F2}s, total {seconds:F2}s)", this);
        return seconds;
    }

    private struct VaultDetonation
    {
        public Vector2Int cell, parent;
        public float time;
    }

    // The standing inner-shell block best facing igniteDir (random without one); any block as a last resort.
    private Vector2Int PickVaultIgnition(HashSet<Vector2Int> cells, Vector2 igniteDir)
    {
        var center = (Vector2)RecordVaultCenter;
        _vaultScratch.Clear();
        foreach (var c in _vaultInnerShell) if (cells.Contains(c)) _vaultScratch.Add(c);
        if (_vaultScratch.Count == 0) _vaultScratch.AddRange(cells);

        if (igniteDir.sqrMagnitude < 0.0001f)
            return _vaultScratch[Random.Range(0, _vaultScratch.Count)];

        igniteDir.Normalize();
        var best = _vaultScratch[0];
        float bestDot = float.NegativeInfinity;
        foreach (var c in _vaultScratch)
        {
            var d = (Vector2)c - center;
            float dot = d.sqrMagnitude < 0.0001f ? -1f : Vector2.Dot(d.normalized, igniteDir);
            if (dot > bestDot) { bestDot = dot; best = c; }
        }
        return best;
    }

    // Detonation times: shortest jittered-fuse paths from the seed over 8-neighbour shell blocks.
    // A piece cut off from the seed (an open hatch) re-ignites once the main chain is done.
    // Sorted by time; compressed to kVaultChainMaxSeconds.
    private List<VaultDetonation> BuildVaultChain(HashSet<Vector2Int> cells, Vector2Int seed)
    {
        var time   = new Dictionary<Vector2Int, float>(cells.Count);
        var parent = new Dictionary<Vector2Int, Vector2Int>(cells.Count);
        var done   = new HashSet<Vector2Int>();
        var open   = new List<Vector2Int>();
        float latest = 0f;

        Vector2Int next = seed;
        float nextStart = 0f;
        while (true)
        {
            time[next] = nextStart; parent[next] = next; open.Add(next);
            while (open.Count > 0)
            {
                // Small sets (a few hundred cells): a linear-scan priority queue is fine.
                int bi = 0;
                for (int i = 1; i < open.Count; i++) if (time[open[i]] < time[open[bi]]) bi = i;
                var c = open[bi];
                open[bi] = open[open.Count - 1]; open.RemoveAt(open.Count - 1);
                if (!done.Add(c)) continue;
                float tc = time[c];
                latest = Mathf.Max(latest, tc);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var n = new Vector2Int(c.x + dx, c.y + dy);
                    if (!cells.Contains(n) || done.Contains(n)) continue;
                    float fuse = Random.Range(kVaultChainFuseMin, kVaultChainFuseMax) * ((dx != 0 && dy != 0) ? 1.4f : 1f);
                    float t = tc + fuse;
                    if (time.TryGetValue(n, out var old) && old <= t) continue;
                    time[n] = t; parent[n] = c; open.Add(n);
                }
            }
            if (done.Count >= cells.Count) break;

            // Cut-off piece: it catches from the nearest exploded block, a fuse after the chain ends.
            float bestD = float.PositiveInfinity;
            foreach (var c in cells)
            {
                if (done.Contains(c)) continue;
                float d = (c - seed).sqrMagnitude;
                if (d < bestD) { bestD = d; next = c; }
            }
            nextStart = latest + kVaultChainFuseMax;
        }

        float scale = latest > kVaultChainMaxSeconds ? kVaultChainMaxSeconds / latest : 1f;
        var list = new List<VaultDetonation>(cells.Count);
        foreach (var kv in time)
            list.Add(new VaultDetonation { cell = kv.Key, parent = parent[kv.Key], time = kv.Value * scale });
        list.Sort((a, b) => a.time.CompareTo(b.time));
        return list;
    }

    private IEnumerator ExplodeChainRoutine(List<VaultDetonation> schedule, Color tint)
    {
        var center = (Vector2)RecordVaultCenter;
        var flash  = Color.Lerp(tint, Color.white, recordVaultFlashLift01);
        float start = Time.time;
        int lit = 0, popped = 0;
        while (popped < schedule.Count)
        {
            float now = Time.time - start;

            // Fuse: each block flashes just before it pops.
            while (lit < schedule.Count && schedule[lit].time - kVaultChainFuseFlash <= now)
            {
                TryFlashSolidCell(schedule[lit].cell, flash, kVaultChainFuseFlash + 0.05f);
                lit++;
            }

            while (popped < schedule.Count && schedule[popped].time <= now)
            {
                var d = schedule[popped++];
                var c = d.cell;
                if (!_vaultGuarded.Remove(c)) continue; // already opened (OpenRecordVault)
                ClearCellFlag(c, CellFlags.Solid);
                _permanentClearCells.Remove(c);
                var outward = ((Vector2)c - center).normalized;
                var along   = c == d.parent ? outward : ((Vector2)(c - d.parent)).normalized;
                var dir     = (outward + along).sqrMagnitude > 0.0001f ? (outward + along).normalized : outward;
                ClearCell(c, DustClearMode.FadeAndHide, kVaultExplodeSeconds, scheduleRegrow: false,
                          runPreExplode: true, explosionTintOverride: tint, burstDirection: dir);
                _permanentClearCells.Add(c);
                _regrow?.CancelRegrow(c);
            }
            yield return null;
        }
        _vaultWalls.Clear();
        _vaultInnerShell.Clear();
        _vaultExplodeRoutine = null;
    }

    /// <summary>Removes whatever is left of the vault at once (reset, bridge). Returns the cells opened.</summary>
    public int OpenRecordVault()
    {
        StopVaultRepair();
        StopVaultExplode();
        if (_vaultRetintRoutine != null) { StopCoroutine(_vaultRetintRoutine); _vaultRetintRoutine = null; }
        _vaultHatch.Clear();
        var cells = new List<Vector2Int>(_vaultGuarded);
        _vaultGuarded.Clear(); // lift the clear guards first
        _vaultWalls.Clear();
        _vaultInnerShell.Clear();
        foreach (var c in cells)
        {
            ClearCellFlag(c, CellFlags.Solid);
            DespawnDustAtAndMarkPermanent(c);
        }
        if (cells.Count > 0) Debug.Log($"[VAULT] Opened instantly ({cells.Count} cells)", this);
        return cells.Count;
    }
}
