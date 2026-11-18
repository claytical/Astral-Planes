# PhaseStar Ownership Boundaries

Updated 2026-11-17. See ECOSYSTEM.md (PhaseStar, Star Maze Navigation & Vines) for behaviour; this note only maps ownership.

- `PhaseStar` (+ partials `.ArmingState`, `.DormantState`, `.ZapState`, `.EjectionLogic`, `.PokeFeedback`, `.SuperNodeTell`, `.VisualUpdate`, `.RoleResolution`, `.PreviewShard`, `.Diagnostics`) is orchestration-first: lifecycle sequencing, cross-component wiring, and top-level gameplay transitions.
  - `.PokeFeedback` owns rejected-poke answers (NotReady head-shake) and the sleep state.
  - `.SuperNodeTell` owns the role-striped ring shown when the next ejection would be a SuperNode.
- `PhaseStarStateController` owns gate policy decisions for arming/disarming eligibility (single decision path for global gate checks).
- `PhaseStarBurstCoordinator` owns burst-hidden transitions (enter + exit), reducing direct `_burstOffScreen` mutation spread.
- `PhaseStarMotion2D` owns movement integration, screen containment and the per-mode maze steering (`StarNavigationParams` / `PhaseStarNavMode` on `MazePatternConfig`).
- `PhaseStarCravingNavigator` owns target acquisition and steering intent for the star's attuned `MusicalRole`. It paths through the maze with `PhaseStarGridPathfinder` (multi-goal BFS, nearest by path) in CorridorFollow / RingOrbit modes.
- `PhaseStarDustAffect` (+ `.Vine`, `.TargetAcquisition`, `.TentacleLifecycle`, `.LineRendering`, `.Gradients`) owns the drain tentacles: living vines routed around dust by `TentacleVinePlanner` (`TentacleVineRoute.cs`), with the root re-pinned to the star each tick.
- `PhaseStarVisuals2D` owns visual presentation states (bright/dim/hidden/sleeping/dual-diamond rendering, not-ready shake).

The old safety bubble is gone; no bubble state remains in the visuals.

Tuning: all shared numbers live on `PhaseStarConfig` (Resources/Config/PhaseStarConfig.asset), read by each component through private properties. Per-star-type values are on `PhaseStarBehaviorProfile`; per-maze steering on `MazePatternConfig.StarNavigationParams`.
