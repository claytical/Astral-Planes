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
| `MusicalRoleProfile` | Role constants: dust colors (base + shadow), energy units, carve/drain resistance, mine node speed + agility, ripeness duration |

`MotifProfile` → references `RoleMotifNoteSetConfig[]` and `ChordProgressionProfile`.  
`MusicalRoleProfile` → consumed by `CosmicDustGenerator` (imprint color), `MineNode` (speed/agility), `PhaseStar` (dust affinity), `InstrumentTrackController` (SFX routing).

---

## Gameplay Subsystem

### PhaseStar
`Assets/Scripts/Phase/Star/PhaseStar.cs`

The spawn authority for the current phase. Accumulates charge from dust interaction; decides when to eject a new node; triggers the bridge when all shards have been resolved.

**Owns:** charge level (by role), safety bubble radius, armed/disarmed state, `_activeNode` / `_activeSuperNode` guards, `_shardsEjectedCount`.

**Emits / calls out:**
- Spawns `MineNode` via `SpawnNodeCommon()` or `SuperNode` via `SpawnSuperNodeCommon()` (SuperNode path = track fully expanded + NoteSet saturated; SuperNode is **in transition**)
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

A destructible carver whose `NoteSet` is its behavior engine: pitch maps to carve speed, rhythm maps to movement pattern.

**Owns:** strength (health), carve direction, carved-path cell record.

**Carve loop (FixedUpdate):**
```
corridor lookahead (wall avoidance)
  → same-role dust affinity bias (CosmicDustGenerator cell query)
  → flee-vehicle avoidance
  → stall escape
  → move + carve cell (CosmicDustGenerator.ClearCell)
```

**Emits / calls out:**
- `OnResolved` event → `PhaseStar` clears `_activeNode`
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

**Autonomous movement:** `HandleLoopBoundaryIdea()` re-evaluates 8 directions by open cell count + wall-hugging bias; `_ideaDirSmoothed` blends toward new idea direction each boundary.

**Listens to:** `DrumTrack.OnLoopBoundary` (idea direction), `DrumTrack.OnStepChanged` (trail follow timing).

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
- `dustGenerator.SetVehicleKeepClear()` / `ChipDustByVehicle()` — dust pocket + plow
- `PhaseStar.IsPointInsideSafetyBubble()` — suppresses keep-clear carving inside bubble

---

### CosmicDustGenerator
`Assets/Scripts/Dust/CosmicDustGenerator.cs`

Authoritative 2D grid. All spatial state lives here — cell solid/empty status, role imprints, regrowth scheduling, vehicle pockets, gravity void disk growth.

**Owns:** `_cellState[,]`, `_imprints`, `_hiddenImprints`, `_solidCountByRole[]` (density tracking), `_mazePatternCells`, per-cell regrowth coroutines.

**Key operations:**
| Method | Who calls it |
|--------|-------------|
| `ClearCell()` | `MineNode`, `Vehicle` plow, `Collectable` pocket |
| `SetVehicleKeepClear()` / `ReleaseVehicleKeepClear()` | `Vehicle` |
| `ChipDustByVehicle()` | `Vehicle` boost plow |
| `GrowVoidDustDiskFromGrid()` | `InstrumentTrackController` (expansion pending) |
| `CreateJailCenterForCollectable()` | `Collectable` spawn pocket |
| `HardStopRegrowthForBridge()` | `GameFlowManager` bridge start |
| `BeginSlowFadeAllDust()` | `GameFlowManager` bridge fade |
| `ResumeRegrowthAfterBridge()` | `GameFlowManager` next motif start |
| `ApplyActiveRoles()` | `GameFlowManager` motif swap |

Role imprinting: when `MineNode` carves through a cell it imprints its `MusicalRole` color + resistance onto the cell. Same-role cells attract nearby `MineNode` and `Collectable` movement.

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
  → MineNode FixedUpdate: carves CosmicDustGenerator cells
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
