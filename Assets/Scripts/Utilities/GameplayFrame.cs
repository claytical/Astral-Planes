using UnityEngine;

/// <summary>
/// The gameplay view rectangle: what the main camera shows when it isn't zoomed.
///
/// The camera zooms for presentation (CameraZoomDirector: record intro, ring completions), but
/// gameplay must not notice — star/note/node containment, off-screen cleanup, vehicle recovery and
/// grid↔world mapping all clamp to "the screen". Those read this frame instead of Camera.main.
/// Visual-only conversions (tethers from UI to world, etc.) keep using the live camera so they
/// line up with what is drawn.
/// </summary>
public static class GameplayFrame
{
    /// <summary>The main camera, or null.</summary>
    public static Camera Camera => Camera.main;

    /// <summary>True when a camera (and so a frame) exists.</summary>
    public static bool IsValid => Camera.main != null;

    private static bool TryHome(out Vector3 center, out float halfH, out float aspect)
    {
        var cam = Camera.main;
        center = default; halfH = 0f; aspect = 1f;
        if (cam == null) return false;
        aspect = cam.aspect;
        var zoom = cam.GetComponent<CameraZoomDirector>();
        if (zoom != null && zoom.HasHome)
        {
            center = zoom.HomePosition;
            halfH  = zoom.HomeHalfHeight;
        }
        else
        {
            center = cam.transform.position;
            halfH  = cam.orthographicSize;
        }
        return true;
    }

    /// <summary>Half the gameplay frame's height in world units.</summary>
    public static float HalfHeight => TryHome(out _, out float h, out _) ? h : 0f;

    /// <summary>Half the gameplay frame's width in world units.</summary>
    public static float HalfWidth => TryHome(out _, out float h, out float a) ? h * a : 0f;

    /// <summary>Centre of the gameplay frame (camera's home position).</summary>
    public static Vector2 Center => TryHome(out var c, out _, out _) ? (Vector2)c : Vector2.zero;

    /// <summary>Orthographic ViewportToWorldPoint against the unzoomed frame (z = 0).</summary>
    public static Vector3 ViewportToWorld(Vector2 viewport)
    {
        if (!TryHome(out var c, out float h, out float a)) return Vector3.zero;
        float w = h * a;
        return new Vector3(c.x - w + viewport.x * 2f * w, c.y - h + viewport.y * 2f * h, 0f);
    }

    /// <summary>Orthographic WorldToViewportPoint against the unzoomed frame (z = 1, i.e. in front).</summary>
    public static Vector3 WorldToViewport(Vector3 world)
    {
        if (!TryHome(out var c, out float h, out float a) || h <= 0f) return new Vector3(0.5f, 0.5f, 1f);
        float w = h * a;
        return new Vector3((world.x - (c.x - w)) / (2f * w), (world.y - (c.y - h)) / (2f * h), 1f);
    }
}
