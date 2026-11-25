# Maze Patterns × Record Vault Audit (2026-11-25)

**Scope.** This audit covers the maze patterns (`CosmicDustMazePatterns`, `MazeTopologyService`, `MazePatternConfig`) together with the record vault (`CosmicDustGenerator.RecordDock.cs`).

**Trigger.** A playtest report:
1. After a motif, the ship spawned in the interstitial maze's right corner, and **that whole corner had no dust**.
2. The ship took an exit into the next motif and **the record's vault was open**, so the ship could drive into the chamber. The vault should stay sealed until the motif completes.

Companion to `RECORD_RECOVERY_STATUS.md` (the record-recovery source of truth), `CONFIG_AUDIT.md` and `VARIABLE_AUDIT.md`.

**Status markers.** **FIXED** = applied, **OPEN** = not done, **KEEP** = deliberately left as is.

**Build state.** Both assemblies compile with msbuild. **Nothing has been playtested.** Nothing is committed.

---

## A. Causes of the playtest symptoms

### A1. Dust claims outlived their maze: empty interstitial corner (likely cause) — **FIXED**
- `DustClaimManager` claims survived `ClearMaze()`.
- **The Star's keep-clear pocket** (`PhaseStarDustAffect.TickKeepClear` → `SetStarKeepClear`, owner `"PhaseStarPocket"`) is a disk 3–5 cells across (`starKeepClearRadiusCells`; Quick Star = 5).
  - `SetStarKeepClear` only ever diffs the pocket against the next one, so **nothing released it when the Star died**.
  - The last pocket therefore survived into the interstitial build, and on into the next motif's build.
  - `BuildMazeGrowthFromConfig` skips claimed cells, so the maze grew with an empty disk there. A radius-5 disk clipped by a corner empties that corner.
- **Travel corridors** (owner `"Corridor"`, permanent) carried over the same way:
  - the motif's entry→Star lane into the interstitial;
  - the interstitial's winning-exit lane (as wide as the hull) into the next motif.
  - Each left an empty stripe and shrank the set of places the vault could go.
- **Fix:**
  - `ClearMaze()` releases the star pocket and the corridors, with no regrow requests, so no stray cell grows into the new maze.
  - `PhaseStarDustAffect.OnDisable` releases the pocket (its cells may then regrow).
  - New: `CosmicDustVehicleReservationController.ReleaseStarKeepClear(requestRegrow)` and `CosmicDustGenerator.ReleaseStarKeepClear()`.
  - `CosmicDustCorridorReservationController.ReleaseCorridors(requestRegrow)` gained the `requestRegrow` parameter.
  - Ship keep-clear pockets and trails are live claims and are untouched.
- **Verify:** the `[MAZE:DIAG] interstitial-built … claimsByOwner=(…)` line (A5) should no longer list `PhaseStarPocket` or `Corridor`.

### A2. Vault shell cells couldn't grow or be repaired under a claim — **FIXED**
- Both `GrowVaultCells` and `RepairVaultCells` go through `SpawnDustAtCells`. That function skipped any claimed cell, and `VoidGrowCellNow` aborted on a claim at the start or the end of its grow-in.
- A shell cell under **any** claim was therefore never filled, and the 0.25 s repair loop skipped it for as long as the claim lasted. That is a permanent hole in a "sealed" shell. Possible claims: a ship's keep-clear pocket or trail, a travel corridor, collectable occupancy, or the Star pocket.
- `TryPickVault` only checked claims at planning time.
- **Fix:** standing vault shell cells (`IsSealedVaultShell`) ignore claims in `SpawnDustAtCells` and both `VoidGrowCellNow` checks. A ship physically on the cell still stops the grow-in, and the next repair tick retries.

### A3. The travel corridor ran through the vault — **FIXED**
- `SceneFlowCoordinator` sets a straight lane (Bresenham line) from the entry to the Star. That line ignored the vault, so it claimed shell cells (feeding A2) and promised a route through an uncarveable wall.
- **Fix:** `CosmicDustCorridorReservationController` takes an exclusion predicate (`IsRecordVaultCell`) and never claims chamber or shell cells.

### A4. The hatch opened for ships scraping the outside — **FIXED**
This most likely explains "the trap was open, allowing me to enter".
- `CheckShipsInsideVault` treated a ship as inside when its centre cell was *any* shell cell. A ship whose centre rounded onto an **outer** shell cell triggered it, for example:
  - scraping the vault;
  - during the grow-in, while colliders are off;
  - a recovery glide.
- The trigger then opened a hatch from that side straight into the chamber, and kept it open while the ship sat in it.
- **Fix:** the hatch counts a ship as trapped only in the **chamber**, or **buried** in the shell (a shell cell with no neighbour outside the vault, `IsBuriedVaultWall`). While a hatch is open, a ship still standing in it counts as leaving. Every opening logs `[VAULT] SHIP INSIDE`.

### A5. No diagnostics for the interstitial — **FIXED**
- `LogMazeDiag` now also runs for the interstitial (`[MAZE:DIAG] interstitial-built`) and appends `claimsByOwner=(owner=cells, …)`, using the new `DustClaimManager.TallyClaimedCellsByOwner`.
- The repair loop warns once per cell: `[VAULT] Shell cell … still empty after 8 repair ticks: state, permanentClear, claimed, shipOnCell, voidGrowing`.

---

## B. Inconsistencies between the patterns and the vault

| # | Item | Status |
|---|---|---|
| B1 | **Patterns ignored the vault.** The vault was reserved before the pattern ran, so each builder laid out its full structure and then lost the vault's cells one by one. Tunnels corridors dead-ended in the shell, Rings' hex-BFS distance field bent around it, ClearBoxes boxes got clipped, and CA counted the vault as empty neighbours, which eroded a moat around it. When the vault exploded it left a disk-shaped hole in whatever structure was there. | **FIXED** via §C: PatternAnchor placement plans the vault *after* the pattern has computed its walls and scores spots against them. Edge-placed patterns (FullFill, DrunkenStrokes) keep the old order. |
| B2 | **The bottom edge has no wall.** `BuildPorousBorderPerimeter` / `BuildPorousBorderCells` wall only the top, left and right edges, so a vault against the bottom edge wasn't fused to anything. | **FIXED** (user decision): edge placement uses the top, left and right only. The border is unchanged. |
| B3 | **The vault ignored the screen filter.** Maze cells outside the gameplay frame are never grown (`IsWorldPositionInsideScreen`), but the vault was only bounds-checked against the grid. That made it partly invisible, and it could "touch the edge" off screen. | **FIXED**: `VisibleGridBounds` sets both the edge lines and the fit check. |
| B4 | **Diagonal leaks in a 1-cell round shell.** The shell was an annulus of radius r..r+1 on a square grid. With r=2 the chamber cell (2,0) touched the outside cell (3,1) diagonally, between two shell circles about 0.41 cell apart. | **FIXED**: every 8-neighbour of the chamber is shell, and the shell is a Euclidean dilation of the chamber. |
| B5 | **Dead code in `VoidGrowCellNow`.** The "keep-clear at end → visual only" branch could never run: `IsKeepClearCell`, `IsDustSpawnBlocked` and `dustClaims.IsBlocked` are the same lookup, so the claim veto had already aborted. | **FIXED**: removed, along with the duplicate checks in `SpawnDustAtCells`. |
| B6 | **Orphaned XML doc** for a deleted Voronoi role-assignment method in `CosmicDustMazePatterns.cs`. | **FIXED**: removed |
| B7 | **`MazeArchetype`** (top of `CosmicDustMazePatterns.cs`) is a compat stub still read by `PhaseTransitionManager.currentPhase`, `BridgeCoordinator` and `MotifSnapshot`. | **OPEN** (stub debt; out of scope) |
| B8 | **Motif mazes keep porous-border gaps.** Every motif maze sets `porousBorder.exitCount` to 3 or 6, but only the interstitial reads the gap groups. In a motif maze the gaps are just openings in the top, left and right border, presumably for boundary wrap. | **KEEP** (behaviour unchanged); worth a deliberate decision |
| B9 | **Boundary wrap next to an edge vault.** A ship wrapping in through the edge the vault touches arrives on the shell's outer layer. With A4 fixed that no longer opens a hatch; physics pushes the ship out. | **OPEN**: watch in play |
| B10 | **Rings' hex BFS** treats reserved ship and Star disks and preinjected trap cells as obstacles, so rings bulge around them. This predates the vault. | **KEEP** |

---

## C. Vault shapes per maze pattern

**New:** `MazePatternConfig.recordVault` (`VaultShapeParams`): `shape`, `placement`, `geodeRoughness01`, `capsuleLength`. Both enums default to **Auto**, so every existing maze asset uses the table below with no asset edits.

| Pattern | Auto shape | Auto placement | How it reads |
|---|---|---|---|
| Tunnels | **RoomCell**: a square room in a square shell | PatternAnchor: chamber in open space, shell on the walls | A locked room in the lattice. The explosion opens it onto existing corridors. |
| ClearBoxes | **Box**: the maze's own clearing outline and aspect (rectangle / ellipse / diamond / plus with stubby arms) | PatternAnchor: chamber in a clearing, shell on the walls | "The locked clearing." |
| RingChokepoints | **Ring** | PatternAnchor: shell on the ring walls, preferring far from the Star | Buried in an outer ring wall. The explosion opens a door in the ring. |
| DiagonalLanes | **Capsule**: long axis along the lanes (`slopeX/slopeY`) | PatternAnchor: chamber between lanes | Lies in the current instead of cutting across lanes. |
| CellularAutomata | **Geode**: ragged chamber | PatternAnchor: chamber and shell inside the rock | A crystal pocket in the rock (and no erosion moat). |
| DrunkenStrokes | **Geode** | Edge | An organic pocket at the edge of the field. |
| FullFill | **Ring** | Edge | Unchanged. |

**How it works (`CosmicDustGenerator.RecordDock.cs`).**
- **Template.** A shape becomes a translation-invariant `VaultTemplate` made of chamber, shell and approach offsets.
  - The shell is every cell within `thickness` (Euclidean) of the chamber, plus the chamber's 8-neighbours.
  - Thickness is still `ceil(nodesPerStar / 4)`, clamped 1..4.
  - Every shape contains the round chamber of radius `recordDockRadiusCells` that the record drifts in.
- **Picking a spot.** `TryPickVault` slides the template across the visible grid.
  - **Edge:** the bounding box touches the top, left or right visible edge.
  - **Interior:** touches no edge.
  - **PatternAnchor:** each spot is scored 0..1 by `VaultAnchorPrefs`: chamber open or solid in the pattern, shell on pattern walls, distance from the Star. The pick is random among spots within 0.08 of the best.
- **Fallbacks.** Ship distance is dropped first, then placement falls back to Edge, then to anywhere. The post-maze dock keeps the shape but always uses Edge.
- **Shape-agnostic parts.**
  - The retint ripple uses BFS depth from the chamber instead of radius.
  - The explosion chain, the hatch and the repair loop were already generic.

**Tuning:** `[VAULT] Planned <shape> vault (<placement>[, fallback]) at … score=…` logs each choice. A low score (< ~0.5) on PatternAnchor means the pattern rarely offers a good spot at this size. Shrink `recordDockRadiusCells`, or set `placement` explicitly on that maze.

---

## D. Playtest checklist
- **Motif → interstitial → next motif:**
  - `claimsByOwner` should show only ship entries (`Vehicle…`, `Trail…`).
  - No empty corner.
  - The vault is closed.
  - Driving along and scraping the shell never logs `[VAULT] SHIP INSIDE`.
- **Each pattern family:** check that the Auto shape and spot read as intended, and look at the explosion's hole in the maze (Tunnels and Box should open onto corridors and rooms).
- **Thickness:** `nodesPerStar` 4, 8, 12 and 16 (shell 1–4). Watch for `[RECORD] No room for the … vault` (this falls back to the post-maze dock).
- **Quick Star motif** (pocket radius 5): no empty disk where the Star died.
- **Vault holes:** any `[VAULT] Shell cell … still empty` warning names the blocker.
