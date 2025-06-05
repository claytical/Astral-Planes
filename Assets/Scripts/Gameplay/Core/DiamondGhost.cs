// DiamondGhost.cs — Updated

using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class DiamondGhost : MonoBehaviour
{
    public Transform visualOnly;
    private DarkStar parent;
    private Vector3 parentCenter = Vector3.zero;
    public int hitsUntilExplosion = 4;
    [Header("Launch Settings")]
    public float ejectionForce = 200f;
    public float torqueRange = 360f;

    public InstrumentTrack sourceTrack;
    public int midiNote;
    public float velocity;
    public int stepIndex;
    public int noteDuration;

    private MusicalRole role;
    private Rigidbody2D rb;
    private enum GhostState { Orbiting, Charging, Ejected }
    private GhostState state = GhostState.Orbiting;

    private Vector3 chargeTarget;
    private float chargeSpeed = 8f;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        rb.isKinematic = true;
        rb.gravityScale = 0f;
        rb.linearVelocity = Vector2.zero;
        parent = GetComponentInParent<DarkStar>();
    }

    void Update()
    {
        switch (state)
        {
            case GhostState.Charging:
                UpdateCharge();
                break;
            case GhostState.Ejected:
                if (role == MusicalRole.Groove) SeekNearestPlayer();
                break;
        }
    }

    public void InitializeWithColor(Color color, float angle, float radius, MusicalRole role = MusicalRole.Bass, Vector3? orbitCenter = null)
    {
        this.role = role;
        this.parentCenter = orbitCenter ?? Vector3.zero;

        if (visualOnly != null && visualOnly.TryGetComponent<DiamondVisual>(out var visual))
        {
            visual.mode = DiamondVisual.DiamondVisualMode.Ghost;
            visual.SetAssignedColor(color);
        }

        state = GhostState.Orbiting; // can skip orbit visuals for ejected ghosts
    }

    public void Eject(float powerMultiplier = 1f)
    {
        if (rb == null || parent == null) return;

        state = GhostState.Ejected;

        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.gravityScale = 0f;
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;
        rb.linearDamping = 0f;
        rb.angularDamping = 0.05f;

        Vector2 direction = (transform.position - parent.GetCenter()).normalized;
        direction = Quaternion.Euler(0, 0, Random.Range(-15f, 15f)) * direction;

        rb.AddForce(direction * ejectionForce * powerMultiplier, ForceMode2D.Impulse);
        rb.AddTorque(Random.Range(-torqueRange, torqueRange));
    }

    private void UpdateCharge()
    {
        Vector3 dir = (chargeTarget - transform.position).normalized;
        rb.linearVelocity = dir * chargeSpeed;
    }

    private void SeekNearestPlayer()
    {
        Transform target = FindNearestPlayer();
        if (target == null) return;

        Vector3 direction = (target.position - transform.position).normalized;
        transform.position += direction * 6f * Time.deltaTime;
    }

    private Transform FindNearestPlayer()
    {
        float closest = float.MaxValue;
        Transform nearest = null;
        foreach (var player in GameFlowManager.Instance.localPlayers)
        {
            float d = Vector3.Distance(transform.position, player.transform.position);
            if (d < closest)
            {
                closest = d;
                nearest = player.transform;
            }
        }
        return nearest;
    }

    public void SetTarget(Vector3 target, float speed)
    {
        state = GhostState.Charging;
        chargeTarget = target;
        chargeSpeed = speed;

        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.linearVelocity = Vector2.zero;

        PlayDarkNote();
    }

    void PlayDarkNote()
    {
        sourceTrack?.PlayDarkNote(midiNote, noteDuration, velocity);
    }

    void OnCollisionEnter2D(Collision2D collision)
    {
        PlayDarkNote();
        if (collision.gameObject.TryGetComponent<HexagonShield>(out var shield))
        {
            shield.BreakHexagon(CollectionEffectType.MazeToxic);            
        }
        else if (collision.gameObject.TryGetComponent<Vehicle>(out var vehicle))
        {
            vehicle.ConsumeEnergy(5f);
            TryExplode();
        }
        else
        {
            TryExplode();
        }

        Destroy(gameObject);
    }

    public void TryExplode()
    {
        if (TryGetComponent<Explode>(out var explode)) explode.Permanent();
    }

    public void DestroySelf() => Destroy(gameObject);
}
