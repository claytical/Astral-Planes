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
       ├─► [PathChoiceCoordinator]    ─── interstitial maze + exit roads between motifs
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
       │    [PhaseStar] ─── maze navigation, vine-tentacle drain, eject decision
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
`Assets/Scripts/Managers/GameFlowManager.cs` (+ `.LifeCycle.cs`, `.PlayerFlow.cs`, `.MatchEnd.cs`, `.QuitFlow.cs`, `.BridgeOrchestration.cs` — a short dispatcher that also passes the competitive `endMatch` flag, see Flow C; `Visualizers/GameFlowManager.BridgeVisuals.cs`)

Top-level facade. Holds the authoritative references to every major system and exposes coordinator-backed properties (`CurrentState`, `BridgePending`, `GhostCycleInProgress` all forward to `SessionState`).

**Owns:** Vehicle list, active `DrumTrack`, `InstrumentTrackController`, `CosmicDustGenerator`, `NoteVisualizer`, ring-glyph applicator reference, phase-in FX, note-tether cleanup, `AnyCollectablesInFlightGlobal()` (shared collectable-in-flight query used by `StarPool`/`PhaseStar`).

**Delegates to:** `SessionState`, `BridgeFlow`, `SceneFlow`, `PathChoice` (below) for everything state/sequencing-related.

**Session coroutines all run on this persistent object** (bridge, path choice, track setup, game over). `QuitToSelection()` and `AbortTutorialSession()` call `AbortSessionCoroutines()` first: `StopAllCoroutines()` + `SceneFlow.ResetAfterAbortedFlows()` (clears `_setupInFlight`, any half-finished screen fade and the motif banner), so a quit mid-bridge or mid-path-choice can't resume against nulled scene refs. A second quit while one is in flight is ignored.

---

### SessionStateCoordinator
`Assets/Scripts/Managers/SessionStateCoordinator.cs`

Owns player-session state: joined `LocalPlayer` list, readiness, `GameState` (`Begin/Selection/Playing/GameOver`), `GhostCycleInProgress`/`BridgePending` flags, game-over guard (`_hasGameOverStarted`). Fires `GameStateChanged` (the bridge-pending / ghost-cycle change events had no listeners and were removed). `CheckAllPlayersOutOfEnergy()` is the game-over gate, called from `GameFlowManager`.

---

### BridgeCoordinator
`Assets/Scripts/Managers/BridgeCoordinator.cs`

Owns the motif-bridge sequence (see **Flow C** for the full call order) and the in-memory `MotifSnapshot` history (`MotifSnapshots` list — this replaces the no-longer-existing `ConstellationMemoryStore` that older documentation referenced). `StampMotifStartTime()` marks when the current motif began, used to compute `MotifSnapshot.TrackBinData.MotifStartTime`.

---

### SceneFlowCoordinator
`Assets/Scripts/Managers/SceneFlowCoordinator.cs`

Owns scene transitions and next-motif/next-phase setup: `TransitionToScene()`, `FadeScreen(toBlack, duration)`, `StartNextMotifInPhase()` (hands the new `MotifProfile` to `DrumTrack.ApplyMotif()`, resets `InstrumentTrackController`, clears `NoteVisualizer`, resumes dust regrowth), `StartNextPhaseMazeAndStar()`, `StartChosenMotifOrPhase()` (path-choice exit). Every motif start ends with `DrumTrack.BeginMotifTransport()` once the new maze and star are ready (see **Drum Transport**).

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
| `OnStepChanged(stepIndex, leaderSteps)` | `InstrumentTrackCompositionSpawner`, `MotifRingGlyphApplicator.Animation` (travel-dot launch), `Collectable` (ghost pulse → movement intent) |
| `OnLoopBoundary` | `TrackNode` (expiry), `PhaseStar`, `HarmonyDirector`, `SoloVoice`, `LoopCollapseController`, `TrackExpansionController` (staged expand commit), `Wisp`, `NoteAscensionDirector`, `InstrumentTrackController.ChordProgression` |

The vehicle and NoteVisualizer playhead poll the DSP clock directly rather than subscribing.

**Key methods called on it:**
- `ApplyMotif(MotifProfile)` — swaps drum clip + timing, deferred to next boundary by `SceneFlowCoordinator`
- `SetBinCount(n)` — updates visual bin count after track expansion
- `WorldToGridPosition()` / `GridToWorldPosition()` — coordinate bridge for all spawners
- `GetRandomAvailableCell()` / `OccupySpawnCell()` / `FreeSpawnCell()` — grid occupancy for node placement

---

### InstrumentTrackController
`Assets/Scripts/Music/InstrumentTrackController.cs`

Coordinates the four `InstrumentTrack` instances; owns gravity void lifecycle; routes role-specific SFX.

**Owns:** multi-track playhead sync, gravity void (`GravityVoidController`), bin frontier allocation (`BinFrontierAllocator`).

Note placement is per player, not per controller: `NotePlacementMethod` (`Gameplay/Core/NotePlacementMethod.cs`) — `Auto` (default; notes auto-deposit at the quantized step) or `Manual` (carry + release button). Chosen in `PlayerSelect`, held on `LocalPlayer`.

**Calls out:**
- Bin growth is per track: each `InstrumentTrack` owns a `TrackExpansionController` (see Flow B)
- `CosmicDustGenerator.GrowVoidDustDiskFromGrid()` — void disk expansion (via `BeginGravityVoidForPendingExpand`)
- `NoteVisualizer.RecomputeTrackLayout()` / `ResyncLeaderBinsNow()` — marker layout rebuild on expansion

**SFX:** `NotifyCollected()` (`.PickupSfx.cs`) → pickup tick; `NotifyBinFilled()` → bin-complete; `NotifySuperNodeResolved()` (`.ChordProgression.cs`).

---

### InstrumentTrack
`Assets/Scripts/Music/InstrumentTrack.cs`

Per-role MIDI loop state. The **bin** is the expansion unit — each bin adds one loop's worth of note slots.

**Owns:** loop notes grid (`_loopNotes`, `persistentLoopNotes`), pre-cached NoteSet per bin (`_binNoteSets`), collectable burst accounting (`_burstParticipants`, `_burstMultiplierOwners`), `loopMultiplier` (1–4 bins active), `TrackExpansionController` (staged bin expansion).

**Gain:** each note pushes the role profile's `volumeMultiplier` as a channel gain trim (`MidiVoice.SetGainTrim`) rather than scaling velocity — see **Audio Mix**.

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
| `MazePatternConfig` | Maze wall pattern + `dustTiming.regrowDelay`: regrow delay for role-less (filler/None) cells only. The per-source overrides were removed 2026-11-17 (`LEGIBILITY_AUDIT.md` A5) |
| `ShipMusicalProfile` | Ship physics, fuel, plow footprint. `regrowDelayMultipliers` was removed 2026-11-17; it was 1.0 on every ship |

`MotifProfile` → references `RoleMotifNoteSetConfig[]`, `ChordProgressionProfile`, and `MazePatternConfig` (`mazePattern`).
`MusicalRoleProfile` → consumed by `CosmicDustGenerator` (imprint color), `DiscoveryTrackNode`/`SuperNodeTrackNode` (speed/agility), `PhaseStar` (dust affinity), `InstrumentTrackController` (SFX routing).

---

## Gameplay Subsystem

### StarPool
`Assets/Scripts/Phase/Star/StarPool.cs` (+ `.Spawning.cs`, `.EjectionEvents.cs`, `.BridgeGate.cs`)

The per-motif ejection authority. One `StarPool` exists per phase (owned by `DrumTrack._starPool`); it spawns a `PhaseStar` per active role with available dust, gates how many DiscoveryTrackNode/SuperNode ejections the motif is allowed, and decides when the harvest is done and the bridge can fire.

**Owns:** `_remainingEjectionsTotal` (seeded from `MotifProfile.nodesPerStar` — a single global count across *all* roles, not per-role), `_mineNodePending` (the single in-flight-sequence gate), `_lastEjectedRole`, `_ejectedBurstWasEmpty`, per-role `_activeStars`.

**Ejection budget — eject-time commitment (`StarPool.Spawning.cs`):** `Tick()` lets every role with dust and an empty star slot spawn a star; there is no spawn-time budget check. (Active roles are cached per motif, and the `HasAnyDustWithRole` grid scan only runs when a slot is actually empty.) The budget is spent only when a star actually ejects: `CanStarCommitEjection(isSuperNode)` = `!_mineNodePending && !HasUnresolvedMineNodeSequence() && (isSuperNode || _remainingEjectionsTotal > 0)`, wired into each spawned star so it self-checks at poke time and plays the not-ready head-shake (`RespondToRejectedPoke`, see Poke feedback) if denied. This deliberately serializes ejections — **one sequence in flight at a time, of any kind, across all roles** (a per-role ledger was considered and rejected as a bigger refactor; see `project_starpool_phase_system.md` memory for the full history).

**`_mineNodePending` gate — 4 clear paths** (`OnStarMineNodeResolved` and `HandleCollectableBurstCleared`, both in `StarPool.EjectionEvents.cs`):
1. **Normal burst** — the ejected role's `OnCollectableBurstCleared(hadNotes:true)` fires → decrements `_remainingEjectionsTotal`, clears the gate, triggers `DespawnLeftoverStars()` if the budget just hit zero.
2. **Empty-burst race** — a burst clears with `hadNotes:false` before the node resolves → sets `_ejectedBurstWasEmpty`; the later `OnStarMineNodeResolved` call clears the gate without spending a harvest.
3. **SuperNode** — no burst ever spawns for a SuperNode outcome; `OnStarMineNodeResolved` sees `wasSuperNode` and clears the gate directly, no spend.
4. **Expired/escaped DiscoveryTrackNode** — `wasExpired`/`wasEscaped` clears the gate and refunds nothing spent (harvest count is untouched either way — the deduction only ever happens in path 1).

**`DespawnLeftoverStars()`** (`StarPool.EjectionEvents.cs`): once `_remainingEjectionsTotal` hits 0, explodes every remaining active/paused star so `CheckBridgeGate()` (`StarPool.BridgeGate.cs`; requires zero live or paused stars and no unresolved node sequence) can pass and `PhaseStar` can hand off to the bridge. `CheckBridgeGate` also has a recovery branch: zero captures with every loop still empty re-runs `BuildPhasePlan()` (the motif restarts) instead of bridging.

**Listens to:** `InstrumentTrack.OnCollectableBurstCleared` (all tracks), `PhaseStar.OnMineNodeResolved` (per spawned star).

---

### PhaseStar
`Assets/Scripts/Phase/Star/PhaseStar.cs`

The per-role drain/eject actor spawned and gated by `StarPool`. Siphons dust from its attuned role's carved cells and, once its zap quota is met, ejects a `DiscoveryTrackNode` or `SuperNode`.

**Owns:** attuned role, tentacle (vine) set, armed/disarmed state, `_activeNode` / `_activeSuperNode` guards, `_heldDrainCells` (cells drained since the last node spawn).

Readiness is **zap-count-only** — there is no charge/threshold gate. `_displayedCharge01` (`PhaseStar.cs:149`) is a pure visual lerp of `ZapProgress01` (drives the diamond-fill animation); it does not gate ejection. `IsEjectionReady()` = descriptor valid + `ReadyLatched` (zap quota met), period. On entering Dormant (`EnterInMaze`), the navigator activates immediately but tentacles wait `tentacleActivationDelay` seconds (`PhaseStar.DormantState.cs`) before `dust.SetTentaclesActive(true)` fires, so the star visibly appears before it starts reaching for dust.

**Movement:** see **Star Maze Navigation & Vines** below.

**Tentacle drain (`PhaseStarDustAffect`):** tentacles are living vines routed around dust (see **Star Maze Navigation & Vines**). One tentacle grows per available role-carved cell, capped by the node payload — the zap budget is `RemainingZapCount` minus in-flight tentacles, so a 5-note payload with 3 carved cells grows 3 tentacles at once and adds more as cells are carved. Each drained cell goes through `CosmicDustGenerator.ZapClearCellHeld()` (no regrow while held) and is registered on the star; the zap is credited immediately on drain (`CreditZap()`), not deferred to full tentacle retraction. When the payload's zap count is met, acquisition disables and the star latches `ReadyLatched`. Targeting itself is gated on `CosmicDust.IsVisuallyPresentForTargeting()`, which since the flower-bloom redesign also requires `HasBloomedFlower` — a freshly-generated or non-vehicle-regrown cell is invisible to tentacles even if solid and role-carved, until a vehicle has plow-carved it at least once.

**Poke feedback and sleep** (`PhaseStar.PokeFeedback.cs`, 2026-11-17, `LEGIBILITY_AUDIT.md` B1/B2).
- **Rejected poke:** every rejected poke plays `PhaseStarVisuals2D.PlayNotReadyShake()`, a diamond head-shake. This includes incidental bumps, rate-limited to once per 0.25s.
- **Internal guards stay silent:** same frame, ejection already in flight, bad state, sibling.
- **Removed:** a "Wait" pointer line to the blocking node or note was removed after playtest. It read as the star zapping a differently colored note: the pickup lock is global, so the nearest blocker was often another role's note.
- **Sleep:** while an armed star `IsBlockedByWorkElsewhere()` (pickup lock, a pending expansion, or `CanCommitEjection(role, true)` false), or is paused (`SiblingActive`), `UpdateSleepVisual` sets `visuals.Sleeping`: desaturated diamonds, rotation ×0.25, no ready-spin.

**Emits / calls out:**
- Spawns `DiscoveryTrackNode` via `SpawnNodeCommon()` or `SuperNode` via `SpawnSuperNodeCommon()` — gated by `ShouldSpawnSuperNodeForTrack()` (target track fully expanded + `MotifProfile.alternateChordProgressionProfile` set)
- Hands the drained-cell batch to the spawned node (`DiscoveryTrackNode.AttachHeldDustBatch()`); releases any unassigned batch via `CosmicDustGenerator.ReleaseHeldCells()` on destroy
- Calls into the bridge path once `StarPool.CheckBridgeGate()` passes (no live stars, budget spent) + no collectables in flight

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
- `ReleaseHeldDustOnce()` on resolve **and** destroy → `CosmicDustGenerator.ReleaseHeldCells()` — the star-drained cells finally regrow (at the role's `regrowthDelay`)
- `InstrumentTrack.SpawnCollectableBurst(noteSet)` on depletion
- Self-destructs after N loop boundaries without capture (NoteSet motif override, else role/archetype baseline off `DiscoveryTrackNodeLocomotionProfile.expireAfterLoops`; read back as `TrackNode.ExpireAfterLoops`), refunding the ejection slot via `StarPool`
- `Explode` component → visual burst

**Listens to:**
- `DrumTrack.OnLoopBoundary` — increments the loop counter that drives expiry

**Collision:** `Vehicle` collision reduces strength; on zero → `HandleDepleted()`. Each hit also resets the expiry counter (`_loopsSinceSpawn = 0`).

**Expiry pips** (`TrackNodeLifetimePips.cs`, 2026-11-17, `LEGIBILITY_AUDIT.md` B3): concentric translucent discs behind the node show `TrackNode.LoopsRemaining`. There is one disc per loop left, with opacity graded from the inner disc to the outer one.
- The outer disc fades out per loop boundary.
- Discs re-emanate on a hit.
- The halo is scale-compensated so it stays a constant world size.
- It's purely a reader: it polls each frame and owns no gameplay state.

---

### SuperNode
`Assets/Scripts/Gameplay/Mining/SuperNode.cs`, `SuperNodeTrackNode.cs` (inherits `TrackNode`), `SuperNodeSteeringMath.cs`

DiscoveryTrackNode's alternate ejection outcome — a multi-track "bonus round" instead of a single mineable shard. `PhaseStar` picks one of the two mutually exclusive paths per poke (`ShouldSpawnSuperNodeForTrack`, `PhaseStar.EjectionLogic.cs`): a SuperNode requires `MotifProfile.alternateChordProgressionProfile` to be set **and** the target track already at `maxLoopMultiplier`. Prefab naming is not obvious from the class names: the SuperNode prefab is `Assets/Prefabs/Interactable Objects/Major Discovery.prefab` (wired via `Star.prefab.superNodePrefab`), and the `SuperNodeTrackNode` prefab is `Assets/Prefabs/Interactable Objects/Ideas.prefab`.

**Tell** (`PhaseStar.SuperNodeTell.cs`, 2026-11-17, `LEGIBILITY_AUDIT.md` B4): while the condition above holds for a star's track, a role-striped ring turns around that star, using the colors of all the motif's active roles.

**Spawn** (`SpawnSuperNodeCommon`, `PhaseStar.EjectionLogic.cs`): instantiates the SuperNode prefab, computes `difficulty01` from harvests-so-far, builds a `shardTracks` list of every other active-role track still below `maxLoopMultiplier` (the target track is filled directly, not chased).

**`SuperNode.Initialize`** spawns one `SuperNodeTrackNode` chaser per shard track in a ring around the spawn point and tracks `_pendingChaseCount`. If there are no shard tracks, it resolves synchronously in the same frame.

**`SuperNodeTrackNode` movement** (`FixedUpdate`): dust-seeking wander — flees nearby vehicles, cycles burst/pause speed, raycast-scores directions favoring dust matching its `AssignedTrack.assignedRole` (via `TrackNode.RunDirectionScan` + its own `ScoreDirection` override), wall-hugs, stall-escapes, and expires on a loop-boundary timer (via `TrackNode`'s shared expiry counter). On matching-role dust contact it carves the cell (`CarveCellPreserveGray`, tagged `DustClearSource.SuperNode`, gray regrow — never feeds the star). On vehicle contact, `Collect()` calls `AssignedTrack.InstantFillAllBins(toMaxCapacity:true)` and fires the completion sequence.

**Resolution:** each `SuperNodeTrackNode.OnResolved` decrements `SuperNode._pendingChaseCount`; at zero, `SuperNode.OnResolved` fires and it despawns. `PhaseStar`'s subscriber mirrors the fill/advance logic for the *target* track, fires the shared `OnMineNodeResolved`, and enters the same await-collectable-clear tail DiscoveryTrackNode uses.

**Dead code note:** `SuperNodeKiteSteering.cs` and `SuperNodeSteeringMath.SteerTowards` were removed. `SuperNodeSteeringMath.cs` itself stays: `EdgeFactor01`/`ClampToBounds` are used by `SuperNodeTrackNode` and `Wisp`.

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
  → release button → Vehicle.TryReleaseQueuedNote() (window check against the DSP step;
      a held button cascades through queued notes, Vehicle.ManualRelease.cs)
  → Vehicle.CommitManualReleaseAtStep()
      → Collectable.MarkAsReportedCollected() / OnManualReleaseConsumed()
      → InstrumentTrack.CommitManualReleasedNote()
```

**Path 3 — Discard → Time Bomb** (`Collectable.Bomb.cs`):
```
Vehicle discards a carried note (missed release window, sacrificed outside window,
or Vehicle death — queue overflow no longer discards: since 2026-11-17 a full ship refuses
the pickup and the note bounces off, Collectable.BounceOffFullVehicle) → OnManualReleaseDiscarded(step)
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
      radius = CollectableTuning.bomb.blastRadius × MusicalRoleProfile.bombBlastRadiusMul
        ÷ cell size — one radius drives everything below
      for every Vehicle within radius: Vehicle.DrainEnergy() + Vehicle.TriggerDrainFeedback()
        + Vehicle.ApplyKnockback() (tuning.bomb.knockbackImpulse, falls off toward
        knockbackEdgeFraction at the blast edge — same distance falloff shape as the drain)
      CosmicDustGenerator.TriggerDiscardBlast(centerCell, radius, source: CollectableBombBlast,
        permanentClearRatio01: MusicalRoleProfile.bombPermanentClearRatio01) — carves every
        solid dust cell in the disk except Cosmic Blocks; that fraction of cells becomes a
        permanent crater (_permanentClearCells), the rest regrow through the normal resolver
        respecting their own identity, and blast-cleared cells bloom like vehicle carves
  → FlashThenImplode() — flashes to a size matching the resolved radius, then collapses → Destroy()
```
An armed bomb still counts toward `GameFlowManager.AnyCollectablesInFlightGlobal()` until
this sequence fully destroys it — `StarPool`/`PhaseStar` bridge gates wait on a ticking
bomb exactly like an uncollected note.

Radius is scaled per role (`MusicalRoleProfile.bombBlastRadiusMul`) so a heavier role
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
  → energy = 0 → Coop: respawn / game-over signal to GameFlowManager
                 Competitive (GameMode.CompetitiveElimination/CompetitiveMotif): no respawn;
                 the ship is eliminated and GameFlowManager.MatchEnd decides MatchEndReason
  → CollectEnergy(int) refuels from Collectables
```

**Note collection loop:**
```
Collectable trigger → EnqueuePendingCollectedNote()
  → release button → TryReleaseQueuedNote() (checks window vs step targets)
  → CommitManualReleaseAtStep() → track.CommitManualReleasedNote()
  → NoteVisualizer ghost cue cleared
```

**Release approach ring** (2026-11-17, `LEGIBILITY_AUDIT.md` C3): in Manual mode, `Vehicle.TickReleaseApproachCue` → `VehicleReleaseCue.SetApproach` draws a track-colored ring that contracts toward the hull over the last `releaseApproachLookaheadSeconds` before the release window opens.
- When the window opens the outline becomes a filled, low-alpha, pulsing disc ("now"), and it stays up through a held-button release cascade (`VehicleReleaseCue` header). Outline = wait, filled disc = window open.
- The component is added at runtime if a ship prefab lacks it.

**Fuel status tells** (2026-11-17, `LEGIBILITY_AUDIT.md` D2): `Vehicle.StatusGlow.cs` draws a runtime glow sprite behind the hull.
- Warm pulse below `VehicleConfig.lowFuelThreshold01`.
- Cool shimmer while gravity-void refueling. `UpdateVoidEnergyRecovery` → `SetVoidRefueling` → `PlayerStats.SetFuelRefueling`.

`Fuel.cs` mirrors both on the bar. `StatusRestColor` replaces the plain base color as the rest look that the change FX lerp from.

**External energy drain:** `DrainEnergy(amount, source)` (`Vehicle.Energy.cs`) is the shared
entry point any system uses to damage a vehicle from outside its own boost loop — dust
boost-collision resistance (`CosmicDust.Collision.cs`) and a detonating bomb Collectable
within blast radius (`Collectable.Bomb.cs`, Path 3 above) both call it. A bomb hit also
calls `TriggerDrainFeedback(color)` (`Vehicle.Damage.cs`) — a momentary flash-to-hit-color,
shrink, and spring-back reaction reusing the same flicker/pulse machinery as the
`DiscoveryTrackNode`-bump reaction, so the hit reads on the vehicle itself rather than only
the fuel UI ticking down.

**Listens to:** nothing on `DrumTrack` — the release cue and window math poll the DSP clock each tick.

**Calls out:**
- `NoteVisualizer.TryGetNextUnlitStepExcluding(track, …)` — finds target for ghost cue (also used by `InstrumentTrack.CollectionResolution` for Auto placement)
- `NoteVisualizer.SetManualReleaseCuePosition()` / `BlastManualReleaseCue()` / `ClearManualReleaseCue()`
- Unreachable notes: `Vehicle.CanTakeNoteNow` refuses a note it couldn't release (`LEGIBILITY_AUDIT.md` C2); the note shows as a ghost (`Collectable.Carry.cs`)
- `dustGenerator.SetVehicleKeepClear()` / `ChipDustByVehicle(…, profile)` — dust pocket + plow. A vehicle carve is also the only path that blooms the cell's flower on regrow, making it PhaseStar-targetable (see CosmicDustGenerator / PhaseStar).

---

### CosmicDustGenerator
`Assets/Scripts/Dust/CosmicDustGenerator.cs`

Authoritative 2D grid. All spatial state lives here — cell solid/empty status, role imprints, regrowth scheduling, vehicle pockets, gravity void disk growth.

**Owns:** `DustGridState` (cell state / GO / dust arrays), `DustImprintStore` (`_imprints`, hidden roles), `DustRoleDensityTracker` (per-role solid counts), `_mazePatternCells`, per-cell regrowth coroutines, `_heldRegrowCells` (star-drained cells held from regrow until their DiscoveryTrackNode dies).

**Key operations:**
| Method | Who calls it |
|--------|-------------|
| `ClearCell()` / `CarveCell()` | internal funnel — every removal carries a `DustClearSource`. It gates the flower bloom and ForceGray color, not timing |
| `CarveCellPreserveGray()` | `Collectable` arrival flight + intent plow, `SuperNodeTrackNode` — always regrows gray/uncharged |
| `ZapClearCellHeld()` / `ReleaseHeldCells()` | `PhaseStarDustAffect` tentacle drain / `DiscoveryTrackNode` death (and star teardown) |
| `SetVehicleKeepClear()` / `ReleaseVehicleKeepClear()` | `Vehicle` |
| `ChipDustByVehicle()` / `CarveDustByVehicle()` | `Vehicle` boost plow (passes `ShipMusicalProfile`) |
| `PaintDustExhaust()` | `DiscoveryTrackNodeDustInteractor` — role tint only, never clears |
| `GrowVoidDustDiskFromGrid()` | `GravityVoidController` (expansion pending). Vehicle traps don't use this: they're injected into the maze-build stagger via `InjectTrapCellsIntoStagger`/`InjectTrapCellsFromList` from `SceneFlowCoordinator`'s trap callback |
| `CreateJailCenterForCollectable()` | `Collectable` spawn pocket |
| `TriggerDiscardBlast()` | `Collectable.Bomb.cs Detonate()` — source `CollectableBombBlast`, part of the disk cleared permanently (`bombPermanentClearRatio01`), blocks skipped |
| `HardStopRegrowthForBridge()` | `BridgeCoordinator` bridge start (also flushes held cells) |
| `BeginSlowFadeAllDust()` | `BridgeCoordinator` bridge fade |
| `ResumeRegrowthAfterBridge()` | `SceneFlowCoordinator` next motif start |
| `ApplyActiveRoles()` | `SceneFlowCoordinator` motif swap |

**Regrow delay resolution** (`ResolveRegrowDelay`, simplified 2026-11-17, `LEGIBILITY_AUDIT.md` A5). Each role regrows at its own speed, in this precedence:
1. Explicit per-call override.
2. The cell's prospective role's `MusicalRoleProfile.regrowthDelay`, whatever cleared it.
3. For role-less cells: the maze's `dustTiming.regrowDelay`, else an 8s fallback.

The result is then multiplied by density easing. Cells flagged `ForceGrayRegrow`/`ZapForceGray` always regrow gray and uncharged — role tint (and drainable charge) is only earned by vehicle plow-carves or DiscoveryTrackNode exhaust painting.

Role imprinting: a vehicle plow-carve promotes the cell's hidden Voronoi role into the active imprint, so it regrows in its true role color with charge. Same-role cells attract nearby `DiscoveryTrackNode`/`SuperNodeTrackNode` and `Collectable` movement.

**Spawn-time color reveal is not hidden by default.** Every occupied cell's `hiddenRole` is a Voronoi ground-truth assigned at maze build time, but `imp.role`/`dust.Role` stay `MusicalRole.None` until a vehicle actually carves it — that's what gates targeting, carve resistance, and star-arming (untouched by any of this). The cell's *displayed color*, though, is a separate, purely cosmetic pass: `ApplyHiddenColorPreview` (`CosmicDustGenerator.MazeGeneration.cs`, run at the end of every `BuildMazeRoleImprints` path — single-voice, multi-voice Voronoi, and Exact-Count below) colors each cell with `Color.Lerp(hiddenRole's true MusicalRoleProfile color, Color.black, hiddenRoleColorDarken01)` instead of flat `mazeTint` gray, so the maze reads as "roughly what it is" from the moment it spawns, well before any carving. `CosmicDustGeneratorConfig.hideDustColorAtSpawn` (default `false`) is the one switch that turns this off entirely, falling back to flat `mazeTint` for every cell. Carving still promotes the real `Role` and reveals the full, un-darkened color exactly as before — this preview only narrows the gap between "secret" and "revealed," it doesn't erase it.

**Dust brightness = star readiness** (`CosmicDust.Legibility.cs`, 2026-11-17, see `LEGIBILITY_AUDIT.md` A1/A2). `CosmicDust.SetBaseTint` gates every rest tint:
- **Bright:** a role cell with a bloomed flower. These are the only cells stars can drain.
- **Darkened** by `hiddenRoleColorDarken01`, the same amount as the spawn preview. This covers:
  - role cells that are not bloomed (zap/collectable/void regrows, node exhaust paint);
  - `None` cells that still hide a role. This is via the revived `_hiddenHintColor`, which `ApplyHiddenHintToDust` sets. So ForceGray and ZapForceGray regrows now show a dark role tint, not flat gray.
- **Raw:** `None` cells with no hidden role (filler, interstitial walls) keep their raw tint.

`BloomFlower`/`HideFlower`/`SetHiddenHintColor` re-apply the rest tint. Use `RestTintRaw`, not `CurrentTint`, whenever you blend a color and write it back.

Note: blooming also happens after `CollectableBombBlast`, not just `VehiclePlow` (`CosmicDustGenerator.cs` `CommitRegrowCell`).

**Regrow fade-in** (`CosmicDust.Lifecycle.cs`, `GrowInAlphaRoutine` / `_growInAlpha01`): a regrowing cell ramps its alpha in alongside its scale instead of popping in at full brightness. The base alpha is captured once, and the ramp is applied at the particle sink. The collider comes back after `regrowColliderEnableDelaySeconds` (one shared `WaitForSeconds`).

**"No musical role" dust is not the same thing as this gray/darkened preview.** A `hiddenRole`-bearing cell is *always* owned by some role underneath, whether it's currently showing a darkened preview, mid-regrow gray (`ForceGrayRegrow`/`ZapForceGray`, see Regrow delay resolution above), or fully revealed. True `MusicalRole.None` dust — no hidden role at all, permanently — only exists as generic filler (exact-count leftovers or `genericFillRatio01` reservation). Filler is a single permanent type: dark `genericFillTint`, never regrows once cleared. See below.

Flower bloom (`CosmicDust.Flower.cs`): each cell carries a hidden `Flower` child sprite pair, normally inactive. `CommitRegrowCell` persists the `DustClearSource` that triggered a pending regrow (`_pendingRegrowSource`, written in `ClearCell`) and, only when that source was `VehiclePlow`, calls `dust.BloomFlower(regrowTint)` — the flower fades in role-tinted alongside the regrowing dust. Every `ClearCell` call unconditionally calls `dust.HideFlower()` first, so any clear (vehicle re-carve, tentacle drain, any other source) resets the tell; only a vehicle carve can re-bloom it. `HasBloomedFlower` gates `CosmicDust.IsVisuallyPresentForTargeting()` (see PhaseStar below) — dust regrown by any non-vehicle source still regrows and is still solid/collidable, it's just untargetable by tentacles until a vehicle carves it at least once.

---

### Exact-Count Maze Mode ("Puzzle" Territory Sizing)
`CosmicDustGenerator.MazeGeneration.cs` (`ClaimExactCountTerritoryAndFillGeneric`), `DustImprintStore.cs`, `DustRoleDensityTracker.cs`, `MazeGeoFeature.cs` (`MazeRoleGeoConfig`), `MotifNoteDensityAnalyzer.cs`, `MotifProfile.cs`

An alternate to the default Voronoi-partition maze build. A per-role `MazeRoleGeoConfig.useExactNoteCount` flag switches that role's territory from the relative density-weight formula to an absolute cell count: `cellCount = round(avgNoteCount * notesToCellsMultiplier)`, claimed by nearest-cell assignment out from greedily-spread seed points (`PickFarthestSeeds`) rather than a full-maze Voronoi split. If *any* active role in the motif uses exact-count sizing, **every** role does — a non-flagged role gets no territory at all in that motif; its would-be cells fall into the leftover pool instead.

Leftover (unclaimed) cells become generic `MusicalRole.None` filler dust (`DustImprintStore.SetHiddenRoleGenericFill`, tagged `hiddenRoleIsGenericFill`).

There is **one filler type** as of 2026-11-17 (`LEGIBILITY_AUDIT.md` A3). It uses the darker `CosmicDustGeneratorConfig.genericFillTint`. Once cleared it never re-enters the regrow cadence for the rest of the motif: `RequestRegrowCellAt` adds the cell to `_permanentClearCells` instead of scheduling regrow.

Removed along with the quick-regrow sub-type:
- `MotifProfile.genericFillNeverRegrowRatio`
- `MotifProfile.genericFillRegrowSoftenMultiplier01`
- the `DustImprint.genericFillNeverRegrows` flag
- the regrowth controller's `shouldBypassStepGate` hook

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
| `Vehicle` (tick) | `SetManualReleaseCuePosition(vehicle, worldPos, visible)` | Position ghost cue toward release target |
| `Vehicle` (release) | `BlastManualReleaseCue(vehicle)` | Release burst on the cue |
| `Vehicle` (pickup) | `ClearManualReleaseCue(vehicle)` | Hide cue after release |
| `InstrumentTrackController` | `RecomputeTrackLayout()` | Rebuild row positions on bin expansion |
| `InstrumentTrackController` | `MarkGhostPadding()` | Show placeholder steps for new bin |

**Queries from `Vehicle` / `InstrumentTrack`:**
- `TryGetNextUnlitStepExcluding(track, …)` — next un-filled step forward from playhead, skipping already-armed steps

**Record departure** (`NoteVisualizer.RecordDeparture.cs`): when the completed record spins off, `SendLoopMarkersWithRecord(record, converge, total)` swirls every timeline marker into the record's centre (per-marker delay, twist and pull curve), then parents them to the record so they tilt and scale away with it, and destroys them at the end. During the swirl the markers are moved off the track rows onto the rows' shared parent, and `RecomputeTrackLayout` / `CanonicalizeTrackMarkers` / `ForceSyncMarkersToPersistentLoop` / marker placement all no-op while `_loopMarkersDetached` is set — the loop keeps playing through the interstitial maze but draws no markers until `BeginNewMotif_ClearAll` (path chosen) resets the flag. `OnDisable` stops the swirl and destroys its markers.

---

### Glyph & Ring Systems

**Ring glyph (live, record/vinyl metaphor):**
`Assets/Scripts/Visualizers/MotifRingGlyphApplicator.cs` (+ partials `.GameplayRings.cs` per-bin rings / tether breaking / record fade-out, `.RecordRings.cs` snapshot rendering / playing-bin highlight / selection backdrop / firework, `.Wave.cs` carousel note-pulse mode, `.SpinOff.cs` roll-off exit, `.RingBuilder.cs` mesh + GameObject construction, `.Animation.cs` draw-in / travel-dot / contour-tween / shake coroutines) + `RingGlyphConfig.cs` (SO knobs) + `MotifRingGlyphGenerator.cs` (geometry) + `Assets/Scripts/Visualizers/BinRingController.cs` (drives gameplay ring spawn/deformation off the drum playhead) + `Assets/Scripts/Visualizers/Notes/NoteTether.cs` (shared cable renderer, also used for the manual-release ghost cue).

Each ring is a filled annulus tinted by musical role, plus a contour line that dips inward ("tug") at each note position — the applicator's own header comment describes the look as record-groove tracks. Three ring collections exist on the applicator: `_gameplayRings` accumulate live, one per completed bin, during play; `_remainingRings` are gray placeholders for motif progress not yet completed (these and locked-preview rings are built with `skipContour` — fill mesh only, `Contour` is null); `_recordRings` render a full completed-motif snapshot for the bridge display, `SolarSystemRecordDisplay` and the PhaseLibrary carousel. Each `RingEntry` owns its fill `Mesh` (`FillMesh`) — `DestroyEntry`/`DestroyList`/`DestroyDisc` free meshes along with GameObjects, and `DestroyAllChildren()` is the single teardown used by `AnimateApply` and `OnDestroy` (it also nulls the cached selection-backdrop/disc refs, since `Destroy` is deferred to end of frame). A fixed sorting-order stack keeps the whole thing layered correctly over the maze (`MotifRingGlyphApplicator.RingBuilder.cs`): background discs (1–2) → ring `Fill` (3) → `Contour` (4) → travel dot (5) → `NoteTether` cable (`Foreground` layer, order 10) — without it the record reads as a see-through wireframe with dust visible behind it.

**Motif-start record reveal:** `SceneFlowCoordinator` calls `BinRingController.ShowInitialRecord()` / `FadeInitialRecordOut()` / `GetRecordHoldDuration()` (thin forwarders onto the applicator) at the start of every motif — immediately, in darkness, before the maze exists for the very first motif of a session; after the new maze has already fully generated for every other transition. `ShowInitialRecord()` populates the full placeholder-ring stack via `SetRemainingProgressRings(nodesPerStar)`. The record holds at full opacity for `RingGlyphConfig.recordHoldDuration`, then `FadeRecordOut()` fades everything (placed + remaining rings, both background discs) out over `recordFadeOutDuration` — or, for the very first motif, over an explicit duration passed in so the fade finishes exactly as the maze fully grows in. Two solid backdrop discs (`_placedBgDisc` sized to completed rings, `_fullBgDisc` sized to the whole record, both built by `CreateBackgroundDisc`/`BuildDiscMesh`) give the record a genuine opaque backing instead of a wireframe. The innermost not-yet-filled placeholder ring previews the motif's active role palette — static if the motif has one role, cycling through them (`AnimateNextUpRingColorCycle`, `nextUpColorCycleDuration`) if it has several — so the "next up" ring always hints at what's coming.

**Note preview tethers + ring-collision breaking:** while a bin's rings are deforming in (`DeformBinRingCoroutine`), each note about to land gets a `NoteTether` cable that grows in (`GrowIn`; built 4 per frame so a full bin's anchors/tethers/shimmers don't all instantiate in one frame, and none are built after `DetachPendingRingTethers` bumps `_tetherDetachGeneration`) from its timeline marker to its future ring landing spot, tracked in `_pendingRingNotes`. The tether retracts and self-destroys (`RetractAndDestroy`) the instant its travel dot actually departs (`onDepart`). While a tether is still live, a `Vehicle` colliding with the ring's `CircleCollider2D` can break it — but only deliberately (boosting, or `coll.relativeVelocity` ≥ `VehicleConfig.ringBreakMinImpactSpeed`; softer bumps just play the denial shake). Because the stack snaps to near-full play-area height when a bin completes, any ship it lands on (or straddles) is put into a ring *phase* (`PhaseVehiclesInsideRing` → `Vehicle.BeginRingPhase`, `Vehicle.RingPhase.cs`): ship↔ring collision is ignored and the ship drawn at `ringPhaseGhostAlpha` until it drives fully outside — no teleport. On a qualifying hit, `Vehicle.OnCollisionEnter2D` (`Vehicle.Damage.cs`) calls `MotifRingGlyphApplicator.TryBreakNextPendingTether(contactPoint)`, which finds the pending note whose ring-side anchor is closest to the contact point, cancels its travel-dot launch, retracts its tether, and calls `InstrumentTrack.RemovePersistentNoteAtStep()` + `NoteVisualizer.RemoveCommittedMarkerAtStep()` (which cancels the note out of `NoteAscensionDirector` first, via `CancelAscension()`, so ascension doesn't hold a dangling reference) — the note is undone as if it had never landed. A successful break plays a low-frequency "released spring" shake (`PlayBreakShake`) and spawns a tint-matched particle burst at both the marker and the ring anchor (`PlayBreakShake` is private to the applicator); colliding with the ring when nothing is pending instead plays a short, stiff denial shake (`PlayDenialShake`). A too-soft hit while a note *is* pending instead plucks that note's tether (`TryPluckNearestPendingTether` → `NoteTether.Pluck`, 2026-11-17, `LEGIBILITY_AUDIT.md` C5), so the two cases read differently — both driven by Perlin-noise-decayed `transform.localPosition` jitter (`ShakeRoutine`), config'd via `RingGlyphConfig`'s Ring Collision Feedback knobs. A bin emptied this way re-fires `OnBinFilled`/`SpawnBinRing` when it refills; `SpawnBinRing(role, binIndex, color, totalSteps)` replaces the stale ring entry in place (matched by `binIndex`+`role`) instead of appending a duplicate. `DetachPendingRingTethers()` retracts all still-pending tethers without undoing their notes (called as the end-of-motif spin begins), and `SetSuperNodeMode(true)` (called by `PhaseStar.EjectionLogic.cs` and `SuperNodeTrackNode.cs`) suppresses the per-bin auto-hide of the ring stack while a SuperNode fills bins. `WaitAndLaunchDot` and `HideGameplayRingStackAfterLoopBoundary` subscribe to `DrumTrack` events inside `try/finally` so a stopped coroutine can't leak its handler.

There are **two distinct consumers**, and they use different methods — this is the biggest place the old documentation was wrong:
- **Live bridge:** `BridgeCoordinator.PlayMotifBridgeAndRestart()` calls `MotifRingGlyphApplicator.SpinAndRollOffActiveRings(spinDur, rollOffDuration)` on the accumulated gameplay rings (spin 360°, tilt, scale to zero). The ring stack is torn down by that method itself.
- **Library/browsing UI:** both render a *past* completed motif independent of live gameplay, but from different sources.
  - `SolarSystemRecordDisplay.cs` (TrackFinished scene) adds a fresh applicator per record and calls `AnimateApply(snapshot)` on this session's `BridgeFlow.MotifSnapshots` (not disk). After a mid-motif game over it has a second mode: `BridgeFlow.CaptureUnfinishedSnapshot()` (taken in `HandleGameOverSequence` before the game-over fade stretches every note) is played back carousel-style through `RingPreviewPlayer` with `unfinishedGlyphConfig`. The unfinished record is not saved to disk.
  - `PhaseLibraryCarousel.cs` (snapshots loaded via `RingSessionStore.LoadAllRingsFromDisk()`) draws the centered glyph with `AnimateApply(..., replayLastRing:false, notePulse:true)`, other glyphs with `ApplyVinyl`, and the not-yet-played entry with `ApplyLockedPreview`. It drives the glyph with `SetFixedFit` (constant world radius), `SetSelectedBackdrop`, `SetAlphaScale` and, from `RingPreviewPlayer`, `SetPlayingBin`, `PulseNote` and `SpawnNoteFirework`.
  - Every applicator calls `GameFlowManager.RegisterRingGlyphApplicator(this)` in `Start()`. That is harmless today because `GameFlowManager` lives only in `Main.unity`, but would overwrite the gameplay reference if a display ever ran alongside it.

**Motif snapshot persistence:** `BridgeCoordinator.BuildPhaseSnapshotForBridge()` builds a `MotifSnapshot` (`Assets/Scripts/Utilities/MotifSnapshot.cs`) fresh each bridge from `InstrumentTrack.GetPersistentLoopNotes()` + per-bin fill state — it is not read from any long-lived store. `RingSessionStore.SaveRingToDisk(motifSnap)` (`Assets/Scripts/Utilities/RingSessionStore.cs`) then writes it to `Application.persistentDataPath/RingSessions/ring_{phaseIndex}_{motifIndex}.json` for later browsing. (Older documentation referenced a `ConstellationMemoryStore.StoreSnapshot()` call — that class does not exist in the current codebase.)

**2D glyph — removed.** `GlyphApplicator.cs`, `MotifGlyphRegistrar.cs`, and `MotifGlyphPreview.cs` (the editor preview tool) have been deleted; `GameFlowManager`'s `motifGlyphApplicator` field and `RegisterGlyphApplicator()` method are gone with them. The three components were attached to a shared, inactive-by-default "Glyph Generator" GameObject in `GeneratedTrack.unity` alongside the live `BinRingController` — that GameObject's component list was trimmed rather than deleting the object itself, since `BinRingController` still needed it. `MotifGlyphGenerator.cs` was deleted 2026-11-17; its one live type, `GlyphPolyline`, now lives in `Visualizers/GlyphPolyline.cs` and is used by `MotifRingGlyphGenerator.cs`.

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

## Path-Choice Locked Exits

`PathChoiceCoordinator` (2026-11-17, `LEGIBILITY_AUDIT.md` E2): phase-change exits withheld by `AllMotifsRecordedInPhase` get a `PathExitLockedStub` dust gate. The dust across that edge's centre band shimmers via `CosmicDustGenerator.TryFlashSolidCell`, with intensity = recorded/total motifs in the phase. Stubs are destroyed in `OnVehicleReachedExit` and at the end of `RunPathChoiceAndAdvance`.

---

## Cosmic Blocks (role-wide, 2026-11-17)

`Dust/CosmicBlockField.cs` (controller) + `Dust/CosmicDustGenerator.Blocks.cs` (dust side) + `Dust/CosmicDust.BlockForm.cs` (look) + `Dust/DustReachability.cs` (search).

**When a ring completes** (`InstrumentTrack.OnBinFilled` → `BinRingController` → `CosmicBlockField.OnRingCompleted`):
- The previous role's blocks revert.
- Every Solid dust cell of the completed role becomes a block **in place**, revealed or still hidden (`GetProspectiveRegrowRole`).
- Generic filler and interstitial walls are excluded, and so are cells a ship is overlapping.
- Swaps coalesce to once per frame (SuperNode multi-bin fills).
- No new wall is placed when no other active role still has unfilled bins. The old blocks still revert.
- Record spin-off, `HardStopRegrowthForBridge` and `ClearMaze` all call `ReleaseRoleBlock`.

**A block is:**
- `CellFlags.RoleBlocked`, gated at every clear choke point: `ClearCell`, `ZapClearCellHeld`, `CarveCellPreserveGray`, `DespawnDustAt`, the discard blast, `PermanentlyClearOneCellForRole`, `ClearAndHoldRoleCells`, and vehicle carve/chip via `IsUnbreakable` / `CanVehicleChip`;
- plus `CosmicDust.SetBlockForm`, which swells the particle start size ×`roleBlockParticleSizeMul` (4) and makes `IsVisuallyPresentForTargeting` return false, so a block is never star food. Gameplay state (flag, collision, `IsBlockForm`) is immediate; the look ripples in — each cell waits a random 0–0.3s before its tint refresh and 0.4s swell, and the swell's intermediate frames go through `DustParticleRewriteBudget`.

The cell stays Solid, so `HasDustAt` and every grid pathfinder already treat it as a wall. `IsBlockAt` is now flag-based.

**No-trap guarantee** (`DustReachability`, a clearance-aware 0-1 BFS):
- **Clearance:** `rc = max(1, ceil(widestShipRadius / cellWorld))`. A ship node needs no hard wall within `rc`.
- **Doorways:** before walling, routes run from the first ship to the other ships, every on-screen PhaseStar (`PhaseStar.Live`, skipping `IsParkedOffScreen`), live notes (`Collectable.TryGetReachCell`, which uses the landing cell while in flight) and queued destinations (`InstrumentTrack.GetPendingSpawnCells`). Candidate cells within `rc` of the cheapest route become **passage form** instead of blocks.
- **Search cost:** `DustReachability.Run` samples the wall predicates once per cell up front and uses an array ring-buffer deque, so a run allocates nothing after warm-up.
- **Regrow:** while a role is blocked, its cells that regrow to Solid join the wall only if `CanBlockWithoutTrapping` passes; otherwise they become passage form. This hooks the registry solid-transition callback. (Cells a ship overlaps are also passage form.)

**Passage form** (`CosmicDust.PassageForm.cs`, `_rolePassageCells`, `MakePassage`/`RevertPassage`): leftover cells of the walled role that must stay open — doorways, ship-overlapped cells, trap-avoiding regrows, opened blocks — turn inert gray. They are ordinary carvable dust for ships but never star food (`IsVisuallyPresentForTargeting` rejects `IsPassageForm`). `IsBlockCandidateRole` matches the live `dust.Role` as well as the prospective regrow role.
- **Note spawns** (`TryResolveSpawnDestinationCell`) reject block cells and cells not `IsCellReachableForShips` (cached per frame). As a last resort, `OpenDoorwayTo` reverts blocks along the cheapest route.

**Watchdog:** `CosmicBlockField` calls `EnsureTargetsReachable` every 0.5s while blocks stand. `OpenDoorwayTo` turns the opened blocks into passage form. Anything that has drifted out of reach gets a doorway: stars and notes are narrower than ships, so they can slip into pockets ships can't follow. It also gives a doorway to any active, non-blocked role with unfilled bins whose dust is entirely unreachable, since its star could never charge (`CollectSealedRoleTerritories`). Together with the deadlock skip, this means walls can't dead-end a motif.

**"About to move" tell:** while any track has a live burst, random blocked cells flash toward white (`TryFlashSolidCell`), denser and faster with the burst's progress.

The 11-16 note-driven system (one `CosmicBlock` prefab per recorded note, provenance, echo sparks, heartbeat) is gone. `CosmicBlock.cs` and `Cosmic Block.prefab` were deleted. The scene's stale `blockPrefab` value is dropped on next save.

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

**Storage:** one JSON file per run — `MazeTelemetryStore.SaveRun()` → `Application.persistentDataPath/GreaterMazeTelemetry/run_{runId}_{timestamp}.json` — never overwritten (unlike `RingSessionStore`'s per-`(phase,motif)`-slot overwrite pattern; this is an append-only history, not a "latest state" cache). Upload is opt-in: `TelemetryConsent` (set from `SettingsPanelController` / `LivingScorePanel`) gates `MazeTelemetryUploader` (Flight Plan API), with `TelemetryUploadQueue` retrying offline runs, `FlightPlanPayloadBuilder` + `MotifSolutionCanonicalizer` shaping the payload, and `PublisherIdentity` holding an anonymous, withdrawable publisher id (no Steam or machine identity).

---

## Dust Particle Rewrite Budget

`Dust/DustParticleRewriteBudget.cs`. Rewriting a dust cell's **live** particles (`GetParticles`/`SetParticles`) is the expensive part of every dust visual. Animation loops — tint pulses, colour flashes (bass-drop shockwave, block shimmer, scouting preview, charge/deny), the rainbow pulse, block-form swell and regrow fade-in — call `ApplyDisplayedTintAnimated` / budgeted `SetDustColorAllParticles` / `ApplyBlockSizeMul(budgeted: true)` on intermediate frames, which ask `DustParticleRewriteBudget.TryRewrite(phase)` (target 48 cells per frame, spread across cells by a per-cell phase when demand is higher, hard-capped on burst frames). `main.startColor`/`startSize` are always written, and **the final write of every animation bypasses the budget**, so a throttled cell just animates at a lower frame rate and never ends mid-colour. Rule for new dust animations: intermediate frames → budgeted call, last frame → unbudgeted call.

Related: `SetTint` and `RefreshLegibleRestTint` do one particle rewrite (they used to do three and two), `ApplyTintVisual` caches its `Explode` child, `CosmicBlockField` caps concurrent shimmer flashes (`shimmerMaxConcurrent`, 24), and the shockwave reuses its ring buckets and reads `DustGridState.CellDust` instead of `TryGetComponent`.

---

## Star Maze Navigation & Vines

**Navigation** — `Phase/Star/PhaseStarCravingNavigator.cs`, `PhaseStarMotion2D.cs`, `PhaseStarGridPathfinder.cs`, `StarSpawnReachabilityWeighter.cs`; tuning in `StarNavigationParams` on `MazePatternConfig.starNavigation` (`Dust/MazePatternConfig.cs`).
- `PhaseStarNavMode`: `Direct`, `CorridorFollow` (BFS through open cells), `LaneGlide`, `RingOrbit` (BFS + tangential swirl), `Burrow` (carves a tunnel ahead). `Auto` maps from the maze pattern: FullFill→Burrow, ClearBoxes/CA/Tunnels→CorridorFollow, RingChokepoints→RingOrbit, DiagonalLanes→LaneGlide, DrunkenStrokes→Direct.
- `PhaseStarGridPathfinder` is multi-goal: a solid colored target counts as reached when the search expands an open neighbour, so the first hit is nearest by path, not by distance. Throttled, reuses its buffers.
- `StarSpawnReachabilityWeighter` biases star spawn cells toward the region vehicles can reach through cleared corridors (soft bias, small floor weight elsewhere).

**Vines** — `PhaseStarDustAffect.Vine.cs`, `TentacleVineRoute.cs` (`TentacleVineRoute` + `TentacleVinePlanner`), rendering in `PhaseStarDustAffect.LineRendering.cs`.
- Each tentacle routes from the star to its target cell with `DustReachability` at clearance 0: open cells cost 0, dust costs 1, so dust is crossed only when no open route exists. The first pass also protects the star's own role's dust. The cell path is string-pulled into a few world-space corners.
- `nodes[0]` is the live root, re-pinned to the star every tick; the rest of the vine is fixed in the world, so bumping the star stretches the root end (pinning breadcrumbs) instead of dragging the whole vine. The grow front is stored as distance from the target.
- If the route is blocked the vine stalls and bores one dust cell (`DustClearSource.StarBurrow`), or gives up after a timeout at Cosmic Blocks.
- Tentacle LineRenderers and shimmer particles share `SharedMaterials.SpriteDefault` (`Utilities/SharedMaterials.cs`) unless `tentacleMaterial` is assigned.

---

## Drum Transport

`Music/DrumTrack.cs` (`BeginMotifTransport`), `DrumTrack.Transport.cs`, `DrumTrack.BeatIntensity.cs`, `DrumTrack.BassDrop.cs`, `Dust/CarveEnergyAccumulator.cs`, `DrumTrackConfig`.
- **Dual deck:** two drum `AudioSource`s; clip changes are scheduled on the inactive deck at a bar and swapped in `TickDeckSwap`.
- **`BeginMotifTransport(who)`** hard-(re)starts the transport on the motif's entry clip so the leader loop's step 0 lands now, dropping any armed swap or crossfade. `SceneFlowCoordinator` calls it at the first start of a session and at every motif start, after the new maze and star are ready — the intro and maze build are drum-silent.
- **Intensity** is carve energy only (Flow D): `CarveEnergyAccumulator` is a weighted, leaky pool of recent vehicle carves (decay applied lazily, no per-frame cost); half-life and saturation live on `DrumTrackConfig`.
- **Bass drop:** each committed Bass note arms the heaviest clip for `bassDropHoldLoops`; when it becomes audible, `CosmicDustGenerator.BeginShockwaveFromCenter` flashes a ring through the solid dust.

---

## Audio Mix

`Music/Config/AudioMixConfig.cs` — single authority for mixing, loaded from `Resources/Config/AudioMixConfig`.
- `roleBuses`: mixer bus per `MusicalRole` (the old Texture bus is now **Rhythm**); `drumsGroup` + `drumVolume` for the drum decks. Live `InstrumentTrack`s, `DrumTrack` and the carousel's `RingPreviewPlayer` all route through it, replacing the per-profile `outputMixerGroup`.
- **Gain trim:** `MusicalRoleProfile.volumeMultiplier` is applied as MPTK channel volume via `MidiVoice.SetGainTrim`, pushed with each note (`InstrumentTrack`), instead of scaling velocity — so timbre-by-velocity is preserved.
- **`UI/AudioBalanceDebugPanel.cs`** (Editor / development builds only, toggle **D** in the PhaseLibraryCarousel): plays the selected motif's drum loop under its MIDI through the same buses and trims, steps through drum loops, and edits drum level and role trims.

---

## Modal Menus

`UI/ModalMenu.cs` (`ModalMenu`, `ModalMenuSpec`, `ModalMenuOption`) — one generic overlay modal (title, optional description, vertical options) on its own Screen Space Overlay canvas. Prefab at `Resources/UI/ModalMenu.prefab`, built by **Tools > Astral Planes > Build Modal Menu Prefab**, loaded on demand and kept across scenes.
- Input is polled directly from gamepads and keyboard (the EventSystem's UI actions are unreliable in these scenes). East / Escape cancel.
- Any open modal locks gameplay: input handlers check `ModalMenu.ShouldSuppressInput`.
- Users: `PauseMenuController` (soft pause on gamepad Select — time and music keep running), `SettingsPanelController`, `QuitPrompt`, `GlobalEscapeQuitListener` / `GameFlowManager.QuitFlow.cs` (tap-vs-hold Escape), the `LocalPlayer` confirm/leave flows, `PhaseLibraryCarousel`, `PreviewBeatToggle`, `IntroInputHandler`.

---

## Path Choice & Interstitial Maze

`Managers/PathChoiceCoordinator.cs`, `Dust/CosmicDustGenerator.Interstitial.cs`, `Dust/PathExitConfig.cs`, `PathExitPortal.cs`, `PathExitParticleBuilder.cs`, `MazePathExit.cs`, `Wisp.cs`, `WispExitPathfinder.cs`, `BridgeSampleFader.cs`.
- After the record spins off (Flow C), `RunPathChoiceAndAdvance` builds an interstitial maze whose exits each lead to a motif or phase. The just-finished loop keeps playing.
- **Road kinds** (`PathExitConfig`, one designer asset): scope (motif in this phase vs. a different phase) × explored (colour = new, black-and-white = already played). The config also owns placement (centre-third Top/Bottom exits), road feel (vibration, capture surge, suction) and timing. `PathExitParticleBuilder` builds the striped "rainbow road" particles; `PathExitPortal` handles capture.
- **Wisps** preview each option: a `Wisp` walks a `WispExitPathfinder` route to its exit's anchor cell and plays that option's bridge sample; `BridgeSampleFader` fades the sample out on its own DontDestroyOnLoad object so the fade survives teardown.
- The first vehicle to reach an exit chooses it (`OnVehicleReachedExit`); other vehicles are redirected (`RedirectLosersAndAdvance`) and `SceneFlowCoordinator.StartChosenMotifOrPhase` drives them in from off-screen into the new maze.
- **Locked exits:** see **Path-Choice Locked Exits** above.

---

## Multiplayer & Competitive Modes

`Managers/GameMode.cs` (`GameMode`: `Coop`, `CompetitiveElimination`, `CompetitiveMotif`; `MatchEndReason`), `Managers/CompetitiveScoring.cs` (+ `CompetitiveScoringConfig`), `Music/InstrumentTrack.CompetitiveScoring.cs`, `Managers/GameFlowManager.MatchEnd.cs`, `UI/MatchResultsPanel.cs`, `VehicleResultView`, `UI/PlayerStatsTracking.cs`, `Managers/TrackSelectionJoinController.cs`.
- **Coop:** shared session, respawns, game over when every player is out of energy.
- **CompetitiveElimination:** no respawn; the match ends when one ship is left. **CompetitiveMotif:** no respawn; ends when the motif completes (the bridge shows results after the spin-off instead of path choice) or everyone is dead.
- `CompetitiveScoring` is stateless; per-player totals live on `PlayerStatsTracking` so they survive respawns. All entry points no-op outside competitive mode.
- **Joining:** `TrackSelectionJoinController` replaces `PlayerInputManager`'s auto-join with explicit per-frame scanning, working around Steam registering one controller as several Unity devices (see `project_steam_deck_input_bug` memory).

---

## Tutorials

- **Narrated tutorial** (`Tutorial/NarratedTutorialController.cs`, `NarratedTutorialScript.cs`, `TutorialEvents.cs`, highlight ring / prompt view): event-gated narration for the Phase 0 motif in GeneratedTrack. Listen-only — gameplay code raises `TutorialEvents` (no-ops with zero subscribers), the controller latches them, and the stage is derived from the latches so narration never lags. Lines have `linesAuto` variants keyed on Auto/Manual placement.
- **Control tutorial** (`Managers/ControlTutorialDirector.cs` + `.AbortHold.cs`, `Helpers/ControlTutorialHighlight.*`): per-player input-gated control walkthrough in TrackSelection. Holding East aborts it (`GameFlowManager.AbortTutorialSession` → `ReturnToJoinScreen`).

---

## Other Systems (index)

| System | File | Role |
|---|---|---|
| `HarmonyDirector` | `Music/HarmonyDirector.cs` | Active `ChordProgressionProfile`, chord cursor, profile swaps (SuperNode alternate progression, staged motif progressions) applied at the next loop boundary with a pivot offset, key re-key so retunes match `NoteSetFactory`'s register |
| `SoloVoice` | `Music/SoloVoice.cs` | Transient procedural solo from the current loop's note pool (MidiVoice output, no loop mutation) |
| `LoopCollapseController` | `Music/LoopCollapseController.cs` | Shrinks a track's loop by one bin at the next loop boundary |
| `NoteAscensionDirector` | `Managers/NoteAscensionDirector*.cs` | Marker ascension animations on the timeline |
| `BinFrontierAllocator` | `Music/BinFrontierAllocator.cs` | Which bin a track's next spawn targets, hole-filling behind the global frontier, the expansion "allow advance" token |
| `PhaseLibrary` / `PhaseTransitionManager` / `SessionGenome` | `Phase/`, `Utilities/SessionGenome.cs` | Phase/motif catalogue, chapter/motif advancement, per-session seed |
| `Hangar` | `UI/Hangar.cs` | DontDestroyOnLoad ship roster (see `project_hangar_ddol_planeselection_bug` memory) |
| Steam | `SteamManager`, `SteamStatsManager*` | Steam init + stats funnel; the `SteamManager` prefab also hosts `MazeTelemetryRecorder` |

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
                   → release button → TryReleaseQueuedNote()
                   → InstrumentTrack.CommitManualReleasedNote()
  → NoteVisualizer.RegisterCollectedMarker() lights step
  → InstrumentTrack.OnCollectableBurstCleared() → StarPool clears gate, PhaseStar re-arms
```

### Flow B — Bin Expansion
```
DiscoveryTrackNode depletes → InstrumentTrack.SpawnCollectableBurst
  → InstrumentTrackCompositionSpawner resolves the burst's target bin
      (BinFrontierAllocator / OverrideNextSpawnBin)
  → targetBin >= loopMultiplier → TrackExpansionController.TryStageExpand(burst, targetBin, voidPos)
      (one staged expand per track; hooks the next DrumTrack.OnLoopBoundary)
  → InstrumentTrack → InstrumentTrackController.BeginGravityVoidForPendingExpand()
      → CosmicDustGenerator.GrowVoidDustDiskFromGrid() (visual signal + refuel zone)
  → next loop boundary: TrackExpansionController commits
      → SetLoopMultiplier / SetTotalSteps / SetBinAllocated, ResyncLeaderBinsNow
      → NoteVisualizer.MarkGhostPadding() / RecomputeAllTrackLayouts()
      → the staged burst spawns (SpawnBurstNow) into the new bin
  → EndGravityVoidForPendingExpand()
```

### Flow C — Bridge / Phase Transition
```
StarPool.CheckBridgeGate() passes (budget spent, no live stars) + no collectables in flight
  → GameFlowManager.BeginMotifBridge() (dispatcher; passes endMatch in CompetitiveMotif)
      fires OnMotifCompleted synchronously, before the coroutine below starts
        ← MazeTelemetryRecorder snapshots the finishing motif's solution here (see Telemetry)
  → BridgeCoordinator.PlayMotifBridgeAndRestart():
      SessionState.SetGhostCycleInProgress(true) / SetBridgePending(true)
      FreezeGameplayForBridge() — clears tethers, pending notes, live Collectables/DiscoveryTrackNodes, destroys the StarPool
      BuildPhaseSnapshotForBridge() → MotifSnapshot from InstrumentTrack.GetPersistentLoopNotes()
      CosmicDustGenerator.HardStopRegrowthForBridge() + BeginSlowFadeAllDust()
      RingSessionStore.SaveRingToDisk(motifSnap)          ← persistence
      MotifRingGlyphApplicator.SpinAndRollOffActiveRings() ← live ring exit
        (NoteVisualizer.SendLoopMarkersWithRecord: markers leave with the record)
      [CompetitiveMotif] → match results (MatchResultsPanel) instead of continuing
      PathChoiceCoordinator.RunPathChoiceAndAdvance()
          interstitial maze (CosmicDustGenerator.Interstitial) with road exits per option
          (see Path Choice & Interstitial Maze); the loop keeps playing
          first vehicle to reach an exit picks it; losers are redirected
      SceneFlowCoordinator.StartChosenMotifOrPhase() / StartNextMotifInPhase()
          → PhaseTransitionManager.AdvanceMotif()/AdvancePhase()/StartChapter()
          fires OnMotifChanged(oldMotif, newMotif)
            ← MazeTelemetryRecorder appends the path visit + transition edge here (see Telemetry)
          DrumTrack.ApplyMotif(newMotif)
          InstrumentTrackController full reset (clears loop notes, bin state, burst state)
          NoteVisualizer.BeginNewMotif_ClearAll()
          CosmicDustGenerator.ResumeRegrowthAfterBridge()
          DrumTrack.BeginMotifTransport() once the new maze + star are ready
      SessionState.SetGhostCycleInProgress(false) / SetBridgePending(false)
  → new StarPool spins up, PhaseStars re-arm for the new phase
```

### Flow D — Carving → Drum Intensity (+ Bass Drop)
Rewritten 2026-11-17 (`LEGIBILITY_AUDIT.md` D1). The fullness/density formula and the role-driven ceiling (v2) are both gone.
```
DrumTrack loop boundary / Continuous poll → EvaluateAndScheduleClip()  (DrumTrack.BeatIntensity.cs)
  gates: no bass drop holding, driveBeatsFromDensity=true, entry-loop window elapsed,
         _carryLatched (first carry/catch), intensity tags non-empty
  → intensity01 = CosmicDustGenerator.GetCarveEnergy01()   — leaky pool of recent vehicle carves,
        weighted per carved role (DrumIntensityTuning.CarveWeight: Bass 1.0, others 0.5)
  → query (depth·I, breadth·I, I) → ResolveNearestIntensityClip → ScheduleDrumLoopChange → TickDeckSwap
Bass note committed (InstrumentTrack.OnNoteCommitted) → DrumTrack.BassDrop.cs
  → heaviest-pressure clip armed at the next bar, held bassDropHoldLoops (refreshed per Bass note)
  → when the drop clip becomes audible (TickDeckSwap finalize): AnnounceBassDropIfAudible()
      → CosmicDustGenerator.BeginShockwaveFromCenter(bass color) — ring flash through solid dust
```
`DrumTrack.ApplyMotif()` is a separate path — phase/motif changes only. It commits the new `MotifProfile` refs, resets entry-loop/carry-latch state, and picks an entry clip via `ChooseEntryClip()` (random from `entryDrumLoops`). It takes no intensity parameter and never touches `ResolveIntensityClip`/`_intensityLoops`; within-motif intensity swaps run entirely separately, once per loop boundary, as above.

### Flow E — Dust Energy Economy
```
Vehicle plow-carves dust → cell regrows in its true role color + charge, flower blooms (vehicle-carve tell)
    (delay = the role's regrowthDelay × density easing)
  → PhaseStar grows one tentacle per bloomed, role-carved cell, capped at the node payload
      → each drained cell: ZapClearCellHeld() — held, no regrow; zap credited immediately; flower hides
  → payload zap count met → tentacles retract → star ReadyLatched
  → DiscoveryTrackNode (or SuperNode) ejected, holding the drained-cell batch
  → node captured / escaped / expired → ReleaseHeldCells()
      → cells regrow at the role's regrowthDelay, dark/unbloomed (not star-drainable), flower stays hidden
  → gray dust must be plow-carved again to re-earn role charge and re-bloom
```
Collectable dust removal (arrival flight + intent-window plowing) and SuperNodeTrackNode dust-seeking always regrow gray and never bloom the flower —
they open temporary corridors but never feed the star.

### Flow F — Discard → Time Bomb → Blast
```
Vehicle discards a carried note (miss window / sacrifice / vehicle death; full queue refuses pickup instead)
  → Collectable.ArmAsTimeBomb(authoredAbsStep)
      Rigidbody2D → Kinematic (pinned, immune to physics-engine depenetration)
      fuse = InstrumentTrack.ComputeDepositDsp(step) − now, floored at minFuseSeconds
      radius resolved once here (ResolveAndCacheBombBlastRadius) so a telegraph ring
        (Collectable.BombTelegraph.cs) can show the true blast radius for the whole fuse
      swells + tints toward warningColor each frame until fuse-out
  → Detonate() — fuse-out OR Vehicle bump (TriggerEarlyDetonation)
      radius = CollectableTuning.bomb.blastRadius × MusicalRoleProfile.bombBlastRadiusMul
      for each Vehicle within radius:
        Vehicle.DrainEnergy(amount, "TimeBombCollectable")
        Vehicle.TriggerDrainFeedback(warningColor)
        Vehicle.ApplyKnockback(tuning.bomb.knockbackImpulse, falls off toward knockbackEdgeFraction)
      CosmicDustGenerator.TriggerDiscardBlast(centerCell, radiusCells,
                                              source: CollectableBombBlast, permanentClearRatio01)
        → CarveCell() per solid, non-block cell in the disk
        → bombPermanentClearRatio01 of them become permanent craters
        → the rest regrow via the normal resolver (role regrowthDelay), revealed role kept,
          still-gray cells fall back to their hidden Voronoi role
  → FlashThenImplode() — flash to the same resolved radius, then collapse → Destroy()
```
The note's own energy was already processed at pickup (a partial refund is granted on most
discard paths via `CollectEnergy(amount * .25f)`, `Vehicle.NoteRelease.cs`) — leaving it out
of the loop is what makes it unstable. Vehicle drain amount scales by the collectable's own
`MusicalRoleProfile.drainResistance01` (the same field/formula dust boost-collision drain
uses) and falls off with distance to `edgeDamageFraction` at the blast edge. Blast **radius**
is a separate per-role knob (`bombBlastRadiusMul`) — heavier roles (Bass) are meant to be
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
