# Config Audit — Designer Legibility (2026-11-17)

**Goal:** a designer can answer "where do I tune X?" without reading code. This doc is the map. It also records where the same concept is split across several assets, and ranks the merges that would fix that.

**Status:** Tiers 1–3 were implemented 2026-11-17/18 and compile; the prefabs were checked in Unity, but nothing has been playtested yet. Remaining items are the smaller T3 notes in §3.

Conventions: asset paths are relative to `Assets/`. "Inspector header" is the bold `[Header]` group you'll see when you select the asset. All config types are created from **Create → Astral Planes → …**.

---

## 1. "Where do I tune…?"

### Ship feel & fuel
| Knob family | Where | Inspector header |
|---|---|---|
| Speed, accel, boost, drag, coasting, mass | `Resources/Profiles/Vehicles/<Ship>.asset` (ShipMusicalProfile, **per ship**) | Movement / Coast-Stop / Physics |
| Impact damage curve | same | Impact |
| Fuel tank, burn rate, boost spool-up | same | Fuel Tradeoffs |
| Plow footprint & carving feel | same | Plow — Footprint / Carving Feel |
| Soul sprite fade (timing pulse copy of the ship art) | same | Soul Sprite (`soulAlphaMin/Max`) |
| Angular-velocity safety cap, input timeout | `Resources/Profiles/Vehicles/DefaultVehicleConfig.asset` (VehicleConfig, **all ships**) | Physics Safety / Input |
| Low-fuel & void-refuel colors, Hz, threshold (ship glow **and** fuel bar) | DefaultVehicleConfig | Fuel Status Tells |
| Fuel **bar UI** (swell, squeeze, shake, tints, status pulses) | DefaultVehicleConfig | Fuel Bar (HUD) ×3 *(T2-4)* |
| Recovery (out of bounds / stuck in void) | DefaultVehicleConfig | Recovery |
| Turbulence through a dormant star | DefaultVehicleConfig | Turbulence |

### Note carry & manual release
| Knob family | Where | Inspector header |
|---|---|---|
| Queue size, arm window, grace, auto-commit | DefaultVehicleConfig | Manual Note Release |
| Ring-break impact speed, ghost alpha while phasing | DefaultVehicleConfig | Manual Note Release |
| **Release cue: approach ring + open-window disc** (size, opacity, pulse, thickness, draw order) | DefaultVehicleConfig | **Release Cue** *(new — was per-prefab)* |
| How early the approach ring appears | DefaultVehicleConfig | Manual Note Release (`releaseApproachLookaheadSeconds`) |
| Tether start point on hull | DefaultVehicleConfig | Note Carry |
| Tether look (sag, noise, shimmer, widths, drip) | `Prefabs/Effects/Collection/Tether Effect.prefab` → NoteTether (single prefab, tooltipped; *T3-3*) | — |

### Dust & maze
| Knob family | Where |
|---|---|
| Spawn, regrow delays, zap, tint diffusion, None-dust resistance | `Resources/Config/CosmicDustGeneratorConfig.asset` |
| Gravity void: size, ring width, spread speed, dust grow-in, ripple | CosmicDustGeneratorConfig → Gravity Void *(T3-2)*; the ship side (probe radius, refuel, eject) is on DefaultVehicleConfig |
| Dust grow/clear/fade animation timing | CosmicDustGeneratorConfig → Cell Animation Timing *(T2-1)* |
| Per-role dust color, resistance, energy, regrowth, bomb | `Resources/Profiles/Musical Roles/<Role>.asset` (MusicalRoleProfile) |
| Maze layout per pattern + star navigation per maze | `Resources/Mazes/<Pattern>.asset` (MazePatternConfig) |
| Wisps | `Resources/WispConfig.asset` |

### Music
| Knob family | Where |
|---|---|
| A level: BPM, key, drums, maze, chords, riffs, trap | `Resources/Profiles/Motifs/**/<Motif>.asset` (MotifProfile) |
| Per-role riff, discovery-node expiry, ascend loops for a motif | RoleMotifNoteSetConfig assets referenced by the motif |
| Drum grid sizing, carve→intensity, bass drop, clip swapping | `Resources/Config/DrumTrackConfig.asset` |
| Mix bus per role, drum level | `Resources/Config/AudioMixConfig.asset` (layered with `MotifProfile.drumVolumeMultiplier` and `MusicalRoleProfile.volumeMultiplier`) |
| Ascension default, loop multiplier | `Resources/Config/InstrumentTrackConfig.asset` |
| Pickup ticks, cohort window | `Resources/Config/InstrumentTrackControllerConfig.asset` |

### Phase star, discovery nodes, collectables
| Knob family | Where |
|---|---|
| Star drift/hungry speed, keep-clear, spawn bias | `Resources/Profiles/Star Behavior/*.asset` (PhaseStarBehaviorProfile) |
| Star speed, keep-clear, ejection prefab (**per star type**) | PhaseStarBehaviorProfile (above) |
| Star charge display, look, motion, targeting, tentacles/vines (**all stars**) | `Resources/Config/PhaseStarConfig.asset`, sections 1–5 *(T3-1)* |
| Discovery node motion per role | `Resources/Config/Discoveries/<Role> Discovery Profile.asset` |
| Discovery node expiry blast, dust drag, spin/wobble | `Resources/Config/DiscoveryTrackNodeConfig.asset` (Expiry / In Dust / Character Animation) *(T2-2)* |
| SuperNode motion, lifetime, look (29 knobs) | `Resources/Config/SuperNodeTrackNodeConfig.asset` *(T2-3)* |
| Collectable motion/deposit/trail/bomb (shared) | `Resources/Config/CollectableTuning.asset` |
| Collectable motion per role | MusicalRoleProfile + `Resources/Profiles/Movement Archetypes/*.asset` |

### Rings, records, path exits, platform
| Knob family | Where |
|---|---|
| Ring glyph layout/alpha/draw-in/shake (4 contexts) | `Resources/Config/{RingGlyph Canvas, RingGlyph Canvas Not Selected, Final Rings, In Game Ring Glyph}.asset` |
| Carousel tilt/spin/spacing | `PhaseLibraryCarousel` component ⚠ |
| Path exits (rainbow road), phase-in delay, bridge fallback hold | `Resources/Config/PathExitConfig.asset` (7. Timing) |
| Competitive points per note, discovery multiplier | GameFlowManager in `Main.unity` → Competitive Mode |
| Phase/chapter order | `Resources/Phase Library.asset` |
| Telemetry | `Resources/Config/TelemetryConfig.asset` (the only copy now) |
| Steam stats exclusion list | `Settings/Platform/SteamStatsExclusionList.asset` (moved from `Audio/`) |

### In-world visual tells: where each one's look is set
| Tell | Opacity / color / size set in | Kind |
|---|---|---|
| Release approach ring (outline closing on hull) | DefaultVehicleConfig → Release Cue | ✅ config |
| Release open-window disc (filled pulsing disc on note anchor) | DefaultVehicleConfig → Release Cue (`releaseDiscAlphaMin/Max`) | ✅ config |
| Status glow (low fuel / void refuel) | DefaultVehicleConfig → Fuel Status Tells | ✅ config |
| Placement resonance tint on hull | DefaultVehicleConfig → Vehicle Resonance | ✅ config |
| Ring phase-through ghost | DefaultVehicleConfig (`ringPhaseGhostAlpha`) | ✅ config |
| Soul sprite pulse | ShipMusicalProfile (per ship); only FloTilla and Tumble assign a soul sprite | ✅ config |
| Bass-drop shockwave | DrumTrackConfig → Bass Drop | ✅ config |
| Energy Cue (circle riding the tether) | DefaultVehicleConfig → Release Cue (`energyCueAlpha`); color/sprite still on `Prefabs/Effects/Energy Cue.prefab` | ✅ config |
| Carried-note pulse on tether | CollectableTuning → Note Trail (`trailIdleAlpha`, `trailReadyAlpha`, `trailAlphaLerp`) | ✅ config |
| Tether line | `Tether Effect.prefab` → NoteTether | single prefab *(T3-3)* |

---

## 2. Config inventory (25 types, after T3-1)

| Type | Assets | Menu (Astral Planes/…) | Purpose |
|---|---|---|---|
| ShipMusicalProfile | 8 | Ship/Ship Musical Profile (per ship) | Per-ship movement, impact, fuel, plow, soul |
| VehicleConfig | 1 | Ship/Vehicle Config (all ships) | Shared ship rules: release, cue, recovery, void, tells |
| MotifProfile | 13 | Music/Motif/Motif Profile | One level |
| RiffAsset | 123 | Music/Motif/Riff Asset | One authored riff |
| RoleMotifNoteSetConfig | 124 | Music/Motif/Role Motif NoteSet Config | Role + riff + node expiry + ascend loops |
| ChordProgressionProfile | 29 | Music/Motif/Chord Progression Profile | Chord sequence |
| MusicalRoleProfile | 36 (loaded via `LoadAll`) | Music/Roles/Musical Role Profile | Per-role dust, collectable, MIDI, mix, regrow, bomb |
| MusicalRoleMovementProfile | 4 | Music/Roles/Musical Role Movement Profile | Collectable orbit/dart/launch |
| AudioMixConfig | 1 (Resources load) | Music/Tracks & Mix/Audio Mix Config | Mixer bus per role, drum level |
| DrumTrackConfig | 1 | Music/Tracks & Mix/Drum Track Config | Grid, intensity, bass drop, clip polling |
| InstrumentTrackConfig | 1 | Music/Tracks & Mix/Instrument Track Config | Ascension, root shift, loop multiplier |
| InstrumentTrackControllerConfig | 1 | Music/Tracks & Mix/Instrument Track Controller Config | Pickup SFX, cohort |
| CosmicDustGeneratorConfig | 1 | Dust & Maze/Cosmic Dust Generator Config | Dust lifecycle + cell animation timing |
| MazePatternConfig | 43 | Dust & Maze/Maze Pattern Config | Maze generation + star nav |
| WispConfig | 1 | Dust & Maze/Wisp Config | Wisps |
| PathExitConfig | 1 | Path Exit/Path Exit Config | Exit portals |
| PhaseLibrary | 1 | Phase & Star/Phase Chapter Library | Phase order |
| PhaseStarBehaviorProfile | 3 | Phase & Star/PhaseStar Behavior Profile | Per-star-type speed, keep-clear, ejection |
| PhaseStarConfig | 1 | Phase & Star/PhaseStar Config (all stars) | Shared star charge, look, motion, targeting, tentacles |
| DiscoveryTrackNodeLocomotionProfile | 4 | Discovery Nodes/Locomotion Profile (per role) | Node motion |
| DiscoveryTrackNodeConfig | 1 | Discovery Nodes/Discovery Track Node Config | Expiry blast, rhythm sprite, in-dust drag, character animation |
| SuperNodeTrackNodeConfig | 1 | Discovery Nodes/SuperNode Config | SuperNode motion, lifetime, look |
| CollectableTuning | 1 | Collectables/Collectable Tuning | Shared collectable behavior |
| RingGlyphConfig | 4 | Rings & Records/Ring Glyph Config | Ring glyph look per context |
| TelemetryConfig | 1 | Platform/Telemetry Config | Telemetry endpoint |
| SteamStatsExclusionList | 1 | Platform/Steam Stats Exclusion List | Dev accounts excluded from stats |

`NarratedTutorialScript` also derives ScriptableObject, but it is built from JSON at runtime. It is not a designer config.

---

## 3. One concept, many places

| Concept | Where it is today | Effective rule | Proposed single home |
|---|---|---|---|
| Drum carve weight | DrumTrackConfig `bass/otherCarveWeight` → copied to static `DrumIntensityTuning` → per-role override on MusicalRoleProfile | role override wins if enabled | keep, tooltips now say so |
| Dust animation timing | CosmicDustGeneratorConfig (Spawn, Zap Clear, Cell Animation Timing) | — | ✅ done (T2-1) |
| Dust resistance/energy | MusicalRoleProfile per role; CosmicDustGeneratorConfig `none*` for filler | separate by design | keep; document |
| Collectable motion | CollectableTuning + MusicalRoleProfile + MusicalRoleMovementProfile | layered | T3: move role fields onto the movement profile |
| Collectable bomb | CollectableTuning `blastRadius` × MusicalRoleProfile `bombBlastRadiusMul` | multiply | keep; document |
| Discovery-node expiry | RoleMotifNoteSetConfig `mineNodeExpireAfterLoops` → else LocomotionProfile `expireAfterLoops`; blast radius on DiscoveryTrackNodeConfig | NoteSet wins when > 0 *(tooltips fixed)* | T2-2 |
| Discovery-node tuning | DiscoveryTrackNodeConfig (shared) + LocomotionProfile (per role) | — | ✅ done (T2-2) |
| Ascend loops | RoleMotifNoteSetConfig `ascendLoops` → else InstrumentTrackConfig `defaultAscendLoops`; NoteAscensionDirector `ascendLoops` is a separate marker default | *(tooltip was wrong, now fixed)* | T3: remove the director field |
| Gravity void | CosmicDustGeneratorConfig → Gravity Void (world side) + VehicleConfig (probe, refuel, eject) | split by design, tooltips cross-reference | ✅ done (T3-2) |
| Pickup SFX | InstrumentTrackControllerConfig | odd home | T3: move to AudioMixConfig |
| Ship rules | ShipMusicalProfile (per ship) vs VehicleConfig (shared) | intended split | keep; the rule is "per-ship = feel, shared = rules & tells" |
| Keep-clear radius | PhaseStarBehaviorProfile + MazePatternConfig offset + ShipMusicalProfile (ship's own) | star = profile + maze offset | keep; tooltips now cross-reference |
| Volume | AudioMixConfig × MotifProfile × MusicalRoleProfile | multiply | keep; one table in AudioMixConfig's class doc |
| Phase star | PhaseStarBehaviorProfile (per type) + PhaseStarConfig (shared) + MazePatternConfig StarNavigationParams (per maze) | layered | ✅ done (T3-1) |
| Path-exit timing | PathExitConfig → 7. Timing (GameFlowManager getters forward to it) | — | ✅ done (T2-5) |

---

## 4. Tuning stuck on components (not findable as assets)

| Component | ~Knobs | Covers | Proposed home |
|---|---|---|---|
| CosmicDust (16 partial files) | 35–50 | flower, compression, emission, jiggle | CosmicDustGeneratorConfig "Cell Visuals" |
| PhaseLibraryCarousel | 26 | tilt, spin, spacing | RingGlyphConfig or a new CarouselConfig |
| NoteTether | 18 | sag, noise, shimmer, widths, drip | leave: one prefab, now tooltipped (T3-3) |
| MotifOrbitLineRenderer | 24 | ripple, wobble, pulse | leave: carousel-only, one prefab, already tooltipped |
| ControlTutorialHighlight | 23 | label/button anims | leave (tutorial-only) |
| NoteVisualizer | 7 | playhead, release pulse, trail | leave: one scene object, now tooltipped (T3-3) |

SuperNodeTrackNode and Fuel moved to configs in Tier 2; the carried-note pulse and Energy Cue alphas are now config knobs.

---

## 5. Ranked proposals

### Tier 1: done 2026-11-17 (safe, no gameplay values changed except the cue disc)
- **T1-1** Release cue settings moved from 8 ship prefabs (`VehicleReleaseCue` fields) to `VehicleConfig → Release Cue`, read live every frame. Disc alpha lowered from 0.12–0.30 to **0.05–0.15**; ring alpha keeps the prefab-tuned 0.10–0.75; ring thickness is now a knob. Stale serialized data stripped from the Fleet prefabs.
- **T1-2** Create menu regrouped by design area (Ship / Music / Dust & Maze / Phase & Star / Discovery Nodes / Collectables / Rings & Records / Path Exit / Game Mode / Platform). Only the attribute changed.
- **T1-3** Tooltips added to ~45 undocumented fields in VehicleConfig, ShipMusicalProfile, MusicalRoleProfile, MotifProfile, DrumTrackConfig and InstrumentTrackControllerConfig. Overlap tooltips now give the order of precedence. Two tooltips were **wrong** and are fixed: ascend-loop fallback, node-expiry precedence.
- **T1-4** Deleted `Settings/TelemetryConfig.asset`. It was an unreferenced duplicate with `bypassConsentForTesting` and insecure HTTP on, so someone could easily have edited the wrong one.
- **T1-5** Moved SteamStatsExclusionList out of `Assets/Audio/` to `Assets/Settings/Platform/`. The GUID was kept, so `Main.unity` still references it.

### Tier 2: done 2026-11-17 (values copied from scene/prefab/asset YAML, so behavior is unchanged except where noted)
- **T2-1** DustVisualTimingSettings folded into CosmicDustGeneratorConfig → "Cell Animation Timing" (`dustTimings`). Old type, asset and the GeneratedTrack scene field removed; the stale `zapRegrowDelaySeconds` key dropped from the asset.
- **T2-2** DustInteractorConfig and CharacterVisConfig merged into DiscoveryTrackNodeConfig (headers "In Dust" and "Character Animation"). `Discovery.prefab` re-pointed; the old types and assets deleted.
- **T2-3** New `SuperNodeTrackNodeConfig` asset (`Resources/Config/`) holds SuperNodeTrackNode's 29 knobs, now with tooltips. `Idea.prefab` references it; a missing assignment logs an error and uses defaults.
- **T2-4** Fuel bar knobs moved to VehicleConfig → "Fuel Bar (HUD)" (3 headers). The bar now shares the ship glow's low-fuel threshold, color and Hz, and the void-refuel color. **Visible change:** the bar's refuel tint goes from (0.85, 1, 1) to the glow's (0.75, 0.95, 1). PlayerStats hands the vehicle's config to Fuel; before that, Fuel loads `Resources/Profiles/Vehicles/DefaultVehicleConfig`.
- **T2-5** `vehiclePhaseInDelaySeconds` (1.2) and `motifBridgeHoldSeconds` (2, the scene value) moved to PathExitConfig → 7. Timing.
- **T2-6** Carried-note pulse alpha is on CollectableTuning → Note Trail. `energyCueAlpha` on VehicleConfig → Release Cue drives the Energy Cue sprite (default 1 = previous look).
- **Also:** CompetitiveScoringConfig deleted; `pointsPerNote` / `discoveryMultiplier` are fields on GameFlowManager → Competitive Mode (Main.unity: 1, 2). 61 unreferenced RoleMotifNoteSetConfigs and 81 RiffAssets deleted (21 unused outright, 60 used only by those note sets).

### Tier 3: structural
- **T3-1 ✅ done 2026-11-17.** New `Resources/Config/PhaseStarConfig.asset` holds the 70 shared settings from PhaseStar, PhaseStarVisuals2D, PhaseStarMotion2D, PhaseStarCravingNavigator and PhaseStarDustAffect, in sections 1. Charge & Shards, 2. Look, 3. Motion, 4. Targeting and 5. Tentacles. Values were copied from Star.prefab. Missing tooltips were added.
  - Components keep only references (sprites, particles, tentacle material, subcomponents) and debug toggles. Each component reads the config through properties; if it is unassigned, the Resources asset is loaded.
  - The navigator's replan interval no longer overwrites its own serialized value on Initialize (it compounded if the motif had no behavior profile).
  - The 3 behavior profiles were left alone: they are small and hold what differs per star type (speeds, keep-clear radius).
- **T3-2 ✅ done 2026-11-17.** `gravityVoidScale`, `voidRingWidthCells` and `gravityVoidImprintTickSeconds` moved from InstrumentTrackControllerConfig to CosmicDustGeneratorConfig → "Gravity Void" (values 1 / 1 / 0.05), next to the void dust grow-in and ripple. InstrumentTrackController reads them through `GameFlowManager.dustGenerator.config`. Vehicle-side void settings stay on VehicleConfig; tooltips point both ways.
- **T3-3 ✅ done 2026-11-18 (revised).** A shared NoteVisualsConfig was dropped: each of the three components lives on exactly one prefab/scene object and they belong to different systems (gameplay tether, HUD playhead, menu carousel), so a config would only add a hop. Instead: NoteTether's code defaults now match `Tether Effect.prefab` (they were misleading, e.g. width 0.05 vs 0.25), every knob has a tooltip, and the unused `litColor` / `fadeAfterSeconds` are gone. NoteVisualizer lost the unused `failureColor` and stopped serializing the runtime `_playheadEnergy01`; its remaining knobs have tooltips. MotifOrbitLineRenderer was already tooltipped and is left as is.
- **T3-4 ✅ done 2026-11-18.** Everything in `Resources/Config/Config/` moved up to `Resources/Config/` with `git mv` (GUIDs kept, so every scene/prefab reference still resolves). No `Resources.Load` read from the nested folder, so no load path changed. The empty `Uno`/`Dos`/`Tres` folders were deleted; the Tool Suite's fallback folder now points at `Assets/Resources/Config`.

### Open decisions
- None outstanding. Unreferenced mazes and chord progressions are kept on purpose as a library (§6).

---

## 6. Unreferenced content

| Type | Unreferenced | Status |
|---|---|---|
| MazePatternConfig | 30 of 43 in `Resources/Mazes/` | **kept** (designer library, selectable in a MotifProfile) |
| ChordProgressionProfile | 12 of 29 in `Resources/Profiles/Chord Progressions/` | **kept** |
| RoleMotifNoteSetConfig | 61 | **deleted** in Tier 2 |
| RiffAsset | 81 (21 unused outright, 60 used only by the deleted note sets) | **deleted** in Tier 2 |

Note: everything under `Resources/` ships in the build whether it is referenced or not.
