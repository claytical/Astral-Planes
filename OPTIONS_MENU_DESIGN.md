# OPTIONS_MENU_DESIGN — One Options Menu (draft, 2026-11-26)

Built from SETTINGS_AUDIT.md §H. It addresses §G items 1–7. This is a design draft: no code has been written, and the open decisions are listed in §8.

---

## 1. Goals
1. **One menu, one button, everywhere.** Select (plus F1/O/P on keyboard) opens the same Options menu in Main, TrackSelection and during a run. The rows that depend on where you are appear at the top.
2. **One preferences file.** Every saved preference lives in `settings.json`: versioned, written atomically, written once per change.
3. **Only show what applies.** Rows for hardware that isn't present are hidden.
4. **Text-only rows.** This keeps the current ModalMenu look. Values appear as text (`◂ 70% ▸`), with no bars or pips, consistent with the "no UI overlays" direction.

## 2. Menu tree

```
Options
├─ [run only]  Resume
├─ [run only]  Run Tutorial
├─ Audio ▸
│   ├─ Master        ◂ 100% ▸
│   ├─ Music         ◂  80% ▸
│   ├─ Effects       ◂  80% ▸
│   └─ Preview Beat  ◂ On ▸            (moves here from the B key)
├─ Controls ▸
│   ├─ Groove Style  ◂ Cozy Co-Pilot ▸ (starting choice at ship select)
│   ├─ [keyboard present] Keyboard Scheme ◂ Keyboard ▸
│   └─ Control Reference ▸             (read-only page for the current device)
├─ Publishing ▸
│   └─ Publish Flight Records ◂ Off ▸
├─ [not in run] Data ▸
│   ├─ Restore Default Settings → confirm
│   └─ Reset All Progress       → confirm (two-step)
└─ Quit ▸            (run: "Quit to Main Menu" · otherwise: "Quit Game")
```

The root stays short: at most 7 rows during a run and 5 outside one. Every setting is one level deep.

### Row behaviour
| Row kind | Up/Down | Left/Right | Confirm | Back |
|---|---|---|---|---|
| Action (Resume, Quit) | move | — | run it | close or go up one level |
| Submenu (Audio ▸) | move | — | open | close or go up one level |
| Value (Music, Groove Style) | move | change value, applied and audible immediately | step +1 (wraps) | go up one level and save |

Volume moves in steps of 10%. Holding Left/Right repeats after 0.35s, then every 0.08s.

## 3. Context rules
| Context | Opened by | Extra rows | Hidden rows |
|---|---|---|---|
| Main (carousel / title) | Select, F1, O | — | Resume, Run Tutorial |
| TrackSelection (join / ship select) | Select, F1, O | — | Resume, Run Tutorial |
| In run (`GameState.Playing`) | Select, P, F1, O | Resume, Run Tutorial | **Data** (a reset mid-run reloads the scene, so it's too risky there) |

- During a run the menu stays a **soft pause**, as today: the music keeps going, which suits a rhythm game.
- In multiplayer, whichever player opens the menu controls it (`ModalMenuSpec.pad`), as the pause menu does now.
- **Escape tap** still opens the Quit prompt directly, so the keyboard fast path is kept. It's listed as open question Q3.

## 4. Settings and their defaults
| Key | Type | Default | Applied by | Replaces |
|---|---|---|---|---|
| `masterVolume` | 0–1 | 1.0 | mixer `MasterVol` | — (new) |
| `musicVolume` | 0–1 | 0.8 | mixer `MusicVol` | — (new) |
| `effectsVolume` | 0–1 | 0.8 | mixer `EffectsVol` | — (new) |
| `previewBeat` | bool | true | `PreviewBeatToggle` | B-key session toggle |
| `grooveStyle` | Auto/Manual | Auto | `PlayerSelect` initial index | `NotePlacementDefaults.Default` |
| `kbmScheme` | Keyboard/Mouse | Keyboard | `LocalPlayer.Input` via `Changed` | `control_prefs.json` |
| `publishEnabled` + `publishDecided` | bool, bool | false, false | uploader / Living Score prompt | `publish_consent.json` |

**Groove Style is a remembered starting choice, not a lock.** Ship select still lets each player switch. When a player confirms ship select, their choice is written back as the new default. The Options row and ship select therefore edit the same value. In co-op the last player to confirm sets it.

**Volume to dB:** `dB = v <= 0.001 ? -80 : 20·log10(v)`. This is applied with `AudioMixer.SetFloat` on load and on every change.

## 5. Persistence: `PlayerSettings`
- A new static class `PlayerSettings` (`Assets/Scripts/UI/PlayerSettings.cs`) owns `persistentDataPath/settings.json`.
  ```json
  { "version": 1, "masterVolume": 1, "musicVolume": 0.8, "effectsVolume": 0.8,
    "previewBeat": true, "grooveStyle": 0, "kbmScheme": 0,
    "publishEnabled": false, "publishDecided": false }
  ```
- **Writes:** .tmp then `File.Replace`, the same pattern as `RingSessionStore.cs:41-47`. Fixes §G5.
- **One write per change.** The publish choice becomes a single `SetPublish(bool)` that sets both fields and saves once. Value rows mark the file dirty, and it is saved when the menu closes or you go back a level, so holding Left/Right doesn't hammer the disk.
- **Migration:** the first time it loads with no `settings.json`, it imports `control_prefs.json` and `publish_consent.json`, saves, then deletes those two files.
- **Callers don't change.** `ControlPrefs.Scheme`/`Changed` and `TelemetryConsent.*` stay as thin facades over `PlayerSettings`, so `LocalPlayer.Input`, the uploader, `SolarSystemRecordDisplay` and `SelectGlyphHint` need no edits.
- **Steam Cloud:** add `settings.json` to the Auto-Cloud pattern (a partner-site change) so preferences follow the player. `publisher_identity.json` stays local.

## 6. Data section semantics
| Action | Erases | Keeps |
|---|---|---|
| **Restore Default Settings** | `settings.json`, reset to the defaults above and applied live | rings, telemetry, identity |
| **Reset All Progress** | rings, telemetry `run_*` + `Sent/` | settings, publisher identity (see Q1) |

This splits today's single reset, which currently wipes both progress and settings (the confirm text says "rings and settings"). Erasing your progress shouldn't also undo your volume or publish choice. The confirm copy becomes: *"Erase every ring and start over? Your settings are kept. This can't be undone."*

## 7. Implementation sketch
1. **ModalMenu value rows** (`UI/ModalMenu.cs`):
   - Add to `ModalMenuOption`:
     - `Func<string> value`: the value label, drawn right-aligned as `◂ x ▸`
     - `Action<int> onAdjust`: called with −1/+1
     - `Func<bool> visible`
   - Today Left/Right are just extra up/down keys (`ModalMenu.cs:112-113, 143-144`). Change that so Left/Right only adjust value rows, Tab keeps moving forward, and the stick X-axis latches into adjust.
   - Skip rows whose `visible` returns false when building the menu.
2. **`PlayerSettings`** (new) + facades in `ControlPrefs.cs` and `TelemetryConsent.cs`.
3. **Mixer:** expose `MasterVol`, `MusicVol`, `EffectsVol` on `Audio/ProceduralMixer.mixer`. **Check first** that:
   - the `effectsMixerGroup` references (`LocalPlayer.cs:14`, `InstrumentTrackController.cs:38`, `MotifRingGlyphApplicator.cs:56`) all sit under an "Effects" group, and
   - MPTK voices and drums (`AudioMixConfig` role groups, `drumsGroup`) all route under Music.

   Anything routed to `AudioMixer.mixer` or with no group at all bypasses the sliders.
4. **`OptionsMenu`** (rename `SettingsPanelController` → `OptionsMenuController`):
   - builds the tree from §2 using the §3 context checks
   - is no longer limited to Main: make it a DontDestroyOnLoad singleton like ModalMenu
   - absorbs `PauseMenuController`, whose rows become the run-only rows
   - `LocalPlayer.Pause.cs` calls `OptionsMenu.Open(opener)`
5. **Groove Style:** `PlayerSelect` reads `PlayerSettings.GrooveStyle` for its initial index, and the confirm in `LocalPlayer.ConfirmFlow.cs` writes it back.
6. **Preview Beat:** `PreviewBeatToggle` reads and writes `PlayerSettings.PreviewBeat`. The B key stays as a shortcut that changes the same value.
7. **Control Reference page:** a read-only ModalMenuSpec whose description is picked by device (gamepad glyphs via `ControllerGlyphProvider`, keyboard, or mouse). It has a single "Back" option.
8. **SaveDataReset:** split into `ResetProgress()` and `PlayerSettings.RestoreDefaults()`.
9. Fix the stale comments (§G8) as part of the same change.

## 8. Open decisions
- **Q1: Does Reset All Progress rotate the publisher identity?**
  - *Recommendation:* only when Publish is on. Ask "Also forget my published identity?" as a third option on the confirm screen.
  - Keeping the identity keeps server-side records linked to the player. Rotating it is the more privacy-friendly choice.
- **Q2: Is Groove Style per device or global?**
  - Global is simpler and matches solo play.
  - Per device (keyed by device path or Steam controller handle) suits a couch group that shares one PC.
  - *Recommendation:* global for v1.
- **Q3: Escape: open Options, or keep the Quit prompt?**
  - Most PC games open the menu on Escape.
  - *Recommendation:* Escape opens Options with Quit pre-selected. That keeps quitting two presses away and makes the menu easy to find.
- **Q4: Display options.** The game is fixed at exclusive fullscreen 1920×1080. Should there be a Display submenu (Fullscreen / Windowed, VSync)? It would be cheap to add given the value-row work above. *Recommendation:* add Window Mode only, which Steam Deck and desktop players both expect.
- **Q5: Should Living Score's first-run prompt move into ModalMenu**, so there's one menu system everywhere?

## 9. Verification (once built)
- Open Options in each of the three contexts and confirm the rows match §3.
- Drag each volume slider to 0 and confirm that category goes silent: drums and every role for Music; pickup, ring and ship SFX for Effects.
- Relaunch and confirm every value persists. Delete `settings.json` while old `control_prefs.json` and `publish_consent.json` are present, relaunch, and confirm the values migrate and the old files are removed.
- Kill the process mid-save (debugger break) and confirm there's no corrupt or partial `settings.json`.
- With no keyboard connected, confirm the Keyboard Scheme row is hidden.
- Reset All Progress keeps volume and publish. Restore Defaults keeps the rings.

---

## 10. Implementation status (2026-11-26): built and compiled, not playtested

Q1–Q4 were built as recommended. Q5 (moving the Living Score prompt into ModalMenu) is not done.

**Decisions as built**
- **Q1:** The Reset confirm shows a third option, "Erase & Withdraw Published Records", only while Publish is on. The publisher identity is deleted only after the server confirms the withdrawal. If the withdrawal fails or takes longer than 20s, nothing is erased and the player sees: Try Again / Erase Progress Only / Cancel.
- **Q2:** Groove Style is a single global value. A player's confirm at ship select writes it, and Options → Controls edits the same value. After a player confirms once, their own choice is kept for the rest of the session, including respawns.
- **Q3:** An Escape tap opens Options with Quit highlighted, replacing the direct Quit prompt.
- **Q4:** Window Mode (Fullscreen = borderless window / Windowed = 75% of the screen) is a row under **Audio & Display**. A one-row Display submenu would have been pointless. Unity saves the window mode itself, so it is not stored in settings.json.

**Deviations from §2–§6**
- **Volume defaults are 100%, not 80%,** so the tuned mix doesn't change. **Preview Beat defaults to Off**, which matches the old behavior.
- **Master volume is `AudioListener.volume`**, not a mixer param, so it reaches every source regardless of routing. Music and Effects are the exposed params `MusicVol` and `EffectsVol` on ProceduralMixer.
- **Effects routing:**
  - `AudioMixConfig.effectsGroup` was added and assigned to ProceduralMixer/Effects. `AudioMixConfig.ResolveEffects()` falls back to it wherever a component's `effectsMixerGroup` field is empty: LocalPlayer, AudioManager, pickup SFX and the ring-glyph animation.
  - `AudioMixer.mixer` (the Main Camera and the Sandwave ship) now outputs into ProceduralMixer/Effects.
  - Any remaining unrouted sources follow only Master. Run **Tools → Astral Planes → Report Unrouted Audio Sources** to list them.
- **Data appears only in Main**, not in TrackSelection. Reset reloads the scene, and Main is the only scene where that's a clean restart.
- **Restore Default Settings keeps the publish choice.** Consent is a deliberate decision, not a preference to reset.
- **Value rows render as `label    <  value  >`** in ASCII, because the modal font may lack arrow glyphs.
- **Toggle rows don't auto-repeat** when Left/Right is held; only the volume rows do.

**Left/Right and Back conflicts fixed.** These handlers now bail out while any modal is open, and on the frame it opens or closes (`ModalMenu.ShouldSuppressInput`):
- `LocalPlayer.OnNext/OnPrevious`, which handle ship, Groove Style and game-mode cycling.
- `LocalPlayer.HandleLeavePressed`, the ship-select undo and the tutorial abort hold.
- `TrackSelectionJoinController.Update`, which handles joining, the hold-back-to-carousel timer and rejoin holds.

Inside the modal, a menu that has value rows uses Left/Right only to adjust values. Menus without value rows, such as the Yes/No prompts, still use Left/Right to move the highlight.

**Files**
- New: `UI/GameSettings.cs` (named this way to avoid `UnityEditor.PlayerSettings`), `UI/OptionsMenu.cs` and `Editor/AudioRoutingReport.cs`.
- Rewritten:
  - `ControlPrefs.cs` and `TelemetryConsent.cs` are now facades over GameSettings. The consent API is `Decide(bool)`, which does one atomic write.
  - `SaveDataReset.cs` has `ResetProgress()`.
  - `SettingsPanelController.cs` is now a bootstrap that installs OptionsMenu.
- Deleted: `UI/PauseMenuController.cs`.
- Patched: ModalMenu (value rows, `onClosed`, horizontal repeat), PreviewBeatToggle, LocalPlayer (Pause / Input / LeaveFlow / SelectionLaunch / ConfirmFlow), GlobalEscapeQuitListener, LivingScorePanel, PublisherIdentity (`Forget`), AudioMixConfig + its asset, and both mixers.

**Still open**
- Playtest everything in §9.
- Add `settings.json` to the Steam Auto-Cloud pattern on the partner site.
- ~~Run the unrouted-audio report~~ Done 2026-11-27: all 10 hits were routed in code at runtime (vehicle AudioManager, MidiStreamPlayer role buses) or Core-mode MPTK voice templates that never play. The report now sorts those into separate lists. ControlTutorialHighlight now uses ResolveEffects; before, an unassigned field set its output to null.
- Q5.
- Options only exists after Main has loaded once. Entering Play mode directly in another scene has no Options menu.

## 11. Main menu & two-pane Options (2026-11-27): built and compiled, not playtested

### Title menu (`UI/MainMenu.cs`, auto-created whenever Main loads)
| Row | When | Leads to |
|---|---|---|
| START | No ring saved yet | Join screen (co-op only), then control sandbox, then the tutorial motif |
| CONTINUE | A ring is saved | Motif carousel, then the join screen. Solo goes straight to the motif; 2+ players run the sandbox first. The tutorial motif never runs. |
| VERSUS | 2+ input devices connected (each gamepad, with raw HID duplicates of a Steam pad not counted, plus the keyboard). Dimmed and skipped otherwise. | Survival / Score prompt, then the carousel (or phase 1 motif 0 if the tutorial isn't done), then the join screen (launch needs 2+ players), then sandbox, then the motif |
| OPTIONS | Always | Two-pane Options, with DATA |

- **StartIntent** (`Managers/StartIntent.cs`) carries the choice. The session mode is co-op unless VERSUS: Survival = `CompetitiveElimination`, Score = `CompetitiveMotif`. It is applied on choice, on TrackSelection load and in `ResetForTrackSelection`, and cleared when Main loads.
- **Ship select no longer picks a mode.** Removed: `CycleMode`, `TryCycleGameMode`, `AwaitingModeConfirm` and the mode lock, and the extra confirm press. `ModeSelectPanel` is now a VERSUS banner that says "needs a second player" until two have joined.
- **Sandbox rule unchanged:** `playerCount > 1 || !TutorialComplete`. "Tutorial complete" is any saved ring (`StartIntent.TutorialComplete`).
- **Carousel:** hidden in Main until it's engaged (`PhaseLibraryCarousel.Engage`). Back (East or Escape) returns to the menu. Its confirm now also calls `StartShipSelectionPhase` (IntroInputHandler used to do that on the same press).
- **Retired:**
  - `IntroInputHandler` is an empty stub. Main.unity's UI/Submit binding still points at it.
  - The "Start Text" prompt and the press-any tutorial prompt.
  - The Settings glyph hint, now hidden on Main every time.
- **Title:** `IntroTitleFader.SetVisible` shows the title while the menu is up and fades it out for the carousel and Options.
- **Escape:**
  - A tap on the menu opens the Quit prompt.
  - In the carousel or Options, Escape is Back, and `MenuNav.ClaimEscape` stops `GlobalEscapeQuitListener` from also opening Quit.
  - In a run, a tap opens Paused with Quit focused.
- **Join screen:** hold-East now reads "Back to Main Menu".

### Options (`UI/OptionsScreen.cs`; rows defined in `UI/OptionsMenu.cs`)
- **Layout:**
  - Left column: AUDIO / CONTROLS / DISPLAY / DATA. DATA appears only in Main and outside a run.
  - Up/Down picks a category. Right or Confirm enters the panel.
  - In the panel, Up/Down picks a row and Left/Right adjusts it; sliders repeat when held. Left on a row with no value, or Back, returns to the categories. Back there closes the screen.
- **Rows:**
  - AUDIO: MASTER / MUSIC / EFFECTS (sliders with a fill bar), PREVIEW BEAT.
  - CONTROLS: GROOVE STYLE, KEYBOARD SCHEME (only with a keyboard), CONTROL REFERENCE (Gamepad/Keyboard/Mouse). The reference text for the chosen device shows under the rows.
  - DISPLAY: WINDOW MODE.
  - DATA: PUBLISH FLIGHT RECORDS, RESTORE DEFAULT SETTINGS, RESET ALL PROGRESS. The two action rows open the existing ModalMenu confirms, including Erase & Withdraw, then return to DATA.
- **Implementation:**
  - Built at runtime (sort order 500), borrowing ModalMenu's fonts and ButtonTemplate (`UI/MenuKit.cs`).
  - Input comes from `UI/MenuInput.cs` (`MenuNav`). ModalMenu's own polling was left unchanged rather than moved into MenuNav.
  - While open, `ModalMenu.NotifyExternalOpen` makes `IsOpen`/`ShouldSuppressInput` true, so every existing gameplay guard (ship-select Left/Right, join controller, Leave, movement) holds.
- **Entry points:**
  - **In a run:** Select/P opens "Paused": Resume / Options / Quit to Main Menu. **Run Tutorial is removed.** Back from Options returns to Paused.
  - **Outside a run:** Select/F1/O opens Options directly, as does the main menu's OPTIONS row.

### Playtest checklist
- [ ] **No rings:** the menu shows START / VERSUS / OPTIONS. VERSUS is dimmed with one pad and no keyboard.
  - [ ] START, 1 player: no mode banner, then sandbox, then tutorial motif.
  - [ ] START, 2 players: same flow, co-op.
- [ ] **With rings:** the menu shows CONTINUE.
  - [ ] CONTINUE opens the carousel; East/Escape returns to the menu, and Escape doesn't open Quit.
  - [ ] Pick, then join solo: no sandbox, straight to the motif.
  - [ ] Pick, then join 2 players: sandbox, then the picked motif.
- [ ] **VERSUS** (keyboard + pad): Survival, then pick, then 1 joined.
  - [ ] It doesn't launch, and the banner says a second player is needed.
  - [ ] With 2 joined: sandbox, then the motif; dying eliminates you.
  - [ ] Score ends the match on motif completion.
  - [ ] VERSUS with no rings starts on phase 1 motif 0, not the tutorial.
- [ ] **Options:** category and panel navigation as above; sliders move and repeat when held; entering with a held Right doesn't nudge MASTER.
  - [ ] The control reference text changes with the device.
  - [ ] DATA confirms return to DATA. Reset reloads Main to START.
- [ ] **Ship select:** Options opened from Select doesn't move ships or Groove Style. Escape with Options open closes it and doesn't open Quit.
- [ ] **In a run:** Paused has no Run Tutorial. Options there has no DATA, and Back returns to Paused.
- [ ] **Layout:** check row and text fit at 1920×1080 and in windowed 16:9. Long values ("Groove Commander") overlap no titles.
