using UnityEngine;

/// <summary>
/// Single source of truth for one path-choice exit's world-space geometry. The capture trigger,
/// the particle lane, the carry-out target and the debug gizmo all read this, so the lit lane,
/// the collider and the line a vehicle is carried out along can never disagree.
///
/// The lane is a rectangle: OpeningWidth wide (along Tangent), CaptureDepth deep, starting at
/// the physical wall face (Center) and running inward.
/// </summary>
public readonly struct PathExitGeometry
{
    public readonly BoundaryWrap.BoundarySide Side;
    /// <summary>Center of the opening on the physical wall face (the reachable boundary).</summary>
    public readonly Vector2 Center;
    /// <summary>Full width of the carved, passable opening along the wall.</summary>
    public readonly float OpeningWidth;
    /// <summary>How far the lane/trigger reaches inward from the wall face.</summary>
    public readonly float CaptureDepth;
    /// <summary>Distance from the wall face to the true screen edge along Outward. 0 for Left/Right
    /// (their walls ARE the screen edge); the UI-bar band height for Top/Bottom.</summary>
    public readonly float ScreenEdgeDistance;

    public readonly Vector2 Inward;
    public readonly Vector2 Outward;
    public readonly Vector2 Tangent;

    public bool HorizontalWall =>
        Side == BoundaryWrap.BoundarySide.Top || Side == BoundaryWrap.BoundarySide.Bottom;

    /// <summary>The true screen edge at this exit's position — where the black hole is drawn, so
    /// it reads as an opening beyond the grid. Trigger and carry-out still use <see cref="Center"/>.</summary>
    public Vector2 VisualCenter => Center + Outward * ScreenEdgeDistance;

    public Vector2 LaneInnerPoint => Center + Inward * CaptureDepth;

    public PathExitGeometry(BoundaryWrap.BoundarySide side, Vector2 center, float openingWidth, float captureDepth, float screenEdgeDistance = 0f)
    {
        ScreenEdgeDistance = Mathf.Max(0f, screenEdgeDistance);
        Side = side;
        Center = center;
        OpeningWidth = openingWidth;
        CaptureDepth = captureDepth;

        Inward = side switch
        {
            BoundaryWrap.BoundarySide.Left  => Vector2.right,
            BoundaryWrap.BoundarySide.Right => Vector2.left,
            BoundaryWrap.BoundarySide.Top   => Vector2.down,
            _ /* Bottom */                  => Vector2.up,
        };
        Outward = -Inward;
        Tangent = (side == BoundaryWrap.BoundarySide.Left || side == BoundaryWrap.BoundarySide.Right)
            ? Vector2.up
            : Vector2.right;
    }

    /// <summary>Signed distance along Tangent from <paramref name="p"/> to the lane center line
    /// (positive = the point must move toward +Tangent to be centered).</summary>
    public float OffsetToCenter(Vector2 p) => Vector2.Dot(Center - p, Tangent);
}
