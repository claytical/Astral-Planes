# Astral Planes — Player-Legibility Audit (2026-11-17)

This audit asks one question per system: **which rules change what happens in play but never reach the player?** Each finding is tagged with a proposal type:

- **Cut**: remove the rule.
- **Merge**: fold it into a rule that's already visible.
- **Tell**: keep the rule and add a visual or audio signal.
- **Fix**: a bug or inconsistency found along the way.

Cost: S ≈ under a day · M ≈ 1–3 days · L ≈ larger.

**Status:**
- **A1 and A2 implemented** (2026-11-17), with the flower kept as decoration. The rule lives in `Dust/CosmicDust.Legibility.cs`. It compiles via msbuild but has not been playtested in Unity yet.
- **D1 implemented** (2026-11-17), compiles; not playtested.
  - Intensity is now carve energy only (`DrumTrack.BeatIntensity.cs` `ComputeIntensity01`).
  - Deleted with it: the driving-role ceiling, the Bass-fill lift, `StarPool.GetDrivingRole`, `InstrumentTrackController.GetRoleBinFill01`, `DrumTrackConfig.otherRoleCeiling01` and `MusicalRoleProfile.drumIntensityCeiling01`.
  - The per-role carve weights were kept.
  - New bass-drop tell: when the drop clip becomes audible, a Bass-colored ring flash sweeps out through the dust from the center. It lives in `CosmicDustGenerator.Shockwave.cs` and `CosmicDust.BeginColorFlash`, and its knobs are in `DrumTrackConfig` under "Bass Drop".
- **B1 revised after playtest:** the Wait pointer was removed. It looked like the star zapping another color's note, because the global pickup lock makes the nearest blocker any role's note. Every rejected poke now gets the head shake, and sleep explains the "wait" case.
- **B1 and B2 implemented** (2026-11-17), compiles; not playtested. The code is in `Phase/Star/PhaseStar.PokeFeedback.cs`.
  - **B1:** every rejected poke now gives one of two answers:
    - **NotReady:** the diamonds shake their head (zaps not met).
    - **Wait:** a pointer line runs from the star to the blocker: its own or another live node, else the nearest floating note, else a ship carrying notes. If no blocker is found it falls back to the shake.
  - The orange `FlashReject` was deleted, along with its stuck-dim bug.
  - The "budget spent" answer was dropped as unreachable.
  - **B2:** while an armed star is blocked by work elsewhere, it **sleeps**: desaturated diamonds, 25% rotation speed, no ready-spin, dimmed particles. "Blocked" means pickup lock, a pending expansion, or another sequence in flight. This replaces the pickup-lock-only dim.
  - Tutorial line ("one star at a time") not added yet.
- **B3 implemented** (2026-11-17), compiles; not playtested. The code is in `Gameplay/Mining/TrackNodeLifetimePips.cs`, attached in `DiscoveryTrackNode.Initialize`.
  - **The halo** (redesigned the same day, after feedback that pips didn't fit the look): one concentric translucent role-colored disc **behind** the node per loop it has left.
  - **Opacity:** the innermost disc is the most opaque. It gets fainter with fewer loops left, from 0.5 down to 0.18 alpha. The outermost disc is nearly translucent (0.06).
  - **At each loop boundary:** the outer disc swells and fades.
  - **On a hit:** lost discs re-emanate from the center with a white flash.
  - **Last loop:** the remaining disc breathes.
  - **Size:** constant world size while the node shrinks with damage.
  - **Data source:** it reads the new `TrackNode.LoopsRemaining` / `ExpireAfterLoops`.
  - **Scope:** not attached to SuperNode chasers yet; that's possible via the same `Attach`.
- **C1 implemented** (2026-11-17), compiles; not playtested.
  - A ship whose manual-release queue is full (`Vehicle.IsManualReleaseQueueFull`, capacity 9) now **refuses** the pickup. The note gets a bounce impulse (`CollectableTuning.noteTrail.queueFullBounceImpulse`, default 2.5) plus the dust-hit recovery window (`Collectable.BounceOffFullVehicle`).
  - The drop-oldest-into-a-bomb loop in `EnqueuePendingCollectedNote` stays only as a defensive fallback.
- **D2 implemented** (2026-11-17), compiles; not playtested.
  - **Low fuel (<20%):** the fuel bar pulses toward a warm tint and swells slightly. The pulse speeds up as fuel nears empty (`Fuel.cs`, new Low Fuel Warning section). The ship gets a matching warm glow behind its hull.
  - **Gravity-void refuel:** the ship shows a steady cool glow with a shimmer, and the fuel bar shimmers too. The refuel used to be a silent trickle. The flag is set in `Vehicle.UpdateVoidEnergyRecovery` → `PlayerStats.SetFuelRefueling`.
  - **Glow implementation:** the ship glow is a runtime child sprite (`Vehicle.StatusGlow.cs`), so it never fights the hull's color writes.
  - **Knobs:** in `VehicleConfig`, "Fuel Status Tells".
  - **Not done:** the void auto-eject still has no tell.
- **A5 implemented** (2026-11-17), compiles; not playtested. The user chose "role speed only", which inverts the original proposal. The authored data showed every role profile sets `regrowthDelay` (1–8s), and that it already beat the clear source for any cell with a role.
  - **Resolver now:** explicit override → role `regrowthDelay` → maze base (role-less cells only) → × density easing.
  - **Deleted:**
    - the 11-entry per-`DustClearSource` maze table. It only ever applied to role-less cells.
    - `ShipMusicalProfile.regrowDelayMultipliers`, which was 1.0 on all 8 ships.
    - `CosmicDustGeneratorConfig.zapRegrowDelaySeconds`, which had no reader.
  - **Only behavior change:** role-less filler hit by a blast now regrows at the maze base (2–4s) instead of 6–8s.
  - **Unchanged:** the safety vetoes and the step-gate queue.
- **A3 implemented** (2026-11-17), compiles; not playtested. The user chose **never-regrow**.
  - All filler is now permanent once cleared, shown as dark `genericFillTint`.
  - **Removed:**
    - `MotifProfile.genericFillNeverRegrowRatio`
    - `MotifProfile.genericFillRegrowSoftenMultiplier01` (only affected regrowing filler)
    - `DustImprint.genericFillNeverRegrows`
    - the regrowth controller's filler step-gate bypass
  - **Motifs affected:** only Switchyard, Phantom Strut and Rooftop Hop have filler. Their previously quick-regrow share (34%, 48% and 79% of their filler, respectively) now stays open once carved.
- **B4 implemented** (2026-11-17), compiles; not playtested. The code is in `Phase/Star/PhaseStar.SuperNodeTell.cs`.
  - **When it shows:** whenever the star's next ejection will be a SuperNode (its track is fully expanded and the motif has an alternate progression).
  - **The ring:** a slowly turning ring sits behind the diamonds, striped with every active role's color, since the SuperNode bonus round touches all tracks.
  - **Fade states:** it fades to 30% while the star sleeps, and hides whenever the star is hidden.
  - **Polling:** a non-logging copy of the `ShouldSpawnSuperNodeForTrack` rule, checked at 4 Hz.
- **C2 implemented** (2026-11-17), compiles; not playtested.
  - **The ghost:** a free-floating note fades to 30% alpha and shrinks to 80% while no ship can take it right now. That covers ships carry-locked to another track, a full manual queue (C1) and dead ships.
  - **Multiplayer:** the note stays solid as long as any ship could take it.
  - **Where:** `Vehicle.CanTakeNoteNow`; `Collectable.TickUnreachableGhost`, folded into the existing idle pulse (`PulseEnergySprite`), polled at 5 Hz.
  - **Exempt:** carried notes, bombs and arriving notes never ghost.
- **C3 implemented, after a correction** (2026-11-17), compiles; not playtested.
  - **The finding was wrong:** the open window *was* already shown, because the release tether only draws while the window is open.
  - **What it actually described:** the ship-side `VehicleReleaseCue` (circle + step bar). It has been inert since March, when its setters were removed. Every prefab's `releaseCue` was unset, and Sandwave and X-agong lack the component.
  - **The real gap:** nothing signalled that a window was *coming*.
  - **The fix:** `VehicleReleaseCue` was rewritten as an **approach ring**. A track-colored ring contracts toward the hull over the last `VehicleConfig.releaseApproachLookaheadSeconds` (1.25s) before the window opens, then vanishes as the tether takes over.
  - **Wiring:** driven by `Vehicle.TickReleaseApproachCue` from `TryGetReleaseWindowState` at 20 Hz. The component is added at runtime when missing, with a procedural sprite.
  - **Removed:** the dead `OnStepTickForReleaseCue` / `SetBeatsRemaining` step-tick path.
  - **Follow-up:** the ring is now sized from the ship's own sprite radius (×2.5 shrinking to ×1.15) instead of fixed world radii, which read far too large.
  - **Centering:** the ring and the D2 fuel-status glow are both centered on `Vehicle.NoteCoreAnchor`. The glow is also sized from the ship sprite: `VehicleConfig.statusGlowRadiusMul` (1.6) replaced `statusGlowRadiusWorld`.
- **C4 implemented** (2026-11-17), compiles; not playtested.
  - **The mismatch was cosmetic.** The effective default was Manual everywhere: `LocalPlayer` pushed its Manual default into `PlayerSelect` on creation, overwriting that file's Auto initializer.
  - **New single source:** `NotePlacementDefaults.Default` (`Gameplay/Core/NotePlacementMethod.cs`), used by `LocalPlayer`, `PlayerSelect` and `Vehicle`.
  - **Behavior change:** at the user's choice, new players now start on **Auto** ("Cozy Co-Pilot"). Manual is opt-in at the groove-style stage. A player's choice still persists across scene loads within a session (not saved to disk).
- **C5 implemented** (2026-11-17), compiles; not playtested. A ring bump now gives three distinct answers:
  - **Hard hit + pending note:** break (unchanged).
  - **Soft hit + pending note:** the tether a harder hit would break **twangs** like a plucked string (`NoteTether.Pluck`, `MotifRingGlyphApplicator.TryPluckNearestPendingTether`). No state change.
  - **Nothing pending:** stiff denial shake (unchanged, but now only means "nothing to break").
  - **Not covered:** the 0.5s post-commit immunity is still untold.
- **D3 resolved** (2026-11-17), compiles; not playtested. The finding was based on dead code.
  - **Dead code:** `GameFlowManager.SpawnVehicleTraps` (the `hideRole` + `roleColor` conflict) had no callers.
  - **The live path:** `SceneFlowCoordinator`'s maze-build trap callback (`InjectTrapCellsIntoStagger` / `FromList`). It injects trap cells into the maze's own staggered grow-in as `None` + `hiddenRole = trapRole`, so the trap arrives *with* the maze rather than popping in after.
  - **Look:** since A1, trap cells render as the trap role's darkened latent color, the same rule as every uncarved cell. No color conflict remains.
  - **Removed:** the dead `SpawnVehicleTraps`, the `hideRole` parameter/branches in `GrowVoidDustDiskFromGrid` and `SpawnDustAtCells`, and the now-unused `MotifProfile.trapGrowSeconds`. Trap tooltips were updated.
  - **No separate pre-warning added:** the maze grow-in already is the arrival. Say so if traps still feel like a surprise in play.
- **E1: superseded** by the role-wide Cosmic Block redesign (same day; see ECOSYSTEM.md "Cosmic Blocks"). The loosen tell became a shimmer on all blocked cells while any burst is live. Original entry:
  - **E1 implemented** (2026-11-17), compiles; not playtested.
  - **When it shows:** while a role has a live collectable burst. That role's next bin fill, and so its Cosmic Block shatter, lands when the burst completes.
  - **What it looks like:** the role's standing blocks **loosen**. Each flower trembles (visual-only Perlin jitter; the collider never moves) and flickers toward white. Intensity runs from 0.2 when the burst spawns to 1.0 as its last notes resolve.
  - **Where:** `InstrumentTrack.TryGetLiveBurstProgress01`; `CosmicBlockField.Update` polls at 5 Hz; `CosmicBlock.SetLoosen` / `TickLoosen`.
  - **Performance:** the flicker is sprite-only, so it never triggers the heavy particle-color rewrite.
  - **Not covered:** where the next block will rise is still untold.
- **E2 implemented** (2026-11-17), compiles; not playtested. The look was redesigned the same day at the user's request: the first bar + pips overlay didn't fit the aesthetic.
  - **The gate:** Up/Down phase exits held back only because not every motif in the phase has a ring now get a **shimmering dust gate**. The dust wall across that edge's centre band (3 cells deep, using the same `topBottomExitCenterBand` the real exits use) flashes random solid cells toward white.
  - **Intensity** is recorded / total, with a 0.12 floor. Waves speed up from every 0.35s to every 0.08s, flash up to 30% of gate cells, and brighten as more motifs are recorded.
  - **Code:** `Managers/PathExitLockedStub.cs`, plus `CosmicDustGenerator.TryFlashSolidCell` (the `CosmicDust.BeginColorFlash` lane). No overlay or collider.
  - **Excluded:** the permanent "no Down out of phase 1" exit gets no gate.
  - **Lifetime:** destroyed when a vehicle wins an exit or the interstitial ends.
  - **Known gap:** if the locked phase exits were the *only* exits, the interstitial is skipped, so no gate shows.
- **All audit items are now addressed.** Open follow-ups: the post-commit ring immunity tell (C5), the next-block-location tell (E1), the void auto-eject tell (D2), and the tutorial lines ("bright dust feeds stars", "one star at a time").

**How A1/A2 were done:** the brightness rule sits inside `CosmicDust.SetBaseTint`, so every recolor path inherits it.
- The dead `_hiddenHintColor` path was revived rather than deleted. Its existing call sites were already in the right places.
- `RestTintRaw` is the undarkened color. Tint diffusion, `RetintOver` and `GetCellVisualColor` blend in that raw space, so the darkening doesn't compound.

All paths are under `Assets/Scripts/` unless noted. Line numbers are as of this audit.

---

## Headline numbers
| System | Behaviors that matter | What the player can tell apart |
|---|---|---|
| Dust cell states | ~11 (hidden-role, revealed, revealed+bloomed, ForceGray, ZapForceGray, quick filler, never-regrow filler, pending, held, permanent-clear, keep-clear) | ~6 looks: dark preview, full color, full color + flower, light gray, dark gray, empty |
| Regrow-delay inputs | ~14 | 1 (a regrow-speed pulse, shown only *after* the cell regrows) |
| PhaseStar poke outcomes | ~12 | 2 (fill-progress visual, one shared orange reject flash) |
| Drum intensity inputs | 4 (carve energy, driving-role ceiling, Bass-fill lift, bass drop) | 0 (music only) |

---

## Tier 1 — Flagged areas

### A. Dust states and regrow

#### A1 · Light gray stands for three different behaviors — **Merge** · S–M
- **The player needs to know:** whether a gray cell will ever turn into star food.
- **What they see:** the same light gray (`mazeTint`, `Music/Config/CosmicDustGeneratorConfig.cs:56`) for:
  - quick-regrow generic filler, which stays `None` forever;
  - ForceGray regrows that still hide a Voronoi role: collectable jail and plow, and SuperNode carve (`Dust/CosmicDustGenerator.CellLifecycle.cs:251,266`; `CarveCellPreserveGray` in `Dust/CosmicDustGenerator.Clearing.cs:113`);
  - role cells that regrew as `None` through the neighbor fallback (`Dust/DustRoleDensityTracker.cs:187-216`).
- **Dead code:** a hint path was meant to tell these apart, but it does nothing. `DustImprintStore.ApplyHiddenHintToDust` (`Dust/DustImprintStore.cs:126`) feeds `CosmicDust.SetHiddenHintColor` (`Dust/CosmicDust.TintPulse.cs:531`). `_hiddenHintColor` is written and cleared but **never read** (verified by grep).
- **Proposal:** when a cell that still has a hidden role regrows gray, give it the same darkened preview a fresh maze uses (`ApplyHiddenColorPreview`, `Dust/CosmicDustGenerator.MazeGeneration.cs:550-572`). Keep light gray only for true `None` filler, and delete the dead hint path (5 call sites, including `CosmicDustGenerator.cs:561` and `VoidGrowth.cs:113,291,398`).
- **Result:** dark tint means "a role is underneath, carve it"; gray means "inert".

#### A2 · Full role color doesn't mean a star can use it — **Merge** · M
- **The player needs to know:** which cells PhaseStar tentacles will drain.
- **The rule:** only *bloomed* cells are targetable (`CosmicDust.IsVisuallyPresentForTargeting`, gate at `Dust/CosmicDust.ColliderVisibility.cs:65-70`).
  - A cell blooms only when it regrows after a `VehiclePlow` **or `CollectableBombBlast`** carve with a non-None role (`CosmicDustGenerator.cs:556-558`).
  - ECOSYSTEM.md says "VehiclePlow only", which is now stale.
- **What they see:** zap-released and other non-plow regrows can come back in full role color but stay untargetable. The only difference is the small flower (`Dust/CosmicDust.Flower.cs:42-66`).
- **Proposal:** non-bloom regrows of a role cell use the darkened preview instead of full color.
  - **Full brightness then means "this will feed a star"**, and dark means "carve it first".
  - The flower can stay as decoration or be cut.
  - Together with A1, dust brightness becomes a single readable axis: gray (inert) → dark (latent) → bright (ripe).
- **Tutorial:** add one line, "bright dust feeds stars".

#### A3 · Two kinds of generic filler — **Cut** · S
- **The two kinds behave very differently:**
  - Never-regrow filler becomes permanent-clear once carved (`CellLifecycle.cs:334-339`).
  - Quick filler regrows and skips the step gate (`CosmicDustGenerator.cs:387-389`).
- **What they see:** the only difference is dark gray 0.35 vs light gray 0.7 (`CosmicDustGeneratorConfig.cs:56,58`). Exact-Count maze mode only.
- **Proposal:** keep one filler behavior (**your call** which one) and remove `MotifProfile.genericFillNeverRegrowRatio`.
  - Never-regrow reads as "progress you keep".
  - Quick-regrow reads as "background".

#### A4 · An empty cell can mean four things — **Tell (selective)** · M
- **The four meanings:** pending regrow, held by a node (`_heldRegrowCells`, `Clearing.cs:151-154,191-194`), permanent-clear (`Clearing.cs:206`, `DiscardBlast.cs:50-57`), or keep-clear. None of them has a tell.
- **Already present:** regrow has a grow-in (`dust.SetGrowInDuration(DustTimings.regrowParticleGrowInSeconds)`, `CosmicDustGenerator.cs:492`). Collision stays off during the grow-in (`SetDustCollision(dust,false)`, `:571`). So "about to regrow" is already partly visible.
- **Proposal:** add a faint ember tinted with the node's role on **held** cells while their node is alive. This makes the chain star drain → node → cells return visible. Leave permanent-clear and keep-clear untold.
- **Optional:** add a short pre-grow shimmer if playtesters get surprised by regrow walls.

#### A5 · The regrow delay has about 14 inputs — **Cut** · M
- **The inputs:**
  - in `ResolveRegrowDelay` (`CellLifecycle.cs:175-225`): explicit override; role `regrowthDelay`; per-source `dustTiming` table; maze base; 8s fallback; density easing (solid fraction minus filler, `densityThrottleHalfAt`, `densityThrottleSkipAt`, `regrowDensityEaseMaxMultiplier`); ship per-role `regrowDelayMultipliers` (`:149-151`);
  - in `RequestRegrowCellAt` (`:316-375`): the held, filler and permanent vetoes; remembered `_pendingRegrowDelay`;
  - in `Dust/CosmicDustRegrowthController.cs:199-268`: keep-clear waits; four retry vetoes; the step-gate queue; global suppression.
- **The player sees:** almost nothing beforehand. Ship regrow multipliers are not on the radar chart (`VehicleStatDisplay` shows speed, agility, power and fuel).
- **Proposal:** reduce the delay to **maze base × source class × density easing**.
  - Source classes: *player carve* / *star drain* / *everything else*.
  - Cut `MusicalRoleProfile.regrowthDelay`.
  - Collapse the `DustClearSource` delay table to those 2–3 classes.
  - Ship multipliers: **your call**, either cut them or show them as a 5th radar stat ("Growth").
  - Keep the vetoes; they are safety mechanisms, not design.

### B. Star → node → notes

#### B1 · About 12 poke outcomes but 2 tells — **Merge** · M
`Phase/Star/PhaseStar.EjectionLogic.cs`:
- **Shared orange flash** (0.12s, then `ShowDim(gray)`, `PhaseStarVisuals2D.cs:223-235`). It covers:
  - disarmed (`:14-19`);
  - pickup-locked (`:23-28`);
  - `CanCommitEjection` failing (`:227-233`), which combines `_mineNodePending`, an unresolved sequence, and budget spent (`StarPool.Spawning.cs:67-69`).
- **Silent** (log line only):
  - dormant (`:12`);
  - expansion pending (`:31-38`);
  - wrong state (`:40-46`);
  - ejection in flight (`:48-54`);
  - zaps not met (`:55-83`; the fill visual does show progress);
  - active node (`:84-89`);
  - same frame (`:91-96`);
  - bin full while notes are still in play (`:110-125`);
  - sibling active (`:127-132`);
  - failed spawn re-latching (`:253-263`).
- **Budget spent is effectively unreachable.** At budget 0, `DespawnLeftoverStars` runs (`StarPool.EjectionEvents.cs:180-181`) and `Tick` stops spawning (`StarPool.Spawning.cs:18`).
- **Proposal:** map every outcome to **two player-facing responses**:
  1. **Not ready** (zaps not met, dormant, disarmed): a soft thud on top of the existing fill visual.
  2. **Wait** (another sequence in flight, pickup lock, expansion pending, bin full with notes in play): a brief pointer or tether toward whatever is blocking, i.e. the live node or the in-flight collectables.

  Internal guards (same frame, ejection in flight) stay silent.
- **Fix as well:** the reject flash ends on dim gray while the star is still latched ready.

#### B2 · The one-sequence-at-a-time gate is invisible — **Tell** · S
- **Context:** the global single-in-flight rule is a design decision you already accepted (see the `project_starpool_phase_system` memory).
- **What they see:** nothing until they poke a star and it gets rejected.
- **Proposal:** show the gate as **state**. While a sequence is in flight, other ready stars "sleep": dimmed, with their idle pulse paused. They wake when the gate clears. The rule itself stays unchanged.
- **Tutorial:** add one line, "one star at a time".

#### B3 · Node expiry has no countdown — **Tell** · S–M
- **The rule:** a node expires after N loop boundaries (`Gameplay/Mining/TrackNode.cs:42-47`). The limit comes from the NoteSet override, otherwise the role profile (`DiscoveryTrackNode.cs:126-129`).
- **Hidden rule:** every hit resets the counter (`_loopsSinceSpawn = 0`, `DiscoveryTrackNode.Resolution.cs:176`).
- **Proposal:** draw loop pips around the node. One pip drains at each loop boundary, and all of them refill on a hit. This one visual teaches both the expiry and the "keep hitting it" rule.

#### B4 · Nothing hints that a poke will produce a SuperNode — **Tell** · S
- **The condition:** the target track is fully expanded and `MotifProfile.alternateChordProgressionProfile` is set (`ShouldSpawnSuperNodeForTrack`, `EjectionLogic.cs:369-401`). The star visuals don't reference it anywhere.
- **Proposal:** when the condition holds for a star's track, give that star a distinct halo or shape, so the bonus round can be anticipated.

#### B5 · Node strength and side-only escape — **Leave** · —
- **Strength** shows as scale 0.3→1.1 (`Resolution.cs:188-190`).
- **Escape** happens only through gaps in the left and right walls (`DiscoveryTrackNode.Fleeing.cs:63-88`). The tell is a faster spin (`DiscoveryTrackNodeCharacterVis.cs:67-71`).
- **Low priority:** at most, make the side exits glow while a node is fleeing.

### D. Energy and music feedback

#### D1 · Drum intensity is a hidden 4-factor formula — **Cut + Tell** · S–M
- **How it works now:**
  - Intensity = carve energy × the driving role's ceiling (`Music/DrumTrack.BeatIntensity.cs:215-246`).
  - Carve energy is a leaky pool with a 3s half-life (`Dust/CarveEnergyAccumulator.cs`), weighted by the carved cell's role: Bass 1.0, others 0.5 (`MusicalRoleProfile.cs:20-37`).
  - The driving role is the last ejected node's role, otherwise the role of the star with the most zaps (`Phase/Star/StarPool.cs:118-138`).
  - The ceiling is Bass 1.0, others 0.6, and the others are lifted toward 1.0 as Bass bins fill (`:236-243`).
  - Bass drop: any Bass commit forces the heaviest clip for `bassDropHoldLoops` (`Music/DrumTrack.BassDrop.cs`).
- **What reaches the player:** only the music. No UI reads intensity, the driving role or the bass-drop state.
- **Proposal:**
  - **Cut** the driving-role ceiling and the Bass-fill lift, so **intensity = carve energy**. Boosting through dust visibly and audibly drives the drums.
  - Keep the per-role carve weights only if you want Bass dust to "hit harder"; it's a feel knob, not a rule.
  - **Keep the bass drop** as the one special event, and **Tell** it with a bass-color shockwave rippling through the dust (or a screen pulse) on the drop bar.
  - Note: the `project_role_driven_drum_intensity` memory describes v2 as deliberate, so this proposal reverses part of it.

#### D2 · No low-fuel warning, and some refuels are hidden — **Tell** · S
- **Fuel UI** (`UI/Fuel.cs:11-120`): it swells and turns white on gain, squeezes and darkens on drain, and judders in proportion to resistance. That last part is a good existing tell.
- **Gaps:**
  - No low-fuel state.
  - Void refuel (`Gameplay/Core/Vehicle.Recovery.cs:177-184`) is a silent trickle.
  - Void auto-eject (`:47-66`) is unexplained.
- **Proposal:** below ~20% fuel, pulse the bar and add a slow flicker on the ship. While void refuel is active, give the ship a soft glow and the bar a sparkle.

#### D3 · Vehicle traps: color conflict and no warning — **Fix** · S
- **The conflict:** `SpawnVehicleTraps` (`Managers/GameFlowManager.cs:441-500`) passes `roleColor`, but the circle path uses `hideRole:true`, which spawns gray (`Dust/CosmicDustGenerator.VoidGrowth.cs:85-90`).
- **No warning:** the trap appears in 0.4s at motif start.
- **Proposal:** choose one look, and add a ~0.5s outline telegraph before the dust grows in.

---

## Tier 2 — Note placement and newer systems

| ID | Finding | Proposal | Cost |
|---|---|---|---|
| C1 | A 10th catch silently drops the oldest queued note, which **becomes a time bomb** (`Gameplay/Core/Vehicle.NoteRelease.cs:95-112`) | **Cut**: when the queue is full, refuse the pickup and let the collectable bounce off | S |
| C2 | A ship carrying notes accepts only its own track's notes (`Vehicle.NoteRelease.cs:11`), with no tell | **Tell**: other tracks' collectables ghost while you carry | S |
| C3 | The release cue (`Gameplay/Core/VehicleReleaseCue.cs`) counts down but never shows whether the window is open. `TryGetReleaseWindowState` (`Vehicle.NoteRelease.cs:123-183`) already exists and only the tutorial uses it | **Tell**: the cue ring solidifies while the window is open | S |
| C4 | Default placement mode disagrees: `UI/PlayerSelect.cs` defaults to Auto, while `Vehicle.cs:114` and `UI/LocalPlayer.cs:12` default to Manual | **Fix**: use one default | S |
| C5 | A ring hit that was too slow and a ring hit with nothing pending both play `PlayDenialShake`. A 0.5s post-commit immunity has no visual (`Vehicle.Damage.cs:79-121`) | **Tell** (low) | S |
| E1 | Cosmic blocks: unbreakable walls. Nothing shows where the next one will rise, or that the next fill of that role clears them (`Dust/CosmicBlockField.cs:513`). An ineligible cell means the block silently never appears (`:186`) | **Tell**: blocks pulse as their role's bin nears full | S |
| E2 | Up/Down phase exits appear only once every motif in the phase has a ring (`Managers/PathChoiceCoordinator.cs:65-67`) | **Tell**: show a sealed exit stub before then | S |

---

## Tutorial coverage
`Assets/Resources/Tutorial/NarratedTutorial.json` covers:
- carving (energy costs more on "heavier sounds");
- stars being drawn to stirred dust;
- poking a ready star;
- chasing and smashing the node;
- catching notes;
- placing notes with a timing window (Manual only);
- loops and rings;
- escape and discard.

It never covers: dust colors or hidden roles, what actually feeds a star, rejects, node expiry or escape, SuperNodes, ring breaking, or ring phasing.

**Recommendation:** after A1/A2 and B2, add one line each: "bright dust feeds stars" and "one star at a time". The tells above should carry everything else.

---

## Suggested pick order (impact ÷ cost)
1. **A1 + A2**: dust brightness as one axis
2. **D1**: intensity = carving, with the bass drop made visible
3. **B1 + B2**: two poke responses, plus visible "sleep"
4. **B3**: node expiry pips
5. **C1**: refuse pickup when the queue is full (removes a silent bomb source)
6. **D2**: low-fuel warning and void refuel tell
7. **A5**: collapse the regrow inputs
8. **A3**: single filler type
9. **B4, C2, C3, C4, D3, E1, E2**: small tells and fixes

## Open decisions for you
- **A3:** which filler survives, never-regrow or quick-regrow?
- **A5:** cut ship regrow multipliers, or show them as a radar stat?
- **D1:** confirm reversing the role-driven ceiling from Drum Intensity v2.
- **A2:** keep the flower as decoration, or cut it?
