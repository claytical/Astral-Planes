# Cosmic Dust Lifecycle Audit — 2026-11-25

Written to prepare the **composition-based regrowth overhaul**, in which the cell's next state alternates between cosmic dust, Cosmic Blocks and open space. All `file:line` references are relative to `Assets/Scripts/Dust/` unless stated otherwise. The audit describes the code **after** the constant-regrowth change made the same day.

---

## 0. Changed today: regrowth delay is now one constant

| Before | After |
|---|---|
| `MusicalRoleProfile.regrowthDelay` per role (Lead 1 · Harmony 2 · Groove 3 · Rhythm 4 · Bass 8) | **removed** |
| `MazePatternConfig.dustTiming.regrowDelay`, used for role-less cells (2–4 s) | **removed** (`DustTimingParams` class deleted) |
| `ApplyDensityRegrowEasing`: up to 2.5× slower when the maze is crowded | **removed** (along with `GetRoleRelevantSolidFraction` and `regrowDensityEaseMaxMultiplier`) |
| Regrow-speed colour preview pulse on commit | **removed** (`CosmicDust.RegrowSpeedPreview.cs` deleted, along with the `regrowSpeedPreview*` config fields) |
| `SuperNodeTrackNodeConfig.dustRegrowDelay` | **removed** |
| — | **`CosmicDustGeneratorConfig.regrowDelaySeconds = 3`** |

`ResolveRegrowDelay` (`CosmicDustGenerator.CellLifecycle.cs:172`) now returns `explicitOverride >= 0 ? explicitOverride : config.regrowDelaySeconds`. No caller passes an override (see F3).

`densityThrottleHalfAt` and `densityThrottleSkipAt` remain. They now only throttle Gravity Void ring growth (`Music/GravityVoidController.cs:249`).

The real time from clear to solid is: **3 s delay + any veto retries + waiting for the step gate (≤ 1 drum step at `regrowCellsPerStep` = 1, longer when the queue backs up) + `regrowColliderEnableDelaySeconds` (0.2 s) before the cell blocks movement.**

Assets still hold the old serialized `regrowthDelay` and `dustTiming` YAML. Unity ignores those values and drops them at the next reserialize, which is already open in VARIABLE_AUDIT.md.

## 0b. Role-driven lifecycle rules (implemented 2026-11-25)

| Event | Dust behaviour | Code |
|---|---|---|
| Motif begins | All musical roles regrow normally. | `ClearMaze` clears `_roleRegrowState`; `ResumeRegrowthAfterBridge` → `ResumeAllRoleRegrowth` |
| Dust is carved | The cell empties and schedules regrowth. | `FinalizeVehicleCarve` → `CarveCell` |
| Dust regrows with buds | It can be eaten by the Star. | `CommitRegrowCell` (VehiclePlow source → `BloomFlower`) |
| Star consumes dust | The cell empties. | `ZapClearCellHeld` (held by the discovery node) |
| Star becomes active (has zapped enough energy) | Regrowth is **suspended** for that role. The Star hands its hold to the discovery it ejects. Captured: the role stays suspended. Escaped or expired, charge lost, a SuperNode resolves, or the Star is destroyed first: the role resumes once no other hold of that role is live. | `PhaseStar.ZapState` `readyNow` → `AcquireRegrowHold`; `SpawnNodeCommon` → `node.AdoptRoleRegrowthHold`; end → `ReleaseRoleRegrowthHold(role, captured)` |
| Ring is completed | The role's remaining dust becomes Cosmic Blocks. Empty cells stay empty. | `ApplyRoleBlock` → `MarkRoleBlocked` |
| Role is released | Blocks turn back into ordinary dust. Empty cells resume regrowth. | `CosmicBlockField.ApplyPendingSwap` → `ResumeRoleRegrowth(previous)` |

- **Per-role state:** `RoleRegrowState { Normal, Suspended, Blocked }`, in `CosmicDustGenerator.RoleRegrowth.cs`.
- **The gate:** it lives in `RequestRegrowCellAt`. A cell whose *territory role* (its painted role, else its hidden role; generic fill excluded) is not Normal never schedules regrowth. Star-consumed cells and player-carved cells therefore follow exactly the same rule.
- **Suspending:** cancels the role's delay timers and drops its PendingRegrow cells back to Empty. Cells already growing in finish, and under a wall they become blocks.
- **Resuming:** re-requests regrowth for every Empty cell in the role's territory. A carved cell's VehiclePlow source is still on record, so it comes back with a bud.
- **`ReleaseRoleBlock` never resumes regrowth,** because it also runs during teardown.
- **Ring completes but no wall goes up** (`!AnyOtherRoleCanProgress`): the role is released straight away.
- **Discovery escapes or expires:** its consumed cells are released while the role is still suspended. If no other discovery of that role is live and none was captured, the role then resumes and every empty cell regrows.
- **Not gated:** Gravity Void rings. They create new dust rather than regrowing it.

---

## 1. State model

### `DustCellState` (`DustGridState.cs:65`)
```
Empty ──(delay coroutine ok)──▶ PendingRegrow ──(step gate)──▶ Regrowing ──(collider delay ok)──▶ Solid
  ▲                               ▲    │                         │                                   │
  │                               └────┴──── veto: re-enqueue ◀──┘                                   │
  └──────────── OnDustVisualFadedOut / instant hide ◀── Clearing ◀──────── ClearCell ◀──────────────┘
```
| Transition | Where |
|---|---|
| Solid → Clearing | `ClearCell`, `Clearing.cs:32` |
| Clearing → Empty | `OnDustVisualFadedOut` `CellLifecycle.cs:23`, `DeferredHideAfterPreExplode`, instant-hide branches in `Clearing.cs` |
| Any → Empty (hard) | `DespawnDustAt` `Clearing.cs:343`, `RemoveActiveAt` `Clearing.cs:262` (ClearMaze) |
| Empty → PendingRegrow | the controller's `RegrowCellAfterDelay` → `setCellPendingRegrow` (`CosmicDustGenerator.cs:391`) |
| PendingRegrow → Regrowing | `CommitRegrowCell` `CosmicDustGenerator.cs:491` |
| Regrowing → Solid | `CommitRegrowCell` `CosmicDustGenerator.cs:597` |
| Regrowing → PendingRegrow (veto) | `CommitRegrowCell` `:590`, and the bulk-spawn vehicle veto `BulkSpawn.cs:193` |
| Empty → Regrowing → Solid (void growth) | `VoidGrowCellNow` `VoidGrowth.cs:375/432` |
| Empty → Solid (maze build) | `BulkSpawn.cs:242` |

The solid-transition callback (`CosmicDustGenerator.cs:69-81`) runs on every change into or out of Solid. It handles role-density tracking, Cosmic Block conversion (`OnCellSolidTransitionForBlocks`), and clearing the pending source and delay.

### `CellFlags` (`CosmicDustCellRegistry.cs:6`)
| Flag | Set by | Cleared by | Meaning |
|---|---|---|---|
| `ForceGrayRegrow` | `CarveCellPreserveGray`, jail | `CommitRegrowCell` (after the role is resolved) | regrow as None and never earn a tint |
| `ZapForceGray` | `ZapClearCellHeld`, `ClearAndHoldRoleCells` | `CommitRegrowCell` | same idea, for star or track drains |
| `VoidGrow` | void ring scheduling | `FinalizeVehicleCarve` (also drops the imprint) | cell came from the Gravity Void |
| `PlayerCarved` | `FinalizeVehicleCarve` | `CommitRegrowCell` | used for legibility and flowers |
| `Solid` (unbreakable) | interstitial walls | ClearMaze | carving can never get through it |
| `RoleBlocked` | `BlockCell` (`Blocks.cs:144`) | `UnblockCell`, `ReleaseRoleBlock` | Cosmic Block; `ClearCell` refuses it |

### Side sets on the generator
| Set | Meaning | Cleared |
|---|---|---|
| `_permanentClearCells` | never regrow this motif | `ClearMaze` (`MazeGeneration.cs:152`); RecordDock removes and adds vault cells |
| `_heldRegrowCells` | empty until an owner calls `ReleaseHeldCells` | `ReleaseHeldCells`, ClearMaze, `HardStopRegrowthForBridge` |
| `_pendingRegrowSource` | which `DustClearSource` started the cycle (gates the flower bloom) | Solid transition, ClearMaze |
| `_pendingRegrowDelay` | delay for the current cycle, kept across keep-clear refreshes | Solid transition, ClearMaze |
| `_carveAccumulator` | fractional chip energy for each cell | finalize carve, ClearMaze |
| `_roleBlockedCells` / `_rolePassageCells` | Cosmic Block state | the Blocks partial |

---

## 2. Birth (first appearance)

| Path | Entry | Notes |
|---|---|---|
| Maze build | `MazeGeneration.cs`, then `StaggeredGrowthFitDuration` (`BulkSpawn.cs:7`) | Frame-budgeted. Skips permanent, keep-clear, VoidGrow, blocked and claimed cells. Cells under a vehicle are skipped during bootstrap and otherwise go into the regrow pipeline (`BulkSpawn.cs:60-66`). |
| Interstitial | `Interstitial.cs` (also calls StaggeredGrowth) | Marks gap and trace cells permanent (`:89/190/320`). |
| Gravity Void rings | `GravityVoidController.cs:292` → `GrowVoidDustDiskFromGrid` (`VoidGrowth.cs:7`) → `VoidGrowCellNow` (`:347`) | Skips the regrow pipeline entirely: its own coroutine dictionary, its own grow-in and ripple timing. Refused while `_regrowthSuppressed`. |
| Cosmic Blocks | `ApplyRoleBlock` (`Blocks.cs:107`) | Not a new cell. Existing Solid cells of the role are converted in place, and regrown cells convert through `OnCellSolidTransitionForBlocks` (`Blocks.cs:182`). |
| Record dock / vault | `RecordDock.cs` | Vault shell cells are immune to clearing (`IsSealedVaultShell`). Rewrites permanent and held membership (`:667-735`, `:1089-1095`). |

## 3. Identity

- **Imprint** (`DustImprintStore`): `role`, `voiceIndex`, `hiddenRole` (Voronoi ground truth), `hiddenRoleIsGenericFill`.
- **Gray start:** cells spawn with role None and a darkened hidden-role hint. `FinalizeVehicleCarve` (`CellLifecycle.cs:126`) promotes the hidden role (`RestoreVoronoiImprint` / `PromoteHiddenRole`), so carving is what reveals a role.
- **Regrow role:** `ResolveRegrowVoice` (`DustRoleDensityTracker.cs:178`) is the authoritative check at commit time, in this order: force-gray → None; generic fill → None; a hidden voice that is active; the imprint voice if active; neighbour plurality; least-dense active voice.
- `GetProspectiveRegrowRole` (`CellLifecycle.cs:~183`) is a cheap guess before commit. It is now used only for explosion tint, drum-intensity weight, tutorial events and the Blocks role checks. **It no longer affects timing.**

## 4. Death (clear paths)

Every clear goes through `ClearCell` (`Clearing.cs:7`) except `DespawnDustAt`, `RemoveActiveAt` and the bulk fades. `ClearCell` refuses RoleBlocked and vault-shell cells. On a cell that is not Solid (Empty / Clearing / PendingRegrow) with `scheduleRegrow`, it **restarts the regrow timer** (see F5).

| Source (`DustClearSource`) | Caller | Regrow behaviour |
|---|---|---|
| VehiclePlow | `ChipDustByVehicle` / `CarveDustByVehicle` → `FinalizeVehicleCarve` | normal; promotes the role; the flower bloom is allowed |
| StarDrain | `ZapClearCellHeld` (`Clearing.cs:152`) via PhaseStarDustAffect | **held**; DiscoveryTrackNode / PhaseStar.OnDestroy releases it |
| TrackFilled | `ClearAndHoldRoleCells` (`Clearing.cs:190`) via `InstrumentTrack.DustCoupling.cs:26` | **held** across the whole role; released at `:45` |
| CollectableArrival / CollectablePlow | `CarveCellPreserveGray` | ForceGray, so it comes back gray |
| Jail | `CreateJailCenterForCollectable` (`CellLifecycle.cs:193`) | ForceGray, plus a DustClaim hold on the centre |
| SuperNode | `SuperNodeTrackNode.cs:377` → `CarveCellPreserveGray` | ForceGray |
| CollectableDiscard / CollectableBombBlast | `TriggerDiscardBlast` (`DiscardBlast.cs:23`) | a `permanentClearRatio01` share becomes permanent craters |
| StarBurrow, Zap, System | star nav, misc | normal |
| ~~(note commit)~~ | ~~`PermanentlyClearOneCellForRole`~~ | **Removed 2026-11-25.** Placing a note no longer clears any dust. Also removed `commitClearMinTerritoryPerRole` and `InstrumentTrack.NotifyNoteCommittedForDust`. |
| (generic fill) | any clear of a cell with `hiddenRoleIsGenericFill` | **permanent** (`CellLifecycle.cs:~300`) |
| (motif end) | `BeginSlowFadeAllDust`, `FadeOutRemainingSolidDust`, `DissolveAllVisuals` | no regrow |

## 5. Rebirth (the regrow pipeline)

1. **`RequestRegrowCellAt`** on the generator (`CellLifecycle.cs:286`). Checks, in order: suppressed → drop; out of bounds → cancel; held → drop; generic fill → mark permanent; permanent / already has dust / no imprint → drop. Then it resolves the delay (explicit → remembered → constant) and hands off.
2. **Controller `RequestRegrowCellAt`** (`CosmicDustRegrowthController.cs:104`). Starts one `RegrowCellAfterDelay` coroutine per cell. `refreshIfPending` cancels any earlier one.
3. **`RegrowCellAfterDelay`** (`:190`). Waits for the delay. Keep-clear means it waits until released and then retries straight away. Other vetoes (claim, spawn availability, a vehicle, a collectable) retry after `regrowVetoRetryDelaySeconds` (0.5 s). On success the cell becomes PendingRegrow and is enqueued.
4. **Step gate** `ProcessStepGate` (`:141`), driven by `Update` on `drums.currentStep`. Each step adds `regrowCellsPerStep` to the budget, which banks up to 2 steps. The same vetoes run again; a vetoed cell is re-enqueued.
5. **`CommitRegrowCell`** (`CosmicDustGenerator.cs:459`). The vehicle gate runs again. Then: state Regrowing, reset for reuse, resolve the role, write the imprint, choose the tint, decide whether the flower bloom shows (VehiclePlow source only), wait `regrowColliderEnableDelaySeconds`, run the vetoes a third time, then Solid with the collider on.

## 6. Holds and vetoes

| System | Type | Checked at |
|---|---|---|
| `dustClaims.IsBlocked` (keep-clear: ship pockets and trails, star pocket) | waits | delay coroutine, step gate, commit |
| `IsDustSpawnBlocked` (DustClaimManager) | retry | same three |
| `IsSpawnCellAvailable` (exclusion map / spawn grid) | retry | delay coroutine, step gate |
| `IsCollectableCellFree` | retry | delay coroutine, step gate |
| `IsVehicleOverlappingCell` (collider-suppression controller) | retry | all three, plus bulk spawn |
| `_heldRegrowCells` | hard hold | `RequestRegrowCellAt` only |
| `_permanentClearCells` | hard veto | all stages |
| `_regrowthSuppressed` (bridge / motif end) | global stop | `RequestRegrowCellAt`, `Update`, commit, void growth |
| Corridor reservation (`CosmicDustCorridorReservationController`) | claims plus a regrow request on release | — |

## 7. Teardown

- **`HardStopRegrowthForBridge`** (`CosmicDustGenerator.cs:259`): suppresses regrowth, releases blocks, clears held cells, cancels every coroutine, hides PendingRegrow cells, fades Regrowing cells, and **sets `_regrow = null`**. Called from BridgeCoordinator, ChordProgression, MatchEnd and RecordRecovery.
- **`ResumeRegrowthAfterBridge`** (`:310`), called from SceneFlowCoordinator.
- **`ClearMaze`** (`MazeGeneration.cs:120`): cancels coroutines, releases blocks, resets the vault, releases the star keep-clear and corridors, removes every active GO, and clears the permanent, held, imprint, flag, accumulator and pending sets.
- **Pooled GOs** live for the whole session. `GetOrCreateCellGO` moves them back into position when reused (`CellLifecycle.cs:43-55`).

---

## 8. Findings

Tiers follow the earlier audits: **T1** = bug or risk, **T2** = duplication or complexity that gets in the way of the overhaul, **T3** = dead code or cleanup.

| # | Tier | Finding |
|---|---|---|
| F1 | T2 | ✅ **Done 2026-11-25.** All vetoes now go through one predicate, `CosmicDustGenerator.IsRegrowBlocked` (keep-clear/claim, collectable on the cell, spawn-grid availability, vehicle overlap), exposed as `_regrow.IsRegrowBlocked`. It is used by the delay coroutine, the step gate, and both checks in `CommitRegrowCell`. Keep-clear still *waits* in the delay coroutine instead of polling. Behaviour change: the commit step now also refuses a cell a note or collectable moved onto during grow-in. Side note: `IsKeepClearCell` and `IsDustSpawnBlocked` were the same `dustClaims.IsBlocked` lookup all along, so the old "two independent systems" comments were wrong. |
| F2 | T2 | ✅ **Done 2026-11-25.** `DustRegrowthScheduler` is deleted. `CosmicDustRegrowthController` now owns the delay, commit and void-grow coroutines plus the step queue, and one `CancelAll()` stops everything. The controller is created lazily and lives as long as the generator; the bridge no longer sets it to null. Also fixed: a routine that finished on its first frame could leave a stale entry that permanently blocked void growth on that cell. `StartTracked` now reserves the key before starting the coroutine. |
| F3 | T3 | **The override plumbing is unused.** `regrowDelaySeconds` / `delayOverride` parameters exist on `ClearCell`, `CarveCell`, `CarveCellPreserveGray`, `CreateJailCenterForCollectable`, `TriggerDiscardBlast`, `ReleaseHeldCells` and `DustClearRequest`, and the `_pendingRegrowDelay` dictionary backs them. Every caller passes -1. Kept for now as the overhaul's hook; delete them if the composition model doesn't need per-call timing. |
| F4 | T3 | ✅ **Done 2026-11-25.** Removed `DustRegrowthScheduler` (including `PendingCorridorRegrowth`, `RegrowCellsPerStep` and `ColliderEnableDelaySeconds`), the `clearImprintOnRefresh` parameter, the `GenericFillSolidCount` counter (generic fill is still kept out of voice density), the `EnsureRegrowController` calls, and the orphan "Density gate" comment from F10. |
| F5 | T1 | **Repeated clears put regrowth off indefinitely.** `ClearCell` on a cell that is already Empty or PendingRegrow restarts the timer (`refreshIfPending: true`, `Clearing.cs:16-19`). A collectable sitting still in its plow window, or a ship nosing a cleared cell over and over, can keep a cell open forever. That may be fine, but nothing makes it a deliberate choice. Decide this explicitly in the composition model. |
| F6 | T1 | **Duplicate step-queue entries.** `CancelRegrow` removes the cell from `_pendingStepRegrowSet` but leaves it in the queue (`CosmicDustRegrowthController.cs:88`). Enqueuing it again adds a second entry, which spends 2 of the step budget on one cell. The effect is minor: a short regrow stall when the queue is busy. |
| F7 | T1 | **Clears during suppression never regrow.** `RequestRegrowCellAt` drops requests while `_regrowthSuppressed` is set and keeps no record of them. That is fine when ClearMaze follows, but if `ResumeRegrowthAfterBridge` ever runs without a maze rebuild (for example a cancelled bridge), those cells stay holes. |
| F8 | T2 | **Five ways a cell can stay open.** Permanent (generic fill, craters, interstitial, star cell; note-commit clearing removed 11-25), held (star drain, track fill), keep-clear claims, ForceGray (stays open in effect, but regrows gray), and Cosmic Block passages. Each has its own set and its own release rule. The composition overhaul's "open space" state should probably replace several of them. |
| F9 | T2 | **Void growth runs outside the regrow pipeline.** It has its own coroutines and state changes, its own suppression check, and its own role and tint choice. It is a second birth system that the composition model will have to either absorb or coordinate with. |
| F10 | T3 | **Stale comments.** (The `CommitRegrowCell` "Density gate" comment was removed with F4.) The `ClearCell` TODO for frontier compensation (`Clearing.cs:~88`) points at commented-out code. |
| F11 | T3 | **`DustClearSource` is only half used now.** It no longer controls timing. It still gates the flower bloom (VehiclePlow only, `CosmicDustGenerator.cs:525`) and flows into ForceGray through the calling method, not through the enum. It is a natural place to hang composition rules, or it could shrink to a `bool bloom`. |

---

## 9. Hooks for the composition overhaul

These are the points where a dust → block → open-space cycle could decide what a cleared cell becomes next:

1. **`RequestRegrowCellAt` (`CellLifecycle.cs:286`).** This is already where "open space" (permanent) is decided for generic fill. A composition rule can send a cell to *permanent / open* here, or to *timed regrow*.
2. **`ResolveRegrowDelay` (`CellLifecycle.cs:172`).** One constant today. If the composition model has its own phases, this becomes "time until the next composition beat".
3. **Step gate (`CosmicDustRegrowthController.ProcessStepGate`).** Already locked to the drum steps, so it is the natural clock for a composition sequence (for example, commit a batch on bar boundaries).
4. **`CommitRegrowCell` → `ResolveRegrowVoice`.** This chooses *what* comes back. The composition model would choose dust (role / None) or block here.
5. **`OnCellSolidTransitionForBlocks` (`Blocks.cs:182`).** This already turns a regrown cell into a block when its role is walled, including the no-trap check (`CanBlockWithoutTrapping`). It is the existing path from regrowth into blocks, and the overhaul should extend it rather than build a parallel one.
6. **`ApplyRoleBlock` / `ReleaseRoleBlock` (`Blocks.cs:107/85`, driven by `CosmicBlockField`).** Blocks currently cycle once per completed ring, which is the composition clock that already exists.

F1, F2 and F4 were cleaned up on 2026-11-25 (compiled, not playtested). Leave F3 until the overhaul decides whether per-call timing survives. F5–F7 are still open.
