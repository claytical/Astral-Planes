# SETTINGS_AUDIT — Player-Changeable Surface (2026-11-26)

## Context
Goal: to simplify and merge the game's settings (controls, audio, publishing, save reset). Before designing anything, this lists everything a player can change and how. This document is an inventory only; no code was changed.

Paths are relative to `Assets/Scripts/` unless noted otherwise.

---

## A. Persisted settings (survive a relaunch)
There is no PlayerPrefs anywhere. All persistence is hand-written JSON under `persistentDataPath`.

| Setting | File | Writer | UI | Write style |
|---|---|---|---|---|
| Keyboard vs Mouse scheme (int 0/1) | `control_prefs.json` | `UI/ControlPrefs.cs:31,96` | Settings and Pause, "Keyboard Controls" | .tmp then Delete+Move |
| Publish flight plans (bool) + HasDecided | `GreaterMazeTelemetry/publish_consent.json` | `Telemetry/TelemetryConsent.cs:83` | Settings "Publish", Living Score first-run prompt | Plain write, **twice per choice** |
| Publisher ID/token | `GreaterMazeTelemetry/publisher_identity.json` | `Telemetry/PublisherIdentity.cs:59` | None (not reset) | Plain write |
| Rings (progress) | `RingSessions/ring_{phase}_{motif}.json` | `Utilities/RingSessionStore.cs:41` | Carousel; Reset | Atomic `File.Replace`; Steam Auto-Cloud |

Tutorial completion, "can skip the sandbox", and where the carousel starts are all **worked out from whether ring files exist**. None of them is stored as a flag.

## B. Session-only choices (lost when you leave the scene or quit)
| Choice | Where | Input |
|---|---|---|
| Ship / vehicle | `UI/PlayerSelect.cs:61`, `Hangar.cs:77` | D-pad L/R, Confirm |
| Groove Style (Auto "Cozy Co-Pilot" / Manual "Groove Commander") | `PlayerSelect.cs:79`; default `Gameplay/Core/NotePlacementMethod.cs:14` | D-pad L/R; **resets to Auto every session** |
| Game mode (Co-op / Elim / Motif competitive) | `Managers/SessionStateCoordinator.cs:51`, `UI/ModeSelectPanel.cs` | 2+ players only, L/R + Confirm |
| Carousel motif/phase pick | `UI/PhaseLibraryCarousel.cs:736` → static `PhaseLibraryStartConfig` | D-pad, Confirm |
| Preview beat toggle | `UI/PreviewBeatToggle.cs:13` | **B** key only, keyboard only, no persistence |
| Player color | prefab `planeIcon.color` | Not a choice |
| Join / rejoin / continue after death | `Managers/TrackSelectionJoinController.cs`, `LocalPlayer.ConfirmFlow.cs:149` | Confirm, hold Confirm 1s, Confirm within 30s |

## C. Menus that exist today
| Menu | Opened by | Options | Scope |
|---|---|---|---|
| **Settings** `UI/SettingsPanelController.cs:70` | Select, F1, O | Publish, Keyboard Controls, Reset All Progress | **Main.unity only** |
| **Pause** `UI/PauseMenuController.cs:27` | Select, P (only while Playing) | Resume, Keyboard Controls (keyboard players only), Run Tutorial, Quit | In-run; soft pause (music keeps playing) |
| **Quit prompt** `UI/QuitPrompt.cs` | Escape tap (everywhere), Pause→Quit | Yes/No | Global |
| **Living Score first-run** `UI/LivingScorePanel.cs:58` | Automatic while `!HasDecided` | Publish / Don't | Separate UI, not ModalMenu |

All except the Living Score prompt are vertical lists in `UI/ModalMenu.cs`, with no sliders or toggles.

## D. Things players cannot change (gaps a settings screen usually covers)
- **Audio volume:** none. Both mixers have `m_ExposedParameters: []`, and nothing calls `SetFloat` or sets `AudioListener.volume`. The levels are designer-only assets (`Music/Config/AudioMixConfig.cs` drumVolume 0.79, `MotifProfile.drumVolumeMultiplier`, `MidiVoice.SetGainTrim`).
- **Display:** none. Fixed to 1920×1080 exclusive fullscreen; vSync depends on the quality level; no `Screen.*` / `QualitySettings` code.
- **Controls:** no rebinding, invert, sensitivity or deadzone setting (deadzone 0.20 is fixed in `ShipMusicalProfile.cs:34`). Gamepad remapping is left to Steam Input. The A/B swap on Switch pads is automatic (`Input/PadSemantics.cs`).
- **Language and accessibility:** none.
- **Export (MIDI, album art, WAV/stems/MP3):** editor-only, in the Ring Session Manager. `UI/RingAudioExporter.cs` compiles into player builds but is never used there.

## E. Back, leave and quit: many different gestures
| Gesture | Effect |
|---|---|
| Escape tap | Quit prompt (`GlobalEscapeQuitListener.cs`) |
| East, one step at a time (ship select) | Ready → groove → vehicle → leave; 1.5s hold when 2+ players |
| Hold East 1.5s, nobody joined | Back to Main |
| Hold East 3s, in tutorial | That player leaves ("Quitting") |
| Hold East 3s, in a run | Give up → Main |
| Hold South | Skip the Control Sandbox (only once a ring is saved) |
| Commit with nothing carried | Skip one narration line |

## F. Debug and dev surfaces
- `UI/AudioBalanceDebugPanel.cs` opens with **D** (there is no F2 key, so the memory note is wrong). It is compiled only in the editor and in DEVELOPMENT_BUILD, and **is live in tester dev builds**.
- `PhaseLibrary.unity` is in the build list but nothing loads it.
- `TelemetryConfig` has dev toggles (bypassConsentForTesting, allowInsecureHttp).

## G. Inconsistencies (input for the design)
1. **Settings and Pause share the Select button but have different contents.** Settings has Publish and Reset but no Run Tutorial. Pause has Run Tutorial but no Publish. Neither menu is a superset of the other.
2. **Settings exists only in Main.** Players can't reach Publish or Reset from TrackSelection or during a run.
3. **Settings always shows "Keyboard Controls"**, even when no keyboard player has joined. Saving "Mouse" with no mouse attached saves a scheme that never takes effect (`LocalPlayer.Input.cs:74`).
4. **Groove Style resets every session.** It is the most personal choice and the only one not saved.
5. **Consent is written twice and not atomically**, so a crash between writes can leave `HasDecided` saved without the publish value. Control prefs use Delete+Move, which is not truly atomic.
6. **Reset leaves cloud and identity data behind.** Ring deletions reach Steam Cloud only on a clean exit. Control prefs and consent aren't covered by the Cloud pattern. Publisher identity survives the reset by design.
7. **Preview beat is a hidden keyboard toggle** (B) with no gamepad route.
8. **Stale comments:** `ControlPrefs.cs:8` ("W/Up boost"), `TelemetryConsent.cs:34` (HasDecided "gates the Settings option").

## H. Consolidation directions to weigh (not decisions)
- **One preferences store.** A single `settings.json` (versioned, atomic) holding scheme, publish, groove-style default, and any new audio/display values. It could be added to the Steam Cloud pattern so preferences follow the player across machines.
- **One Options menu, reachable from Main, Pause and TrackSelection**, with context-specific extras (Run Tutorial and Quit only in a run).
- **Audio:** at minimum Master / Music / Effects sliders. This needs exposed mixer params and a slider row type in ModalMenu, since ModalMenu currently has only buttons.
- **Controls section:** show keyboard or mouse only when a keyboard is present; a read-only control reference for the gamepad.
- **Groove Style:** save the last choice per device, or move it into Settings as a default.
- **Data section:** Publish + Reset together; decide whether Reset also rotates the publisher identity.

---

