# Astral Planes — Subsystem Ecosystem

Current-state reference for how the gameplay, music, backbone, and UI layers interlock.
Coral and the 2D glyph path have both been fully removed — noted briefly rather than described in depth, see the **Glyph & Ring Systems** and **Removed / Dormant Systems** sections.

---

## Layer Map

```
[GameFlowManager] ─── scene refs, top-level facade
       │
       ├─► [SessionStateCoordinator]  ─── game state, players, ghost-cycle / bridge-pending flags
       ├─► [BridgeCoordinator]        ─── bridge sequence: freeze, snapshot, fade, ring spin-off
       ├─► [SceneFlowCoordinator]     ─── scene transitions, next-motif / next-phase setup
       │
       ├─► [DrumTrack]  ─── DSP clock, step ticks, loop boundaries, spawn grid
       │         │
       │    [InstrumentTrackController] ─── multi-track sync, gravity void, SFX routing
       │         │
       │    [InstrumentTrack ×4] ─── MIDI loop state, bin expansion, burst accounting
       │
       ├─► [CosmicDustGenerator] ─── authoritative cell grid, role imprints, regrowth
       │
       ├─► [StarPool] ─── per-role star lifecycle, ejection gate + budget, refunds
       │         │
       │    [PhaseStar] ─── zap-count drain, tentacle siphon, eject decision
       │         │
       │    [DiscoveryTrackNode] ─── note-driven carver → burst on depletion
       │         │
       │    [SuperNode] ─── alternate ejection: multi-track chase bonus round
       │         │
       │    [Collectable] ─── autonomous drifter → Vehicle pickup → DSP deposit
       │
       ├─► [Vehicle] ─── player physics, energy, manual release queue
       │
       └─► [NoteVisualizer] / [MotifRingGlyphApplicator] ─── loop grid, playhead, ring bridge FX
```

---

## Backbone

`GameFlowManager` used to own the full bridge/scene sequence directly. A refactor ("scene, bridge, and session coordinators") split that logic out into three sealed helper classes it delegates to; `GameFlowManager` itself is now a thin facade holding scene references.

### GameFlowManager
`Assets/Scripts/Managers/GameFlowManager.cs` (+ `.LifeCycle.cs`, `.PlayerFlow.cs`, `.BridgeOrchestration.cs` — now an 11-line dispatcher, see Flow C)

Top-level facade. Holds the authoritative references to every major system and exposes coordinator-backed properties (`CurrentState`, `BridgePending`, `GhostCycleInProgress` all forward to `SessionState`).

**Owns:** Vehicle list, active `DrumTrack`, `InstrumentTrackController`, `CosmicDustGenerator`, `NoteVisualizer`, glyph/ring-glyph applicator references, phase-in FX, vehicle-trap spawning, note-tether cleanup, `AnyCollectablesInFlightGlobal()` (shared collectable-in-flight query used by `StarPool`/`PhaseStar`).

**Delegates to:** `SessionState`, `BridgeFlow`, `SceneFlow` (below) for everything state/sequencing-related.

---

### SessionStateCoordinator
`Assets/Scripts/Managers/SessionStateCoordinator.cs`

Owns player-session state: joined `LocalPlayer` list, readiness, `GameState` (`Begin/Selection/Playing/GameOver`), `GhostCycleInProgress`/`BridgePending` flags, game-over guard (`_hasGameOverStarted`). Fires `GameStateChanged`/`BridgePendingChanged`/`GhostCycleChanged` events. `CheckAllPlayersOutOfEnergy()` is the game-over gate, called from `GameFlowManager`.

---

### BridgeCoordinator
`Assets/Scripts/Managers/BridgeCoordinator.cs`

Owns the motif-bridge sequence (see **Flow C** for the full call order) and the in-memory `MotifSnapshot` history (`MotifSnapshots` list — this replaces the no-longer-existing `ConstellationMemoryStore` that older documentation referenced). `StampMotifStartTime()` marks when the current motif began, used to compute `MotifSnapshot.TrackBinData.MotifStartTime`.

---

### SceneFlowCoordinator
`Assets/Scripts/Managers/SceneFlowCoordinator.cs`

Owns scene transitions and next-motif/next-phase setup: `TransitionToScene()`, `FadeScreenToBlack()`/`FadeScreenFromBlack()`, `StartNextMotifInPhase()` (hands the new `MotifProfile` to `DrumTrack.ApplyMotif()`, resets `InstrumentTrackController`, clears `NoteVisualizer`, resumes dust regrowth), `StartNextPhaseMazeAndStar()`.

---

## Music Subsystem

### DrumTrack
`Assets/Scripts/Music/DrumTrack.cs` (+ `.MotifApplication.cs`, `.BeatIntensity.cs`, `.Transport.cs`)
`Assets/Scripts/Music/DrumTrackGridMapper.cs` — owned grid/play-area mapping helper

The single DSP time authority. Every timing-sensitive system derives loop position from it.
A modularization pass split motif-clip scheduling, beat/energy-driven intensity, and
DSP-clock ticking into three `partial` files that still share DrumTrack's fields directly
(same pattern as `InstrumentTrack.Lifecycle.cs` etc.); grid/play-area world mapping and
spawn-grid delegation (fully decoupled, no DSP-timing dependency) moved into an owned
`DrumTrackGridMapper` instance instead, mirroring the `CosmicDustGenerator` /
`CosmicDustCellRegistry` split. All public method signatures on `DrumTrack` are unchanged,
so external callers are unaffected.

**Owns:** BPM, step count, active `MotifProfile` clip pool, loop-boundary DSP anchors, spawn grid (world ↔ grid coordinate mapping, via `DrumTrackGridMapper`), `DiscoveryTrackNode` registry, `_starPool` reference, current bin count.

**Emits:**
| Event | Consumers |
|-------|-----------|
| `OnStepChanged(stepIndex, leaderSteps)` | `Vehicle` (release cue window), `NoteVisualizer` (playhead) |
| `OnLoopBoundary` | `DiscoveryTrackNode` (path prune), `Collectable` (idea direction), `PhaseStar` (re-arm logic) |

**Key methods called on it:**
- `ApplyMotif(MotifProfile)` — swaps drum clip + timing, deferred to next boundary by `SceneFlowCoordinator`
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
| `OnCollectableBurstCleared(burstId, hadNotes)` | `StarPool` — clears the ejection gate; `PhaseStar` — triggers re-arm |
| `OnAscensionCohortCompleted(startStep, endStep)` | `NoteVisualizer` — ascension animation |

**Receives:**
- `OnCollectableCollected(note, step)` — auto-deposit path; writes note to loop grid
- `CommitManualReleasedNote(note, step)` — manual release path from `Vehicle`
- `SpawnCollectableBurst(noteSet)` — called by `DiscoveryTrackNode` (or `InstantFillAllBins()` by `SuperNode`) on depletion; spawns collectable swarm

**Calls out:**
- `NoteVisualizer.RegisterCollectedMarker(track, step)` — lights marker
- `PlayNote127()` / `PlayOneShotMidi()` — immediate MIDI feedback

---

### ScriptableObject Data Layer

| Asset | Purpose |
|-------|---------|
| `MotifProfile` | Phase identity: BPM, drum clip pools, `nodesPerStar`, per-role note configs, chord progression ref, `alternateChordProgressionProfile` (gates SuperNode) |
| `RoleMotifNoteSetConfig` | Per-role generation rules: weighted scales, melodic patterns, rhythms, chord function palette, optional riff override |
| `ChordProgressionProfile` | Ordered chord sequence; `NoteSetFactory` shifts note pitches to each chord's root |
| `MusicalRoleProfile` | Role constants: dust colors (base + shadow), energy units (dust-cell plow HP, not a star currency), carve/drain resistance, mine node speed + agility, ripeness duration, per-role regrowth delay override (`regrowthDelay`, vehicle carves only) |
| `MazePatternConfig` | Maze wall pattern + `dustTiming`: base regrow delay plus per-source overrides (vehicle plow, collectable arrival/plow, jail, SuperNode, zap, star-drain release) keyed by `DustClearSource` |
| `ShipMusicalProfile` | Ship physics, fuel, plow footprint, and `regrowDelayMultipliers` — per-role scaling of regrow delay for cells this ship carves (growth agent) |

`MotifProfile` → references `RoleMotifNoteSetConfig[]`, `ChordProgressionProfile`, and `MazePatternConfig` (`mazePattern`).
`MusicalRoleProfile` → consumed by `CosmicDustGenerator` (imprint color), `DiscoveryTrackNode`/`SuperNodeTrackNode` (speed/agility), `PhaseStar` (dust affinity), `InstrumentTrackController` (SFX routing).

---

## Gameplay Subsystem

### StarPool
`Assets/Scripts/Phase/Star/StarPool.cs`

The per-motif ejection authority. One `StarPool` exists per phase (owned by `DrumTrack._starPool`); it spawns a `PhaseStar` per active role with available dust, gates how many DiscoveryTrackNode/SuperNode ejections the motif is allowed, and decides when the harvest is done and the bridge can fire.

**Owns:** `_remainingEjectionsTotal` (seeded from `MotifProfile.nodesPerStar` — a single global count across *all* roles, not per-role), `_mineNodePending` (the single in-flight-sequence gate), `_lastEjectedRole`, `_ejectedBurstWasEmpty`, per-role `_activeStars`.

**Ejection budget — eject-time commitment (`StarPool.cs:150-196`):** `Tick()` lets every role with dust and an empty star slot spawn a star; there is no spawn-time budget check. The budget is spent only when a star actually ejects: `CanStarCommitEjection(isSuperNode)` (`:195-196`) = `!_mineNodePending && (isSuperNode || _remainingEjectionsTotal > 0)`, wired into each spawned star so it self-checks at poke time and `FlashReject`s if denied. This deliberately serializes ejections — **one sequence in flight at a time, of any kind, across all roles** (a per-role ledger was considered and rejected as a bigger refactor; see `project_starpool_phase_system.md` memory for the full history).

**`_mineNodePending` gate — 4 clear paths** (`OnStarMineNodeResolved`, `StarPool.cs:409-441`; `HandleCollectableBurstCleared`, `:446-509`):
1. **Normal burst** — the ejected role's `OnCollectableBurstCleared(hadNotes:true)` fires → decrements `_remainingEjectionsTotal`, clears the gate, triggers `DespawnLeftoverStars()` if the budget just hit zero.
2. **Empty-burst race** — a burst clears with `hadNotes:false` before the node resolves → sets `_ejectedBurstWasEmpty`; the later `OnStarMineNodeResolved` call clears the gate without spending a harvest.
3. **SuperNode** — no burst ever spawns for a SuperNode outcome; `OnStarMineNodeResolved` sees `wasSuperNode` and clears the gate directly, no spend.
4. **Expired/escaped DiscoveryTrackNode** — `wasExpired`/`wasEscaped` clears the gate and refunds nothing spent (harvest count is untouched either way — the deduction only ever happens in path 1).

**`DespawnLeftoverStars()`** (`:511-530`): once `_remainingEjectionsTotal` hits 0, explodes every remaining active/paused star so `CheckBridgeGate()` (`:562-`, requires zero live stars) can pass and `PhaseStar` can hand off to the bridge.

**Listens to:** `InstrumentTrack.OnCollectableBurstCleared` (all tracks), `PhaseStar.OnMineNodeResolved` (per spawned star).

---

### PhaseStar
`Assets/Scripts/Phase/Star/PhaseStar.cs`

The per-role drain/eject actor spawned and gated by `StarPool`. Siphons dust from its attuned role's carved cells and, once its zap quota is met, ejects a `DiscoveryTrackNode` or `SuperNode`.

**Owns:** attuned role, safety-bubble-free tentacle set, armed/disarmed state, `_activeNode` / `_activeSuperNode` guards, `_shardsEjectedCount`, `_heldDrainCells` (cells drained since the last node spawn).

Readiness is **zap-count-only** — there is no charge/threshold gate. `_displayedCharge01` (`PhaseStar.cs:149`) is a pure visual lerp of `ZapProgress01` (drives the diamond-fill animation); it does not gate ejection. `IsEjectionReady()` = descriptor valid + `ReadyLatched` (zap quota met), period.

**Tentacle drain (`PhaseStarDustAffect`):** one tentacle grows per available role-carved cell, capped by the node payload — the zap budget is `RemainingZapCount` minus in-flight tentacles, so a 5-note payload with 3 carved cells grows 3 tentacles at once and adds more as cells are carved. Each drained cell goes through `CosmicDustGenerator.ZapClearCellHeld()` (no regrow while held) and is registered on the star; the zap is credited immediately on drain (`CreditZap()`), not deferred to full tentacle retraction. When the payload's zap count is met, acquisition disables and the star latches `ReadyLatched`.

**Emits / calls out:**
- Spawns `DiscoveryTrackNode` via `SpawnNodeCommon()` or `SuperNode` via `SpawnSuperNodeCommon()` — gated by `ShouldSpawnSuperNodeForTrack()` (target track fully expanded + `MotifProfile.alternateChordProgressionProfile` set)
- Hands the drained-cell batch to the spawned node (`DiscoveryTrackNode.AttachHeldDustBatch()`); releases any unassigned batch via `CosmicDustGenerator.ReleaseHeldCells()` on destroy
- Calls into the bridge path once `StarPool.CheckBridgeGate()` passes (no live stars, budget spent) + no collectables in flight
- Queries `InstrumentTrack.IsSaturatedForRepeatingNoteSet()` to decide DiscoveryTrackNode vs SuperNode path

**Listens to:**
- `InstrumentTrack.OnCollectableBurstCleared` — sets `_awaitingCollectableClear = false`, re-evaluates bridge
- `DiscoveryTrackNode.OnResolved` / `SuperNode.OnResolved` — clears `_activeNode`/`_activeSuperNode`; re-arms or advances to bridge
- `DrumTrack.OnLoopBoundary` — evaluates bridge readiness at each boundary

---

### TrackNode
`Assets/Scripts/Gameplay/Mining/TrackNode.cs`

Abstract `MonoBehaviour` base shared by `DiscoveryTrackNode` and `SuperNodeTrackNode` — two independently-built "wandering node" AIs that echoed each other's shape without sharing code. Extracted in two passes: lifecycle/sampling plumbing first (proven against `DiscoveryTrackNode` alone before `SuperNodeTrackNode` was touched), then the direction-scan skeleton once both subclasses inherited it.

**Owns:**
- `EightDirections` — the 8-direction scan table both subclasses independently declared before this refactor.
- Loop-boundary expiry: `SubscribeLoopBoundary`/`UnsubscribeLoopBoundary` + `HandleLoopBoundary()` (increments a counter, calls abstract `Expire()` once `_expireAfterLoops` is hit), gated by abstract `IsResolvedOrHandled`.
- `TrySampleStall(...)` — periodic low-movement *sampling* only (timer + hit-count decay). What to do about a confirmed stall stays subclass-owned.
- `TryMarkResolved()` / `ResolvedFired` — an internal resolve-once guard. Each subclass's public `OnResolved` event keeps its own signature and consumers (`Action<DiscoveryTrackNode, DiscoveryTrackNodeOutcome>` vs. `Action<bool>`) — not unified.
- `RunDirectionScan(Vector2 pos)` — "pick best of 8 directions, jitter, set reaction/commit timers," backed by four abstract hooks (`ScoreDirection`, `TurnJitterDegrees`, `NextReactionDelay`, `NextPathCommitDuration`) so each subclass keeps its own scoring source.
- `Rotate()` — shared vector-rotation math helper.

**Explicitly does not own** (deliberate, not an oversight — re-attempting these merges was the top regression risk identified when this base was designed):
- The direction-scoring **formula** — `DiscoveryTrackNode` scores via `DrumTrack` grid dust lookups + decision-archetype category biases; `SuperNodeTrackNode` scores via `Physics2D.Raycast` against `CosmicDust` colliders + role-color matching. Same control-flow shape, genuinely different math.
- Gap-finding — `DiscoveryTrackNodeFleeGapFinder` (BFS to a side-column exit) has no `SuperNodeTrackNode` counterpart; it never seeks a boundary exit.
- Stall-escape *direction* logic and cooldown — `DiscoveryTrackNode` biases toward grid center via decision-archetype aggressiveness, `SuperNodeTrackNode` does a random 140–200° flip.
- Boundary/rect clamping — camera-viewport single-hard-correction-per-tick invariant vs. play-area rect with flat velocity damp.
- "When to rescan" gating — wall-ahead detection, rescan timers, and `DiscoveryTrackNode`'s `SetBehaviorIntent` broadcasting all stay in each subclass's own `FixedUpdate`/`RunCorridorLookahead`.

---

### DiscoveryTrackNode
`Assets/Scripts/Gameplay/Mining/DiscoveryTrackNode.cs` — inherits `TrackNode`

A destructible shard whose `NoteSet` is its behavior engine. It does **not** carve dust — it navigates existing open corridors (contained by dust walls) and re-tints nearby solid cells with its role via `DiscoveryTrackNodeDustInteractor` → `CosmicDustGenerator.PaintDustExhaust()`.

**Owns:** strength (health), movement intent, traveled-path cell record, held star-drain dust batch (`AttachHeldDustBatch()` — the cells whose energy built this node).

**Movement loop (FixedUpdate):**
```
corridor lookahead (wall avoidance, gates into TrackNode.RunDirectionScan on rescan)
  → same-role dust affinity bias (CosmicDustGenerator cell query, inside ScoreDirection override)
  → flee-vehicle avoidance
  → stall escape (TrackNode.TrySampleStall for detection, direction stays here)
  → move + paint role exhaust (CosmicDustGenerator.PaintDustExhaust — tints, never clears)
```

**Emits / calls out:**
- `OnResolved` event → `PhaseStar` clears `_activeNode`; `StarPool` reads `WasCaptured`/expiry off the resolved star
- `ReleaseHeldDustOnce()` on resolve **and** destroy → `CosmicDustGenerator.ReleaseHeldCells()` — the star-drained cells finally regrow (gray, `starDrainReleaseDelay`)
- `InstrumentTrack.SpawnCollectableBurst(noteSet)` on depletion
- Self-destructs after N loop boundaries without capture (NoteSet motif override, else role/archetype baseline off `DiscoveryTrackNodeLocomotionProfile`, else `DiscoveryTrackNodeConfig.defaultExpireAfterLoops`), refunding the ejection slot via `StarPool`
- `Explode` component → visual burst

**Listens to:**
- `DrumTrack.OnLoopBoundary` — increments the loop counter that drives expiry

**Collision:** `Vehicle` collision reduces strength; on zero → `HandleDepleted()`.

---

### SuperNode
`Assets/Scripts/Gameplay/Mining/SuperNode.cs`, `SuperNodeTrackNode.cs` (inherits `TrackNode`), `SuperNodeSteeringMath.cs`

DiscoveryTrackNode's alternate ejection outcome — a multi-track "bonus round" instead of a single mineable shard. `PhaseStar` picks one of the two mutually exclusive paths per poke (`ShouldSpawnSuperNodeForTrack`, `PhaseStar.EjectionLogic.cs:353-386`): a SuperNode requires `MotifProfile.alternateChordProgressionProfile` to be set **and** the target track already at `maxLoopMultiplier`. Prefab naming is not obvious from the class names: the SuperNode prefab is `Assets/Prefabs/Interactable Objects/Major Discovery.prefab` (wired via `Star.prefab.superNodePrefab`), and the `SuperNodeTrackNode` prefab is `Assets/Prefabs/Interactable Objects/Ideas.prefab`.

**Spawn** (`SpawnSuperNodeCommon`, `PhaseStar.EjectionLogic.cs:256-351`): instantiates the SuperNode prefab, computes `difficulty01` from harvests-so-far, builds a `shardTracks` list of every other active-role track still below `maxLoopMultiplier` (the target track is filled directly, not chased).

**`SuperNode.Initialize`** spawns one `SuperNodeTrackNode` chaser per shard track in a ring around the spawn point and tracks `_pendingChaseCount`. If there are no shard tracks, it resolves synchronously in the same frame.

**`SuperNodeTrackNode` movement** (`FixedUpdate`): dust-seeking wander — flees nearby vehicles, cycles burst/pause speed, raycast-scores directions favoring dust matching its `AssignedTrack.assignedRole` (via `TrackNode.RunDirectionScan` + its own `ScoreDirection` override), wall-hugs, stall-escapes, and expires on a loop-boundary timer (via `TrackNode`'s shared expiry counter). On matching-role dust contact it carves the cell (`CarveCellPreserveGray`, tagged `DustClearSource.SuperNode`, gray regrow — never feeds the star). On vehicle contact, `Collect()` calls `AssignedTrack.InstantFillAllBins(toMaxCapacity:true)` and fires the completion sequence.

**Resolution:** each `SuperNodeTrackNode.OnResolved` decrements `SuperNode._pendingChaseCount`; at zero, `SuperNode.OnResolved` fires and it despawns. `PhaseStar`'s subscriber mirrors the fill/advance logic for the *target* track, fires the shared `OnMineNodeResolved`, and enters the same await-collectable-clear tail DiscoveryTrackNode uses.

**Dead code note:** `SuperNodeKiteSteering.cs` (an alternate, unattached steering component superseded by the dust-seeking AI above) and its sole dependency `SuperNodeSteeringMath.SteerTowards` have been removed — zero scene/prefab/code references at time of removal.

---

### Collectable
`Assets/Scripts/Gameplay/Mining/Collectable.cs`

A note-carrying orb spawned in bursts by `DiscoveryTrackNode` (or instant-filled by `SuperNode`). Has two lifecycle paths:

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

---

### CosmicDustGenerator
`Assets/Scripts/Dust/CosmicDustGenerator.cs`

Authoritative 2D grid. All spatial state lives here — cell solid/empty status, role imprints, regrowth scheduling, vehicle pockets, gravity void disk growth.

**Owns:** `_cellState[,]`, `_imprints`, `_hiddenImprints`, `_solidCountByRole[]` (density tracking), `_mazePatternCells`, per-cell regrowth coroutines, `_heldRegrowCells` (star-drained cells held from regrow until their DiscoveryTrackNode dies).

**Key operations:**
| Method | Who calls it |
|--------|-------------|
| `ClearCell()` / `CarveCell()` | internal funnel — every removal carries a `DustClearSource` for per-source regrow delay |
| `CarveCellPreserveGray()` | `Collectable` arrival flight + intent plow, `SuperNodeTrackNode` — always regrows gray/uncharged |
| `ZapClearCellHeld()` / `ReleaseHeldCells()` | `PhaseStarDustAffect` tentacle drain / `DiscoveryTrackNode` death (and star teardown) |
| `SetVehicleKeepClear()` / `ReleaseVehicleKeepClear()` | `Vehicle` |
| `ChipDustByVehicle()` / `CarveDustByVehicle()` | `Vehicle` boost plow (passes `ShipMusicalProfile`) |
| `PaintDustExhaust()` | `DiscoveryTrackNodeDustInteractor` — role tint only, never clears |
| `GrowVoidDustDiskFromGrid()` | `InstrumentTrackController` (expansion pending), `GameFlowManager.SpawnVehicleTraps` |
| `CreateJailCenterForCollectable()` | `Collectable` spawn pocket |
| `HardStopRegrowthForBridge()` | `BridgeCoordinator` bridge start (also flushes held cells) |
| `BeginSlowFadeAllDust()` | `BridgeCoordinator` bridge fade |
| `ResumeRegrowthAfterBridge()` | `SceneFlowCoordinator` next motif start |
| `ApplyActiveRoles()` | `SceneFlowCoordinator` motif swap |

**Regrow delay resolution** (`ResolveRegrowDelay`, precedence high → low): explicit per-call override → carved cell's `MusicalRoleProfile.regrowthDelay` (vehicle carves only) → maze `dustTiming` per-source delay → maze base `regrowDelay` → 8s fallback. Vehicle carves then scale the result by the carving ship's per-role `regrowDelayMultipliers`. Cells flagged `ForceGrayRegrow`/`ZapForceGray` always regrow gray and uncharged — role tint (and drainable charge) is only earned by vehicle plow-carves or DiscoveryTrackNode exhaust painting.

Role imprinting: a vehicle plow-carve promotes the cell's hidden Voronoi role into the active imprint, so it regrows in its true role color with charge. Same-role cells attract nearby `DiscoveryTrackNode`/`SuperNodeTrackNode` and `Collectable` movement.

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

### Glyph & Ring Systems

**Ring glyph (live, record/vinyl metaphor):**
`Assets/Scripts/Visualizers/MotifRingGlyphApplicator.cs` + `RingGlyphConfig.cs` (SO knobs) + `MotifRingGlyphGenerator.cs` (geometry) + `Assets/Scripts/Managers/BinRingController.cs` (drives gameplay ring spawn/deformation off the drum playhead).

Each ring is a filled annulus tinted by musical role, plus a contour line that dips inward ("tug") at each note position — the applicator's own header comment describes the look as record-groove tracks. Two separate rings collections exist on the applicator: `_gameplayRings` accumulate live, one per completed bin, during play; `_recordRings` render a full completed-motif snapshot for browsing UI.

There are **two distinct consumers**, and they use different methods — this is the biggest place the old documentation was wrong:
- **Live bridge:** `BridgeCoordinator.PlayMotifBridgeAndRestart()` calls `MotifRingGlyphApplicator.SpinAndRollOffActiveRings(spinDur, rollOffDuration)` on the accumulated gameplay rings (spin 360°, tilt, scale to zero). It does **not** call `AnimateApply`/`FadeOutAndClear` — `FadeOutAndClear()` doesn't exist anywhere in the codebase.
- **Library/browsing UI:** `Assets/Scripts/UI/PhaseLibraryCarousel.cs` and `Assets/Scripts/UI/SolarSystemRecordDisplay.cs` call `AnimateApply(MotifSnapshot)` to render a *past* completed motif (loaded via `RingSessionStore.LoadAllRingsFromDisk()`), independent of live gameplay.

**Motif snapshot persistence:** `BridgeCoordinator.BuildPhaseSnapshotForBridge()` builds a `MotifSnapshot` (`Assets/Scripts/Utilities/MotifSnapshot.cs`) fresh each bridge from `InstrumentTrack.GetPersistentLoopNotes()` + per-bin fill state — it is not read from any long-lived store. `RingSessionStore.SaveRingToDisk(motifSnap)` (`Assets/Scripts/Utilities/RingSessionStore.cs`) then writes it to `Application.persistentDataPath/RingSessions/ring_{phase}_{motif}.json` for later browsing. (Older documentation referenced a `ConstellationMemoryStore.StoreSnapshot()` call — that class does not exist in the current codebase.)

**2D glyph — removed.** `GlyphApplicator.cs`, `MotifGlyphRegistrar.cs`, and `MotifGlyphPreview.cs` (the editor preview tool) have been deleted; `GameFlowManager`'s `motifGlyphApplicator` field and `RegisterGlyphApplicator()` method are gone with them. The three components were attached to a shared, inactive-by-default "Glyph Generator" GameObject in `GeneratedTrack.unity` alongside the live `BinRingController` — that GameObject's component list was trimmed rather than deleting the object itself, since `BinRingController` still needed it. `MotifGlyphGenerator.cs` stays: it declares `GlyphPolyline`, which the *live* `MotifRingGlyphGenerator.cs` reuses (`Generate()`/`GenerateSingleRing()` etc.) — only the parts of that file specific to the dead 2D-glyph geometry (`GlyphOutput`, `GlyphSpeciesParams`, the `MotifGlyphGenerator` static class itself) are now unreachable, left in place rather than extracting the shared type into its own file.

---

## Removed / Dormant Systems

- **Coral** — fully removed (`MotifCoralVisualizer`, `MotifCoralAnimationController`, and related files deleted in commit `c3315424`). `GameFlowManager.SetBridgeCinematicMode()` no longer exists anywhere in the codebase. The only surviving artifact was an orphaned, unreferenced `Coral Base Segment.prefab`, since deleted. Do not treat coral as "in transition" — there's nothing left to finish.
- **2D glyph** — fully removed, see **Glyph & Ring Systems** above.

---

## Cross-System Data Flows

### Flow A — Note Birth → Loop Commit
```
StarPool spawns PhaseStar for role → tentacles drain role-carved dust cells (1 zap/cell)
  → zap quota met → star ReadyLatched → vehicle pokes → PhaseStar ejects
      [DiscoveryTrackNode path] → DiscoveryTrackNode navigates corridors, paints role exhaust, depletes on vehicle collision
                          → InstrumentTrack.SpawnCollectableBurst(noteSet)
      [SuperNode path] → target track InstantFillAllBins(); shard tracks chased via SuperNodeTrackNode
  → Vehicle trigger collides Collectable
      [auto] → BeginCarryAndDepositAtDsp() → InstrumentTrack.OnCollectableCollected()
      [manual] → Vehicle.EnqueuePendingCollectedNote()
                   → DrumTrack.OnStepChanged → TryReleaseQueuedNote()
                   → InstrumentTrack.CommitManualReleasedNote()
  → NoteVisualizer.RegisterCollectedMarker() lights step
  → InstrumentTrack.OnCollectableBurstCleared() → StarPool clears gate, PhaseStar re-arms
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
StarPool.CheckBridgeGate() passes (budget spent, no live stars) + no collectables in flight
  → GameFlowManager.BeginMotifBridge() (11-line dispatcher)
  → BridgeCoordinator.PlayMotifBridgeAndRestart():
      SessionState.SetGhostCycleInProgress(true) / SetBridgePending(true)
      FreezeGameplayForBridge() — clears tethers, pending notes, live Collectables/DiscoveryTrackNodes, destroys the StarPool
      BuildPhaseSnapshotForBridge() → MotifSnapshot from InstrumentTrack.GetPersistentLoopNotes()
      CosmicDustGenerator.HardStopRegrowthForBridge() + BeginSlowFadeAllDust()
      RingSessionStore.SaveRingToDisk(motifSnap)          ← persistence, not ConstellationMemoryStore
      MotifRingGlyphApplicator.SpinAndRollOffActiveRings() ← live ring exit, not AnimateApply/FadeOutAndClear
      wait for loop boundary
      SceneFlowCoordinator.StartNextMotifInPhase():
          DrumTrack.ApplyMotif(newMotif)
          InstrumentTrackController full reset (clears loop notes, bin state, burst state)
          NoteVisualizer.BeginNewMotif_ClearAll()
          CosmicDustGenerator.ResumeRegrowthAfterBridge()
      SessionState.SetGhostCycleInProgress(false) / SetBridgePending(false)
  → new StarPool spins up, PhaseStars re-arm for the new phase
```

### Flow D — Energy / Drum Intensity
```
Vehicle.TurnOnBoost() → ConsumeEnergy() per frame
  → GameFlowManager accumulates burn rate → intensity level (0–4)
  → DrumTrack.ApplyMotif(intensityLevel) → swaps to intensity drum clip
  → Vehicle energy = 0 → respawn + SessionStateCoordinator.CheckAllPlayersOutOfEnergy()
```

### Flow E — Dust Energy Economy
```
Vehicle plow-carves dust → cell regrows in its true role color + charge
    (delay = maze dustTiming table × ship's per-role regrowDelayMultiplier)
  → PhaseStar grows one tentacle per revealed cell, capped at the node payload
      → each drained cell: ZapClearCellHeld() — held, no regrow; zap credited immediately
  → payload zap count met → tentacles retract → star ReadyLatched
  → DiscoveryTrackNode (or SuperNode) ejected, holding the drained-cell batch
  → node captured / escaped / expired → ReleaseHeldCells()
      → cells regrow gray + uncharged (starDrainReleaseDelay)
  → gray dust must be plow-carved again to re-earn role charge
```
Collectable dust removal (arrival flight + intent-window plowing) and SuperNodeTrackNode dust-seeking always regrow gray —
they open temporary corridors but never feed the star.
