using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class SuperNode : MonoBehaviour
{
    [SerializeField] private SoloVoice soloVoice;
    [SerializeField] public DrumTrack drumTrack;
    private InstrumentTrack _track;
    [SerializeField] private float spawnCollisionGraceSeconds = 0.15f;
    [SerializeField] private float minImpactSpeed = 2.0f;   // tune
    [SerializeField] private bool despawnOnNextBoundary = true;

    public System.Action OnResolved;

    private float _spawnTime;
    private bool _sawFirstBoundary;
    private bool _triggered;
    private bool _resolvedFired;

    private void Awake()
    {
        _spawnTime = Time.time;

        if (!soloVoice) soloVoice = FindAnyObjectByType<SoloVoice>();

        // Late bind if not inspector-wired
        if (drumTrack == null)
        {
            var gfm = GameFlowManager.Instance;
            if (gfm != null && gfm.activeDrumTrack != null)
                drumTrack = gfm.activeDrumTrack;
            else
                drumTrack = FindAnyObjectByType<DrumTrack>();
        }
    }
    public void Initialize(SoloVoice sv, DrumTrack drum, InstrumentTrack track = null)
    {
        soloVoice = sv;
        drumTrack = drum;
        _track    = track;
    }
    private void OnEnable()
    {
        _spawnTime = Time.time;
        TrySubscribeToBoundary();
    }
    private void OnDisable()
    {
        if (drumTrack != null)
            drumTrack.OnLoopBoundary -= OnLoopBoundary;
    }
    private void FireResolvedOnce()
    {
        if (_resolvedFired) return;
        _resolvedFired = true;
        OnResolved?.Invoke();
    }

    private void TrySubscribeToBoundary()
    {
        if (drumTrack == null)
        {
            var gfm = GameFlowManager.Instance;
            if (gfm != null && gfm.activeDrumTrack != null)
                drumTrack = gfm.activeDrumTrack;
        }

        if (drumTrack != null)
        {
            // prevent double-subscribe
            drumTrack.OnLoopBoundary -= OnLoopBoundary;
            drumTrack.OnLoopBoundary += OnLoopBoundary;
        }
    }
    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (_triggered) return;

        // Grace period so it doesn't trigger instantly on spawn overlap
        if (Time.time - _spawnTime < spawnCollisionGraceSeconds)
            return;

        // Resolve Vehicle robustly (child colliders)
        var vehicle =
            (collision.collider != null ? collision.collider.GetComponentInParent<Vehicle>() : null)
            ?? (collision.rigidbody != null ? collision.rigidbody.GetComponent<Vehicle>() : null);

        if (vehicle == null)
            return;

        // Require a real "hit", not gentle contact
        float impact = collision.relativeVelocity.magnitude;
        if (impact < minImpactSpeed)
            return;

        _triggered = true;

        if (_track != null)
            _track.InstantFillAllBins();

        FireResolvedOnce();
        Destroy(gameObject);

    }
    private void OnLoopBoundary()
    {
        // First boundary after spawn: arm despawn
        if (!_sawFirstBoundary)
        {
            _sawFirstBoundary = true;
            if (despawnOnNextBoundary)
            {
                FireResolvedOnce();
                Destroy(gameObject);
            }
            return;
        }

        // If you ever decide to keep it for >1 loop, you can use _triggered here.
        if (_triggered)
        {
            FireResolvedOnce();
            Destroy(gameObject);
        }
    }
    private IEnumerable<InstrumentTrack> ResolveTracks()
    {
        var gfm = GameFlowManager.Instance;
        var ctrl = gfm != null ? gfm.controller : null;
        return (ctrl != null) ? ctrl.tracks : null;
    }
}