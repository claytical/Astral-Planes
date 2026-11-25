# Record Recovery — Status

Last updated 2026-11-25 (v2: sealed vault; per-pattern shapes and the open-vault fixes in `MAZE_VAULT_AUDIT.md`). Everything below compiles (msbuild, outside Unity). **None of it has been playtested.** Nothing is committed.

Original design assessment: `~/.claude/plans/pasted-content-id-5a27-astral-toasty-whisper.md` (P0–P4 roadmap, §8 decisions).

## What the feature does (co-op / solo; competitive is unchanged)

**v2 (2026-11-24): the vault is sealed.** It replaced the gated key.

1. **Maze generation** plans a sealed vault before the maze is laid out, builds it first, docks the record inside, then grows the maze around it.
   - Shape: a chamber (it always contains a round room of radius `recordDockRadiusCells`) inside a **shell** of Solid dust.
   - **Per maze pattern (2026-11-25):** `MazePatternConfig.recordVault` picks the shape and placement (Auto: Tunnels → RoomCell, ClearBoxes → Box, Lanes → Capsule, CA/Strokes → Geode, Rings/FullFill → Ring). PatternAnchor placement is planned after the pattern's walls are computed and scored against them. Edge placement touches the top, left or right **visible** edge, never the bottom. Details: `MAZE_VAULT_AUDIT.md` §C.
   - Thickness: `ceil(nodesPerStar / 4)`, clamped 1..4 (4 → 1, 8 → 2, 12 → 3, 16 → 4).
   - Look: the shell starts in the **inert-block colour** (`CosmicDustGeneratorConfig.inertBlockTint`). That is the one colour for uncarveable, role-less dust: interstitial walls, resealed exits and the vault. It is in Cosmic Block form (big particles, never star food).
2. **The record is trapped.**
   - The record drifts inside its chamber and lunges at the shell every few seconds; the shell cells it hits flash white.
   - Only the next-up placeholder ring strains outward.
   - That ring lerps between the colours of the roles that could fill it now: active roles with an unfilled bin, minus the role currently walled into Cosmic Blocks.
3. **Intro:** once the maze has grown, the camera zooms in on the record, holds, and pulls back.
4. **Each recorded ring:** the camera spotlights the record while the notes land. On pull-back, the whole shell turns toward the ring's role colour (muted by `recordVaultRoleTint01`), rippling outward.
   - During any zoom (`CameraZoomDirector.ZoomActive`) the record glides to its chamber centre and holds still.
5. **Motif complete** (StarPool's bridge gate passes) puts the game into **record ready**. This plays in the normal view: the final ring gets **no zoom**, and any earlier ring's zoom still running is released.
   - Role walls are released and dust stops growing.
   - The final ring's notes land (`DockForCollection`, capped at one loop).
   - The shell takes the final ring's colour (retint ripple), then **explodes** in a chain reaction: the block where the record last struck ignites, and each block sets off its neighbours after a short jittered fuse (flash, then pop), capped at ~1.8 s.
   - The record is then collectable.
6. **Collect:** a ship touching the record triggers the **centred** zoom (`HoldCentered`): the camera pans and zooms until the record fills the view like the original full-screen record.
   - The bridge starts as soon as the camera has landed (`CameraZoomDirector.IsSettled`, minimum 0.3s, capped at settle + 0.5s).
   - The record exits at once: spin 0.8s, tilt 1.2s, shrink 1.5s (in-game ring config).
   - There is no wait for the loop boundary; the loop keeps playing unseen.
   - The interstitial maze grows under the dim, then the camera pulls back (`ReleaseRecordExitZoom`).
   - Backstops: next motif start, end of path choice, a cap of `recordExitMaxHoldSeconds` after the exit, and game over.
7. **Fallbacks:** if there's no room for the vault before the maze, it's carved into the finished maze. Failing that, the old fade intro runs and the record docks at completion.

## Where the code is (`Assets/Scripts/`)

| Concern | File |
|---|---|
| Vault layout, placement, build, repair, retint, explode | `Dust/CosmicDustGenerator.RecordDock.cs` |
| Vault planned and built inside maze generation | `Dust/CosmicDustGenerator.MazeGeneration.cs` (`GenerateMazeForPhaseWithPaths`) |
| Clear-path guards (vault cells refuse clearing) | `Dust/CosmicDustGenerator.Clearing.cs` |
| Vault outside as a reach target for role walls | `Dust/CosmicDustGenerator.Blocks.cs` (`GatherReachSeedsAndTargets`) |
| Flow: intro, per-ring focus/retint, ready/explode, collect/exit zoom, palette, reset | `Managers/GameFlowManager.RecordRecovery.cs` |
| Palette refresh when the wall moves | `Dust/CosmicBlockField.cs` |
| Exit zoom release | `Managers/PathChoiceCoordinator.cs`, `Managers/SceneFlowCoordinator.cs`, `Managers/BridgeCoordinator.cs` |
| Harvest counted → `OnMotifHarvestCounted` | `Phase/Star/StarPool.EjectionEvents.cs` |
| Docked record (placement, drift, collection prep) | `Visualizers/MotifRingGlyphApplicator.Dock.cs` |
| Next-up ring palette + strain pulse | `Visualizers/MotifRingGlyphApplicator.GameplayRings.cs` |
| Exit without loop wait (`skipLoopWait`) | `Visualizers/MotifRingGlyphApplicator.SpinOff.cs` |
| Camera zoom and dim spotlight | `Visualizers/CameraZoomDirector.cs` |
| Ring recorded → camera focus | `Visualizers/BinRingController.cs` |

Tunables:
- **GameFlowManager vault:** `recordDockRadiusCells`, `recordDockMinShipDistanceCells`, `recordSizeInChamber` (default now 0.65; **the scene's serialized 0.8 still wins, so set it in the inspector**), `recordExitMaxHoldSeconds`.
- **GameFlowManager camera:** the `Record Camera` header.
- **CosmicDustGenerator:** `recordVaultParticleSizeMul` (3), `recordVaultRoleTint01` (0.45), `recordVaultFlashLift01` (0.35).
- **CosmicDustGeneratorConfig:** `inertBlockTint`, the single inert-block colour.
- **RingGlyphConfig:** `lineWidth` (the in-game record's committed contours and placeholder bands share it) and `maxLineWidthToRadius` (radius cap; 0 = off). Also `nextUpStrainPeriod`, `nextUpStrainScale`, `dockDriftWanderSpeed`, `dockDriftWanderShare`, `dockLungeIntervalRange`, `dockLungeSeconds`.

## Decisions made

- **Shape:** a plain thick ring, not a key with a shaft (user choice, v2). From 2026-11-25 the shape follows the maze pattern (user choice); the Ring is still the FullFill and Rings default.
- **Bottom edge:** the vault never sits against the bottom edge, because the maze border doesn't wall it (user choice, 2026-11-25).
- **Thickness:** from `nodesPerStar`, as above.
- **No gates:** nothing counts rings down on the vault. Progress reads from the record's rings and the shell's colour.
- **Next-up palette:** excludes only the currently walled role. A role comes back once another role takes the wall, and roles with no unfilled bin are dropped.
- **Exit:** starts immediately on collect (no loop wait; user choice).
- **Walls:** role walls keep rotating per bin fill.
- **Competitive:** unchanged; the bridge fires at completion with no vault.
- **Tutorial / sandbox:** the tutorial motif gets a vault. The control sandbox never runs the maze generator, so it never has one. Don't gate the vault on `SandboxActive`: it's still true while the first real motif starts.
- **After completion:** no dust growth until the next motif.
- **Tells:** in-world only, no UI.

## Left to do

### Before or alongside playtesting
- **Set `recordSizeInChamber` to about 0.65** on GameFlowManager in the scene. The serialized 0.8 leaves almost no room to drift.
- **`[VAULT]` logs** exist for build, retint, explode, instant open, and exit-zoom release (with reason). `[RECORD]` logs cover dock, ready, and collect.
- **Playtest matrix:**
  - each of the 7 maze patterns
  - `nodesPerStar` 2, 4, 8, 12, 16 (shell 1/1/2/3/4)
  - multiplayer
  - a SuperNode motif
  - the zero-capture maze restart
  - game over while the record is ready, and during the exit zoom

### Polish pass (2026-11-24), not yet seen in Unity
- `RingUnlit.shader` gained `_BlendOp` and `_PremultiplyOut`.
- The new material `Assets/Materials/RingLineGameplay.mat` (Max blend, premultiplied) stops overlapping line folds from adding up.
- It is assigned to GeneratedTrack's `MotifRingGlyphApplicator.lineMaterial` and to `In Game Ring Glyph.asset`'s `remainingRingMaterial`. **PhaseLibrary.unity also uses that config**, so its locked-preview placeholder rings now use the Max material too.
- Check the console for shader import errors.

### Vault found open at a motif start (2026-11-25)
- **Report:** the next motif's vault was open and the ship could drive in. The interstitial before it had an empty corner.
- **Causes and fixes** (`MAZE_VAULT_AUDIT.md` §A):
  - The hatch opened for ships scraping the outer shell. It now opens only for a ship in the chamber or buried in the shell.
  - Claimed cells (keep-clear, trail, corridor) were never grown or repaired in the shell. Shell cells now ignore claims.
  - The entry→Star lane ran through the vault. Lanes now skip vault cells.
  - The dead Star's keep-clear pocket and the old lanes outlived their maze, which left the empty corner. `ClearMaze` now releases them, and the Star's pocket is also released when the Star is disabled.
- **New logs:** `[MAZE:DIAG] interstitial-built … claimsByOwner=(…)` and `[VAULT] Shell cell … still empty after N repair ticks`.

### Ship trapped inside the vault: cause found (2026-11-24)
- **Diagnosis:** the `[VAULT] SHIP INSIDE` log showed Sandwave inside the chamber (centre (32,4), shell 2 deep, against the bottom edge). Every shell cell around it was solid, guarded, and had its collider on.
- **Cause:** vehicle recovery (`Vehicle.Recovery.cs`: snap-back for out of viewport or play area, and the gravity-void eject) glides the ship by setting `rb.position` straight through walls, to "a nearby empty cell". `IsCellEmpty` counted the sealed chamber as empty.
- **Fix:** `IsCellEmpty` rejects vault cells while the vault is sealed. The void eject now logs `[VEHICLE:RECOVER] … void eject` (it was silent).
- The hatch and the diagnostic below stay as a safety net.

### Ship trapped inside the vault (original report)
- In a playtest a ship got inside the sealed vault and couldn't get out.
- The cause wasn't found in code:
  - vehicle chips and plow suppression skip Solid cells;
  - clears skip guarded cells;
  - ships use continuous collision.
- **Safety net:** every repair tick, if a ship's cell is inside the vault, a ship-wide hatch opens through the shell on its side. It reseals 1.5s after every ship is out.
- **Diagnostic:** the first time this happens, a `[VAULT] SHIP INSIDE` warning lists each nearby shell cell's state, Solid flag, guard, dust, and collider. **Send that log line next time it happens.**

### Ring line width (2026-11-24)
- Unity's LineRenderer width ignores transform scale, so the docked record's lines were drawn at world width and swamped the shrunken rings.
- In-game contour widths are now in the stack's **local units** and re-applied × scale every frame (`SyncGameplayLineWidths`). That makes `lineWidth` comparable to `ringThickness` (1).

### Known risks to check in play
- **Thick shell fit:** at thickness 4 the ring is 13 cells across, against the ~18-row grid. The post-maze dock and then the old fade are the fallbacks; watch the `[RECORD]` warnings.
- **Maze routes:** a thick shell can cut through a pattern's intended routes.
- **Drift vs. shake:** the drift pauses while the record shakes, and the shake writes `localPosition`, so check for a pop when they hand over.
- **Exit zoom length:** after the spin-off the bridge still waits for the loop boundary, then grows the interstitial maze, all under the dim. If that feels long, release the zoom earlier (for example when the spin-off ends).
- **Not dimmed:** the dim overlay doesn't cover overlay-mode canvases or the Star tentacle shimmer (Foreground 40).
- **Grid edge vs. screen edge:** the grid edge may not line up with the visible screen edge, so the vault can look detached from it.
- **Mid-motif maze rebuild** (zero-capture restart): the vault is rebuilt at a new spot and the record moves there.
- **First motif:** the vault and record grow right after launch. Check they aren't hidden under a fading intro screen.

### Blocked on system issues (to be addressed first)
- **P3 — tells:** the docked record pulses when it can be collected. The v2 explosion may already cover this.
- **P4 — tuning:**
  - ~~per-maze-pattern vault placement~~: done 2026-11-25 (`MazePatternConfig.recordVault`). What remains is tuning the PatternAnchor weights in play;
  - a `MazeTelemetryRecorder` field for time from ready to collect.

### Still-undecided design questions
- **Notes during record ready:** can players keep adding notes on the way to the record? (Dust growth is already decided: it stops.)
- **Competitive survival:** should collecting the record matter there?
