# Variable / Value Audit (2026-11-24)

**Scope.** This audit looks for variables and config values left obsolete, without effect, redundant or inefficient after the work since `cc45ecc4`: record vault, control sandbox and dock, cosmic blocks, flower buds, and the dust core/haze split. It is a companion to CODE_HEALTH_AUDIT.md and CONFIG_AUDIT.md.

**Status markers.** **FIXED** = applied, **OPEN** = not done, **KEEP** = deliberately left.

**Method.** Two passes:
- Mechanical scans over `Assets/Scripts` and `Assets/Editor`, grouped by partial class: unread serialized fields, write-only fields, dead public properties, and stale YAML keys in assets, prefabs and scenes.
- Two semantic reviews: one of the config ScriptableObjects, one of the recently changed components.

**Build state.** Both assemblies compile with msbuild. Nothing has been playtested.

---

## Tier 0 — Bugs found during the audit
| # | Item | Status |
|---|---|---|
| B1 | **Telemetry URL doubled.** `TelemetryConfig.asset` `baseUrl` already ended in `/api/publish.php`, and `PublishUrl`/`RemoveUrl` append the path again. The asset is now `https://astralplanes.claytical.com`. `sendInEditor: 1` is still on, so Editor sessions upload; confirm that's intended. | **FIXED** (sendInEditor: OPEN, decide) |
| B2 | **`NoteAscensionDirector.ascendLoops` (scene 8) overrode each marker's own loop count.** It affected `ResetPhrasing()` on every chord change and the fade ratio. `MarkerState.totalLoops` now holds each marker's count, and the field is only a fallback. | **FIXED** |
| B3 | **`TutorialPromptView.Bind` re-bind.** The `overrideSorting=true` / order 20000 override stayed on when the panel was already in the overlay, which sorted it above menus. It is now cleared whenever the panel's root canvas is a ScreenSpaceOverlay. | **FIXED** |
| B4 | **SuperNode ascend loops.** `PhaseStar.EjectionLogic` hard-coded `3`. It now reads `SuperNodeTrackNodeConfig.Default.ascendLoopsOverride`; `Default` is a new Resources loader. | **FIXED** |
| B5 | **`GlowPulseText` threw an NRE in `Start` in Main and TrackSelection.** The `fontMaterial` grab was commented out, so `_textMaterial` was always null. The throwing Start is removed; the component is still inert. **Decide:** restore the glow, or remove the component from both scenes. | **FIXED** (NRE) / **OPEN** (decide) |

## Tier 1 — Dead variables
**FIXED (deleted):**

Config and profile fields:
- `HarmonyDirector.burnIfOutsideWindow`, `.seedFromProfileOnPhaseStart`
- `InstrumentTrackControllerConfig.cohortWindowFraction`
- `PhaseStarConfig.tentacleBloomMinScale`, `dormantSeedScale`, `dualMaxSeparationDeg`, `shardMinAlpha`, plus PhaseStar's property for `shardMinAlpha`
- `MotifProfile.selectionWeight`, `ChordProgressionProfile.progressionName`, `DrumTrack.SessionPhases`
- `Riff.overlapPolicy`, the `RiffOverlapPolicy` enum, and the importer's write of it

Component fields:
- `VehicleStatDisplay` speed/agility/power/fuel labels (pre-radar), `PlayerSelect.tutorialControls`, `IntroInputHandler.gameModeSelect`
- `InstrumentTrack.trackShadowColor`, `TrackShadowColor` and `MusicalRoleProfile.GetShadowColor()`
- `InstrumentTrack.channel`

Write-only fields:
- `HarmonyDirector._globalBuiltCount`
- `PhaseStarCravingNavigator._star`
- `SessionStateCoordinator._gameFlow`, along with its constructor parameter
- `GlowPulseText._tmpText`

Other:
- `CameraZoomDirector.kAboveDimOrder`
- `VaultLayout.Radius`
- `CosmicDust._baseColor`. It is now inlined, and the stale rest-colour comment is removed.

Dead public properties:
- `QuitPrompt.IsShowing`, `LocalPlayer.DisplayName`, `MazePathExit.PortalWidth`, `PhaseStarCravingNavigator.NavMode`
- `TutorialPromptView.PanelRect`, `SessionStateCoordinator.HasGameOverStarted`, `MotifSnapshot.FillDurationSeconds`
- `Vehicle.IsRingPhasing`, `ControlTutorialHighlight.TutorialSequence` / `IsSandboxMode`

Signatures:
- `GameFlowManager.IntroduceRecord()`: dropped the never-passed `fadeDuration` and `alreadyShown`.
- `PhaseStar.ComputeDiamondRotation(dt)`: now returns rotA only. Diamond 1 still feeds the drain-sway timer.
- `MotifRingGlyphApplicator.GameplayLineWidth()`: dropped the unused parameter and computes the width once per ring.

**KEEP:**
- JSON DTOs: `FlightPlanResponseModels.*`, `TelemetryModels.*`.
- `StillFrameRadius`: used by `Editor/RingAlbumArtRenderer`.
- The `PhaseStarCravingNavigator.Initialize(star, …)` parameter is now unused. It stays so call sites don't change.

## Tier 2 — Stale serialized keys (no field behind them)
**FIXED (hand-stripped):**
- `DrumTrackConfig.asset`: pressureDustWeight, pressureFuelWeight
- `InstrumentTrackConfig.asset`: spawnPickMaxTries, spawnColumnBandFraction
- `InstrumentTrackControllerConfig.asset`: noteCommitMode, commitStinger* (8 keys), cohortWindowFraction
- `Final Rings.asset`: 9 removed ring-animation keys
- `In Game Ring Glyph.asset`: dockDriftWanderShare
- `PhaseStarConfig.asset`: the 4 fields deleted above

**OPEN — run Unity → right-click → *Force Reserialize Assets*, one file at a time.** Don't hand-edit scene YAML.
- `Main.unity`: GameFlowManager recordVaultNeutralTint and recordVaultExplodeTint (old gate vault); HarmonyDirector's 2 deleted bools
- `PhaseLibrary.unity`: MotifRingGlyphApplicator.sphericalRotation, PhaseLibraryCarousel.nextSceneName
- `Main.unity` and `TrackSelection.unity`: GlowPulseText (only if the component is removed)
- `Local Player.prefab`: 8 old mouse, stick and mini-tutorial keys
- `Note.prefab` carrySettings, `Cosmic Potential.prefab` regrowAlphaCapped, `NoteSet Factory.prefab` configLibrary
- `Player Select Ship.prefab`: the 4 VehicleStatDisplay label refs. The PlayerSelect and IntroInputHandler refs are in their scenes.
- Asset families:
  - 22 chord progressions: beatsPerChord, progressionName
  - 12–13 motifs: genericFill*, trapGrowSeconds, selectionWeight
  - 13–16 role profiles: drumIntensityCeiling01, outputMixerGroup
  - 8 ship profiles: regrowDelayMultipliers
  - RoleMotifNoteSetConfig: 5 old-schema assets plus 11 with mineNodeTintBudget
  - About 123 riff-bearing assets: overlapPolicy

## Tier 3 — Knobs that have no effect as shipped
| Knob | Why | Status |
|---|---|---|
| `VehicleConfig.spawnRestPocketDelaySeconds` | 0 in the asset; it was an ordering workaround, and the code now always waits one frame | **FIXED** (deleted) |
| `MazePatternConfig.porousBorder.topBottomCenterBand` | Only set at runtime from `PathExitConfig.topBottomExitCenterBand` | **FIXED** (`[HideInInspector]`) |
| `CameraZoomDirector` smoothTime, dimColor, dimStrength, dimFullAt | The component is only added at runtime, so Inspector values never applied | **FIXED** (consts) |
| `DrumTrackConfig.intensityPollIntervalSeconds`, `binFillPollDebounceSeconds` | The asset uses `clipPollMode` LoopBoundary; these are live only in the Continuous and BinFill modes | **KEEP** (documented mode options) |
| `CosmicDustGeneratorConfig` min/maxBpmForDensity, note/bpmDensityWeight | `bpmDensityWeight` is 0, so only the note-density term counts. The tooltip documents "0 = current behavior". | **KEEP** (dormant design option) |
| `PhaseStarConfig.replanIntervalSeconds` | All 13 motifs set a behavior profile, so this is fallback only. The tooltip already says so. | **KEEP** |
| `VehicleConfig.spawnRestPocketRadiusCells` | Used only when Auto Radius is off. The tooltip already says so. | **KEEP** |
| `CollectableTuning.maxDepositTravelSeconds` | Only the upper clamp on depositTravelSeconds | **KEEP** (valid range bound) |
| `PathExitConfig.leftRightExitInset` | 0 in the asset (code default 3.4), so its branch never runs | **OPEN**: restore or delete? |
| `InstrumentTrackControllerConfig.pickupTickBass/Lead/Harmony/Groove` | All unassigned; only `pickupTickDefault` plays | **OPEN**: fold pickup SFX into AudioMixConfig (CONFIG_AUDIT open item) |
| `PathExitConfig` visited `palette` under monochrome | Only changes the stripe count | **OPEN**: tooltip note |

## Tier 4 — Inefficiencies
| Item | Status |
|---|---|
| `Camera.main.GetComponent<CameraZoomDirector>()` ran 6 times in GameFlowManager.RecordRecovery, including per-frame polls. It now uses the new `CameraZoomDirector.Current`, set in Awake and cleared in OnDestroy. | **FIXED** |
| `CameraZoomDirector.s_overlays` kept destroyed canvases. It is now pruned before a new overlay is added. | **FIXED** |
| `RepairVaultCells` allocated a list every 0.25 s; it now reuses a scratch list. `ApplyVaultLook` already skipped cells that looked correct. | **FIXED** |
| Bud highlight allocated 2 key arrays per bud per frame. It now uses static scratch arrays, since `SetKeys` copies them. | **FIXED** |
| `GameplayFrame.TryHome` still uses `cam.GetComponent<CameraZoomDirector>()`. Its callers aren't per-frame. | **OPEN** (low) |

## Tier 5 — Consolidation
| Item | Status |
|---|---|
| Drum volume fallback 0.79 appeared 3 times. It is now `AudioMixConfig.DefaultDrumVolume`. | **FIXED** |
| Star sort-order fallback 2000 appeared 3 times. It is now `PhaseStar.DefaultBaseSortingOrder`. | **FIXED** |
| The dock radius formula and the bump-state reset were duplicated. They are now `DockRadiusWorld()` and `ResetDockFeedback()`. | **FIXED** |
| `SetBlockForm(on, sizeMul = 4f)` duplicated the block size. The default is now 1: only the `off` calls rely on it, and every `on` call passes its config value. | **FIXED** |
| Stale "gates" comments in RecordRecovery and Blocks | **FIXED** |
| SuperNode face/wobble values are identical to DiscoveryTrackNodeConfig (420 / 12 / 2.5–4.5) | **KEEP**: separate tuning surfaces, may diverge |

---

## Playtest checklist
- Change chord mid-ascension: markers keep their own loop count and fade evenly.
- Tutorial prompt: re-bind it (second narrated sequence), then open the pause menu. The menu draws above the prompt.
- Complete a SuperNode: the ascension uses the config's loops (3).
- Record vault: it docks, repairs, bursts and is collected as before. The camera spotlight releases correctly.
- Star buds: the highlight pulse looks unchanged.
- Telemetry (if enabled): the request goes to a single `/api/publish.php`.
