using UnityEngine;

/// <summary>
/// Shared wall-face / screen-edge math for path-exit geometry, used both by the exit triggers
/// (PathChoiceCoordinator) and by the matching entry portal on the next maze's mirrored edge
/// (SceneFlowCoordinator), so the two can never disagree about where a boundary is.
/// </summary>
public static class PathExitGeometryFactory
{
    /// <summary>
    /// Builds geometry for an opening on <paramref name="side"/> whose along-wall position is that
    /// of <paramref name="alongWallWorld"/> (its normal-axis coordinate is ignored and snapped).
    /// </summary>
    public static PathExitGeometry Create(
        BoundaryWrap.BoundarySide side, Vector2 alongWallWorld, float openingWidth, float captureDepth)
    {
        Vector2 center = SnapToPhysicalBoundary(alongWallWorld, side);

        // Distance from the reachable wall face to the true screen edge: 0 on Left/Right, the
        // UI-bar band on Top/Bottom.
        Vector2 outward = SceneFlowCoordinator.OutwardDirection(side);
        float edgeDist = Mathf.Clamp(
            Vector2.Dot(SceneFlowCoordinator.OffscreenPointAlong(center, side, 0f) - center, outward), 0f, 20f);

        return new PathExitGeometry(side, center, openingWidth, captureDepth, edgeDist);
    }

    // The gap-cell average centers correctly along the boundary edge, but the boundary-normal
    // coordinate comes from DrumTrack's grid→world mapping, which insets top/bottom for UI
    // deadspace. Snap that one axis to the real wall collider's inner face instead, so the
    // trigger sits exactly where a vehicle can actually reach it — this MUST stay the true
    // collision wall (not the further-out screen edge) or the trigger becomes unreachable.
    public static Vector2 SnapToPhysicalBoundary(Vector2 center, BoundaryWrap.BoundarySide side)
    {
        var boundaries = Boundaries.Instance;
        if (boundaries == null) return center;

        BoxCollider2D wall = side switch
        {
            BoundaryWrap.BoundarySide.Left  => boundaries.leftBoundary,
            BoundaryWrap.BoundarySide.Right => boundaries.rightBoundary,
            BoundaryWrap.BoundarySide.Top   => boundaries.topBoundary,
            _ /* Bottom */                  => boundaries.bottomBoundary,
        };
        if (wall == null) return center;

        Bounds b = wall.bounds;
        switch (side)
        {
            case BoundaryWrap.BoundarySide.Left:  center.x = b.max.x; break;
            case BoundaryWrap.BoundarySide.Right: center.x = b.min.x; break;
            case BoundaryWrap.BoundarySide.Top:   center.y = b.min.y; break;
            default /* Bottom */:                 center.y = b.max.y; break;
        }
        return center;
    }
}
