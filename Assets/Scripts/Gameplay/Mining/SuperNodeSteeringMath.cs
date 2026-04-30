using UnityEngine;

public static class SuperNodeSteeringMath
{
    public static Vector2 SteerTowards(Vector2 currentVel, Vector2 desiredVel, float turnRateDegPerSec, float accelerationUnitsPerSecondSq, float deltaTime)
    {
        float curSpeed = currentVel.magnitude;
        float desSpeed = desiredVel.magnitude;

        float speed = Mathf.MoveTowards(curSpeed, desSpeed, accelerationUnitsPerSecondSq * deltaTime);

        Vector2 curDir = curSpeed > 0.001f ? currentVel / curSpeed : (desSpeed > 0.001f ? desiredVel / desSpeed : Vector2.right);
        Vector2 desDir = desSpeed > 0.001f ? desiredVel / desSpeed : curDir;

        float maxRadians = turnRateDegPerSec * Mathf.Deg2Rad * deltaTime;
        Vector2 newDir = Vector3.RotateTowards(curDir, desDir, maxRadians, 0f);

        return newDir * speed;
    }

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
