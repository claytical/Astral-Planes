# System-Wide Code Audit — 2026-06-01

## Scope

This audit focused on repository hygiene and safe static-analysis findings after the recent update pass. It intentionally avoids removing runtime C# types solely because they have low textual reference counts: Unity scenes, prefabs, editor menus, serialized fields, and reflection can make those references non-obvious.

## Cleanup applied

- Removed tracked macOS `.DS_Store` files from the repository. These files are operating-system metadata and should not be versioned.
- Removed tracked Mono crash dumps (`mono_crash*.json`, `mono_crash*.blob`, and `mono_crash.mem.*`). These dumps were historical runtime artifacts, not source assets, and the memory blobs alone accounted for roughly 70 MB of obsolete repository payload.
- Removed orphan Unity `.meta` files whose matching asset folders/files are not present in the repository:
  - `Assets/MidiPlayer/Demo.meta`
  - `Assets/MidiPlayer/Resources/MidiDB.meta`
  - `Assets/Resources/Profiles/Musical Roles/Groove.meta`
  - `Assets/Sounds/Drums/Loops/168/147.meta`
  - `Assets/StreamingAssets/CoralSessions.meta`
- Updated `.gitignore` so new `.DS_Store` files and Mono crash dumps do not re-enter source control.
- Corrected the existing MidiPlayer demo ignore pattern from `/Aa]ssets/MidiPlayer/Demo/` to `/[Aa]ssets/MidiPlayer/Demo/` so it matches Unity-style path casing correctly.

## Audit findings to review before future cleanup

### Large classes that may benefit from decomposition

The following files are the highest-risk overcomplexity candidates by line count. They are not necessarily dead code, but they are good targets for future extraction into smaller services, policies, or presenter classes:

| Lines | File |
| ---: | --- |
| 2,877 | `Assets/Scripts/Music/InstrumentTrack.cs` |
| 2,449 | `Assets/Scripts/Dust/CosmicDustGenerator.cs` |
| 1,838 | `Assets/Scripts/Visualizers/Notes/NoteVisualizer.cs` |
| 1,690 | `Assets/Scripts/Gameplay/Core/Vehicle.cs` |
| 1,637 | `Assets/Scripts/Gameplay/Mining/Collectable.cs` |
| 1,587 | `Assets/Scripts/Phase/Star/PhaseStar.cs` |
| 1,533 | `Assets/Scripts/Music/DrumTrack.cs` |
| 1,409 | `Assets/Scripts/Music/InstrumentTrackController.cs` |
| 1,394 | `Assets/Scripts/Visualizers/Coral/MotifCoralVisualizer.cs` |
| 1,345 | `Assets/Scripts/Dust/CosmicDust.cs` |

### Low-reference C# candidates

A text-reference scan found these types with very few C# identifier references and no direct scene/prefab/asset GUID references in the scanned YAML assets. Treat these as review candidates only; some are likely kept alive by Unity editor menus, generated workflows, or runtime conventions.

- `Assets/Editor/ChordProgressionGenerator.cs` — editor tooling candidate.
- `Assets/Editor/MidiToRiffImporterWindow.cs` — editor tooling candidate.
- `Assets/Scripts/Dust/DustRegrowthScheduler.cs` — service/helper candidate.
- `Assets/Scripts/Effects/RainbowLerp.cs` — potential unreferenced MonoBehaviour.
- `Assets/Scripts/Gameplay/Core/NoteBehaviorPolicy.cs` — legacy-alias policy; currently tied to obsolete enum values.
- `Assets/Scripts/Gameplay/Mining/GridSweepContainmentUtility.cs` — utility candidate.
- `Assets/Scripts/Gameplay/Mining/MineNodeBehaviorCategory.cs` — extension-type candidate.
- `Assets/Scripts/Managers/EndSceneManager.cs` — potential unreferenced scene manager.
- `Assets/Scripts/Phase/PhaseBridgeSignature.cs` — bridge-library candidate.
- `Assets/Scripts/Phase/Star/PhaseStarInteractionState.cs` — status-helper candidate.
- `Assets/Scripts/Phase/Star/StarPool.cs` — nested/relay helper candidate.
- `Assets/Scripts/Utilities/JsonUtilityWrapper.cs` — utility candidate.
- `Assets/Scripts/Utilities/PlayerPrefsX.cs` — utility candidate.
- `Assets/Scripts/Utilities/SpinObject.cs` — potential unreferenced MonoBehaviour.
- `Assets/Scripts/Visualizers/Coral/MotifCoralAnimationController.cs` — service candidate.

### Legacy / obsolete code markers

`Assets/Scripts/Gameplay/Core/NoteBehaviorPolicy.cs` still contains obsolete `NoteBehavior` aliases (`Drone`, `Lead`, `Percussion`, `Glitch`, `Harmony`, `Sustain`, `Hook`, `Bass`) and maps them to canonical behaviors. These should remain until serialized assets and code paths are migrated away from the legacy values.

## Suggested next pass

1. Open the large-class candidates in Unity/Rider and extract pure calculation or policy sections first, because those are easiest to test outside MonoBehaviour lifecycle methods.
2. For each low-reference MonoBehaviour candidate, verify with Unity's scene/prefab references before deletion.
3. For utility candidates, search for reflection, editor menu usage, and serialized type names before removal.
4. Migrate any serialized `NoteBehavior` legacy values to canonical behavior arrays before removing the obsolete aliases.
