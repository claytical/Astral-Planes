# Cosmic Plasma (metaball dust look) — status: PAUSED, not working, renderer feature disabled

Experiment (2026-11-10/11): replace the CosmicDust look with a "grid of bubbly, merged plasma". The live dust prefab
(`Cosmic Potential.prefab`) is untouched and still works; all collision is correct on it. The plasma variant is **not
rendering correctly** and work on it was stopped. Nothing here is wired into the scene by default
(`CosmicDustGenerator.dustPrefab` in `GeneratedTrack.unity` points back at `Cosmic Potential`).

None of this code was ever verified end-to-end by the author of these notes; Unity was driven by the user.

## What exists (all committed)

**Renderer feature disabled 2026-11-17** (`m_Active: 0` on the referenced `CosmicPlasmaFeature` in `2D URP Renderer_Renderer.asset`). It was still active, running a half-res field pass plus a fullscreen composite every frame with nothing drawing into it — flagged as a GPU cost in `CODE_HEALTH_AUDIT.md`. The asset also holds a second, unreferenced `CosmicPlasmaFeature` sub-asset. Re-tick the feature to resume.
| File | Purpose |
|---|---|
| `Assets/Shaders/CosmicPlasmaParticle.shader` | Particle shader, single pass `LightMode "PlasmaField"` (never drawn by the 2D renderer). Writes `rgb = tint*dim*d`, `a = d` (density) with additive blend. Kernel `(1-r²)²`, vertex alpha < ~0.1–0.2 gated to zero (ghost/collider-off cells). |
| `Assets/Shaders/CosmicPlasmaComposite.shader` | Fullscreen composite: blur, threshold -> surface, tint/rim/swirl shading, tone-map, debug views. |
| `Assets/Scripts/Dust/CosmicPlasmaFeature.cs` | URP 2D `ScriptableRendererFeature` (Render Graph). Field pass draws the dust layer's `PlasmaField` renderers into a half-res RGBA16F buffer; composite pass blends over camera color. Has `debugView` (SolidTest / FieldDensity / FieldColor) and one-time log lines. Added to `Assets/Settings/2D URP Renderer_Renderer.asset` (now inactive, see above). |
| `Assets/Editor/CosmicPlasmaSetup.cs` | Menu `Tools/Astral Planes/Dust/Build Cosmic Plasma Variant`: builds `Assets/Materials/CosmicPlasma.mat` and the variant prefab `Assets/Prefabs/Interactable Objects/Cosmic Plasma.prefab`. |
| `Assets/Scripts/Dust/CosmicDust.Helpers.cs` / `CosmicDust.SpriteScale.cs` | New opt-in `visual.particleCellFraction` (0 = legacy behavior) + `ApplyCellRelativeParticleSizing()` sizing the particle in cell units, emitter forced to the cell center. Harmless to other prefabs. |

## Design (current)
- One stationary particle per cell (`maxParticles 1`, lifetime 100000, `startSpeed 0`, `gravityModifier 0`, shape radius 0,
  AlwaysSimulate), kernel diameter = `particleCellFraction` (1.8) x cell tile. Threshold ~0.45 should put an isolated cell's
  edge on the cell boundary and fuse neighbors.
- Alpha is the game's "collider on/off" signal: `SetTerrainColliderEnabled(false)` mirrors alpha to `colliderDisabledAlpha`
  (0.08); drained dust goes to ~0.05. Plasma must not draw those cells.
- Nose squash/offset (`noseCompression`) disabled on the variant (it deformed the surface away from the collider).

## Things learned (useful if this is resumed)
- Unity 6000.5 / URP 17.5: Render Graph only. Custom shader includes must include URP `Core.hlsl` **before** `Blit.hlsl`
  (missing `TEXTURE2D_X` compile error silently dropped the composite draw; the Frame Debugger then shows no event).
- Debug ladder that worked: SolidTest (composite reaches screen) -> FieldDensity (particles reach the field).
- The base prefab has `startSpeed 0.1`, `gravityModifier 0.001`: with a never-dying particle it drifted cells away from the
  collider. Fixed in the variant build, but any "long-lived particle" design must keep this in mind.
- An earlier random-multi-particle-per-cell design cannot line up with per-cell colliders (random sparse density).
- A runtime `shape.radius = 0.25*cell` in `ApplyCellRelativeParticleSizing` jittered the one particle off-center
  (removed).
- Color: boosting dark tints linearly just clips to white; a soft tone-map is in place. Dust tints are mostly dark greys.

## Last known symptoms (before stopping)
- Plasma did not render the metaballs at all in the latest run (Gizmos on, plasma variant).
- Previous run: top grid row missing in Game view (appeared in Scene view when parent moved -0.25 y); a 6–8 cell gap in
  the plasma with colliders still active; passable cells that still showed plasma (addressed by the alpha gate but not
  re-verified); collider/visual disagreement varied by position and direction.
- Root cause for the last failures is **not identified**.

## If resuming
1. Re-enable the feature in the renderer asset (it is now off) and `dustPrefab` points at `Cosmic Plasma`.
2. Re-run the setup menu (prefab changes only apply on rebuild), then use `debugView` SolidTest -> FieldDensity.
3. Verify with Game view + Physics2D "Always Show Colliders" so plasma and colliders are compared in the same camera.
4. Cheaper alternative worth weighing: keep the original particle dust and only change its material/size for a bubbly
   look; true metaball fusion is the part that turned out fragile.

## To remove entirely
Delete the shader, feature, editor-setup, material and prefab files in the table above, and remove both Cosmic Plasma
Feature sub-assets from `2D URP Renderer_Renderer.asset`. **Do not** `git checkout` `CosmicDust.Helpers.cs` /
`CosmicDust.SpriteScale.cs`: Cosmic Blocks (`CosmicDust.BlockForm.cs`) now build on that sizing path (`_blockSizeMul` in
`ApplyCellRelativeParticleSizing`). Leave `particleCellFraction` in place (0 = legacy behaviour).
