using UnityEngine;

public static class SuperNodeSteeringMath
{
    public static float EdgeFactor01(Vector2 position, Rect bounds, float softnessWorld)
    {
        float dx = Mathf.Min(position.x - bounds.xMin, bounds.xMax - position.x);
        float dy = Mathf.Min(position.y - bounds.yMin, bounds.yMax - position.y);
        float nearestEdgeDistance = Mathf.Min(dx, dy);

        return Mathf.Clamp01(1f - (nearestEdgeDistance / Mathf.Max(0.0001f, softnessWorld)));
    }

    public static void ClampToBounds(Rigidbody2D rigidbody, Rect bounds)
    {
        Vector2 position = rigidbody.position;
        bool clamped = false;

        float x = position.x;
        float y = position.y;

        if (x < bounds.xMin) { x = bounds.xMin; clamped = true; }
        else if (x > bounds.xMax) { x = bounds.xMax; clamped = true; }

        if (y < bounds.yMin) { y = bounds.yMin; clamped = true; }
        else if (y > bounds.yMax) { y = bounds.yMax; clamped = true; }

        if (clamped)
        {
            rigidbody.position = new Vector2(x, y);
            rigidbody.linearVelocity *= 0.65f;
        }
    }
}
