# Puzzle Design Guide: Mazes × Motifs (2026-11-25)

**What this is.** A designer's guide to the puzzle that a `MotifProfile` and its `MazePatternConfig` create together. **Platform Zero** (Somnambulist Station) is the worked example throughout.

- Each layer explains what the knobs do to play.
- §9 estimates how many ways there are to solve a motif.
- §10 lists recipes.
- File and line references are collected in the appendix.

**Companions:**
- `CONFIG_AUDIT.md`: where to tune a given value.
- `ECOSYSTEM.md`: how the systems connect.
- `DUST_LIFECYCLE_AUDIT.md`: the dust state machine.
- `MAZE_VAULT_AUDIT.md`: vault shapes.
- `RECORD_RECOVERY_STATUS.md`: the record and its vault.

**Confidence.** Every number below was read from the asset YAML or the code on 2026-11-25. Items marked **(est.)** are derived, not observed. Confirm them in a playtest with `[MAZE:DIAG]` and `[VAULT] Planned`.

---

## 0. The puzzle in one paragraph

The motif grows a maze of dust. The **pattern** decides where the walls are. Each wall cell secretly belongs to one of the motif's **roles** (Bass, Harmony, …) or is grey **filler**, and it shows that role only as a darkened, latent tint.

1. The player boosts to **carve** dust. Carving reveals the cell's role.
2. A carved cell regrows 3 s later with a **flower bud**.
3. Each role has a **Star** that can only eat **bloomed cells of its own role**. Its tendrils show how many more it needs: one per note it will release.
4. A fed Star ejects a **discovery node**.
5. Catching the node releases the notes, and carrying them home fills one **bin** of that role's ring.
6. When a ring is complete, that role's dust hardens into impassable **Cosmic Blocks**, so the maze changes shape.
7. When the motif's shared **harvest budget** (`nodesPerStar`) is spent, the record's sealed **vault** explodes.
8. The player collects the record and picks an **exit**.

The puzzle is a routing and scheduling problem:
- **which** role to feed next,
- **where** its dust is,
- **how** to get there through walls you can cut,
- and what the maze will look like **after** that role walls off.

---

## 1. Platform Zero at a glance

`Assets/Resources/Profiles/Motifs/Somnambulist Station/Platform Zero.asset`

| Field | Value | Puzzle effect |
|---|---|---|
| `bpm` / `stepsPerLoop` | 175 / 16 | Fast loops: about 1.37 s each. Discovery expiry ("3 loops") is therefore only **about 4.1 s**. |
| `keyRootMidi` | 60 (C) | None (musical only). |
| `nodesPerStar` | **6** | The harvest budget, shared across all roles. It also sets vault shell thickness ⌈6/4⌉ = **2**. |
| `starBehavior` | Drifting Star | Slow (drift 0.5, hungry 1.1), keep-clear radius 3, jitter 0.2. |
| `mazePattern` | Tunnels – Boulevards | See §2. |
| `roleGeoConfigs` | *(empty)* | Territory comes from defaults (§3). |
| `genericFillRatio01` | 0 | The maze's floor of **0.10** applies, so 10% of the dust is grey filler. |
| `chordProgression` | Uneasy – Mirror (i-V-i-V) | None (musical only). |
| `alternateChordProgressionProfile` | Sad – Resolve (i-iv-V-i) | Enables the **SuperNode** branch (§6). |
| `intensityClipTags` | 6 clips on the diagonal (0, .2 … 1) | Carving moves one ladder of intensity (§7). |
| `entryLoopCount` | 1 | The drums wait one loop and then for the first carry. |
| `spawnVehicleTrap` | off | — |
| `roleNoteConfigs` | 5 configs, 4 roles (below) | Defines the roles, their order, and how many notes each harvest needs. |

**Roles, in list order.** List order matters only on Tunnels (§3).

| # | Role | Profile | Carve resistance | Selection | Riff (notes per bin) |
|---|---|---|---|---|---|
| 0 | **Bass** | Finger Bass | **0.78** (hardest) | RandomPerBin, 1 config | bass-bounce: **3** |
| 1 | **Rhythm** | Jazz Keys | **0.10** (softest) | ByVoice, 1 config, so 1 voice | groove-bounce: **3** |
| 2 | **Harmony** | Still Sky | **0.475** | RandomPerBin, 1 config | harmony-bounce: **2** |
| 3 | **Lead** | Palm Guitar | **0.25** | **ByBin, 2 configs** | lead-bounce uses `lead-16`: **16**; lead-bounce-alt: **1**. Alternates 16 / 1 / 16 / 1. |

Things to notice:
- **Lead costs the most and swings the most.** Its first bin needs 16 zaps; its second needs 1. That makes Lead a toll you pay once to unlock a cheap follow-up (see §9).
- **Rhythm is the softest dust (0.10), and Bass is the hardest (0.78).** Bass also pushes the drums twice as hard as any other role (§7).

---

## 2. Layer 1: Walls (Tunnels – Boulevards)

`Assets/Resources/Mazes/Tunnels - Boulevards.asset`, `patternType: Tunnels`.

| Knob | Value | Meaning |
|---|---|---|
| `corridorStep` | 6 | Room-to-room pitch. |
| `corridorWidth` | 5 | Corridors and rooms are **5 cells wide**, so walls are 6 − 5 = **1 cell thick**. |
| `straightness01` | 0.8 | The DFS carver usually keeps its heading, which gives long straight boulevards. |
| `braid01` | 0.3 | Each dead-end room has a 30% chance of being knocked through to a neighbour, creating a loop. |
| `plazaChance01` | 0 | No oversized chambers. |
| `porousBorder.exitCount` | 2 | Openings in the top, left and right border walls. Nothing uses them in a motif maze (MAZE_VAULT_AUDIT B8). |
| `minGenericFillRatio01` | 0.1 | The filler floor (§3). |

**Room lattice.**
- The play grid is **40 × 18** cells: DrumTrackConfig sets 40 reference columns, and the usable height is 1080 − 160 − 52 = 868 px at 48 px per cell.
- Tunnels lays rooms on a ⌊40/6⌋ × ⌊18/6⌋ = **6 × 3 = 18-room** lattice.
- The carver builds a spanning tree (17 passages), then braids it.

Simulated over 300 seeds with the real algorithm (est.):
- about **3.0 dead ends**
- about **0.85 extra loops** (0 loops 38%, 1 loop 42%, 2+ loops 20%)
- about **1.8 routes** between an average pair of rooms

```
 #####################################   <- top border wall (porous gaps)
 #.....#...........................#..
 #.....#...........................#..   5-wide boulevards,
 #.....#.....#######.....#.....#####..   1-cell walls (#)
 #.....#.....#.....#.....#.....#......
 #.................#.....#............
 #######.....#######.....#######......
 ......#...........#..................   (no bottom border wall)
```
*(Illustrative only; every seed differs.)*

**Why it matters.**
- **Thin walls make the maze advisory, not absolute.** One 1-cell wall separates every pair of neighbouring rooms, and there are 27 − 17 = **10 unused neighbour pairs** in a typical layout. Each is one short boost away from being a shortcut.
- The cost of that shortcut is set by **whose dust the wall is** (§4). A Rhythm wall (0.10) is almost free. A Bass wall (0.78) takes about 4 × as long to cut as a Rhythm wall, and its hull drag burns fuel about 3 × faster. The real maze is therefore the pattern **weighted by role resistance**.
- **Stale field:** `dustTiming.regrowDelay: 4` is still serialized in this asset, but the code no longer has the field. Regrowth is a constant 3 s (§4).

---

## 3. Layer 2: Territories (who owns which wall)

After the walls are laid, `BuildMazeRoleImprintsRoutine` paints every wall cell with a role or with filler. Platform Zero takes the default path:

1. **Filler first.** Effective ratio = max(motif `genericFillRatio01`, pattern `minGenericFillRatio01`) = max(0, 0.10) = **10%**.
   - It is scattered at random across all wall cells.
   - It uses its own grey (`genericFillTint`) and has carve resistance 0.
   - It is **permanent once cleared**: it never regrows.
   - It gives free holes in the walls and is never fed to a Star.
2. **Voronoi territories.** Each role gets seeds placed by its **geography feature**, and every remaining wall cell goes to the nearest seed. Tunnels has a special rule: **the first-listed role gets Ridge**, which is 2 seeds at opposite ends of the long axis. Every other role gets a feature from its **density rank**:

   *weight = (role's average notes per bin ÷ motif mean) ÷ 2, clamped to 0..1*

   - weight ≥ 0.66 → **Continent** (one sprawling territory)
   - weight ≤ 0.33 → **Island** (one compact cluster)
   - anything between → **Archipelago** (2–8 scattered footholds)

| Role | Average notes per bin | Weight (est.) | Feature (est.) | What the player sees |
|---|---|---|---|---|
| Bass | 3 | 0.36 | **Ridge** (Tunnels, index 0) | Two hard Bass ends at the far left and far right of the screen. |
| Rhythm | 3 | 0.36 | Archipelago | Soft Rhythm patches scattered around. |
| Harmony | 2 | 0.24 | Island | One compact Harmony cluster. |
| Lead | (16+1)/2 = 8.5 | 1.0 (clamped) | **Continent** | Lead owns the biggest single block of the maze. |

*Motif mean = 16.5 / 4 = 4.125. The estimate assumes the Lead bins alternate evenly. The real figure is `NoteSet.persistentTemplate.Count` per bin.*

**Design reading.**
- **The biggest territory belongs to the role with the most notes, not the first-listed role.** The `MotifProfile.cs:261` comment ("first role … largest Voronoi territory") is wrong; see §11.
- In Platform Zero, Lead's Continent puts its 16-zap harvest in the largest field of carveable, low-resistance (0.25) dust. That is easy to feed but slow to fill.
- The Bass Ridge splits the hardest dust across the two far ends. Feeding the Bass Star means a long boulevard run to one side.

**Override.** Add `roleGeoConfigs` entries to pin a feature per role. You can also use exact-count mode (`useExactNoteCount`, `notesToCellsMultiplier`, `minCellCount`), as Switchyard does, to size territories to note counts. In exact-count mode, unclaimed cells become filler.

---

## 4. Layer 3: Carving (paying to move)

- **Only boosting carves.** The plow is a forward rectangle that chips each cell `plowChipAmount` per tick. Boosted hull contact chips at the same rate. When not boosting, the ship only bumps and plucks a chord.
- **Each chip** adds `chip × (1 − resistance)` to a cell. Every cell has **1 HP**, so time to break a cell ∝ 1/(1 − resistance):

| Role | Resistance | Relative time to carve |
|---|---|---|
| Filler | 0.00 | 1.0× |
| Rhythm | 0.10 | 1.1× |
| Lead | 0.25 | 1.3× |
| Harmony | 0.475 | 1.9× |
| Bass | 0.78 | **4.5×** |

- **Fuel.** Hull drag drains energy at `energyDrainPerSecond × lerp(1, 0.1, drainResistance) × lerp(1, 5, resistance)`. Hard dust costs time and fuel.
- **Reveal.** A carved cell drops its latent tint and shows its true role colour. Carving is also how you **scout**: territories are only hinted at until you cut into them.
- **Regrow.** Cleared role cells come back after a **constant 3 s**, carrying a **flower bud**.
  - Only plow and bomb-blast clears bloom; other clears regrow without one.
  - Filler never regrows.
  - The per-role `regrowthDelay` values (Bass 8, Harmony 2, Rhythm 4, Lead 1) are **stale data**. No code reads them.

**Design reading.** Carving is the "spend" verb, and it does two jobs: cutting a path, and making food for Stars. A carve through a Rhythm wall opens a shortcut *and* plants Rhythm buds. A carve through filler opens a shortcut that stays open forever, but plants nothing.

---

## 5. Layer 4: Stars (who you are feeding)

- **One Star per active role**, so Platform Zero has 4 on screen. A role is skipped while it is walled, mid-sequence, or out of budget.
- **The Drifting Star personality:** drift 0.5, hungry 1.1, jitter 0.2. It is slow, so the player usually brings food to it rather than chasing it.
- **Navigation.** Tunnels with nav mode Auto resolves to **CorridorFollow**: a BFS through open cells, looking 2 cells ahead. The Star travels the boulevards and does not cut walls.
- **Keep-clear pocket.** The Star holds a dust-free disk of radius **3** around itself. In 5-wide corridors that almost fills the corridor, so a Star parked in a room makes the room an open plaza.
- **Feeding rule.** A Star eats only **bloomed cells of its own role**. Its required zap count is the number of notes in its role's **next bin** (`PhaseStar.ZapState.cs:276-300`). Tendrils (buds) show how many it still needs.
- **The budget.** `nodesPerStar` = 6 successful harvests, shared by all roles. Expired, escaped, empty and SuperNode outcomes **refund** their slot. Only one ejection can be in flight at a time.

**Design reading.** Each Star is a **demand** sitting at a location. The player's carving produces **supply** at other locations. Bloomed buds are only useful near the Star that wants them, so good play carves *the right role's walls near the right Star*.

---

## 6. Layer 5: Capture and its consequences

- **Discovery node.** A fed Star ejects a node, which expires after `mineNodeExpireAfterLoops` = **3 loops**. At 175 bpm that is about **4.1 s**, a tight window. An expired node refunds its budget slot.
- **Regrowth hold.** Once a Star's zaps are met, its role's regrowth is held. If the node is **captured**, the role stays **suspended**: cleared cells of that role do not regrow while it is suspended. Holes you cut stay open for a while.
- **Bins and rings.** Each successful harvest fills one bin. The cap is `maxLoopMultiplier` = **4** bins per role.
- **Ring complete → wall.** When a role's ring completes, that role's dust becomes impassable **Cosmic Blocks**.
  - In Boulevards, where every wall is 1 cell thick, a ring completion turns that role's shortcuts back into **hard walls**.
  - Exception: this does not happen if no other role can still make progress.
  - Filling Harmony's Island is local. Filling Lead's Continent can reshape most of the map.
- **Lead's alternation.** `GetConfigForRoleAtBin` hands Lead bin 0 → lead-bounce (16 notes) and bin 1 → lead-bounce-alt (1 note). The first Lead harvest is the most expensive action in the motif; the second is the cheapest.
- **SuperNode.**
  - **Trigger:** a Star's target track is already full (4 bins), and `alternateChordProgressionProfile` is set.
  - **Effect:** the Star ejects a SuperNode instead, whose shards fan out to other roles that aren't full yet.
  - **Payoff:** catching every shard ends the motif early. Once every track is full, harmony switches to i-iv-V-i.

---

## 7. Layer 6: Drums as feedback

- **Carve energy.** Each carve adds weight **1.0 for Bass** and **0.5 for every other role**. The weights feed a leaky accumulator.
- **Axes.** That energy drives **pressure**. **Depth** comes from the leader loop multiplier and **breadth** from the share of filled bins, both scaled by intensity.
- **Clip choice.** `ResolveNearestIntensityClip` picks the nearest tag.
- **Platform Zero's ladder.** All 6 tags sit on the diagonal (d = b = p), so the motif behaves as **one intensity ladder** rather than three axes. Nothing moves until the first carry.

**Design reading.**
- The drums are the motif's in-world progress tell.
- Cutting Bass walls is expensive, but it is the fastest way to "turn the music up".
- Off-diagonal tags would let different play styles (heavy carving vs. many bins) sound different.

---

## 8. Layer 7: Vault and exit

- **Shape.** Tunnels with vault shape Auto gives **RoomCell**: a square chamber `|dx|,|dy| ≤ recordDockRadiusCells` (2), so **5 × 5**, which is exactly one Boulevards room.
- **Shell.** It is ⌈6/4⌉ = **2 cells** thick, giving a footprint of about 9 × 9 (est.). The shell is wider than a 1-cell Boulevards wall, so the vault **eats about one cell into each neighbouring corridor**.
- **Placement.** PatternAnchor puts the chamber in an open room with the shell on walls, preferring spots far from the Stars.
- **Seal.** Shell cells are `Solid`: no clear path can touch them. The hatch opens only for a ship already inside.
- **Release.** When the budget is spent:
  1. walls are released;
  2. regrowth hard-stops;
  3. the final ring lands;
  4. the shell retints and explodes, opening the room onto the boulevards.
- **Exit.** After the record is collected, the bridge builds an interstitial Tunnels maze with one exit per direction. A single **wisp** heads for an unexplored exit. Touching it refuels the ship and plays the next motif's bridge sample.

---

## 9. Solutions: how many ways to solve it

A "solution" is a run that spends the budget. Failed harvests refund their slot and change nothing, so they don't count. The estimate below is a stack of four layers. Each layer multiplies the one above it, and only the first two are strategic choices.

### A. Strategic: which role, in what order

N = `nodesPerStar` harvests across R active roles, each role capped at c = `maxLoopMultiplier` bins. Only one ejection is in flight at a time, so harvests form an **ordered sequence**:

```
W(N, R, c) = N! · [xᴺ] ( Σ_{i=0..c} xⁱ / i! )ᴿ        (words of length N, no letter more than c times)
A(N, R, c) = [xᴺ] ( Σ_{i=0..c} xⁱ )ᴿ                  (allocations: how many harvests each role gets)
```

For Platform Zero (N = 6, R = 4, c = 4):
- W = 4⁶ − 4·(C(6,5)·3 + C(6,6)) = 4096 − 76 = **4,020 ordered sequences**.
- A = C(9,3) − 4·C(4,3) = 84 − 16 = **68 allocations**.
- The SuperNode branch (§6) adds endings that don't fit this count. When it is enabled, treat W as a lower bound.

### B. Work: what each sequence costs to feed

Feeding cost = Σ (notes in that role's next bin). With the per-bin note counts Bass 3, Rhythm 3, Harmony 2 and Lead 16 / 1 / 16 / 1, here is how the 4,020 sequences break down (computed):

| Lead harvests in sequence | Zaps to feed | Share of sequences | Cheapest / priciest |
|---|---|---|---|
| 0 | **14–18** | 17% | 14 = 4× Harmony + Bass + Rhythm (or 4× H + 2× B, or 4× H + 2× R) |
| 1–2 | **25–31** | 64% | median 29 |
| 3–4 | **38–42** | 19% | 42 = 3× Lead + three of Bass/Rhythm |

The solution space splits into **three bands**, and Lead's 16-note opener is the dividing line. Notes on the bands:
- **Second Lead harvest.** It costs 1, so once a player has paid the toll, a second Lead harvest is the best value in the motif.
- **Cheap band trade-off.** It fills Harmony's ring, which walls Harmony off. Because Harmony is the Island, that hardly matters, which is why the cheap band is genuinely cheap here.

### C. Navigation: routes between the places you must reach

Rooms = ⌊W_grid/step⌋ × ⌊H_grid/step⌋, so Boulevards gives 6 × 3 = 18 rooms. Over the 6 × 3 room graph (computed):

| Case | Loops (k) | Routes per room pair |
|---|---|---|
| Classic DFS (no braid) | 0 | exactly **1** |
| **Boulevards** (straight .8, braid .3) | about 0.85 | about **1.8** |
| Long Hallways–style (.95 / .1) | about 0.3 | about 1.25 |
| Braided Loops–style (.2 / .8) | about 2.8 | about 5.0 |
| Every wall carved open (complete 6 × 3 grid) | 10 | mean **189**, max 415 (corner to corner: 414) |

Routes grow roughly as 2ᵏ, with k ≈ `braid01` × (number of dead ends). Because Boulevards walls are 1 cell thick, carving moves the real figure from the braided row toward the bottom row. Resistance sets how far: soft roles and filler open up, Bass stays mostly closed.

### D. Feeding: which bloomed cells

There are C(T, Z) ways to choose Z bloomed cells out of a territory of T cells. The number is astronomically large but carries no strategy, so it is **excluded**.

### Headline

```
Distinct solutions  S ≈ W(N,R,c) · ρ^(L·N)
   ρ = mean routes per leg (§C),  L = legs per harvest (≥ 1: reach food → reach Star → chase node → carry home)
```

**Platform Zero:**
- **Floor** (no carving, L = 1): 4,020 × 1.8⁶ ≈ **1.4 × 10⁵**.
- **Realistic** (L = 2, light carving, ρ ≈ 3): 4,020 × 3¹² ≈ **2 × 10⁹**.
- **Ceiling** (walls fully carvable, L = 1): 4,020 × 189⁶ ≈ **2 × 10¹⁷**.
- The **strategic core** is small and readable: **68 allocations in 3 cost bands**. That is the part a player actually plans around.

### How the knobs scale each layer

| Knob | Layer | Effect |
|---|---|---|
| `nodesPerStar` (N) | A | Grows W roughly as Rᴺ, and thickens the vault shell. |
| Number of roles (R) | A | Same: raises the base. Each role also adds a Star and a territory. |
| `maxLoopMultiplier` (c) | A | Only matters once c < N. Lower c forces you to spread across roles. |
| Riff note counts | B | Widen or narrow the bands. One heavy role creates a toll gate (Lead here). |
| Carve resistance | C | Decides which walls are really walls. |
| `braid01` | C | Increases loops k, so ρ ≈ 2ᵏ. |
| `straightness01` | C | Fewer dead ends, which means fewer braid opportunities and longer sightlines. |
| `corridorStep − corridorWidth` | C | Wall thickness. 1 = carvable maze (Boulevards); 2+ = real maze (Braided Loops). |
| `corridorStep` | C | Room count ≈ (40/step) × (18/step). |
| `genericFillRatio01` | C | Permanent free holes, so walls leak. |
| `mineNodeExpireAfterLoops`, bpm | — | Execution difficulty, not solution count: they shorten the capture window. |

**Caveats.** This model assumes that each successful harvest fills exactly one bin, in order 0 → 3, and that a ring completion doesn't take a role out early. Grid rows (18) and dead-end counts are estimates; check them with `[MAZE:DIAG]`. Scratch script: `solutions.py` in the session scratchpad, not checked in.

---

## 10. Difficulty levers and recipes

### Platform Zero vs. its siblings

| | **Platform Zero** | **Switchyard** | **Last Light Boogie** |
|---|---|---|---|
| bpm | 175 | 160 | 75 |
| `nodesPerStar` | 6 | 5 | 6 |
| Roles | 4 (B, R, H, L) | 5 (B, R, H, L, Groove); **Rhythm has 4 ByVoice configs, so 4 Rhythm voices** | 4 (B, R, H, L) |
| Maze | Boulevards (6/5, walls 1, braid .3) | Long Hallways (4/3, walls 1, braid .1, about 40 rooms) | Braided Loops (5/3, **walls 2**, braid .8, about 24 rooms) |
| Territory | Defaults (Ridge + density rank) | **Exact-count**, 20 cells per note, minimum 25 | Defaults |
| Filler | 10% (floor) | **33%** | 10% (floor) |
| Star | Drifting (slow, pocket 3) | **Quick** (drift 10, pocket 5) | Drifting |
| Character | Wide roads, paper walls, one toll role | Long hallways, a Star you chase, leaky grey walls | Slow tempo, real walls, many loops |

### Recipes

- **Harder Platform Zero.** Switch to *Tunnels – Braided Loops*. Walls become 2 thick and stop being shortcuts, so routes actually matter. Keep `braid01` high so there are still about 5 routes per pair.
- **Remove the toll.** Replace `lead-16` with an 8-note riff. The bands merge and Lead stops dominating strategy. (It also shrinks Lead's Continent, since density rank uses note counts.)
- **Make Bass the hub.** Add `roleGeoConfigs { Bass: Continent }`. The hardest dust now dominates the map, so routes curve around it.
- **Leaky maze for new players.** Set `genericFillRatio01 = 0.3`. Carved grey holes never close, so the maze opens up permanently as you play.
- **Shorter, denser motif.** Set `nodesPerStar = 4`: W drops to 4⁴ = 256, and the vault shell thins to 1.
- **Longer capture window at speed.** Raise `mineNodeExpireAfterLoops` on each NoteSetConfig to 5–6. At 175 bpm, 3 loops is only about 4 s.

---

## 11. Known drift and stale fields (found while writing this)

| Where | What | Truth |
|---|---|---|
| `Platform Zero.asset` | `genericFillNeverRegrowRatio`, `genericFillRegrowSoftenMultiplier01` | No longer in the class. Harmless leftover YAML; it disappears on reserialize. |
| `Tunnels - Boulevards.asset` | `dustTiming.regrowDelay: 4` | The field is gone. Regrowth is a constant 3 s (`CosmicDustGeneratorConfig.regrowDelaySeconds`). |
| Role profiles | `regrowthDelay` (8 / 2 / 4 / 1) | Not read anywhere since the 11-25 dust lifecycle pass. |
| `MotifProfile.cs:261` | "first role … gets the largest Voronoi territory" | False. Size comes from the feature: density rank, or Ridge for index 0 on Tunnels. |
| `MotifProfile.cs:92` | `driveBeatsFromDensity` tooltip says "dust density" | Intensity is carve energy now. |
| `CosmicDustGeneratorConfig.cs:94` | Filler only comes from exact-count mode | It also comes from the universal ratio and the pattern floor. |
| `CosmicDustGenerator.RecordDock.cs:15-17` | Vault at "a random edge spot" | Placement now follows the pattern (PatternAnchor). |
| ECOSYSTEM.md (≈166, 269, 460-505, 609, 710, 839-871) | Per-role regrow delays, `PermanentlyClearOneCellForRole`, "wisps" (plural) | Constant 3 s, a removed method, and a single wisp. |
| CONFIG_AUDIT.md:43, 107 | Lists "regrowth" as a per-role setting | Not per-role any more. |

---

## Appendix: where each rule lives

Paths are relative to `Assets/Scripts/`.

| Rule | Location |
|---|---|
| Tunnels generator | `Dust/CosmicDustMazePatterns.cs:748-930` |
| Pattern dispatch, border | `Dust/MazeTopologyService.cs:24-189` |
| Nav and vault Auto tables, filler floor | `Dust/MazePatternConfig.cs:70-84, 411-434, 483` |
| Geo feature resolution | `Dust/CosmicDustGenerator.MazeGeneration.cs:27-96` |
| Role imprint pipeline | same file `:246-340` (filler `:453-479`, exact-count `:352-405`, Voronoi `:588-942`, Ridge `:746-767`) |
| Density weights | `Dust/MotifNoteDensityAnalyzer.cs:74-108` |
| Role and config selection | `Music/Motif/MotifProfile.cs:168-336` |
| Plow and carve | `Gameplay/Core/Vehicle.Plow.cs:150-228`, `Dust/CosmicDust.Collision.cs:44-63` |
| Chip, reveal, regrow delay | `Dust/CosmicDustGenerator.CellLifecycle.cs:96-177, 300-317` |
| Flower bloom sources | `Dust/CosmicDustGenerator.cs:524-526` |
| Star eats only bloomed cells | `Dust/CosmicDustGenerator.cs:925-931` |
| Zap count = notes in next bin | `Phase/Star/PhaseStar.ZapState.cs:255-304` |
| Star spawning, budget | `Phase/Star/StarPool.Spawning.cs:33-93`, `StarPool.EjectionEvents.cs:140-220` |
| Regrowth hold and suspend | `Dust/CosmicDustGenerator.RoleRegrowth.cs`, `PhaseStar.ZapState.cs:95`, `PhaseStar.EjectionLogic.cs:193` |
| SuperNode | `Phase/Star/PhaseStar.EjectionLogic.cs:316-395` |
| Ring → Cosmic Blocks | `Dust/CosmicBlockField.cs:86-154`, `CosmicDustGenerator.Blocks.cs:78-107` |
| Drum intensity axes | `Dust/CarveEnergyAccumulator.cs`, `Music/DrumTrack.BeatIntensity.cs:80-225` |
| Vault size, shape, release | `Managers/GameFlowManager.RecordRecovery.cs:66, 167-171, 439-509`, `Dust/CosmicDustGenerator.RecordDock.cs:382-385` |
| Grid size | `Music/DrumTrackGridMapper.cs:428-460`, `Resources/Config/DrumTrackConfig.asset` |
| Exit, wisp | `Managers/PathChoiceCoordinator.cs:118-348`, `Dust/Wisp.cs:350-446` |
