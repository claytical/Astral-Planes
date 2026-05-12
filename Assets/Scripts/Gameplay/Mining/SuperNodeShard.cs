using UnityEngine;

[DisallowMultipleComponent]
public class SuperNodeShard : MonoBehaviour
{
    [SerializeField] private SpriteRenderer _renderer;
    [SerializeField] private Explode _explode;

    [SerializeField] private float spawnGraceSeconds  = 0.15f;
    [SerializeField] private float minImpactSpeed     = 2.0f;
    [SerializeField] private float orbitSpeedDegPerSec = 45f;
    [SerializeField] private float orbitRadius        = 1.5f;

    public InstrumentTrack AssignedTrack { get; private set; }
    public bool IsCollected              { get; private set; }
    public System.Action<SuperNodeShard> OnHit;

    private Rigidbody2D _rb;
    private Transform   _centerTransform;
    private float       _orbitAngleRad;
    private float       _spawnTime;

    private void Awake()
    {
        if (_rb == null) _rb = GetComponent<Rigidbody2D>();
        _spawnTime = Time.time;
    }

    private void Reset()
    {
        _renderer = GetComponent<SpriteRenderer>();
        _rb       = GetComponent<Rigidbody2D>();
        _explode  = GetComponent<Explode>();
    }

    public void Setup(InstrumentTrack track, Transform center, float initialAngleRad)
    {
        AssignedTrack    = track;
        _centerTransform = center;
        _orbitAngleRad   = initialAngleRad;
        _spawnTime       = Time.time;

        // Place immediately at orbit position so the shard isn't stuck at center on spawn.
        Vector2 startPos = (Vector2)center.position
                           + new Vector2(Mathf.Cos(initialAngleRad), Mathf.Sin(initialAngleRad)) * orbitRadius;
        transform.position = startPos;
        if (_rb != null) _rb.position = startPos;

        if (track != null)
        {
            // Idle visual: complementary hue of the track color.
            Color.RGBToHSV(track.trackColor, out float h, out float s, out float v);
            if (_renderer != null)
                _renderer.color = Color.HSVToRGB((h + 0.5f) % 1f, s, v);

            // Explosion tinted with the natural track color.
            _explode?.SetTint(track.trackColor);
        }
    }

    private void FixedUpdate()
    {
        if (IsCollected || _centerTransform == null) return;

        _orbitAngleRad += orbitSpeedDegPerSec * Mathf.Deg2Rad * Time.fixedDeltaTime;

        Vector2 worldPos = (Vector2)_centerTransform.position
                           + new Vector2(Mathf.Cos(_orbitAngleRad), Mathf.Sin(_orbitAngleRad)) * orbitRadius;
        if (_rb != null)
            _rb.MovePosition(worldPos);
        else
            transform.position = worldPos;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (IsCollected) return;
        if (Time.time - _spawnTime < spawnGraceSeconds) return;

        var vehicle = other.GetComponentInParent<Vehicle>();
        if (vehicle == null) return;

        var vrb = vehicle.GetComponent<Rigidbody2D>();
        if (vrb == null || vrb.linearVelocity.magnitude < minImpactSpeed) return;

        MarkCollected();
        OnHit?.Invoke(this);
    }

    public void MarkCollected()
    {
        if (IsCollected) return;
        IsCollected = true;
        _explode?.Permanent();
    }
}
