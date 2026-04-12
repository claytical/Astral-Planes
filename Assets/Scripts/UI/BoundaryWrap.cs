using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Placed on each boundary trigger collider by Boundaries.cs at runtime.
/// When a Vehicle enters the trigger, it teleports to the inner edge of the opposite boundary.
/// </summary>
public class BoundaryWrap : MonoBehaviour
{
    public enum WrapAxis { Horizontal, Vertical }

    public WrapAxis axis;
    public BoxCollider2D oppositeBoundary;
    public GameObject warpPrefab;

    // Per-rigidbody cooldown shared across ALL BoundaryWrap instances.
    // Prevents ping-pong: right boundary wraps the vehicle, left boundary sees the
    // cooldown and skips, so the vehicle doesn't bounce straight back.
    private const float WrapCooldown = 0.3f;
    private static readonly Dictionary<Rigidbody2D, float> _lastWrapTime = new();

    private void OnTriggerEnter2D(Collider2D other)
    {
        var rb = other.attachedRigidbody;
        if (rb == null) return;

        var vehicle = rb.GetComponentInParent<Vehicle>();
        if (vehicle != null)
        {
            WrapVehicle(rb, vehicle);
            Instantiate(warpPrefab, vehicle.transform.position, Quaternion.identity);
        }
        else
            BounceRigidbody(rb);
    }

    private void WrapVehicle(Rigidbody2D rb, Vehicle vehicle)
    {
        if (oppositeBoundary == null) return;

        // Cooldown: skip if this vehicle just wrapped (avoids immediate re-trigger).
        float now = Time.time;
        if (_lastWrapTime.TryGetValue(rb, out float last) && now - last < WrapCooldown)
            return;
        _lastWrapTime[rb] = now;

        Vector2 pos = rb.position;

        if (axis == WrapAxis.Horizontal)
        {
            float cx = oppositeBoundary.transform.position.x;
            pos.x = cx - Mathf.Sign(cx) * oppositeBoundary.size.x * 0.5f;
        }
        else
        {
            float cy = oppositeBoundary.transform.position.y;
            pos.y = cy - Mathf.Sign(cy) * oppositeBoundary.size.y * 0.5f;
        }

        rb.position = pos;
        vehicle.ClearTrailForWrap();
    }

    private void BounceRigidbody(Rigidbody2D rb)
    {
        // Reflect velocity and push the object back to the inner edge of this boundary.
        var col = GetComponent<BoxCollider2D>();
        if (col == null) return;

        Vector2 pos = rb.position;
        Vector2 vel = rb.linearVelocity;

        if (axis == WrapAxis.Horizontal)
        {
            float cx        = col.transform.position.x;
            float innerEdge = cx - Mathf.Sign(cx) * col.size.x * 0.5f;
            pos.x = innerEdge;
            // Reflect X and ensure the velocity points away from this boundary.
            vel.x = Mathf.Abs(vel.x) * (cx < 0f ? 1f : -1f);
        }
        else
        {
            float cy        = col.transform.position.y;
            float innerEdge = cy - Mathf.Sign(cy) * col.size.y * 0.5f;
            pos.y = innerEdge;
            vel.y = Mathf.Abs(vel.y) * (cy < 0f ? 1f : -1f);
        }

        rb.position       = pos;
        rb.linearVelocity = vel;

        // MineNode steers by _carveDir, not velocity — reflect it so the node turns away.
        var mine = rb.GetComponent<MineNode>();
        if (mine != null)
            mine.ReflectCarveDir(reflectX: axis == WrapAxis.Horizontal);
    }
}
