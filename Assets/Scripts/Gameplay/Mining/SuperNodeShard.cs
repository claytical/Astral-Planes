using UnityEngine;

[DisallowMultipleComponent]
public class SuperNodeShard : MonoBehaviour
{
    [SerializeField] private SpriteRenderer _renderer;
    [SerializeField] private Explode _explode;

    [SerializeField] private float spawnGraceSeconds     = 0.15f;
    [SerializeField] private float minImpactSpeed        = 2.0f;
    [SerializeField] private float selfRotationDegPerSec = 180f;

    [SerializeField] private float highlightScale     = 1.25f;
    [SerializeField] private float dimmedAlpha        = 0.35f;
    [SerializeField] private float highlightLerpSpeed = 8f;

    public InstrumentTrack AssignedTrack { get; private set; }
    public bool IsCollected              { get; private set; }
    public System.Action<SuperNodeShard> OnHit;

    private Rigidbody2D _rb;
    private Collider2D  _collider;
    private float       _spawnTime;
    private bool        _isHighlighted;
    private Vector3     _baseScale;

    private void Awake()
    {
        if (_rb == null) _rb = GetComponent<Rigidbody2D>();
        _collider  = GetComponent<Collider2D>();
        _baseScale = transform.localScale;
        _spawnTime = Time.time;
    }

    private void Reset()
    {
        _renderer = GetComponent<SpriteRenderer>();
        _rb       = GetComponent<Rigidbody2D>();
        _explode  = GetComponent<Explode>();
    }

    public void Setup(InstrumentTrack track, float initialAngleRad)
    {
        AssignedTrack = track;
        _spawnTime    = Time.time;

        // Stagger each shard's Z-rotation so the layered diamonds form a star/pinwheel.
        // Local position stays (0,0,0) — shards sit at the SuperNode center, not in orbit.
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.Euler(0f, 0f, initialAngleRad * Mathf.Rad2Deg);

        if (track != null)
        {
            if (_renderer != null)
                _renderer.color = track.trackColor;
            _explode?.SetTint(track.trackColor);
        }

        SetHighlighted(false, instant: true);
    }

    public void SetHighlighted(bool highlighted, bool instant = false)
    {
        _isHighlighted = highlighted;
        if (_collider != null) _collider.enabled = highlighted;

        if (instant)
        {
            float a = highlighted ? 1f : dimmedAlpha;
            if (_renderer != null)
            {
                var c = _renderer.color;
                c.a = a;
                _renderer.color = c;
            }
            float s = highlighted ? highlightScale : 1f;
            transform.localScale = _baseScale * s;
        }
    }

    private void Update()
    {
        if (IsCollected) return;

        float targetAlpha = _isHighlighted ? 1f : dimmedAlpha;
        if (_renderer != null)
        {
            var c = _renderer.color;
            c.a = Mathf.Lerp(c.a, targetAlpha, highlightLerpSpeed * Time.deltaTime);
            _renderer.color = c;
        }

        float targetScale = _isHighlighted ? highlightScale : 1f;
        transform.localScale = Vector3.Lerp(transform.localScale, _baseScale * targetScale,
                                            highlightLerpSpeed * Time.deltaTime);
    }

    private void FixedUpdate()
    {
        if (IsCollected) return;
        transform.Rotate(0f, 0f, selfRotationDegPerSec * Time.fixedDeltaTime);
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
        SetHighlighted(false, instant: true);
        _explode?.Permanent();
    }
}
