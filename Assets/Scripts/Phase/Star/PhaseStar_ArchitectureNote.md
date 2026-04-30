# PhaseStar Ownership Boundaries (Post-refactor)

- `PhaseStar` is orchestration-first: lifecycle sequencing, cross-component wiring, and top-level gameplay transitions.
- `PhaseStarStateController` owns gate policy decisions for arming/disarming eligibility (single decision path for global gate checks).
- `PhaseStarBurstCoordinator` owns burst-hidden transitions (enter + exit), reducing direct `_burstOffScreen` mutation spread.
- `PhaseStarMotion2D` owns movement integration and screen containment.
- `PhaseStarVisuals2D` owns visual presentation states (bright/dim/hidden/bubble/dual-diamond rendering).
- `PhaseStarCravingNavigator` owns target acquisition and steering intent from dust hunger.

This keeps state-policy and burst-policy traceable to dedicated authorities while `PhaseStar` coordinates sequence/timing.
