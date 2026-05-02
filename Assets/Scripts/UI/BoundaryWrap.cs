using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Boundary trigger with intentional wrap behavior:
/// - Vehicles do NOT wrap immediately on entry
/// - Outward motion is resisted while inside the trigger
/// - Moving back into the play area is always free
/// - Wrap only occurs after sufficient penetration + outward speed
/// 
/// IMPORTANT:
/// We do NOT infer "left/right/top/bottom" from transform position,
/// because the bottom boundary may be aligned to UI deadspace rather than world origin.
/// Boundaries.cs must assign the explicit side.
/// </summary>
public class BoundaryWrap : MonoBehaviour
{
    public enum WrapAxis { Horizontal, Vertical }

    public enum BoundarySide
    {
        Left,
        Right,
        Top,
        Bottom
    }

    [Header("Setup")]
    public WrapAxis axis;
    public BoundarySide side;
    public BoxCollider2D oppositeBoundary;
    public GameObject warpPrefab;

    [Header("Vehicle Wrap Feel")]
    [Range(0.05f, 0.95f)]
    [Tooltip("How deep into the trigger the vehicle must get before wrap is allowed.")]
    public float commitDepth01 = 0.55f;

    [Tooltip("Minimum outward speed required to commit to a wrap.")]
    public float minCommitSpeed = 2.25f;

    [Tooltip("How strongly the boundary bleeds outward speed before wrap.")]
    public float outwardSpeedBleed = 18f;

    [Tooltip("Tiny inward bias while the ship is still pressing outward but not yet committed.")]
    public float inwardBiasSpeed = 1.25f;

    private const float WrapCooldown = 0.3f;
    private static readonly Dictionary<Rigidbody2D, float> _lastWrapTime = new();

    private BoxCollider2D _self;

    private void Awake()
    {
        _self = GetComponent<BoxCollider2D>();
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        var rb = other.attachedRigidbody;
        if (rb == null) return;

        var vehicle = rb.GetComponentInParent<Vehicle>();
        if (vehicle != null)
            return; // vehicles handled continuously in OnTriggerStay2D

        var mine = rb.GetComponent<MineNode>();
        if (mine != null) { HandleMineNodeBoundary(mine, rb); return; }

        BounceRigidbody(rb);
    }

    private void HandleMineNodeBoundary(MineNode mine, Rigidbody2D rb)
    {
        bool isHorizontal = side == BoundarySide.Left || side == BoundarySide.Right;

        // Top/Bottom: always hard bounce
        if (!isHorizontal) { BounceRigidbody(rb); return; }

        // If there is a dust cell at the boundary position, deny exit regardless of state —
        // the node hit a solid border cell and must find a gap opening to escape.
        var dt = mine.DrumTrack;
        if (IsMineExitBlockedByDust(dt, rb.position))
        {
            BounceRigidbody(rb);
            return;
        }

        // Left/Right + Drifting: bounce to keep it in play
        if (mine.State == MineNodeState.Drifting) { BounceRigidbody(rb); return; }

        // Left/Right + Fleeing + no dust at this position (gap): let it escape
        mine.HandleEscape();
    }

    private bool IsMineExitBlockedByDust(DrumTrack dt, Vector2 worldPos)
    {
        if (dt == null) return false;

        // Sample slightly inward and outward from the current position.
        // Using only rb.position can miss a blocking boundary cell because the trigger
        // can fire while the rigidbody center is still in a neighboring open cell.
        Vector2 outward = side switch
        {
            BoundarySide.Left => Vector2.left,
            BoundarySide.Right => Vector2.right,
            BoundarySide.Top => Vector2.up,
            BoundarySide.Bottom => Vector2.down,
            _ => Vector2.zero,
        };

        const float sampleOffset = 0.35f;
        Vector2 inwardSample = worldPos - outward * sampleOffset;
        Vector2 outwardSample = worldPos + outward * sampleOffset;

        return dt.HasDustAt(dt.CellOf(worldPos))
            || dt.HasDustAt(dt.CellOf(inwardSample))
            || dt.HasDustAt(dt.CellOf(outwardSample));
    }

    private void OnTriggerStay2D(Collider2D other)
    {
        var rb = other.attachedRigidbody;
        if (rb == null) return;

        var vehicle = rb.GetComponentInParent<Vehicle>();
        if (vehicle == null) return;

        HandleVehicleMembrane(rb, vehicle);
    }

    private void HandleVehicleMembrane(Rigidbody2D rb, Vehicle vehicle)
    {
        if (_self == null || oppositeBoundary == null) return;

        float now = Time.time;
        if (_lastWrapTime.TryGetValue(rb, out float last) && now - last < WrapCooldown)
            return;

        GetAxisData(
            out float posAlong,
            out float velAlong,
            out float innerEdge,
            out float thickness,
            out float outwardSign,
            rb
        );

        thickness = Mathf.Max(0.0001f, thickness);

        // positive penetration means deeper into the wrap trigger toward teleport
        float penetration = (posAlong - innerEdge) * outwardSign;
        float depth01 = Mathf.Clamp01(penetration / thickness);

        // positive means still pushing outward toward teleport
        float outwardSpeed = velAlong * outwardSign;

        bool committed =
            depth01 >= commitDepth01 &&
            outwardSpeed >= minCommitSpeed;

        if (committed)
        {
            _lastWrapTime[rb] = now;
            WrapVehicle(rb, vehicle);

            if (warpPrefab != null)
                Instantiate(warpPrefab, vehicle.transform.position, Quaternion.identity);

            return;
        }

        // If player is moving back into the arena, boundary should not interfere.
        if (outwardSpeed <= 0f)
            return;

        Vector2 vel = rb.linearVelocity;

        float newOutwardSpeed = Mathf.MoveTowards(
            outwardSpeed,
            0f,
            outwardSpeedBleed * Time.fixedDeltaTime
        );

        // Small inward bias only while still pressing outward.
        newOutwardSpeed = Mathf.MoveTowards(
            newOutwardSpeed,
            -inwardBiasSpeed,
            inwardBiasSpeed * Time.fixedDeltaTime
        );

        if (axis == WrapAxis.Horizontal)
            vel.x = newOutwardSpeed * outwardSign;
        else
            vel.y = newOutwardSpeed * outwardSign;

        rb.linearVelocity = vel;
    }

    private void GetAxisData(
        out float posAlong,
        out float velAlong,
        out float innerEdge,
        out float thickness,
        out float outwardSign,
        Rigidbody2D rb)
    {
        Vector2 pos = rb.position;
        Vector2 vel = rb.linearVelocity;

        if (axis == WrapAxis.Horizontal)
        {
            thickness = _self.size.x;
            posAlong = pos.x;
            velAlong = vel.x;

            if (side == BoundarySide.Left)
            {
                outwardSign = -1f;
                innerEdge = _self.bounds.max.x;
            }
            else // Right
            {
                outwardSign = 1f;
                innerEdge = _self.bounds.min.x;
            }
        }
        else
        {
            thickness = _self.size.y;
            posAlong = pos.y;
            velAlong = vel.y;

            if (side == BoundarySide.Bottom)
            {
                outwardSign = -1f;
                innerEdge = _self.bounds.max.y;
            }
            else // Top
            {
                outwardSign = 1f;
                innerEdge = _self.bounds.min.y;
            }
        }
    }

    private void WrapVehicle(Rigidbody2D rb, Vehicle vehicle)
    {
        if (oppositeBoundary == null) return;

        Vector2 pos = rb.position;

        switch (side)
        {
            case BoundarySide.Left:
                pos.x = oppositeBoundary.bounds.min.x;
                break;

            case BoundarySide.Right:
                pos.x = oppositeBoundary.bounds.max.x;
                break;

            case BoundarySide.Bottom:
                pos.y = oppositeBoundary.bounds.min.y;
                break;

            case BoundarySide.Top:
                pos.y = oppositeBoundary.bounds.max.y;
                break;
        }

        rb.position = pos;
        vehicle.ClearTrailForWrap();
    }

    private void BounceRigidbody(Rigidbody2D rb)
    {
        if (_self == null) return;

        Vector2 pos = rb.position;
        Vector2 vel = rb.linearVelocity;

        switch (side)
        {
            case BoundarySide.Left:
                pos.x = _self.bounds.max.x;
                vel.x = Mathf.Abs(vel.x);
                break;

            case BoundarySide.Right:
                pos.x = _self.bounds.min.x;
                vel.x = -Mathf.Abs(vel.x);
                break;

            case BoundarySide.Bottom:
                pos.y = _self.bounds.max.y;
                vel.y = Mathf.Abs(vel.y);
                break;

            case BoundarySide.Top:
                pos.y = _self.bounds.min.y;
                vel.y = -Mathf.Abs(vel.y);
                break;
        }

        rb.position = pos;
        rb.linearVelocity = vel;

        var mine = rb.GetComponent<MineNode>();
        if (mine != null)
            mine.ReflectCarveDir(reflectX: axis == WrapAxis.Horizontal);
    }
}