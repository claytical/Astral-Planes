# MineNode ↔ CosmicDust jump-risk audit

Date: 2026-05-02

This audit flags code paths where MineNode can be repositioned or receive abrupt velocity changes during/around dust contact, which can appear as a sudden jump.

## Flagged instances

1. **Dust-contact escape impulse on MineNode**
   - `MineNodeDustInteractor.FixedUpdate()` adds force away from dust whenever MineNode is in a dust cell.
   - `AddForce(escapeDir.normalized * escapePushForce, ForceMode2D.Force)` can create a sharp displacement when overlap starts in a constrained corridor.
   - File: `Assets/Scripts/Gameplay/Mining/MineNodeDustInteractor.cs` (lines 120-130).

2. **Dust-contact velocity projection (hard component cancellation)**
   - During dust contact, MineNode velocity is directly modified by subtracting inward wall velocity component.
   - `_rb.linearVelocity -= wallNormal * intoWallVel` is an instantaneous velocity rewrite and can look like a snap when collision normals change quickly.
   - File: `Assets/Scripts/Gameplay/Mining/MineNodeDustInteractor.cs` (lines 134-137).

3. **Swept containment direct position correction in dust interactor**
   - On sampled dust intersection, MineNode is depenetrated with a direct position write.
   - `_rb.position += Vector2.ClampMagnitude(correction, maxCorrectionPerTick)` is a positional teleport up to `maxCorrectionPerTick` per physics step.
   - File: `Assets/Scripts/Gameplay/Mining/MineNodeDustInteractor.cs` (lines 235-237).

4. **Swept containment velocity overwrite in dust interactor**
   - Same containment path projects MineNode velocity onto tangent or zeros it.
   - `_rb.linearVelocity = tangent * tangentialSpeed` / `_rb.linearVelocity = Vector2.zero` can produce abrupt movement direction/speed changes at collision samples.
   - File: `Assets/Scripts/Gameplay/Mining/MineNodeDustInteractor.cs` (lines 223-233).

5. **MineNode core containment velocity rewrite + forced depenetration force**
   - MineNode's own `EnforceGridContainment()` rewrites velocity to tangent or zero when a blocked (dust) cell is found.
   - Then it applies a computed depenetration force from desired depenetration velocity.
   - Combined sudden velocity overwrite + strong force can read as a jump under high leak distance.
   - File: `Assets/Scripts/Gameplay/Mining/MineNode.cs` (lines 330-342, 374-378).

6. **Boundary bounce used when MineNode tries to leave through blocked dust boundary**
   - If a horizontal exit is blocked by dust, `BoundaryWrap` bounces MineNode instead of allowing escape.
   - Bounce path directly sets position to boundary edge and flips velocity sign/magnitude component.
   - This can present as a fast positional snap near dust-heavy boundaries.
   - File: `Assets/Scripts/UI/BoundaryWrap.cs` (lines 83-85, 276-307).

## Not flagged

- `CosmicDust.Collision` currently handles `Vehicle` contact only; it does not directly move MineNode.
  - File: `Assets/Scripts/Dust/CosmicDust.Collision.cs` (lines 39-46).
