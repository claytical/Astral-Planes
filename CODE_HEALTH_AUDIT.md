# Code Health Audit — 2026-11-17

A full pass over `Assets/Scripts` (about 330 files, 65k lines; third-party MPTK excluded) looking at hygiene, bug risk and **stutter**. The stutter seen in play is hitches, not steady low FPS, and happens with logging off.

**Status key:**
- **FIXED**: changed in this pass. `Assembly-CSharp` compiles with msbuild; not yet opened in Unity or playtested.
- **OPEN**: flagged only. *Profiler-gated* means: only worth doing if the profiler shows it.
- **Passes:** tier 1 and tier 2 (committed `d876f7fa` on branch `code-health-audit`); pass 3 = the remaining small allocations, B4 boundaries, B5 and the C leaks.

Severity:
- **Hitch**: a single-frame spike.
- **Steady**: a per-frame cost.
- **GC**: allocation that feeds garbage-collection pauses.

Line numbers drift. Methods are named so each item can be found by grep.

---

## A. Stutter

**Logging is not the cause.** Hot call sites check `GameFlowManager.VerboseLogging` or a local flag *before* building the `$""` string. The `PhaseStar.Diagnostics` wrappers that take prebuilt strings are only called from rare paths.

Logs that are not gated, but only fire on events:
- `InstrumentTrack.Lifecycle.cs` `OnDisable`: a warning that includes `Environment.StackTrace`, once per track at teardown.
- `InstrumentTrack.cs`: a zero-length note window warning.

### Fixed in this pass

| # | Where | What it was | Fix |
|---|---|---|---|
| A1 | `ProjectSettings.asset` `gcIncremental` | Incremental GC was **off**. Every collection was a full stop-the-world pause, the classic periodic hitch. | `gcIncremental: 1` |
| A2 | `2D URP Renderer_Renderer.asset` `CosmicPlasmaFeature` | The paused plasma experiment's renderer feature was **active**. Every frame it ran a half-res RGBA16F field pass plus a fullscreen blur/composite, with nothing drawing into it. | Feature set inactive (`m_Active: 0`). See `COSMIC_PLASMA_STATUS.md`. |
| A3 | `Dust/DustReachability.cs` `Run` | A `LinkedList<int>` deque allocated a node per visit (~720+ objects per run). The wall predicates were also called for every disk cell of every neighbour visit, which is ~37k delegate calls per run, each going through `TryGetCellState` → `EnsureCellGrid`. Shared by Cosmic Blocks doorways, the 0.5s watchdog, `CanBlockWithoutTrapping` per regrowing cell, spawn reachability and vine planning. | Predicates are sampled once per cell into `bool[]`, and the deque is an array ring buffer. Nothing allocates after warm-up, and there are ~720 predicate calls per run. |
| A4 | `CosmicDustGenerator.cs` `EnsureCellGrid` | Ran `TryEnsureRefs` and the DrumTrack grid-size getters on **every** `TryGetCellState` / `TryGetCellGo` / `SetCellState` call. | Per-frame fast path once the grid has been confirmed for the current `DrumTrack`. |
| A5 | `CosmicDust.ResistancePreview.cs` `PreviewResistance` | The scouting telegraph runs at 50 Hz per cell in a non-boosting ship's footprint. Each call cancelled the pulse and started a new coroutine that rewrote all particles every frame. | An in-flight preview pulse runs until 60% of its duration before it can restart. |
| A6 | `StarPool.Spawning.cs` `Tick` | Every frame: `MotifProfile.GetActiveRoles()` allocated a HashSet and a List, and `HasAnyDustWithRole` scanned the grid for every role, even when that role's star slot was full. | Roles cached per motif. The dust scan only runs when a spawn is possible, or when the diagnostic dump wants it. |
| A7 | `CosmicDust.VehicleCompression.cs` `TickVehicleCompression` | Every idle dust cell (hundreds) rewrote its particle transform's `localScale`/`localPosition` every frame, dirtying transforms and particle bounds. | The rest pose is written once when the cell settles, then skipped. |
| A8 | `CosmicDustGenerator.cs` `CommitRegrowCell` | `new WaitForSeconds` per regrowing cell. | One shared instance, rebuilt only if the config value changes. |
| A9 | `PhaseStarDustAffect.cs` tentacle build; `NoteTether.cs` `Awake` / shimmer | `new Material(Shader.Find("Sprites/Default"))` per tentacle, per shimmer, per tether; all leaked. Reading `NoteTether._lr.material` also cloned the prefab material per tether. | New `Utilities/SharedMaterials.SpriteDefault`; these renderers use `sharedMaterial`. Their colour comes from gradients and `startColor`, so sharing is safe. |
| A10 | `Vehicle.cs` `GetColliderBoundingRadiusWorld` | `GetComponent<Collider2D>` on every reachability run. | Cached. |
| A11 | `CosmicDustGenerator.Blocks.cs` `OpenDoorwayTo` | `new List` per doorway. | Reused scratch list. |

`CanBlockWithoutTrapping` was not batched per frame as planned. A3 and A4 cut each run's cost by about 50×, so profile before adding a batching layer.

### Open, ranked

| # | Where | Cost | Suggested fix |
|---|---|---|---|
| A12 | **FIXED (tier 2)** — `CosmicDust.BlockForm.cs` `SetBlockForm` | Was: every cell of the role converted in one frame, each with a 0.4s per-frame particle rewrite plus two more rewrites for the tint and a `GetComponentInChildren<Explode>`. | Gameplay state stays immediate. The visual is staggered: a random 0–0.3s start per cell (no `WaitForSeconds` alloc), the tint refresh is deferred into that start, intermediate swell frames are budgeted (A28), live sizes are written absolutely so skipped frames lose nothing, and the final size always lands. |
| A13 | **FIXED (tier 2)** — `CosmicBlockField.cs` shimmer | Was: up to 15% of blocked cells re-flashed every ~0.08s, each running 0.7s of per-frame rewrites. | New `shimmerMaxConcurrent` (24) caps live flashes. Flash frames are budgeted (A28). |
| A14 | **FIXED (tier 2)** — `CosmicDustGenerator.Shockwave.cs` | Was: every solid cell flashed with per-frame rewrites, plus a List per ring and `TryGetComponent` per cell. | Flash frames are budgeted (A28). Ring buckets are reused. Dust refs come from `DustGridState.CellDust`, as does `TryFlashSolidCell`. |
| A15 | **FIXED (tier 2)** — `CosmicDust.Lifecycle.cs` regrow fade-in | Was: a per-frame full rewrite per regrowing cell, plus five full rewrites on the spawn frame. | Fade frames are budgeted (A28). The redundant trailing `ApplyTintVisual` in `RunSpawnVisuals` was removed, and `SetTint` is now a single rewrite (A16), so the spawn frame does 1 rewrite instead of 5. The double `CaptureBaseVisual` is left (no particle cost). |
| A16 | **FIXED (tier 2)** — `CosmicDust.SetTint` / `ApplyTintVisual` / `RefreshLegibleRestTint` | Was: three (`SetTint`) or two (`RefreshLegibleRestTint`) full rewrites per call, each overwritten by the next, plus `GetComponentInChildren<Explode>` every call. | One rewrite each (a `display:` flag on `SetBaseTint`; `ApplyTintVisual` lets its work-shader write be the only one). `Explode` is cached. |
| A17 | **FIXED (tier 2)** — `MotifRingGlyphApplicator.GameplayRings.cs` `DeformBinRingCoroutine` | Was: every note's anchor, tether and shimmer were built in one frame. | Built 4 per frame. A `_tetherDetachGeneration` check stops building once `DetachPendingRingTethers` runs, so no tether outlives the record leaving. The LINQ sort was kept: it is stable and runs once per bin. |
| A18 | **FIXED (pass 3)** — `CosmicDustVehicleReservationController.cs` | Was: a new HashSet and a `$"Vehicle#{id}"` string every 0.1s per ship, and a HashSet every 0.15s per star. | One scratch footprint set (the exclusion map copies it). Vehicle and trail owner keys are cached per owner id. |
| A19 | OPEN (profiler-gated) — `PhaseStarDustAffect.cs` `RebuildTentaclesForRole` | **Hitch** on attune or role change. Destroys and recreates every tentacle GameObject, LineRenderer, AnimationCurve and shimmer ParticleSystem. Once per attune, so it only matters if the profiler shows it. | Pool tentacles and recolour them instead of rebuilding. |
| A20 | **FIXED (pass 3)** — `PhaseStarDustAffect.Vine.cs`, `TentacleVineRoute.cs` | Was: local-function and lambda closures allocated per tentacle plan (two layers: the role/common predicates and the planner's Run wrappers). | Cached delegates read per-plan fields; planning is synchronous, so the fields are set before and cleared after. The second fallback search remains (by design). |
| A21 | **FIXED (pass 3)** — `PhaseStarDustAffect.Gradients.cs`, `NoteVisualizer.Playhead.cs`, `NoteVisualizer.AnimationTasks.cs` | Was: `Gradient.SetKeys` with new key arrays per frame (the drain pulse rebuilds every frame). | Static scratch key arrays (`SetKeys` copies them). |
| A22 | **FIXED (pass 3)** — `NoteVisualizer.Playhead.cs` `GetScreenWidth`; `PhaseStar.SuperNodeTell.cs` `IsStarVisibleForTell` | Was: `GetComponent` every frame. | Cached, and re-fetched when the source canvas or preview visual changes. |
| A23 | **FIXED (pass 3)** — `NoteVisualizer.Layout.cs`, `NoteVisualizer.MarkerSync.cs`, `NoteVisualizer.GetWorldCornerY` | Was: two Dictionaries per row, `new Vector3[4]`, LINQ into a HashSet, `ToList` of keys, and per-call Lists/HashSets in the canonicalize passes. | Scratch collections, a static corners array and a cached RectTransform; `TryGetComponent<MarkerTag>`; `System.Linq` dropped from MarkerSync. `ForceSyncMarkersToPersistentLoop` still allocates its loop-step set, because it iterates it while placing markers. |
| A24 | OPEN (profiler-gated) — `InstrumentTrackCompositionSpawner.cs`, `NoteVisualizer.MarkerPlacement.cs`, `MotifRingGlyphApplicator.Animation.cs` | **Moderate** event hitches. Unpooled `Instantiate` of collectables, markers and travel dots on drum steps. | Pool if the profiler shows them. |
| A25 | **FIXED (pass 3)** — `DrumTrackGridMapper.cs` `ValidateSpawnGrid` | Was: `Physics2D.OverlapCircleAll` plus `GetComponent` per cell, every 10s. | `OverlapCircle(…, ContactFilter2D, results)` into a 64-slot static buffer, with a filter matching `OverlapCircleAll`'s defaults (`DefaultRaycastLayers`, `queriesHitTriggers`); `TryGetComponent`. |
| A26 | **VERIFIED** — `PhaseStarVisuals2D.cs` `traceParticleState` | `GetComponentsInChildren<ParticleSystem>` per frame when the flag is on. | It is `0` on `Star.prefab` (the only asset that serializes it). No change. |
| A27 | **WON'T FIX** — `MotifProfile.GetActiveRoles()` | Allocates on every call. | After A6 there is no per-frame caller left; the rest are event-time. Not worth changing the return contract. |
| A28 | **NEW, tier 2** — `Dust/DustParticleRewriteBudget.cs` | — | A shared per-frame budget (target 48 cells) for live-particle rewrites from dust animation loops: `ApplyDisplayedTintAnimated`, budgeted `SetDustColorAllParticles` / `SetRainbowParticleColor` / `ApplyBlockSizeMul`. Cells are spread across frames by a per-cell phase when demand exceeds the target, with a hard cap on burst frames. Final writes always bypass it. Used by the charge, deny/preview, colour-flash and rainbow pulses, the block swell and the regrow fade-in. |

**Profile first:** a ring completion, a bass drop and a regrow wave during a live burst. If animations look choppy under load, raise `DustParticleRewriteBudget.TargetPerFrame`. If they still hitch, lower it. A18–A27 remain open.

---

## B. Bug risk

| # | Status | Where | Issue | Notes / fix |
|---|---|---|---|---|
| B1 | **FIXED** | `NoteVisualizer.RecordDeparture.cs` (was uncommitted) | Swirling markers stayed children of their track rows. `RecomputeTrackLayout` (reached via `RequestLeaderGridChange` from several callers), `ReconcileDuplicateMarkersInRow` and `CanonicalizeTrackMarkers` could re-adopt them into `noteMarkers` mid-swirl. `UpdateNoteMarkerPositions` then fought the motion every frame and misplaced them after they were parented to the record. The swirl was also not stopped on disable. | Markers move to the rows' shared parent for the swirl. `RecomputeTrackLayout` and `CanonicalizeTrackMarkers` no-op while `_loopMarkersDetached` is set. The coroutine handle is tracked and stopped (destroying its markers) in `OnDisable` and on re-entry. The stale header comment was fixed. |
| B2 | **FIXED** | `SceneFlowCoordinator.QuitToSelection` / `ReturnToJoinScreen` | Scene references were nulled and `_setupInFlight` reset without stopping the bridge, path-choice, setup and game-over coroutines. All of those run on the persistent `GameFlowManager`, so a quit mid-bridge could resume against nulled or destroyed state, or double-run setup. | `GameFlowManager.AbortSessionCoroutines()` runs `StopAllCoroutines()` plus `SceneFlow.ResetAfterAbortedFlows()` (setup guard, fade overlay, banner) before quitting or aborting the tutorial. A second quit while one is in flight is ignored. |
| B3 | **FIXED (tier 2)** | 21 sites of `GetComponent<T>() ?? AddComponent<T>()`: NoteVisualizer.MarkerPlacement ×7, MarkerSync, MarkerLifecycle, `InstrumentTrack` ×3, `InstrumentTrack.BurstLifecycle` ×2, `InstrumentTrack.Lifecycle`, `CosmicDust`, `NoteTether`, `MotifRingGlyphApplicator.GameplayRings`, `SceneFlowCoordinator` ×2, `Vehicle.NoteTrailVisuals`, `ControlTutorialHighlight` | Unity's fake-null in the Editor: a missing component comes back as a non-C#-null object, so `??` never adds it and later use throws `MissingComponentException`. This happens in the Editor only; builds behave differently. | New `GetOrAddComponent<T>()` extensions (`Utilities/UnityObjectExtensions.cs`, `TryGetComponent`-based) replace all 20 `?? AddComponent` sites. The two `GetComponent ?? GetComponentInChildren/InParent` fallbacks (`CosmicDust` particle system, `SceneFlowCoordinator` MIDI player) use `TryGetComponent` explicitly. |
| B4 | PARTIAL (pass 3) | ~80 `Instance?.` plus `?.` on `_gfm`, `controller`, `noteViz`, `dustGenerator` | `?.` does not respect Unity's destroyed-object null, so the call reaches a destroyed object. | Teardown and bridge boundaries converted to explicit `!= null` checks: `SceneFlowCoordinator` (16 statement sites, the two record-hold waits, `Hangar.Instance`) and `MotifRingGlyphApplicator.SpinOff`. The rest: fix opportunistically. |
| B5 | **FIXED (pass 3)** | `PhaseStar.cs` `Initialize` | Was: `dust.OnAttuned += OnAttuned_SetRole` with no guard. | `-=` then `+=`. Also removed an unused `_gfm?.activeDrumTrack` local in `OnDisable`. |

---

## C. Leaks

| # | Where | Issue | Fix |
|---|---|---|---|
| C1 | **FIXED (pass 3)** — `UI/BoundaryWrap.cs` static `_lastWrapTime` / `_lastMineBounceTime` | Was: keyed by `Rigidbody2D` and never pruned. | Cleared on every single-mode scene load (and at subsystem registration); the entries only matter for a fraction of a second. |
| C2 | **FIXED (pass 3)** — `Managers/TrackSelectionJoinController.cs` static `sceneLoaded +=` | Would add a hook per session with fast play-mode. | `-=` then `+=`. |
| C3 | ACCEPTED — `Dust/PathExitParticleBuilder.cs` static texture/material caches | Acceptable as caches; they only leak if domain reload is disabled. | No change. |
| C4 | **FIXED (pass 3)** — `PhaseStar.SuperNodeTell.cs` | Was: `new Material(Shader.Find)` per star, never destroyed. | The owned material is now cloned from `SharedMaterials.SpriteDefault` (no shader lookup) and destroyed in `PhaseStar.OnDestroy`. |

Subscribe/unsubscribe balance is otherwise clean across partials.

---

## D. Dead code (verified by grepping every top-level type, plus GUIDs in scenes/prefabs/assets)

**Tier 2: all rows below removed (2026-11-17) except the serialized Inspector knobs at the end, which are flagged only.**

| Item | Notes |
|---|---|
| `Visualizers/MotifGlyphGenerator.cs` (660 lines) | Only `GlyphPolyline` is live (used by `MotifRingGlyphGenerator`). `MotifGlyphGenerator`, `GlyphOutput`, `DerivedMetrics`, `RoleMetrics` and `GlyphSpeciesParams` have zero references and no GUID uses. **Removed.** `GlyphPolyline` moved to `Visualizers/GlyphPolyline.cs`. |
| `Phase/PhaseBridgeSignature.cs` | Marked "NOT CURRENTLY INTEGRATED". `BridgeLibrary.Default()` always returns `AtBridgeEnd`, so the `AtBridgeStart` / `MidBridge` cases in `HarmonyDirector` and `CommitOnNextBoundary` never run. **Removed**, along with `CommitNextChordNow` and `_forceCommitNextBoundary`. Nothing ever set the flag on the live path, so motif progressions still swap at the next boundary via `_hasPendingProfileSwap`. |
| Zero-listener events | `SessionStateCoordinator.BridgePendingChanged`, `.GhostCycleChanged`, and `InstrumentTrack.OnLoopNotePlayed` (still raised every step). **Removed.** |
| Unused fields | `HarmonyDirector._pendingCharges`; `InstrumentTrack._boundaries`, `_ascendQueued`, `_pendingLoopMultiplier`; `PlayerSelect._selectedColor`; `MidiVoice._forcedProgramPropsCached`; `NoteTether._cumDist`, `_totalLen`, `_cachedCount`, `_cachedHash`. **Removed.** |
| Write-only fields | `CosmicDustGenerator._runtimeVoidOnlyDustCreation`, `_targetSolidCount`; `DiscoveryTrackNode._lastProcessedStep`; `PhaseStar._buildingPreview`; `LocalPlayer._gamepadDriving`, `_suppressChoose`; `MarkerLight._base`. **Removed.** Also found by the compiler and removed: `HarmonyDirector._lastPreviewLoopIdx`, `_ringsArmedThisLoop`, `_previewChordIdx`; `GameFlowManager.currentState` plus its unused `sceneHandlers` / `Start()` / `HandleTrackFinishedSceneSetup` / `HandleTrackSelectionSceneSetup` (a dead duplicate of `SceneFlowCoordinator`'s live handlers); and the `dbgTag` local in `InstrumentTrack`. |
| Inspector knobs nothing reads | `HarmonyDirector.retunePolicy`, `conflictPolicy` and their enums: **removed**. **Still flagged** (serialized, so removing them would silently drop values set in scenes; confirm they are abandoned first): `HarmonyDirector.remixRings`, `commitWindowBeats`, `spreadOverLoops`; `Boundaries.commitDepth01`, `minCommitSpeed`, `outwardSpeedBleed`, `returnToPlaySpeed`, `innerSettleBuffer`, `settleSpeedThreshold`; `GameFlowManager.phaseBridgeFadeOutSeconds`, `phaseBridgeFadeInSeconds`; `NoteVisualizer.playheadTrailEnabled`; `HideParentTextForChild.changeToStatus`. |
| Possibly dead | `PhaseRecord` / `GardenRecord` (in `PhaseStar.cs`, never used): **removed**, along with the stale comment in `DrumTrack.cs`. `_baseEmission` / `BaseEmissionScalar` is **live** (`ResetVisualToBase` falls back to it). |

Already gone: `CosmicBlock.cs` and its prefab, `_regrowExcludeRoleByCell`, `OnAscensionCohortCompleted`, the old trap spawner.

Not dead despite having no code references (used from scenes or prefabs): `IntroTitleFader`, `ModeSelectPanel`, `SelectGlyphHint`, `GlowPulseText`, `HideParentTextForChild`, `DiscoveryTrackNodeCharacterVis`, `CosmicPlasmaFeature`, `MazeTelemetryRecorder`.

---

## E. Cosmetic / hygiene

- **E1:** the MarkerTag/MarkerLight setup is repeated ×5. With `GetOrAddComponent` (B3) each site is now 2–5 lines, and each sets a different subset of fields (track/step, placeholder, conditional burst id). A shared helper would change behaviour or need a flag per field, so it was **left as is**.
- **E2:** `config != null ? config.scaleDownDuration : 0.5f` and similar fallbacks are duplicated across `MotifRingGlyphApplicator.SpinOff` / `GameplayRings`. Use one non-null default config.
- **E3:** the `RecordDeparture` swirl tuning (`0.35`, `0.65–1.6` twist, `1.6–3.2` pull) is hard-coded. It belongs on `RingGlyphConfig` next to the spin-off timings.
- **E4:** stale comments:
  - `CosmicBlockField.cs` still mentions the deleted `CosmicBlock` prefab.
  - `HarmonyDirector.cs` "your bridge coroutine should call…": **removed with the dead switch.** The `CosmicBlockField.cs` note is intentional history, so it stays.
- **E5:** files over 800 lines:
  - `CosmicDustMazePatterns.cs` (1145)
  - `CosmicDustGenerator.MazeGeneration.cs` (1045)
  - `SceneFlowCoordinator.cs` (~1050)
  - `CosmicDustGenerator.cs` (~1000)
  - `InstrumentTrack.cs` (971)
  - `PhaseLibraryCarousel.cs` (936)
  - `RingPreviewPlayer.cs` (802)
- **E6:** there is one TODO (`CosmicDustGenerator.Clearing.cs`, density conservation). No FIXME or HACK markers, and no large commented-out blocks.

**Already clean:**
- All `MenuItem`s are under `Tools/Astral Planes/`.
- `AudioBalanceDebugPanel` is wrapped in `#if UNITY_EDITOR || DEVELOPMENT_BUILD`.
- Every `UnityEditor.*` use in runtime code is guarded.

---

## Docs updated in this pass

- `ECOSYSTEM.md`:
  - Fixed about 30 stale statements (method names, StarPool partials, Flow B/C, bomb blast, release cue, placement modes, telemetry upload, passage form).
  - Added sections: Star Maze Navigation & Vines, Drum Transport, Audio Mix, Modal Menus, Path Choice & Interstitial Maze, Multiplayer & Competitive Modes, Tutorials, and an Other Systems index.
- `LEGIBILITY_AUDIT.md`: B1 Wait-pointer text, C3 disc, E1 superseded refs, flower gating, passage form.
- `COSMIC_PLASMA_STATUS.md`: everything is committed now, the feature is disabled, and the removal steps would break Cosmic Blocks.
- `Phase/Star/PhaseStar_ArchitectureNote.md`: rewritten for the current partials, maze navigation and vines.

## Verification still needed (Unity)

1. Open in Unity: `Assembly-CSharp` already builds with msbuild. `SharedMaterials.cs` is new and gets its `.meta` on import.
2. Profiler (GC Alloc, GC.Collect markers, Timeline). Compare against the previous build on:
   - a ring completion
   - a bass drop
   - boost-free plowing
   - a regrow wave
   - a record departure
3. Playtest:
   - the record-departure swirl (markers should still swirl in and leave with the record);
   - quitting mid-bridge, mid-path-choice and from TrackFinished (no exceptions, no stuck black overlay);
   - tentacle and tether look unchanged with the shared material;
   - the scouting preview still reads.
