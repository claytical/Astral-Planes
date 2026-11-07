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
       │                       (or Vehicle discard → time bomb → blast →
       │                        Vehicle drain + CosmicDustGenerator dust clear)
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

**Owns:** Vehicle list, active `DrumTrack`, `InstrumentTrackController`, `CosmicDustGenerator`, `NoteVisualizer`, ring-glyph applicator reference, phase-in FX, vehicle-trap spawning, note-tether cleanup, `AnyCollectablesInFlightGlobal()` (shared collectable-in-flight query used by `StarPool`/`PhaseStar`).

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

Readiness is **zap-count-only** — there is no charge/threshold gate. `_displayedCharge01` (`PhaseStar.cs:149`) is a pure visual lerp of `ZapProgress01` (drives the diamond-fill animation); it does not gate ejection. `IsEjectionReady()` = descriptor valid + `ReadyLatched` (zap quota met), period. On entering Dormant (`EnterInMaze`), the navigator activates immediately but tentacles wait `tentacleActivationDelay` seconds (`PhaseStar.DormantState.cs`) before `dust.SetTentaclesActive(true)` fires, so the star visibly appears before it starts reaching for dust.

**Tentacle drain (`PhaseStarDustAffect`):** one tentacle grows per available role-carved cell, capped by the node payload — the zap budget is `RemainingZapCount` minus in-flight tentacles, so a 5-note payload with 3 carved cells grows 3 tentacles at once and adds more as cells are carved. Each drained cell goes through `CosmicDustGenerator.ZapClearCellHeld()` (no regrow while held) and is registered on the star; the zap is credited immediately on drain (`CreditZap()`), not deferred to full tentacle retraction. When the payload's zap count is met, acquisition disables and the star latches `ReadyLatched`. Targeting itself is gated on `CosmicDust.IsVisuallyPresentForTargeting()`, which since the flower-bloom redesign also requires `HasBloomedFlower` — a freshly-generated or non-vehicle-regrown cell is invisible to tentacles even if solid and role-carved, until a vehicle has plow-carved it at least once.

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

A note-carrying orb spawned in bursts by `DiscoveryTrackNode` (or instant-filled by `SuperNode`). Has three lifecycle paths:

**Path 1 — Auto-deposit:**
```
Spawned → BeginSpawnArrival() → MovementRoutine() (autonomous drift)
  → Vehicle trigger pickup → OnCollectableCollected()
  → BeginCarryThenDepositAtDsp() → CarryAndDepositRoutine()
  → arrives at NoteVisualizer marker → InstrumentTrack.OnCollectableCollected()
```

**Path 2 — Manual release:**
```
Vehicle trigger pickup → OnCollectablePickedUpForManualRelease()
  → Vehicle.EnqueuePendingCollectedNote()
  → DrumTrack.OnStepChanged → Vehicle.TryReleaseQueuedNote()
  → Vehicle.CommitManualReleaseAtStep()
      → Collectable.MarkAsReportedCollected() / OnManualReleaseConsumed()
      → InstrumentTrack.CommitManualReleasedNote()
```

**Path 3 — Discard → Time Bomb** (`Collectable.Bomb.cs`):
```
Vehicle discards a carried note (missed release window, sacrificed outside window,
manual-release queue overflow, or Vehicle death) → OnManualReleaseDiscarded(step)
  → ArmAsTimeBomb(step)
      Rigidbody2D → Kinematic (pinned exactly in place — immune to the physics
        engine's own collision depenetration as its collider grows, which a
        Dynamic body is not; still generates collisions against the Dynamic
        Vehicle for bump-detonation)
      fuse = InstrumentTrack.ComputeDepositDsp(step) − now, floored at minFuseSeconds
        (same DSP step-clock math that schedules normal note deposits — a note
        discarded right after its own step passed gets nearly a full loop to
        clear the area; discarded right before its step gets a short fuse)
      radius resolved once here, not in Detonate() (ResolveAndCacheBombBlastRadius,
        Collectable.Bomb.cs:114-122), so a telegraph ring (Collectable.BombTelegraph.cs)
        can show the true blast radius for the whole fuse — pulsing via
        tuning.bomb.telegraphPulseHz/telegraphPulseMin, alpha ramping to telegraphMaxAlpha
      swells + tints toward warningColor every frame until fuse-out
  → Detonate() — on fuse-out OR immediately on Vehicle collision (TriggerEarlyDetonation)
      radius = MusicalRoleProfile.discardBlastRadiusCells (grid cells; 0 = fall back to
        CollectableTuning.bomb.blastRadius ÷ cell size) — one radius drives everything below
      for every Vehicle within radius: Vehicle.DrainEnergy() + Vehicle.TriggerDrainFeedback()
        + Vehicle.ApplyKnockback() (tuning.bomb.knockbackImpulse, falls off toward
        knockbackEdgeFraction at the blast edge — same distance falloff shape as the drain)
      CosmicDustGenerator.TriggerDiscardBlast(centerCell, radius, regrowDelaySeconds) — carves
        every solid dust cell in the disk (source CollectableDiscard); regrow delay comes from
        MusicalRoleProfile.discardBlastRegrowDelay when set, else the normal resolver default;
        each cell regrows respecting its own identity (hidden Voronoi role if still gray, its
        already-revealed role otherwise) — no role is forced or biased, this is the same
        carve/regrow pipeline every other clear source uses
  → FlashThenImplode() — flashes to a size matching the resolved radius, then collapses → Destroy()
```
An armed bomb still counts toward `GameFlowManager.AnyCollectablesInFlightGlobal()` until
this sequence fully destroys it — `StarPool`/`PhaseStar` bridge gates wait on a ticking
bomb exactly like an uncollected note.

Radius is authored per role (`MusicalRoleProfile.discardBlastRadiusCells`) so a heavier role
(Bass) can be tuned to a bigger blast than a lighter one (Lead) — the same number is used for
the visual flash size, the vehicle-damage falloff, and the dust-clear disk, so what the player
sees blasted is exactly what gets cleared.

**Owns:** assigned note + step + bin, carry state, grid occupancy cell (`_currentCell`), home-pull anchor state (`_homeWorld`/`_homeFreeRadius` — a numeric elastic pull, not a rendered tether line), bomb-armed state once discarded (fuse timing, swell/detonation/implosion scale, Kinematic pin — `Collectable.Bomb.cs`).

**Autonomous movement:** the timeline ghost pulse (`DrumTrack.OnStepChanged` crossing the note's step) sounds the note and arms a movement intent for the note's duration; role-specific patterns (Bass charges, Lead headings, Groove darts, Harmony orbits) run under a home pull with vehicle-flee. **While an intent is armed the note plows through dust** (`CarveCellPreserveGray`, source `CollectablePlow` — gray regrow, temporary corridors); between intents it bounces off dust. A collectable only fully stops plowing/bouncing when its own cell *and* all 8 neighbor cells are solid (`IsFullyTrapped`, `Collectable.Movement.cs`) — anything less and it keeps moving. The arrival flight also carves its path gray (source `CollectableArrival`); during that flight solid colliders are disabled and pickup is refused, so vehicles pass through a still-arriving collectable rather than colliding with or collecting it (`BeginSpawnArrival` protected-flight window, `Collectable.cs`).

**Listens to:** `DrumTrack.OnStepChanged` (ghost pulse → intent arming, trail follow timing).

**Emits:** `OnDestroyed` (cleanup accounting) — pickup itself is a direct call (`InstrumentTrack.OnCollectableCollected()` / `.OnCollectablePickedUpForManualRelease()`), not an emitted event.

**Tuning:** `Assets/Scripts/Gameplay/Mining/CollectableTuning.cs` is a `ScriptableObject` centralizing the non-role-varying numeric knobs referenced above (base motion, deposit timing, absorb VFX, idle pulse, note trail, dust-collision, bomb params) — role-varying values (blast radius, regrow delay, drain resistance) stay on `MusicalRoleProfile` instead.

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

**External energy drain:** `DrainEnergy(amount, source)` (`Vehicle.Energy.cs`) is the shared
entry point any system uses to damage a vehicle from outside its own boost loop — dust
boost-collision resistance (`CosmicDust.Collision.cs`) and a detonating bomb Collectable
within blast radius (`Collectable.Bomb.cs`, Path 3 above) both call it. A bomb hit also
calls `TriggerDrainFeedback(color)` (`Vehicle.Damage.cs`) — a momentary flash-to-hit-color,
shrink, and spring-back reaction reusing the same flicker/pulse machinery as the
`DiscoveryTrackNode`-bump reaction, so the hit reads on the vehicle itself rather than only
the fuel UI ticking down.

**Listens to:** `DrumTrack.OnStepChanged` (release cue beat countdown).

**Calls out:**
- `NoteVisualizer.TryGetNextUnlitStepExcluding()` — finds target for ghost cue
- `dustGenerator.SetVehicleKeepClear()` / `ChipDustByVehicle(…, profile)` — dust pocket + plow; the ship's `regrowDelayMultipliers` scale the carved cell's regrow delay per role (growth agent — a Bass-accelerator ship regrows Bass dust sooner). A vehicle carve is also the only path that blooms the cell's flower on regrow, making it PhaseStar-targetable (see CosmicDustGenerator / PhaseStar).

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
| `TriggerDiscardBlast()` | `Collectable.Bomb.cs Detonate()` — discard-detonation dust clear, source `CollectableDiscard` |
| `HardStopRegrowthForBridge()` | `BridgeCoordinator` bridge start (also flushes held cells) |
| `BeginSlowFadeAllDust()` | `BridgeCoordinator` bridge fade |
| `ResumeRegrowthAfterBridge()` | `SceneFlowCoordinator` next motif start |
| `ApplyActiveRoles()` | `SceneFlowCoordinator` motif swap |

**Regrow delay resolution** (`ResolveRegrowDelay`, precedence high → low): explicit per-call override → carved cell's `MusicalRoleProfile.regrowthDelay` (vehicle carves only) → maze `dustTiming` per-source delay → maze base `regrowDelay` → 8s fallback. Vehicle carves then scale the result by the carving ship's per-role `regrowDelayMultipliers`. Cells flagged `ForceGrayRegrow`/`ZapForceGray` always regrow gray and uncharged — role tint (and drainable charge) is only earned by vehicle plow-carves or DiscoveryTrackNode exhaust painting.

Role imprinting: a vehicle plow-carve promotes the cell's hidden Voronoi role into the active imprint, so it regrows in its true role color with charge. Same-role cells attract nearby `DiscoveryTrackNode`/`SuperNodeTrackNode` and `Collectable` movement.

**Spawn-time color reveal is not hidden by default.** Every occupied cell's `hiddenRole` is a Voronoi ground-truth assigned at maze build time, but `imp.role`/`dust.Role` stay `MusicalRole.None` until a vehicle actually carves it — that's what gates targeting, carve resistance, and star-arming (untouched by any of this). The cell's *displayed color*, though, is a separate, purely cosmetic pass: `ApplyHiddenColorPreview` (`CosmicDustGenerator.MazeGeneration.cs`, run at the end of every `BuildMazeRoleImprints` path — single-voice, multi-voice Voronoi, and Exact-Count below) colors each cell with `Color.Lerp(hiddenRole's true MusicalRoleProfile color, Color.black, hiddenRoleColorDarken01)` instead of flat `mazeTint` gray, so the maze reads as "roughly what it is" from the moment it spawns, well before any carving. `CosmicDustGeneratorConfig.hideDustColorAtSpawn` (default `false`) is the one switch that turns this off entirely, falling back to flat `mazeTint` for every cell. Carving still promotes the real `Role` and reveals the full, un-darkened color exactly as before — this preview only narrows the gap between "secret" and "revealed," it doesn't erase it.

**"No musical role" dust is not the same thing as this gray/darkened preview.** A `hiddenRole`-bearing cell is *always* owned by some role underneath, whether it's currently showing a darkened preview, mid-regrow gray (`ForceGrayRegrow`/`ZapForceGray`, see Regrow delay resolution above), or fully revealed. True `MusicalRole.None` dust — no hidden role at all, permanently — only exists via Exact-Count Maze Mode's leftover generic fill, in two visually- and behaviorally-distinct sub-types (never-regrow vs. quick-regrow); see below.

Flower bloom (`CosmicDust.Flower.cs`): each cell carries a hidden `Flower` child sprite pair, normally inactive. `CommitRegrowCell` persists the `DustClearSource` that triggered a pending regrow (`_pendingRegrowSource`, written in `ClearCell`) and, only when that source was `VehiclePlow`, calls `dust.BloomFlower(regrowTint)` — the flower fades in role-tinted alongside the regrowing dust. Every `ClearCell` call unconditionally calls `dust.HideFlower()` first, so any clear (vehicle re-carve, tentacle drain, any other source) resets the tell; only a vehicle carve can re-bloom it. `HasBloomedFlower` gates `CosmicDust.IsVisuallyPresentForTargeting()` (see PhaseStar below) — dust regrown by any non-vehicle source still regrows and is still solid/collidable, it's just untargetable by tentacles until a vehicle carves it at least once.

---

### Exact-Count Maze Mode ("Puzzle" Territory Sizing)
`CosmicDustGenerator.MazeGeneration.cs` (`ClaimExactCountTerritoryAndFillGeneric`), `DustImprintStore.cs`, `DustRoleDensityTracker.cs`, `MazeGeoFeature.cs` (`MazeRoleGeoConfig`), `MotifNoteDensityAnalyzer.cs`, `MotifProfile.cs`

An alternate to the default Voronoi-partition maze build. A per-role `MazeRoleGeoConfig.useExactNoteCount` flag switches that role's territory from the relative density-weight formula to an absolute cell count: `cellCount = round(avgNoteCount * notesToCellsMultiplier)`, claimed by nearest-cell assignment out from greedily-spread seed points (`PickFarthestSeeds`) rather than a full-maze Voronoi split. If *any* active role in the motif uses exact-count sizing, **every** role does — a non-flagged role gets no territory at all in that motif; its would-be cells fall into the leftover pool instead.

Leftover (unclaimed) cells become generic `MusicalRole.None` filler dust, split by `MotifProfile.genericFillNeverRegrowRatio` into two visually-distinct sub-types (`DustImprintStore.SetHiddenRoleGenericFill`, tagged `hiddenRoleIsGenericFill`/`genericFillNeverRegrows`):
- **Permanent filler** (darker `CosmicDustGeneratorConfig.genericFillTint`) — once cleared, never re-enters the regrow cadence for the rest of the motif (`CosmicDustGenerator.CellLifecycle.cs` checks the flag in `RequestRegrowCellAt` and adds the cell to `_permanentClearCells` instead of scheduling regrow).
- **Quick-regrow filler** (ordinary `mazeTint`) — regrows normally, but bypasses the shared step-gate queue entirely (`CosmicDustRegrowthController`'s `shouldBypassStepGate` hook) so hundreds of inert filler cells can't starve real role-dust out of the queue.

`DustRoleDensityTracker` tracks generic-fill solid count separately (`GenericFillSolidCount`) so `GetRoleRelevantSolidFraction()` — which paces regrow-delay density easing — can exclude inert filler from the density figure a role's authored `regrowthDelay` is judged against; the literal filler-inclusive `GetSolidFraction()` is left untouched for the systems that intentionally key off it (`GravityVoidController`, `DrumTrack`'s density-driven beat intensity, Flow D). `ResolveRegrowVoice` also special-cases `hiddenRoleIsGenericFill` so a generic cell always regrows `None` rather than drifting into a neighboring role's color via the normal plurality/least-dense fallback.

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
`Assets/Scripts/Visualizers/MotifRingGlyphApplicator.cs` (+ partials `.GameplayRings.cs` per-bin rings / tether breaking / record fade-out, `.RecordRings.cs` snapshot rendering / playing-bin highlight / selection backdrop / firework, `.Wave.cs` carousel note-pulse mode, `.SpinOff.cs` roll-off exit, `.RingBuilder.cs` mesh + GameObject construction, `.Animation.cs` draw-in / travel-dot / contour-tween / shake coroutines) + `RingGlyphConfig.cs` (SO knobs) + `MotifRingGlyphGenerator.cs` (geometry) + `Assets/Scripts/Visualizers/BinRingController.cs` (drives gameplay ring spawn/deformation off the drum playhead) + `Assets/Scripts/Visualizers/Notes/NoteTether.cs` (shared cable renderer, also used for the manual-release ghost cue).

Each ring is a filled annulus tinted by musical role, plus a contour line that dips inward ("tug") at each note position — the applicator's own header comment describes the look as record-groove tracks. Three ring collections exist on the applicator: `_gameplayRings` accumulate live, one per completed bin, during play; `_remainingRings` are gray placeholders for motif progress not yet completed (these and locked-preview rings are built with `skipContour` — fill mesh only, `Contour` is null); `_recordRings` render a full completed-motif snapshot for the bridge display, `SolarSystemRecordDisplay` and the PhaseLibrary carousel. Each `RingEntry` owns its fill `Mesh` (`FillMesh`) — `DestroyEntry`/`DestroyList`/`DestroyDisc` free meshes along with GameObjects, and `DestroyAllChildren()` is the single teardown used by `AnimateApply` and `OnDestroy` (it also nulls the cached selection-backdrop/disc refs, since `Destroy` is deferred to end of frame). A fixed sorting-order stack keeps the whole thing layered correctly over the maze (`MotifRingGlyphApplicator.RingBuilder.cs`): background discs (1–2) → ring `Fill` (3) → `Contour` (4) → travel dot (5) → `NoteTether` cable (`Foreground` layer, order 10) — without it the record reads as a see-through wireframe with dust visible behind it.

**Motif-start record reveal:** `SceneFlowCoordinator` calls `BinRingController.ShowInitialRecord()` / `FadeInitialRecordOut()` / `GetRecordHoldDuration()` (thin forwarders onto the applicator) at the start of every motif — immediately, in darkness, before the maze exists for the very first motif of a session; after the new maze has already fully generated for every other transition. `ShowInitialRecord()` populates the full placeholder-ring stack via `SetRemainingProgressRings(nodesPerStar)`. The record holds at full opacity for `RingGlyphConfig.recordHoldDuration`, then `FadeRecordOut()` fades everything (placed + remaining rings, both background discs) out over `recordFadeOutDuration` — or, for the very first motif, over an explicit duration passed in so the fade finishes exactly as the maze fully grows in. Two solid backdrop discs (`_placedBgDisc` sized to completed rings, `_fullBgDisc` sized to the whole record, both built by `CreateBackgroundDisc`/`BuildDiscMesh`) give the record a genuine opaque backing instead of a wireframe. The innermost not-yet-filled placeholder ring previews the motif's active role palette — static if the motif has one role, cycling through them (`AnimateNextUpRingColorCycle`, `nextUpColorCycleDuration`) if it has several — so the "next up" ring always hints at what's coming.

**Note preview tethers + ring-collision breaking:** while a bin's rings are deforming in (`DeformBinRingCoroutine`), each note about to land gets a `NoteTether` cable that grows in (`GrowIn`) from its timeline marker to its future ring landing spot, tracked in `_pendingRingNotes`. The tether retracts and self-destroys (`RetractAndDestroy`) the instant its travel dot actually departs (`onDepart`). While a tether is still live, a `Vehicle` colliding with the ring's `CircleCollider2D` can break it: `Vehicle.OnCollisionEnter2D` (`Vehicle.Damage.cs`) calls `MotifRingGlyphApplicator.TryBreakNextPendingTether(contactPoint)`, which finds the pending note whose ring-side anchor is closest to the contact point, cancels its travel-dot launch, retracts its tether, and calls `InstrumentTrack.RemovePersistentNoteAtStep()` + `NoteVisualizer.RemoveCommittedMarkerAtStep()` (which cancels the note out of `NoteAscensionDirector` first, via `CancelAscension()`, so ascension doesn't hold a dangling reference) — the note is undone as if it had never landed. A successful break plays a low-frequency "released spring" shake (`PlayBreakShake`) and spawns a tint-matched particle burst at both the marker and the ring anchor (`PlayBreakShake` is private to the applicator); colliding with the ring when nothing is pending instead plays a short, stiff denial shake (`PlayDenialShake`) — both driven by Perlin-noise-decayed `transform.localPosition` jitter (`ShakeRoutine`), config'd via `RingGlyphConfig`'s Ring Collision Feedback knobs. A bin emptied this way re-fires `OnBinFilled`/`SpawnBinRing` when it refills; `SpawnBinRing(role, binIndex, color, totalSteps)` replaces the stale ring entry in place (matched by `binIndex`+`role`) instead of appending a duplicate. `DetachPendingRingTethers()` retracts all still-pending tethers without undoing their notes (called as the end-of-motif spin begins), and `SetSuperNodeMode(true)` (called by `PhaseStar.EjectionLogic.cs` and `SuperNodeTrackNode.cs`) suppresses the per-bin auto-hide of the ring stack while a SuperNode fills bins. `WaitAndLaunchDot` and `HideGameplayRingStackAfterLoopBoundary` subscribe to `DrumTrack` events inside `try/finally` so a stopped coroutine can't leak its handler.

There are **two distinct consumers**, and they use different methods — this is the biggest place the old documentation was wrong:
- **Live bridge:** `BridgeCoordinator.PlayMotifBridgeAndRestart()` calls `MotifRingGlyphApplicator.SpinAndRollOffActiveRings(spinDur, rollOffDuration)` on the accumulated gameplay rings (spin 360°, tilt, scale to zero). The ring stack is torn down by that method itself.
- **Library/browsing UI:** both render a *past* completed motif independent of live gameplay, but from different sources.
  - `SolarSystemRecordDisplay.cs` adds a fresh applicator per record and calls `AnimateApply(snapshot)` on this session's `BridgeFlow.MotifSnapshots` (not disk).
  - `PhaseLibraryCarousel.cs` (snapshots loaded via `RingSessionStore.LoadAllRingsFromDisk()`) draws the centered glyph with `AnimateApply(..., replayLastRing:false, notePulse:true)`, other glyphs with `ApplyVinyl`, and the not-yet-played entry with `ApplyLockedPreview`. It drives the glyph with `SetFixedFit` (constant world radius), `SetSelectedBackdrop`, `SetAlphaScale` and, from `RingPreviewPlayer`, `SetPlayingBin`, `PulseNote` and `SpawnNoteFirework`.
  - Every applicator calls `GameFlowManager.RegisterRingGlyphApplicator(this)` in `Start()`. That is harmless today because `GameFlowManager` lives only in `Main.unity`, but would overwrite the gameplay reference if a display ever ran alongside it.

**Motif snapshot persistence:** `BridgeCoordinator.BuildPhaseSnapshotForBridge()` builds a `MotifSnapshot` (`Assets/Scripts/Utilities/MotifSnapshot.cs`) fresh each bridge from `InstrumentTrack.GetPersistentLoopNotes()` + per-bin fill state — it is not read from any long-lived store. `RingSessionStore.SaveRingToDisk(motifSnap)` (`Assets/Scripts/Utilities/RingSessionStore.cs`) then writes it to `Application.persistentDataPath/RingSessions/ring_{phaseIndex}_{motifIndex}.json` for later browsing. (Older documentation referenced a `ConstellationMemoryStore.StoreSnapshot()` call — that class does not exist in the current codebase.)

**2D glyph — removed.** `GlyphApplicator.cs`, `MotifGlyphRegistrar.cs`, and `MotifGlyphPreview.cs` (the editor preview tool) have been deleted; `GameFlowManager`'s `motifGlyphApplicator` field and `RegisterGlyphApplicator()` method are gone with them. The three components were attached to a shared, inactive-by-default "Glyph Generator" GameObject in `GeneratedTrack.unity` alongside the live `BinRingController` — that GameObject's component list was trimmed rather than deleting the object itself, since `BinRingController` still needed it. `MotifGlyphGenerator.cs` stays: it declares `GlyphPolyline`, which the *live* `MotifRingGlyphGenerator.cs` reuses (`Generate()`/`GenerateSingleRingAtRadius()` etc.) — only the parts of that file specific to the dead 2D-glyph geometry (`GlyphOutput`, `GlyphSpeciesParams`, the `MotifGlyphGenerator` static class itself) are now unreachable, left in place rather than extracting the shared type into its own file.

**Notation-line contour knobs (`RingGlyphConfig`).** The contour is the thin `LineRenderer` outline of each ring; the filled annulus never dips. `MotifRingGlyphGenerator.BuildRingPoints` gives every note a `cos²` bell of angular half-width `tugHalfWidthRad` (scaled 0.7×–1.3× by the note's `CommitTime01`) and depth `tugDepthFraction` × that ring's radius; the **deepest single note wins** (no summing), so overlapping notches fuse into a plateau. Depth 1.0 reaches the glyph center. `tugDepthFraction` also positions travel-dot landings, tethers and fireworks (`outerRadius × (1 − depth)`), and the carousel's centered glyph (note-pulse mode, `.Wave.cs`) shows only `waveRestDepth` of the baked depth between notes. Neighboring steps are only ~0.2–0.4 rad apart, so half-widths above ~0.3 rad merge notes; `tugHalfWidthRad` is range-limited to (0, π].

**Orbit-line needle.** `MotifOrbitLineRenderer` wobbles **every** orbit line like a record groove, with the **needle** as the epicenter: `DrawOrbit` flags the first near-side run after the selected glyph's footprint (its right edge, by the playback point); that run's start is the epicenter and its length the unit distances are measured in. Each run is resampled densely and `Update` adds a faint idle wave that radiates from the needle plus a ripple per note (`PulseNeedle(startDsp)`, from `PhaseLibraryCarousel.HandleNotePulse` on the dip's clock) that travels outward in both directions over `rippleReach` needle-run lengths, briefly widening and brightening each line as it passes. Amplitudes are in line widths, scaled by the line's alpha, so dim orbits wobble less. If no run is flagged (mid-slide) the last epicenter is kept. Toggle with `shimmerEnabled`.

**Playing bin takes the outer slots.** In the selected glyph, `SetPlayingBin` also re-lays out the bands: the playing bin's rings move to the outermost slots, the other bands keep their order and shift inward (A,B,C → B,C,A when A plays), and the recorded order returns when playback stops. `ComputeSlotOuterRadii` reorders the stored ring keys and reuses `ComputeRadii`; `AnimateRingSlots` slides each ring over `binSwapDuration` by rewriting its fill annulus (`SetRingOuterRadius`, thickness preserved) and scaling its baked contour via `RingPulseState.Scale`. `RingEntry.CurOuter` is the live radius (fireworks use it). Toggle with `playingBinToOuter`.

**Note-pulse rotation.** In the selected glyph, each ring turns **clockwise only, continuously** (the direction notes play in): its angle is a constant-speed path through the notes, so each note passes the cue point exactly as it pulses (`notePulseCueDeg`: degrees CCW from 3 o'clock; default 0 = 3 o'clock, 90 = 12, 270 = 6) and the ring keeps moving to cue the next one — it never stops on a note. A ring starts toward its next note when its previous note starts, so the long return after its last note spans the whole idle gap, even through other bins' playback. `RingPreviewPlayer` fires `OnNoteApproach(role, bin, nextLocalStep, nextStartDsp, prevStartDsp)` when a role's note fires, targeting that ring's next note (wrapping to its first note next loop); at playback start it fires one per ring for its first note. `ApproachNote` (`.Wave.cs`) queues the turn per ring, chained end to start. Speed is constant per segment, so it changes at the boundary between a dense run and a long gap. Pulse state (dip centers) is built from the baked contour when the ring is built (`PreparePulseState`), not after the draw-in, so look-aheads work from the very first loop. `PulseNote` turns the ring itself (0.3s) only as a fallback if no approach arrived. Toggle with `notePulseRotateToTop`.

**Ring layout knobs (`RingGlyphConfig`, "Layout (Shared)").** Think of tree-trunk rings: `innerRadius` = size of the centre hole, `ringThickness` = band width, `ringSpacing` = gap between rings in the same bin band, `interBinSpacing` = gap between bin bands. Which mode honors them:

| Glyph | Layout | `innerRadius` / `ringThickness` | `ringSpacing` / `interBinSpacing` |
|---|---|---|---|
| Carousel **selected** (center) | free (`SetFreeLayout`) | literal local units | literal local units |
| Carousel **unselected** | fixed-fit (`SetFixedFit`, hardcoded hole/fill in `ComputeFixedRadii`) | ignored | only the ratio `interBinSpacing / ringSpacing` |
| Gameplay rings | `RingInnerRadius`, then scaled to the play area | proportions only | proportions only (`ringSpacing` uniform) |

The selected glyph's size follows from its ring count times these units, multiplied by `PhaseLibraryCarousel.selectedGlyphScale`; it can be much larger than its neighbors. Layout is baked at build time, so `Settle` rebuilds a glyph via `ApplyEntry` when its selected state flips.
- **Which asset drives what:** `RingGlyph Canvas.asset` = the carousel's centered glyph; `RingGlyph Canvas Not Selected.asset` = every other carousel glyph (both wired in `PhaseLibraryCarousel.prefab`); `In Game Ring Glyph.asset` = gameplay rings; `Final Rings.asset` = TrackFinished.
- **Baked at build:** contour points are generated when a ring is built, from the glyph's config at that moment, so editing a config in Play mode does not change glyphs already on screen. `PhaseLibraryCarousel.Settle` rebuilds a glyph via `ApplyEntry` when its selected state flips so it picks up the other config's shape (it used to swap only the config and keep the old baked shape). `SetContourWidth` and `SetNotePulse` now have no carousel callers.
- `RingGlyphConfigEditor` shows warnings for `tugDepthFraction ≥ 0.95`, `tugHalfWidthRad ≥ 1.2` and `segments < 128`.

**Ring glyph audit (2026-11-06) — known, unfixed:**
- Note angle (`localStep/binSteps·2π + ringStartAngleOffset + RingOrdinalAngleJitter`) and the polar-to-vector math are duplicated in `GameplayRings`, `RecordRings` (twice), `Animation`, `MotifRingGlyphGenerator` and `RingPreviewPlayer`; track-color matching is implemented four different ways (`RecordRings.NotesForRing`, `trackByQColor`, `BuildRingKeys`, `RingPreviewPlayer.ResolveRoleForNote`). A shared `NoteAngle`/`PolarPoint`/color comparer would remove it.
- `SolarSystemRecordDisplay.CountRingKeys` mirrors private applicator logic (`BuildRingKeys`, `ComputeRadii`) and ignores the fixed-fit path.
- `LineRenderer` contours are written point-by-point (`SetPosition` loops) outside `.Wave.cs`, which already uses a reusable array + `SetPositions`; `AnimateMeshFill` calls `SetTriangles` + `RecalculateBounds` every frame; the carousel's `FadeSelection`/`DrawOrbit`/`PlaceGroup` allocate or `GetComponent` per frame.
- `SetNotePulse` is a silent no-op for glyphs not drawn via `AnimateApply(notePulse:true)`/`ApplyVinyl`; `_pulseStates` is only pruned when a new pulse starts. `.Wave.cs` / `waveRestDepth` are legacy names for the "note pulse" feature.
- `effectsMixerGroup` is unset in every scene/prefab, so launch/impact one-shots (`PlayOneShotRouted`) bypass the Effects mixer group.
- `ShakeRoutine` resets `localPosition` to zero while `RefreshPlayAreaFit` writes the world position — only correct if the applicator's parent sits at the origin.
- Legacy serialized keys remain in `Final Rings.asset` and `In Game Ring Glyph.asset`; Unity drops them on next save (the `FormerlySerializedAs("noteTetherGrowDuration")` is still needed until then).

---

## Telemetry

### MazeTelemetryRecorder — Greater Maze Telemetry
`Assets/Scripts/Telemetry/MazeTelemetryRecorder.cs`, `TelemetryModels.cs`, `MazeTelemetryStore.cs`

A passive, fully decoupled observer — not referenced by `GameFlowManager` or any gameplay system (absent from the Layer Map above for that reason, same treatment as Glyph & Ring Systems), and it never mutates state anywhere else. Deployed as a third `MonoBehaviour` on the `SteamManager.prefab` GameObject, piggybacking on its existing `DontDestroyOnLoad`. It captures, per run, the player's ordered path through Phase → Motif → Motif and the exact committed musical solution for each completed motif — the data set behind a future public "greater maze" visualization (motifs as graph nodes weighted by visitation, transitions as edges weighted by frequency, and per-motif solution distributions). Complementary to, and shares no code with, the Steam Stats funnel (`SteamStatsManager`) — Steam Stats answers "how far did players get," this answers "where did they go, and what did they make there."

**Wiring** (lazily polled in `Update()`, same pattern `SteamStatsManager.Hooks.cs` uses, since `GameFlowManager.Instance`/`SessionState`/`phaseTransitionManager` aren't guaranteed non-null at `Awake()`):

| Event | Fired by | Recorder does |
|-------|----------|----------------|
| `SessionStateCoordinator.GameStateChanged` | `SessionStateCoordinator.SetCurrentState` | `Playing` → starts a new `MazeRunRecord` (fresh GUID, UTC start time); `GameOver` → finalizes + writes (`EndReason="Completed"`); any other transition while a run is active — i.e. an early quit via `SceneFlowCoordinator.QuitToSelection` — finalizes + writes (`EndReason="QuitEarly"`) |
| `PhaseTransitionManager.OnMotifChanged(oldMotif, newMotif)` | `ApplyMotifToAudioAndTracks`, on every genuine `StartChapter`/`AdvanceMotif`/`JumpToMotif` transition — including the run's very first motif, where `oldMotif` is null | Appends a `MotifVisit` (`PhaseId`, `MotifId`, `VisitOrder`) for `newMotif`; if a prior visit already exists this run, also appends a `MotifTransition` edge from it |
| `GameFlowManager.OnMotifCompleted` | `BeginMotifBridge`, fired synchronously before the bridge coroutine starts (see Flow C) — `phaseTransitionManager.currentMotif` still refers to the motif that just finished at this instant | Walks `InstrumentTrackController.tracks`, builds a `MotifSolution` from each track's `GetPersistentLoopNotes()` + `IsBinFilled()`, tagged to the current `VisitOrder` |

Because `phaseTransitionManager` is a fresh instance every run (nulled by `QuitToSelection`, reassigned on the next `GeneratedTrack` scene load), the recorder resets its wiring flag on every `Playing` transition so it re-subscribes against the new instance — unlike `SteamStatsManager`'s once-per-process wiring, which only needs first-time milestones and never faces this. This lazy-poll wiring has one known edge case: `StartChapter` can fire the run's first `OnMotifChanged` before the recorder's next `Update()` has subscribed, so `HandleMotifCompleted` self-heals by backfilling the missing first `MotifVisit` from `phaseTransitionManager.currentMotif` if `Path` is still empty when a motif completes — real gameplay pacing gives every later transition enough time for the poll to catch up, so this only ever applies to motif one.

**Solution capture is deliberately narrow** — only gameplay state that materially determines the resulting composition. Per track: `MusicalRole`, `VoiceIndex`, committed notes (`StepIndex`, `MidiNote`, `DurationTicks`, `Velocity` — sourced from `InstrumentTrack.GetPersistentLoopNotes()`, the same quantized-step data `BridgeCoordinator.BuildPhaseSnapshotForBridge()` reads for `MotifSnapshot`) and per-bin fill state. Voices the player never touched are skipped entirely. Explicitly excluded: vehicle movement, carving history, failed placements, elapsed time, remaining energy, collisions — none of it is read. Voices are sorted by `(Role, VoiceIndex)` and notes by `(StepIndex, MidiNote)` before serializing, so two runs that commit the same notes always serialize identically regardless of real-time collection order — this determinism is what lets a future aggregation layer treat two solutions as equal by exact comparison, without any similarity heuristics baked into capture itself.

**Storage:** one JSON file per run — `MazeTelemetryStore.SaveRun()` → `Application.persistentDataPath/GreaterMazeTelemetry/run_{runId}_{timestamp}.json` — never overwritten (unlike `RingSessionStore`'s per-`(phase,motif)`-slot overwrite pattern; this is an append-only history, not a "latest state" cache). No backend/upload path exists yet — capture and local persistence only, in a format sized for easy later batch ingestion.

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
      [auto] → BeginCarryThenDepositAtDsp() → InstrumentTrack.OnCollectableCollected()
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
  → burst cleared
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
      fires OnMotifCompleted synchronously, before the coroutine below starts
        ← MazeTelemetryRecorder snapshots the finishing motif's solution here (see Telemetry)
  → BridgeCoordinator.PlayMotifBridgeAndRestart():
      SessionState.SetGhostCycleInProgress(true) / SetBridgePending(true)
      FreezeGameplayForBridge() — clears tethers, pending notes, live Collectables/DiscoveryTrackNodes, destroys the StarPool
      BuildPhaseSnapshotForBridge() → MotifSnapshot from InstrumentTrack.GetPersistentLoopNotes()
      CosmicDustGenerator.HardStopRegrowthForBridge() + BeginSlowFadeAllDust()
      RingSessionStore.SaveRingToDisk(motifSnap)          ← persistence
      MotifRingGlyphApplicator.SpinAndRollOffActiveRings() ← live ring exit
      wait for loop boundary
      SceneFlowCoordinator.StartNextMotifInPhase() → PhaseTransitionManager.AdvanceMotif()/AdvancePhase()
          fires OnMotifChanged(oldMotif, newMotif)
            ← MazeTelemetryRecorder appends the path visit + transition edge here (see Telemetry)
          DrumTrack.ApplyMotif(newMotif)
          InstrumentTrackController full reset (clears loop notes, bin state, burst state)
          NoteVisualizer.BeginNewMotif_ClearAll()
          CosmicDustGenerator.ResumeRegrowthAfterBridge()
      SessionState.SetGhostCycleInProgress(false) / SetBridgePending(false)
  → new StarPool spins up, PhaseStars re-arm for the new phase
```

### Flow D — Track Fullness / Dust Density → Drum Intensity
Energy-burn-driven intensity is gone — `MotifProfile.driveBeatsFromDensity` (`[FormerlySerializedAs("driveBeatsFromEnergy")]`) marks that the old burn-rate tracking plumbing was removed outright, not just renamed.
```
DrumTrack.Update() → TickLoopBoundaries() (once per leader-loop boundary)
  → HandleBeatSequencingAtLoopBoundary()
      gates: driveBeatsFromDensity=true, entry-loop window elapsed, _carryLatched
             (a vehicle has carried at least one collectable), intensityDrumLoops non-empty
      → ComputeTrackFullness01() — avg loopMultiplier/maxLoopMultiplier across all InstrumentTracks (tier driver)
      → ComputeDensityIntensity(CosmicDustGenerator.GetSolidFraction()) — smoothed/hysteresis dust density (nudge)
      → intensity01 = fullness01 + (density01 − 0.5) × densityNudgeRange
      → ResolveIntensityClip(intensity01) — linear index into MotifProfile.intensityDrumLoops
      → ScheduleDrumLoopChange() → ArmPendingDrumLoopForNextLeaderBoundary() → TickDeckSwap()
          (dual A/B AudioSource decks, bar-aligned swap)
```
`DrumTrack.ApplyMotif()` is a separate path — phase/motif changes only. It commits the new `MotifProfile` refs, resets entry-loop/carry-latch state, and picks an entry clip via `ChooseEntryClip()` (random from `entryDrumLoops`). It takes no intensity parameter and never touches `ResolveIntensityClip`/`_intensityLoops`; within-motif intensity swaps run entirely separately, once per loop boundary, as above.

### Flow E — Dust Energy Economy
```
Vehicle plow-carves dust → cell regrows in its true role color + charge, flower blooms (vehicle-carve tell)
    (delay = maze dustTiming table × ship's per-role regrowDelayMultiplier)
  → PhaseStar grows one tentacle per bloomed, role-carved cell, capped at the node payload
      → each drained cell: ZapClearCellHeld() — held, no regrow; zap credited immediately; flower hides
  → payload zap count met → tentacles retract → star ReadyLatched
  → DiscoveryTrackNode (or SuperNode) ejected, holding the drained-cell batch
  → node captured / escaped / expired → ReleaseHeldCells()
      → cells regrow gray + uncharged (starDrainReleaseDelay), flower stays hidden
  → gray dust must be plow-carved again to re-earn role charge and re-bloom
```
Collectable dust removal (arrival flight + intent-window plowing) and SuperNodeTrackNode dust-seeking always regrow gray and never bloom the flower —
they open temporary corridors but never feed the star.

### Flow F — Discard → Time Bomb → Blast
```
Vehicle discards a carried note (miss window / sacrifice / queue overflow / vehicle death)
  → Collectable.ArmAsTimeBomb(authoredAbsStep)
      Rigidbody2D → Kinematic (pinned, immune to physics-engine depenetration)
      fuse = InstrumentTrack.ComputeDepositDsp(step) − now, floored at minFuseSeconds
      radius resolved once here (ResolveAndCacheBombBlastRadius) so a telegraph ring
        (Collectable.BombTelegraph.cs) can show the true blast radius for the whole fuse
      swells + tints toward warningColor each frame until fuse-out
  → Detonate() — fuse-out OR Vehicle bump (TriggerEarlyDetonation)
      radius = MusicalRoleProfile.discardBlastRadiusCells × cell size
               (0 → falls back to CollectableTuning.bomb.blastRadius, today's fixed value)
      for each Vehicle within radius:
        Vehicle.DrainEnergy(amount, "TimeBombCollectable")
        Vehicle.TriggerDrainFeedback(warningColor)
        Vehicle.ApplyKnockback(tuning.bomb.knockbackImpulse, falls off toward knockbackEdgeFraction)
      CosmicDustGenerator.TriggerDiscardBlast(centerCell, radiusCells, regrowDelaySeconds)
        → CarveCell() per solid cell in the disk, source CollectableDiscard
        → each cell regrows via the normal resolver: revealed role stays the same role,
          still-gray cells fall back to their permanent hidden Voronoi role — never biased
          toward a different role; regrow delay is MusicalRoleProfile.discardBlastRegrowDelay
          when set, else falls back to the normal resolver (MazePatternConfig.dustTiming
          .collectableDiscardRegrowDelay)
  → FlashThenImplode() — flash to the same resolved radius, then collapse → Destroy()
```
The note's own energy was already processed at pickup (a partial refund is granted on most
discard paths via `CollectEnergy(amount * .25f)`, `Vehicle.NoteRelease.cs`) — leaving it out
of the loop is what makes it unstable. Vehicle drain amount scales by the collectable's own
`MusicalRoleProfile.drainResistance01` (the same field/formula dust boost-collision drain
uses) and falls off with distance to `edgeDamageFraction` at the blast edge. Blast **radius**
is a separate per-role knob (`discardBlastRadiusCells`) — heavier roles (Bass) are meant to be
tuned to a bigger radius than lighter ones (Lead), and that one radius drives the vehicle
falloff, the dust-clear disk, and the flash-visual size together, so the visible blast is
exactly the area that gets cleared.

### Flow G — Ring Note Preview → Vehicle Break
```
InstrumentTrack bin fills → MotifRingGlyphApplicator.SpawnBinRing() → BeginBinRingDeformation()
  → DeformBinRingCoroutine(), per note in the bin:
      NoteTether.GrowIn() from timeline marker (BindStartByStep) to ring landing anchor
      → registered in _pendingRingNotes (tether, anchor, track, step, cancel flag)
      → WaitAndLaunchDot() waits for the note's live-recomputed trigger step
  [vehicle collides with ring's CircleCollider2D before the note's step arrives]
    → Vehicle.OnCollisionEnter2D → MotifRingGlyphApplicator.TryBreakNextPendingTether(contactPoint)
        nearest-anchor pending note found → cancel flag set → tether.RetractAndDestroy()
        → InstrumentTrack.RemovePersistentNoteAtStep()
        → NoteVisualizer.RemoveCommittedMarkerAtStep()
            → NoteAscensionDirector.CancelAscension() (drops it from ascension tracking first)
        → PlayBreakShake() + tint-matched particle burst at marker + ring anchor
    [no tether pending] → PlayDenialShake() only, no state change
  [note's step reached first] → onDepart: tether retracts, dot launches → onLaunch reveals
      the note on the ring's persisted contour (survives into the eventual MotifSnapshot)
```
A bin emptied by a break re-fires `OnBinFilled` once refilled; `SpawnBinRing` replaces the
stale ring entry in place (matched by `binIndex`+`role`) rather than duplicating it.
