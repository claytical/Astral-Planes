# PhaseStar Member Usage Audit (Step: policy consolidation)

| File | Member | Classification | Confidence | Rationale |
|---|---|---|---|---|
| PhaseStar.cs | `behaviorProfile`, `shardReadyThreshold` | Live | High | Read in runtime charge/ejection logic. |
| PhaseStar.cs | `superNodePrefab`, `soloVoice` | Live | High | Used by super-node spawn path. |
| PhaseStar.cs | `entryOffscreenMargin`, `entryArriveThreshold`, `entryFadeInSeconds`, `entryDriftSeconds`, `entryDriftSpeedMul`, `entrySettleInset` | Dead candidate (confirmed) | High | No runtime reads in code; removed in this pass. |
| PhaseStar.cs | `_previewInitialized`, `_previewVisual`, `_previewVisualB` | Live | High | Used by preview ring build/update scale paths. |
| PhaseStar.cs | `_awaitingCollectableClearSinceLoop`, `_awaitingCollectableClearSinceDsp` | Live | High | Used in loop-boundary timeout recovery. |
| PhaseStar.cs | `_burstOffScreen` | Live | High | Burst hide/recovery gate across state transitions. |
| PhaseStar.cs | `_bubbleActive`, `_bubbleCenterWorld`, `_bubbleRadiusWorld`, `s_bubble*` mirrors | Live | High | Owned by `SetSafetyBubbleState` for local + static bubble queries. |
| PhaseStar.cs | `bubbleTint`, `bubbleShardInnerTint` | Inspector-only | Medium | Visual tuning inputs for bubble render path. |
| PhaseStar.cs | `_cachedIsSuperNode` | Live | Medium | Cached selection for node type decision. |
| PhaseStarMotion2D.cs | `bounds`, `avoidance`, `steering`, `navBlend` and nested fields | Live | High | Read in FixedUpdate steering/containment logic. |
| PhaseStarVisuals2D.cs | `dim*`, `rejectFlash*`, `bubble*`, `dualMaxSeparationDeg`, `dimShardTint` | Live | High | Read directly by visual update methods. |
| PhaseStarCravingNavigator.cs | `retargetOnTargetLost`, timers/weights | Live | High | Used in update/replan and target validation paths. |
| PhaseStarStateController.cs | policy methods | Live | High | Called from arming/disarming policy call sites. |
| PhaseStarBurstCoordinator.cs | burst state methods | Live | High | Used to enter/exit burst-hidden mode. |

## Notes
- Prefab/scene override verification is still required before deleting any serialized member marked Inspector-only.
- This pass consolidates gating ownership/burst authority and removes confirmed dead fields that had no runtime reads.
