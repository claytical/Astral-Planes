using UnityEngine;

[DisallowMultipleComponent]
public class SuperNodeShard : MonoBehaviour
{
    [SerializeField] private SpriteRenderer _renderer;

    [SerializeField] private float selfRotationDegPerSec = 180f;
    [SerializeField] private float highlightScale        = 1.25f;
    [SerializeField] private float dimmedAlpha           = 0.35f;
    [SerializeField] private float highlightLerpSpeed    = 8f;

    public InstrumentTrack AssignedTrack { get; private set; }

    private bool    _isHighlighted;
    private Vector3 _baseScale;

    private void Awake()
    {
        _baseScale = transform.localScale;
    }

    private void Reset()
    {
        _renderer = GetComponent<SpriteRenderer>();
    }

    public void Setup(InstrumentTrack track, float initialAngleRad)
    {
        AssignedTrack = track;

        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.Euler(0f, 0f, initialAngleRad * Mathf.Rad2Deg);

        if (track != null && _renderer != null)
            _renderer.color = track.trackColor;

        SetHighlighted(false, instant: true);
    }

    public void SetHighlighted(bool highlighted, bool instant = false)
    {
        _isHighlighted = highlighted;

        if (instant)
        {
            if (_renderer != null)
            {
                var c = _renderer.color;
                c.a = highlighted ? 1f : dimmedAlpha;
                _renderer.color = c;
            }
            transform.localScale = _baseScale * (highlighted ? highlightScale : 1f);
        }
    }

    private void Update()
    {
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
        transform.Rotate(0f, 0f, selfRotationDegPerSec * Time.fixedDeltaTime);
    }
}
