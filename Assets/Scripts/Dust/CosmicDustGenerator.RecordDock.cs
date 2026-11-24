using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  Record dock + key-shaped vault (record recovery): where the record sits for the motif.
//
//      #########
//      #       #############################
//      # (rec)  G  G  G  G  G     mouth →         G = gate cells (one per ring), uncoloured
//      #       #############################      # = Solid dust (bypass-proof)
//      #########
//
//  The vault sits at a random spot along the edge of the field: the chamber's wall ring touches the
//  grid boundary, so the walls read as fused to the edge, and the channel points into the field.
//
//  The chamber (a cleared disk of radius r holding the record) is ringed by Solid dust. A straight
//  channel (a row or a column, as wide as the widest ship) leads out of it, walled on both sides
//  by Solid dust and plugged by a line of gate cells, one per ring the motif needs. The gates are
//  Solid too, in a neutral tint: no role colour until a ring is recorded.
//
//  Each recorded ring opens the outermost remaining gate (OpenNextRecordGate): the gate takes the
//  recorded ring's colour, then dissolves. When the motif completes, any gates still standing open
//  (OpenAllRecordGates), so a ship can drive down the channel to the record. The walls stand until
//  the record is collected (OpenRecordVault, from the bridge).
//
//  Standing vault cells refuse every clear path (ClearCell, DespawnDustAt, zaps, role sweeps) and
//  RepairVaultLoop regrows any that come up empty. Vault cells (chamber, walls, gates) never take
//  spawns: IsRecordVaultCell is checked by DrumTrackGridMapper.IsSpawnCellAvailable (stars) and
//  the note spawner. Everything is forgotten with the maze (ClearMaze → ResetRecordVault).
// =========================================================================
public partial class CosmicDustGenerator
{
    private static readonly Vector2Int[] kKeyDirs =
        { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down };

    private readonly HashSet<Vector2Int>    _vaultInterior = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int>    _vaultWalls    = new HashSet<Vector2Int>();    // Solid walls
    private readonly List<List<Vector2Int>> _vaultGates    = new List<List<Vector2Int>>(); // outermost first
    private readonly HashSet<Vector2Int>    _vaultGuarded  = new HashSet<Vector2Int>();    // walls + unopened gates
    private readonly List<Vector2Int>       _vaultScratch  = new List<Vector2Int>();
    private readonly List<Vector2Int>       _vaultMouth    = new List<Vector2Int>();    // open cells at the channel's outer end
    private int       _vaultGatesOpened;
    private Color     _vaultGateTint = new Color(0.62f, 0.62f, 0.66f, 1f);
    private Coroutine _vaultRepairRoutine;
    private KeyLayout _plannedVault; // chosen before the maze is laid out, built before it grows

    /// <summary>True once this maze's vault has been built (its centre is <see cref="RecordVaultCenter"/>).</summary>
    public bool HasRecordVault { get; private set; }

    /// <summary>The record's cell inside the vault (valid while <see cref="HasRecordVault"/>).</summary>
    public Vector2Int RecordVaultCenter { get; private set; }

    private const float kVaultRepairInterval = 0.25f;
    private const float kGateTintHold        = 0.45f; // a gate shows the ring's colour this long, then dissolves
    private const float kGateDissolveSeconds = 0.45f;

    /// <summary>True for the record's chamber, its walls and its gate channel (no spawns there).</summary>
    public bool IsRecordVaultCell(Vector2Int cell)
    {
        if (_vaultInterior.Contains(cell) || _vaultWalls.Contains(cell)) return true;
        if (_plannedVault != null && (_plannedVault.All.Contains(cell) || _plannedVault.Mouth.Contains(cell))) return true;
        foreach (var g in _vaultGates) if (g.Contains(cell)) return true;
        return false;
    }

    /// <summary>True while any vault wall or gate is standing.</summary>
    public bool IsRecordVaultSealed => _vaultGuarded.Count > 0;

    /// <summary>Gates in the channel (0 when the record has no vault).</summary>
    public int RecordGateCount => _vaultGates.Count;

    /// <summary>Gates already opened.</summary>
    public int RecordGatesOpened => _vaultGatesOpened;

    // A standing vault cell: every clear path refuses it, like a Cosmic Block.
    private bool IsSealedVaultShell(Vector2Int cell) => _vaultGuarded.Contains(cell);

    /// <summary>Forgets the vault (new maze). Called from ClearMaze before the grid is emptied.</summary>
    private void ResetRecordVault()
    {
        StopVaultRepair();
        _plannedVault = null;
        HasRecordVault = false;
        _vaultInterior.Clear();
        _vaultWalls.Clear();
        _vaultGates.Clear();
        _vaultGuarded.Clear();
        _vaultMouth.Clear();
        _vaultGatesOpened = 0;
    }

    /// <summary>
    /// Reach target for the role-wall watchdog (GatherReachSeedsAndTargets): while the vault stands,
    /// the channel's mouth must stay reachable, so a Cosmic Block wall can never seal the only way
    /// to the record (CarveDoorways leaves a doorway; EnsureTargetsReachable opens one).
    /// </summary>
    private void AddRecordVaultReachTargets(List<Vector2Int> targets)
    {
        if (!HasRecordVault || !IsRecordVaultSealed || _vaultMouth.Count == 0) return;
        targets.Add(_vaultMouth[_vaultMouth.Count / 2]);
    }

    private void StopVaultRepair()
    {
        if (_vaultRepairRoutine != null) { StopCoroutine(_vaultRepairRoutine); _vaultRepairRoutine = null; }
    }

    // ── Placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// Chooses the record's dock cell and permanently clears a chamber of <paramref name="radiusCells"/>
    /// around it. With <paramref name="gateCount"/> &gt; 0 it also builds the key: Solid walls around the
    /// chamber and a walled channel of <paramref name="gateCount"/> gates leading out of it. Returns false
    /// if no cell qualifies.
    /// </summary>
    public bool TryClearRecordDock(int radiusCells, int minShipDistanceCells, out Vector2Int dockCell,
                                   int gateCount = 0, Color? gateTint = null)
    {
        dockCell = default;
        if (drums == null) return false;
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return false;
        radiusCells = Mathf.Max(1, radiusCells);
        gateCount   = Mathf.Max(0, gateCount);
        if (gateTint.HasValue) _vaultGateTint = gateTint.Value;

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

        int channelWidth = ComputeShipDiameterCells();
        var layout = new KeyLayout();
        // Against a random stretch of the boundary if at all possible; anywhere only as a last resort.
        if (!TryPickKey(w, h, radiusCells, gateCount, channelWidth, minShipDistanceCells, shipCells, haveReach, true, layout)
            && !TryPickKey(w, h, radiusCells, gateCount, channelWidth, 0, shipCells, haveReach, true, layout)
            && !TryPickKey(w, h, radiusCells, gateCount, channelWidth, 0, shipCells, haveReach, false, layout))
            return false;

        ResetRecordVault();
        dockCell = layout.Center;

        foreach (var c in layout.Interior) DespawnDustAtAndMarkPermanent(c);
        _vaultInterior.UnionWith(layout.Interior);

        if (gateCount > 0)
        {
            foreach (var c in layout.Mouth) DespawnDustAtAndMarkPermanent(c); // the gates' outer face is open
            _vaultMouth.AddRange(layout.Mouth);
            BuildVault(layout);
        }
        HasRecordVault    = true;
        RecordVaultCenter = layout.Center;

        Debug.Log($"[RECORD] Vault at {layout.Center} r={radiusCells} dir={layout.Dir} gates={_vaultGates.Count} " +
                  $"width={channelWidth} walls={_vaultWalls.Count}", this);
        return true;
    }

    // ── Built with the maze ──────────────────────────────────────────────────
    // GenerateMazeForPhaseWithPaths asks for the vault before the maze is laid out, so the maze
    // grows around it: PlanRecordVaultForMaze picks the spot and reserves its cells (the maze
    // pattern skips them), and BuildPlannedRecordVault grows the walls and gates and docks the
    // record (GameFlowManager.OnRecordVaultBuilt) just before the maze's own growth starts.

    private bool PlanRecordVaultForMaze(Vector2Int starCell, IReadOnlyList<Vector2Int> vehicleCells,
                                        HashSet<Vector2Int> reserved, int starHoleRadius, int vehicleHoleRadius)
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || drums == null) return false;
        if (!gfm.TryGetRecordVaultSpec(out int r, out int minShipDist, out int gates, out Color gateTint)) return false;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return false;
        r = Mathf.Max(1, r);
        _vaultGateTint = gateTint;

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

        int width  = ComputeShipDiameterCells();
        var layout = new KeyLayout();
        // No reach search: nothing is built yet, and ordinary maze dust is carveable.
        if (!TryPickKey(w, h, r, gates, width, minShipDist, shipCells, false, true, layout, avoid)
            && !TryPickKey(w, h, r, gates, width, 0, shipCells, false, true, layout, avoid)
            && !TryPickKey(w, h, r, gates, width, 0, shipCells, false, false, layout, avoid))
        {
            Debug.LogWarning("[RECORD] No room for the vault in this maze; the record will dock after it grows.", this);
            return false;
        }

        _plannedVault = layout;
        reserved.UnionWith(layout.All);
        reserved.UnionWith(layout.Mouth);
        return true;
    }

    private void BuildPlannedRecordVault()
    {
        var layout = _plannedVault;
        if (layout == null) return;
        _plannedVault = null;

        foreach (var c in layout.Interior) DespawnDustAtAndMarkPermanent(c);
        foreach (var c in layout.Mouth)    DespawnDustAtAndMarkPermanent(c);
        _vaultInterior.UnionWith(layout.Interior);
        _vaultMouth.AddRange(layout.Mouth);
        if (layout.Gates.Count > 0 || layout.Walls.Count > 0) BuildVault(layout);
        HasRecordVault    = true;
        RecordVaultCenter = layout.Center;

        Debug.Log($"[RECORD] Vault built with the maze at {layout.Center} dir={layout.Dir} gates={_vaultGates.Count} " +
                  $"walls={_vaultWalls.Count}", this);
        GameFlowManager.Instance?.OnRecordVaultBuilt(layout.Center);
    }

    private sealed class KeyLayout
    {
        public Vector2Int Center;
        public Vector2Int Dir;
        public readonly HashSet<Vector2Int>    Interior = new HashSet<Vector2Int>();
        public readonly HashSet<Vector2Int>    Walls    = new HashSet<Vector2Int>();
        public readonly List<List<Vector2Int>> Gates    = new List<List<Vector2Int>>(); // outermost first
        public readonly List<Vector2Int>       Mouth    = new List<Vector2Int>();
        public readonly HashSet<Vector2Int>    All      = new HashSet<Vector2Int>();

        public void Clear()
        {
            Interior.Clear(); Walls.Clear(); Gates.Clear(); Mouth.Clear(); All.Clear();
        }

        public void CopyFrom(KeyLayout o)
        {
            Clear();
            Center = o.Center; Dir = o.Dir;
            Interior.UnionWith(o.Interior);
            Walls.UnionWith(o.Walls);
            foreach (var g in o.Gates) Gates.Add(new List<Vector2Int>(g));
            Mouth.AddRange(o.Mouth);
            All.UnionWith(o.All);
        }
    }

    // Lays out a key at `center` pointing along `dir`. False if any part falls outside the grid.
    private static bool BuildKeyLayout(Vector2Int center, Vector2Int dir, int r, int gates, int width,
                                       int w, int h, KeyLayout k)
    {
        k.Clear();
        k.Center = center; k.Dir = dir;
        var perp = new Vector2Int(-dir.y, dir.x);
        int jMin = -(width - 1) / 2, jMax = jMin + width - 1;

        bool InBounds(Vector2Int c) => c.x >= 0 && c.y >= 0 && c.x < w && c.y < h;

        // The whole chamber ring must fit (FillDisk clips at the edges).
        if (!InBounds(center + new Vector2Int(r + 1, 0)) || !InBounds(center - new Vector2Int(r + 1, 0)) ||
            !InBounds(center + new Vector2Int(0, r + 1)) || !InBounds(center - new Vector2Int(0, r + 1)))
            return false;

        // Chamber and its ring of walls.
        var outer = new HashSet<Vector2Int>();
        DustGridMath.FillDisk(k.Interior, center, r, w, h);
        DustGridMath.FillDisk(outer, center, r + 1, w, h);
        foreach (var c in outer) if (!k.Interior.Contains(c)) k.Walls.Add(c);

        if (gates > 0)
        {
            // A full-width path from the chamber centre out to the first gate.
            for (int i = 0; i <= r; i++)
                for (int j = jMin; j <= jMax; j++)
                {
                    var c = center + dir * i + perp * j;
                    if (!InBounds(c)) return false;
                    k.Interior.Add(c);
                }

            // Channel: gate rows from just outside the chamber outward, walled on both sides.
            var channel = new HashSet<Vector2Int>();
            for (int i = 0; i < gates; i++)
            {
                var row = new List<Vector2Int>(width);
                var basePos = center + dir * (r + 1 + i);
                for (int j = jMin; j <= jMax; j++)
                {
                    var c = basePos + perp * j;
                    if (!InBounds(c)) return false;
                    row.Add(c);
                    channel.Add(c);
                }
                var sideA = basePos + perp * (jMin - 1);
                var sideB = basePos + perp * (jMax + 1);
                if (!InBounds(sideA) || !InBounds(sideB)) return false;
                k.Walls.Add(sideA);
                k.Walls.Add(sideB);
                k.Gates.Insert(0, row); // outermost first
            }
            k.Walls.ExceptWith(channel);
            k.Walls.ExceptWith(k.Interior);

            var mouthBase = center + dir * (r + 1 + gates);
            for (int j = jMin; j <= jMax; j++)
            {
                var c = mouthBase + perp * j;
                if (!InBounds(c)) return false;
                k.Mouth.Add(c);
            }
        }

        k.All.UnionWith(k.Interior);
        k.All.UnionWith(k.Walls);
        foreach (var g in k.Gates) k.All.UnionWith(g);
        return true;
    }

    // Picks a random qualifying key. edgeOnly: the chamber's wall ring must touch the grid boundary
    // (the record is tucked against an edge, its walls fused to it) with the channel pointing into
    // the field; otherwise any spot and direction qualifies (last-resort fallback).
    private bool TryPickKey(int w, int h, int r, int gates, int width, int minShipDist, List<Vector2Int> shipCells,
                            bool haveReach, bool edgeOnly, KeyLayout best,
                            HashSet<Vector2Int> avoid = null)
    {
        int minShipDistSq = minShipDist * minShipDist;
        var k = new KeyLayout();
        var candidates = new List<(Vector2Int center, Vector2Int dir)>();
        var dirs = new List<Vector2Int>(4);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var gp = new Vector2Int(x, y);

            dirs.Clear();
            if (edgeOnly)
            {
                // Wall ring on the boundary; the channel heads away from that edge.
                if (x == r + 1)         dirs.Add(Vector2Int.right);
                if (x == w - 2 - r)     dirs.Add(Vector2Int.left);
                if (y == r + 1)         dirs.Add(Vector2Int.up);
                if (y == h - 2 - r)     dirs.Add(Vector2Int.down);
                if (dirs.Count == 0) continue;
                if (gates <= 0) { dirs.Clear(); dirs.Add(Vector2Int.right); }
            }
            else
            {
                if (gates > 0) dirs.AddRange(kKeyDirs); else dirs.Add(Vector2Int.right);
            }

            bool nearShip = false;
            foreach (var s in shipCells)
                if ((s - gp).sqrMagnitude < minShipDistSq) { nearShip = true; break; }
            if (nearShip) continue;

            foreach (var dir in dirs)
            {
                if (!BuildKeyLayout(gp, dir, r, gates, width, w, h, k)) continue;

                // Not on top of other walls, claims (corridors, star pocket), or a ship.
                bool blocked = false;
                foreach (var c in k.All)
                    if (HasCellFlag(c, CellFlags.Solid) || IsKeepClearCell(c) || (avoid != null && avoid.Contains(c)))
                    { blocked = true; break; }
                if (!blocked && avoid != null)
                    foreach (var c in k.Mouth) if (avoid.Contains(c) && !_permanentClearCells.Contains(c)) { blocked = true; break; }
                foreach (var s in shipCells)
                {
                    if (blocked) break;
                    for (int dy = -1; dy <= 1 && !blocked; dy++)
                    for (int dx = -1; dx <= 1 && !blocked; dx++)
                        if (k.All.Contains(s + new Vector2Int(dx, dy))) blocked = true;
                }
                if (blocked) continue;

                // A ship has to be able to reach the channel mouth (or the record, with no gates).
                if (haveReach)
                {
                    bool reachable = false;
                    if (k.Mouth.Count > 0) { foreach (var c in k.Mouth) if (_reach.IsReachableNear(c)) { reachable = true; break; } }
                    else reachable = _reach.IsReachableNear(gp);
                    if (!reachable) continue;
                }

                candidates.Add((gp, dir));
            }
        }

        if (candidates.Count == 0) return false;
        var pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        BuildKeyLayout(pick.center, pick.dir, r, gates, width, w, h, k);
        best.CopyFrom(k);
        return true;
    }

    // Widest ship's diameter in cells: the channel must let it through.
    private int ComputeShipDiameterCells()
    {
        float cellWorld = drums != null ? Mathf.Max(0.01f, drums.GetCellWorldSize()) : 1f;
        float widest = 0f;
        var vehicles = GameFlowManager.Instance != null ? GameFlowManager.Instance.GetVehicles() : null;
        if (vehicles != null)
            foreach (var v in vehicles)
                if (v != null && v.isActiveAndEnabled)
                    widest = Mathf.Max(widest, v.GetColliderBoundingRadiusWorld());
        return Mathf.Clamp(Mathf.CeilToInt(2f * widest / cellWorld), 1, 3);
    }

    // ── Building, repairing, opening ─────────────────────────────────────────

    private void BuildVault(KeyLayout k)
    {
        _vaultWalls.UnionWith(k.Walls);
        foreach (var g in k.Gates) _vaultGates.Add(new List<Vector2Int>(g));
        _vaultGatesOpened = 0;

        _vaultGuarded.UnionWith(_vaultWalls);
        foreach (var g in _vaultGates) _vaultGuarded.UnionWith(g);
        foreach (var c in _vaultGuarded)
        {
            // A permanent-clear cell (e.g. the maze's star-anchor disk) would refuse the wall.
            _permanentClearCells.Remove(c);
            _regrow?.CancelRegrow(c);
            _heldRegrowCells.Remove(c);
        }

        GrowVaultCells(_vaultWalls, config.solidDustTint, 0.4f);
        GrowVaultCells(UnopenedGateCells(), _vaultGateTint, 0.4f);

        StopVaultRepair();
        _vaultRepairRoutine = StartCoroutine(RepairVaultLoop());
    }

    private List<Vector2Int> UnopenedGateCells()
    {
        var list = new List<Vector2Int>();
        for (int i = _vaultGatesOpened; i < _vaultGates.Count; i++) list.AddRange(_vaultGates[i]);
        return list;
    }

    // Grows Solid dust in `cells` and tints dust already there (SpawnDustAtCells recolours
    // existing dust with the maze tint, which would hide the vault in plain sight).
    private void GrowVaultCells(IEnumerable<Vector2Int> cells, Color tint, float growSeconds)
    {
        _vaultScratch.Clear();
        _vaultScratch.AddRange(cells);
        if (_vaultScratch.Count == 0) return;
        SpawnDustAtCells(_vaultScratch, MusicalRole.None, tint, 1f, growSeconds, solid: true);
        foreach (var c in _vaultScratch) TintVaultDust(c, tint);
    }

    private void TintVaultDust(Vector2Int c, Color tint)
    {
        if (!HasDustAt(c) || !TryGetCellGo(c, out var go) || go == null) return;
        if (!go.TryGetComponent<CosmicDust>(out var dust) || dust == null) return;
        var cur = dust.CurrentTint;
        if (Mathf.Abs(cur.r - tint.r) + Mathf.Abs(cur.g - tint.g) + Mathf.Abs(cur.b - tint.b) < 0.02f) return;
        dust.ApplyRoleAndCharge(MusicalRole.None, tint, 1f);
        dust.HideFlower();
    }

    // Keeps the vault whole while it stands. A vault cell can come up empty when SpawnDustAtCells
    // skipped it (a ship's keep-clear claim, a cell mid-regrow, maze growth still running) or when
    // something cleared it; empty cells regrow Solid and stray tints are restored.
    private IEnumerator RepairVaultLoop()
    {
        var wait = new WaitForSeconds(kVaultRepairInterval);
        while (_vaultGuarded.Count > 0)
        {
            yield return wait;
            RepairVaultCells(_vaultWalls, config.solidDustTint);
            RepairVaultCells(UnopenedGateCells(), _vaultGateTint);
        }
        _vaultRepairRoutine = null;
    }

    private void RepairVaultCells(IEnumerable<Vector2Int> cells, Color tint)
    {
        var missing = new List<Vector2Int>();
        foreach (var c in cells)
        {
            if (!_vaultGuarded.Contains(c)) continue;
            SetCellFlag(c, CellFlags.Solid);
            if (HasDustAt(c)) { TintVaultDust(c, tint); continue; }
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
    }

    /// <summary>
    /// A ring was recorded: the outermost standing gate takes <paramref name="ringColor"/>, then
    /// dissolves. Returns false when no gate is left.
    /// </summary>
    public bool OpenNextRecordGate(Color ringColor)
    {
        if (_vaultGatesOpened >= _vaultGates.Count) return false;
        var gate = _vaultGates[_vaultGatesOpened++];
        StartCoroutine(OpenGateRoutine(gate, ringColor, kGateTintHold));
        return true;
    }

    /// <summary>Motif complete: every standing gate opens, outermost first. Returns the seconds the cascade takes.</summary>
    public float OpenAllRecordGates(Color tint, float stagger = 0.15f)
    {
        float t = 0f;
        while (_vaultGatesOpened < _vaultGates.Count)
        {
            var gate = _vaultGates[_vaultGatesOpened++];
            StartCoroutine(OpenGateRoutine(gate, tint, kGateTintHold + t));
            t += stagger;
        }
        return t + kGateTintHold + kGateDissolveSeconds;
    }

    private IEnumerator OpenGateRoutine(List<Vector2Int> gate, Color color, float holdSeconds)
    {
        foreach (var c in gate) TintVaultDust(c, color);   // the gate takes the ring's colour…
        foreach (var c in gate) _vaultGuarded.Remove(c);   // (and is no longer repaired)
        if (holdSeconds > 0f) yield return new WaitForSeconds(holdSeconds);

        foreach (var c in gate)                             // …then dissolves for good
        {
            ClearCellFlag(c, CellFlags.Solid);
            _permanentClearCells.Remove(c);
            ClearCell(c, DustClearMode.FadeAndHide, kGateDissolveSeconds, scheduleRegrow: false,
                      runPreExplode: true, explosionTintOverride: color);
            _permanentClearCells.Add(c);
            _regrow?.CancelRegrow(c);
        }
    }

    /// <summary>Dissolves whatever is left of the vault (record collected, bridge, reset). Returns the cells opened.</summary>
    public int OpenRecordVault()
    {
        StopVaultRepair();
        var cells = new List<Vector2Int>(_vaultGuarded);
        _vaultGuarded.Clear(); // lift the clear guards first
        _vaultWalls.Clear();
        _vaultGatesOpened = _vaultGates.Count;
        foreach (var c in cells)
        {
            ClearCellFlag(c, CellFlags.Solid);
            DespawnDustAtAndMarkPermanent(c);
        }
        if (cells.Count > 0) Debug.Log($"[RECORD] Vault opened ({cells.Count} cells)", this);
        return cells.Count;
    }
}
