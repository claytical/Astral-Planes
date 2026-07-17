# Astral Planes — Subsystem Ecosystem

Current-state reference for how the gameplay, music, backbone, and UI layers interlock.  
SuperNode, glyph, and coral systems are **in transition** — their call-sites are noted but their internals are not described.

---

## Layer Map

```
[GameFlowManager] ─── scene / motif / bridge lifecycle
       │
       ├─► [DrumTrack]  ─── DSP clock, step ticks, loop boundaries, spawn grid
       │         │
       │    [InstrumentTrackController] ─── multi-track sync, gravity void, SFX routing
       │         │
       │    [InstrumentTrack ×4] ─── MIDI loop state, bin expansion, burst accounting
       │
       ├─► [CosmicDustGenerator] ─── authoritative cell grid, role imprints, regrowth
       │
       ├─► [PhaseStar] ─── node spawner, charge accumulator, bridge trigger
       │         │
       │    [MineNode] ─── note-driven carver → burst on depletion
       │         │
       │    [Collectable] ─── autonomous drifter → Vehicle pickup → DSP deposit
       │
       ├─► [Vehicle] ─── player physics, energy, manual release queue
       │
       └─► [NoteVisualizer] ─── loop grid, playhead, release cue, ascension
```

---

## Backbone

### GameFlowManager
`Assets/Scripts/Managers/GameFlowManager.cs` (+ `.LifeCycle.cs`, `.PlayerFlow.cs`, `.BridgeOrchestration.cs`, `.BridgeVisuals.cs`)

Top-level state machine: **Begin → Selection → Playing → GameOver**.  
Owns the authoritative references to every major system and drives scene transitions, motif swaps, and bridge cinematics.

**Owns:** Vehicle list, active `DrumTrack`, `InstrumentTrackController`, `CosmicDustGenerator`, `NoteVisualizer`, motif snapshot history, bridge-pending / ghost-cycle flags.

**Emits / calls out:**
- `DrumTrack.ApplyMotif()` — hands new `MotifProfile` to the transport
- `CosmicDustGenerator.HardStopRegrowthForBridge()` / `BeginSlowFadeAllDust()` — freezes dust on bridge
- `motifRingGlyphApplicator.AnimateApply(snapshot)` / `FadeOutAndClear()` — ring glyph active call-sites
- *(commented-out)* `motifGlyphApplicator.Apply()` — 2D glyph block, in transition
- *(commented-out)* `SetBridgeCinematicMode(true)` / coral grow — coral block, in transition
- `ConstellationMemoryStore.StoreSnapshot()` — persists completed-phase history
- `CheckAllPlayersOutOfEnergy()` — game-over gate

**Listens to:** energy-out signals from `Vehicle` (intensity mapping), all-players-out (game-over).

---

## Music Subsystem

### DrumTrack
`Assets/Scripts/Music/DrumTrack.cs`

The single DSP time authority. Every timing-sensitive system derives loop position from it.

**Owns:** BPM, step count, active `MotifProfile` clip pool, loop-boundary DSP anchors, spawn grid (world ↔ grid coordinate mapping), `MineNode` registry, current bin count.

**Emits:**
| Event | Consumers |
|-------|-----------|
| `OnStepChanged(stepIndex, leaderSteps)` | `Vehicle` (release cue window), `NoteVisualizer` (playhead) |
| `OnLoopBoundary` | `MineNode` (path prune), `Collectable` (idea direction), `PhaseStar` (re-arm logic) |

**Key methods called on it:**
- `ApplyMotif(MotifProfile)` — swaps drum clip + timing, defers to next boundary
- `SetBinCount(n)` — updates visual bin count after track expansion
- `WorldToGridPosition()` / `GridToWorldPosition()` — coordinate bridge for all spawners
- `GetRandomAvailableCell()` / `OccupySpawnCell()` / `FreeSpawnCell()` — grid occupancy for node placement

---

### InstrumentTrackController
`Assets/Scripts/Music/InstrumentTrackController.cs`

Coordinates the four `InstrumentTrack` instances; owns gravity void lifecycle; routes role-specific SFX.

**Owns:** multi-track playhead sync, gravity void prefab instance, `noteCommitMode` (Performance = auto-commit vs Composition = step-sequenced).

**Calls out:**
- `InstrumentTrack.TryExpandNextBin()` / `CommitBinExpansion()` — manages bin growth
- `CosmicDustGenerator.GrowVoidDustDiskFromGrid()` — void disk expansion (via `BeginGravityVoidForPendingExpand`)
- `NoteVisualizer.RecomputeTrackLayout()` / `ResyncLeaderBinsNow()` — marker layout rebuild on expansion

**SFX:** `NotifyCollected()` → pickup tick; `NotifyCommitted()` → commit stinger (role-specific audio).

---

### InstrumentTrack
`Assets/Scripts/Music/InstrumentTrack.cs`

Per-role MIDI loop state. The **bin** is the expansion unit — each bin adds one loop's worth of note slots.

**Owns:** loop notes grid (`_loopNotes`, `persistentLoopNotes`), pre-cached NoteSet per bin (`_binNoteSets`), ascending cohort state, collectable burst accounting (`_burstRemaining`, `_burstCollected`), `loopMultiplier` (1–4 bins active).

**Emits:**
| Event | Consumer |
|-------|----------|
| `OnCollectableBurstCleared(burstId, hadNotes)` | `PhaseStar` — triggers re-arm |
| `OnAscensionCohortCompleted(startStep, endStep)` | `NoteVisualizer` — ascension animation |

**Receives:**
- `OnCollectableCollected(note, step)` — auto-deposit path; writes note to loop grid
- `CommitManualReleasedNote(note, step)` — manual release path from `Vehicle`
- `SpawnCollectableBurst(noteSet)` — called by `MineNode` on depletion; spawns collectable swarm

**Calls out:**
- `NoteVisualizer.RegisterCollectedMarker(track, step)` — lights marker
- `PlayNote127()` / `PlayOneShotMidi()` — immediate MIDI feedback

---

### ScriptableObject Data Layer

| Asset | Purpose |
|-------|---------|
| `MotifProfile` | Phase identity: BPM, drum clip pools, `nodesPerStar`, per-role note configs, chord progression ref, `PhaseStarBehaviorProfile` |
| `RoleMotifNoteSetConfig` | Per-role generation rules: weighted scales, melodic patterns, rhythms, chord function palette, optional riff override |
| `ChordProgressionProfile` | Ordered chord sequence; `NoteSetFactory` shifts note pitches to each chord's root |
| `MusicalRoleProfile` | Role constants: dust colors (base + shadow), energy units, carve/drain resistance, mine node speed + agility, ripeness duration, per-role regrowth delay override (`regrowthDelay`, vehicle carves only) |
| `MazePatternConfig` | Maze wall pattern + `dustTiming`: base regrow delay plus per-source overrides (vehicle plow, collectable arrival/plow, jail, supernode, zap, star-drain release) keyed by `DustClearSource` |
| `ShipMusicalProfile` | Ship physics, fuel, plow footprint, and `regrowDelayMultipliers` — per-role scaling of regrow delay for cells this ship carves (growth agent) |

`MotifProfile` → references `RoleMotifNoteSetConfig[]`, `ChordProgressionProfile`, and `MazePatternConfig` (`mazePattern`).  
`MusicalRoleProfile` → consumed by `CosmicDustGenerator` (imprint color), `MineNode` (speed/agility), `PhaseStar` (dust affinity), `InstrumentTrackController` (SFX routing).

---

## Gameplay Subsystem

### PhaseStar
`Assets/Scripts/Phase/Star/PhaseStar.cs`

The spawn authority for the current phase. Accumulates charge from dust interaction; decides when to eject a new node; triggers the bridge when all shards have been resolved.

**Owns:** charge level (by role), safety bubble radius, armed/disarmed state, `_activeNode` / `_activeSuperNode` guards, `_shardsEjectedCount`, `_heldDrainCells` (cells drained since the last node spawn).

**Tentacle drain (`PhaseStarDustAffect`):** one tentacle grows per available role-carved cell, capped by the node payload — the zap budget is `RemainingZapCount` minus in-flight tentacles, so a 5-note payload with 3 carved cells grows 3 tentacles at once and adds more as cells are carved. Each drained cell goes through `CosmicDustGenerator.ZapClearCellHeld()` (no regrow while held) and is registered on the star. When the payload's zap count is met, acquisition disables and the star latches ready.

**Emits / calls out:**
- Spawns `MineNode` via `SpawnNodeCommon()` or `SuperNode` via `SpawnSuperNodeCommon()` (SuperNode path = track fully expanded + NoteSet saturated; SuperNode is **in transition**)
- Hands the drained-cell batch to the spawned node (`MineNode.AttachHeldDustBatch()`); releases any unassigned batch via `CosmicDustGenerator.ReleaseHeldCells()` on destroy
- Calls `GameFlowManager.BeginMotifBridge()` when all shards resolved + no collectables in flight
- Queries `InstrumentTrack.IsSaturatedForRepeatingNoteSet()` to decide MineNode vs SuperNode path

**Listens to:**
- `InstrumentTrack.OnCollectableBurstCleared` — sets `_awaitingCollectableClear = false`, re-evaluates bridge
- `MineNode.OnResolved` — clears `_activeNode`; re-arms or advances to bridge
- `SuperNode.OnResolved` — clears `_activeSuperNode` *(in transition)*
- `DrumTrack.OnLoopBoundary` — evaluates bridge readiness at each boundary

**Structural call-sites (transitioning systems):**
- `SpawnSuperNodeCommon()` instantiates `SuperNode` prefab and wires `OnResolved` — SuperNode in transition
- Bridge path calls `GameFlowManager.BeginMotifBridge()` which internally calls `motifRingGlyphApplicator.AnimateApply()` (active) and the commented-out coral block

---

### MineNode
`Assets/Scripts/Gameplay/Mining/MineNode.cs`

A destructible shard whose `NoteSet` is its behavior engine. It does **not** carve dust — it navigates existing open corridors (contained by dust walls) and re-tints nearby solid cells with its role via `MineNodeDustInteractor` → `CosmicDustGenerator.PaintDustExhaust()`.

**Owns:** strength (health), movement intent, traveled-path cell record, held star-drain dust batch (`AttachHeldDustBatch()` — the cells whose energy built this node).

**Movement loop (FixedUpdate):**
```
corridor lookahead (wall avoidance)
  → same-role dust affinity bias (CosmicDustGenerator cell query)
  → flee-vehicle avoidance
  → stall escape
  → move + paint role exhaust (CosmicDustGenerator.PaintDustExhaust — tints, never clears)
```

**Emits / calls out:**
- `OnResolved` event → `PhaseStar` clears `_activeNode`
- `ReleaseHeldDustOnce()` on resolve **and** destroy → `CosmicDustGenerator.ReleaseHeldCells()` — the star-drained cells finally regrow (gray, `starDrainReleaseDelay`)
- `InstrumentTrack.SpawnCollectableBurst(noteSet)` on depletion
- `Explode` component → visual burst

**Listens to:**
- `DrumTrack.OnLoopBoundary` — prunes `_carvedPath` to only open cells (prevents ghost trail re-occupation)

**Collision:** `Vehicle` collision reduces strength; on zero → `HandleDepleted()`.

---

### Collectable
`Assets/Scripts/Gameplay/Mining/Collectable.cs`

A note-carrying orb spawned in bursts by `MineNode`. Has two lifecycle paths:

**Path 1 — Auto-deposit:**
```
Spawned → BeginSpawnArrival() → MovementRoutine() (autonomous drift)
  → Vehicle trigger pickup → OnCollectableCollected()
  → BeginCarryAndDepositAtDsp() → CarryAndDepositRoutine()
  → arrives at NoteVisualizer marker → InstrumentTrack.OnCollectableCollected()
```

**Path 2 — Manual release:**
```
Vehicle trigger pickup → OnCollectablePickedUpForManualRelease()
  → Vehicle.EnqueuePendingCollectedNote()
  → DrumTrack.OnStepChanged → Vehicle.TryReleaseQueuedNote()
  → Vehicle.CommitManualReleaseAtStep()
  → Collectable.CommitManualReleaseAtStep() → InstrumentTrack.CommitManualReleasedNote()
```

**Owns:** assigned note + step + bin, carry state, grid occupancy cell (`_currentCell`), tether line.

**Autonomous movement:** the timeline ghost pulse (`DrumTrack.OnStepChanged` crossing the note's step) sounds the note and arms a movement intent for the note's duration; role-specific patterns (Bass charges, Lead headings, Groove darts, Harmony orbits) run under a home tether with vehicle-flee. **While an intent is armed the note plows through dust** (`CarveCellPreserveGray`, source `CollectablePlow` — gray regrow, temporary corridors); between intents it bounces off dust. The arrival flight also carves its path gray (source `CollectableArrival`).

**Listens to:** `DrumTrack.OnStepChanged` (ghost pulse → intent arming, trail follow timing).

**Emits:** `OnCollected` (pickup), `OnDestroyed` (cleanup accounting).

---

### Vehicle
`Assets/Scripts/Gameplay/Core/Vehicle.cs`

Player-controlled ship. Arcade physics (linear + angular momentum). Owns the manual release queue.

**Owns:** energy level (`energyLevel` / `capacity`), `_pendingNotes` queue, `_armedReleases` queue, position history ring buffer (trail), boost state.

**Energy loop:**
```
TurnOnBoost() → ConsumeEnergy() each frame
  → energy = 0 → respawn / game-over signal to GameFlowManager
  → CollectEnergy(int) refuels from Collectables
```

**Note collection loop:**
```
Collectable trigger → EnqueuePendingCollectedNote()
  → DrumTrack.OnStepChanged → TryReleaseQueuedNote() (checks window vs step targets)
  → CommitManualReleaseAtStep() → track.CommitManualReleasedNote()
  → NoteVisualizer ghost cue cleared
```

**Listens to:** `DrumTrack.OnStepChanged` (release cue beat countdown).

**Calls out:**
- `NoteVisualizer.TryGetNextUnlitStepExcluding()` — finds target for ghost cue
- `dustGenerator.SetVehicleKeepClear()` / `ChipDustByVehicle(…, profile)` — dust pocket + plow; the ship's `regrowDelayMultipliers` scale the carved cell's regrow delay per role (growth agent — a Bass-accelerator ship regrows Bass dust sooner)
- `PhaseStar.IsPointInsideSafetyBubble()` — suppresses keep-clear carving inside bubble

---

### CosmicDustGenerator
`Assets/Scripts/Dust/CosmicDustGenerator.cs`

Authoritative 2D grid. All spatial state lives here — cell solid/empty status, role imprints, regrowth scheduling, vehicle pockets, gravity void disk growth.

**Owns:** `_cellState[,]`, `_imprints`, `_hiddenImprints`, `_solidCountByRole[]` (density tracking), `_mazePatternCells`, per-cell regrowth coroutines, `_heldRegrowCells` (star-drained cells held from regrow until their MineNode dies).

**Key operations:**
| Method | Who calls it |
|--------|-------------|
| `ClearCell()` / `CarveCell()` | internal funnel — every removal carries a `DustClearSource` for per-source regrow delay |
| `CarveCellPreserveGray()` | `Collectable` arrival flight + intent plow, `SuperNodeTrackNode` — always regrows gray/uncharged |
| `ZapClearCellHeld()` / `ReleaseHeldCells()` | `PhaseStarDustAffect` tentacle drain / `MineNode` death (and star teardown) |
| `SetVehicleKeepClear()` / `ReleaseVehicleKeepClear()` | `Vehicle` |
| `ChipDustByVehicle()` / `CarveDustByVehicle()` | `Vehicle` boost plow (passes `ShipMusicalProfile`) |
| `PaintDustExhaust()` | `MineNodeDustInteractor` — role tint only, never clears |
| `GrowVoidDustDiskFromGrid()` | `InstrumentTrackController` (expansion pending) |
| `CreateJailCenterForCollectable()` | `Collectable` spawn pocket |
| `HardStopRegrowthForBridge()` | `GameFlowManager` bridge start (also flushes held cells) |
| `BeginSlowFadeAllDust()` | `GameFlowManager` bridge fade |
| `ResumeRegrowthAfterBridge()` | `GameFlowManager` next motif start |
| `ApplyActiveRoles()` | `GameFlowManager` motif swap |

**Regrow delay resolution** (`ResolveRegrowDelay`, precedence high → low): explicit per-call override → carved cell's `MusicalRoleProfile.regrowthDelay` (vehicle carves only) → maze `dustTiming` per-source delay → maze base `regrowDelay` → 8s fallback. Vehicle carves then scale the result by the carving ship's per-role `regrowDelayMultipliers`. Cells flagged `ForceGrayRegrow`/`ZapForceGray` always regrow gray and uncharged — role tint (and drainable charge) is only earned by vehicle plow-carves or MineNode exhaust painting.

Role imprinting: a vehicle plow-carve promotes the cell's hidden Voronoi role into the active imprint, so it regrows in its true role color with charge. Same-role cells attract nearby `MineNode` and `Collectable` movement.

---

## UI

### NoteVisualizer
`Assets/Scripts/Visualizers/Notes/NoteVisualizer.cs`

Renders the loop grid, DSP-synced playhead, manual-release ghost cue, note ascension animation, and bin expansion indicators.

**Owns:** marker dict `(track, step) → Transform`, per-vehicle release cue GameObject, `NoteAscensionDirector` reference.

**Key inbound calls:**
| Caller | Method | Purpose |
|--------|--------|---------|
| `InstrumentTrack` | `RegisterCollectedMarker(track, step)` | Light a step marker on note commit |
| `InstrumentTrack` | `CanonicalizeTrackMarkers()` | Stabilize marker IDs after burst clears |
| `Vehicle` (tick) | `UpdateManualReleaseCueExcluding(vehicle, step)` | Position ghost cue toward release target |
| `Vehicle` (pickup) | `ClearManualReleaseCue(vehicle)` | Hide cue after release |
| `InstrumentTrackController` | `RecomputeTrackLayout()` | Rebuild row positions on bin expansion |
| `InstrumentTrackController` | `MarkGhostPadding()` | Show placeholder steps for new bin |

**Queries from `Vehicle`:**
- `TryGetNextUnlitStep()` — next un-filled step forward from playhead
- `TryGetNextUnlitStepExcluding(reserved)` — skips already-armed steps

---

## Cross-System Data Flows

### Flow A — Note Birth → Loop Commit
```
PhaseStar ejects → MineNode spawned (NoteSet from MotifProfile via InstrumentTrack)
  → MineNode FixedUpdate: navigates corridors, paints role exhaust on CosmicDustGenerator cells
  → Vehicle collides with MineNode → MineNode.HandleDepleted()
  → InstrumentTrack.SpawnCollectableBurst(noteSet) → Collectables spawn + drift
  → Vehicle trigger collides Collectable
      [auto] → BeginCarryAndDepositAtDsp() → InstrumentTrack.OnCollectableCollected()
      [manual] → Vehicle.EnqueuePendingCollectedNote()
                   → DrumTrack.OnStepChanged → TryReleaseQueuedNote()
                   → InstrumentTrack.CommitManualReleasedNote()
  → NoteVisualizer.RegisterCollectedMarker() lights step
  → InstrumentTrack.OnCollectableBurstCleared() → PhaseStar re-arms
```

### Flow B — Bin Expansion
```
InstrumentTrack.IsSaturatedForRepeatingNoteSet() → true
  → InstrumentTrackController.BeginGravityVoidForPendingExpand()
      → CosmicDustGenerator.GrowVoidDustDiskFromGrid() (visual signal to player)
  → burst cleared + ascension cohort complete
  → InstrumentTrackController.CommitBinExpansion()
      → InstrumentTrack.TryExpandNextBin() → CommitBinExpansion()
      → DrumTrack.SetBinCount(n)
      → NoteVisualizer.RecomputeTrackLayout() / MarkGhostPadding()
  → EndGravityVoidForPendingExpand()
```

### Flow C — Bridge / Phase Transition
```
PhaseStar: all shards resolved, no collectables in flight, no active node
  → GameFlowManager.BeginMotifBridge()
  → CosmicDustGenerator.HardStopRegrowthForBridge() + BeginSlowFadeAllDust()
  → motifRingGlyphApplicator.AnimateApply(snapshot)       ← active call-site
  → [commented-out: motifGlyphApplicator.Apply()]         ← glyph, in transition
  → [commented-out: SetBridgeCinematicMode / coral grow]  ← coral, in transition
  → wait for boundary → motifRingGlyphApplicator.FadeOutAndClear()
  → StartNextMotifInPhase()
      → DrumTrack.ApplyMotif(newMotif)
      → InstrumentTrackController full reset (clears loop notes, bin state, burst state)
      → NoteVisualizer.BeginNewMotif_ClearAll()
      → CosmicDustGenerator.ResumeRegrowthAfterBridge()
  → PhaseStar re-arms for new phase
```

### Flow D — Energy / Drum Intensity
```
Vehicle.TurnOnBoost() → ConsumeEnergy() per frame
  → GameFlowManager accumulates burn rate → intensity level (0–4)
  → DrumTrack.ApplyMotif(intensityLevel) → swaps to intensity drum clip
  → Vehicle energy = 0 → respawn + GameFlowManager.CheckAllPlayersOutOfEnergy()
```

### Flow E — Dust Energy Economy
```
Vehicle plow-carves dust → cell regrows in its true role color + charge
    (delay = maze dustTiming table × ship's per-role regrowDelayMultiplier)
  → PhaseStar grows one tentacle per revealed cell, capped at the node payload
      → each drained cell: ZapClearCellHeld() — held, no regrow
  → payload zap count met → tentacles retract → star ReadyLatched
  → MineNode ejected, holding the drained-cell batch
  → node captured / escaped / expired → ReleaseHeldCells()
      → cells regrow gray + uncharged (starDrainReleaseDelay)
  → gray dust must be plow-carved again to re-earn role charge
```
Collectable dust removal (arrival flight + intent-window plowing) always regrows gray —
it opens temporary corridors but never feeds the star.

---

## Transitioning System Call-Sites

SuperNode, glyph, and coral are in transition. These are the stable-system locations that reach into them:

| Stable System | File | Call | Target |
|---------------|------|------|--------|
| `PhaseStar` | `PhaseStar.cs:1733` | `SpawnSuperNodeCommon()` → instantiates SuperNode prefab, wires `OnResolved` | SuperNode |
| `PhaseStar` | `PhaseStar.cs:1808` | `ShouldSpawnSuperNodeForTrack()` → gate for SuperNode vs MineNode path | SuperNode |
| `GameFlowManager` (bridge) | `GameFlowManager.BridgeOrchestration.cs:39` | `motifRingGlyphApplicator?.AnimateApply(motifSnap)` | Ring glyph (active) |
| `GameFlowManager` (bridge) | `GameFlowManager.BridgeOrchestration.cs:110` | `motifRingGlyphApplicator.FadeOutAndClear()` | Ring glyph (active) |
| `GameFlowManager` (bridge) | `GameFlowManager.BridgeOrchestration.cs:83–96` | `motifGlyphApplicator.Apply(motifSnap)` block | 2D glyph (commented out) |
| `GameFlowManager` (bridge) | `GameFlowManager.BridgeOrchestration.cs:69` | `SetBridgeCinematicMode(true)` + coral grow block | Coral (commented out) |
