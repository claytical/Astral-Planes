using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class SuperNode : MonoBehaviour
{
    [SerializeField] private SoloVoice soloVoice;
    [SerializeField] public DrumTrack drumTrack;
    [SerializeField] private bool despawnOnNextBoundary = false;
    [SerializeField] private GameObject shardPrefab;
    [SerializeField] private float bodyMinImpactSpeed  = 2.0f;
    [SerializeField] private float bodyGraceSeconds    = 0.15f;
    [SerializeField] private SuperNodeKiteSteering kiteSteering;
    [SerializeField] private float groupRotationDegPerSec = 30f;
    [SerializeField] private ParticleSystem particles;
    [SerializeField] private float dustTintRadius = 10f;

    public System.Action OnResolved;

    private GameFlowManager _gfm;
    private bool  _sawFirstBoundary;
    private bool  _resolvedFired;
    private float _spawnTime;

    private readonly List<SuperNodeShard>            _shards          = new();
    private readonly Dictionary<InstrumentTrack, int> _snapshotMult   = new();
    private readonly List<InstrumentTrack>            _collectedTracks = new();
    private ChordProgressionProfile _alternateProg;
    private bool _allShardsCollected;
    private int  _collectedCount;
    private int  _highlightIndex;

    private Collider2D           _bodyCollider;
    private Rigidbody2D          _rb;
    private CosmicDustGenerator  _dustGen;
    private readonly List<(Vector2Int center, Color color)> _pendingTints = new();

    private void Awake()
    {
        _spawnTime    = Time.time;
        _bodyCollider = GetComponent<Collider2D>();
        _rb           = GetComponent<Rigidbody2D>();

        if (!soloVoice)    soloVoice    = FindAnyObjectByType<SoloVoice>();
        if (!kiteSteering) kiteSteering = GetComponent<SuperNodeKiteSteering>();
        if (!particles)    particles    = GetComponentInChildren<ParticleSystem>();

        if (drumTrack == null)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            if (_gfm != null && _gfm.activeDrumTrack != null)
                drumTrack = _gfm.activeDrumTrack;
            else
                drumTrack = FindAnyObjectByType<DrumTrack>();
        }

        if (_dustGen == null && drumTrack != null)
            _dustGen = drumTrack.GetComponentInChildren<CosmicDustGenerator>()
                       ?? FindAnyObjectByType<CosmicDustGenerator>();
    }

    public void Initialize(SoloVoice sv, DrumTrack drum,
                           InstrumentTrack initiatingTrack,
                           List<InstrumentTrack> shardTracks,
                           ChordProgressionProfile alternateProgression)
    {
        soloVoice      = sv;
        drumTrack      = drum;
        _alternateProg = alternateProgression;

        if (shardTracks != null && shardTracks.Count > 0)
        {
            int total = shardTracks.Count;
            for (int i = 0; i < total; i++)
            {
                var track = shardTracks[i];
                if (track == null) continue;

                if (shardPrefab == null)
                {
                    Debug.LogError("[SuperNode] shardPrefab is null — cannot spawn shards.");
                    break;
                }
                var go = Instantiate(shardPrefab, transform);
                var shard = go.GetComponent<SuperNodeShard>();
                if (shard == null)
                {
                    Debug.LogError("[SuperNode] shardPrefab missing SuperNodeShard component.");
                    Destroy(go);
                    continue;
                }

                float angle = 2f * Mathf.PI * i / total;
                shard.Setup(track, angle);
                _snapshotMult[track] = track.loopMultiplier;
                shard.OnHit += OnShardHit;
                _shards.Add(shard);
            }

            if (_shards.Count > 0)
            {
                _highlightIndex = 0;
                _shards[0].SetHighlighted(true, instant: true);
                // Hide the background particle visual while shards are present
                particles?.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        UpdateBodyCollider();
    }

    private void OnEnable()
    {
        TrySubscribeToBoundary();
    }

    private void OnDisable()
    {
        if (drumTrack != null)
            drumTrack.OnLoopBoundary -= OnLoopBoundary;
    }

    private void TrySubscribeToBoundary()
    {
        if (drumTrack == null)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            if (_gfm != null && _gfm.activeDrumTrack != null)
                drumTrack = _gfm.activeDrumTrack;
        }

        if (drumTrack != null)
        {
            drumTrack.OnLoopBoundary -= OnLoopBoundary;
            drumTrack.OnLoopBoundary += OnLoopBoundary;
        }
    }

    private void FixedUpdate()
    {
        if (_allShardsCollected || _shards.Count == 0) return;
        if (_rb != null) _rb.angularVelocity = groupRotationDegPerSec;
    }

    private void UpdateBodyCollider()
    {
        if (_bodyCollider != null)
            _bodyCollider.enabled = _shards.Count == 0;
    }

    private void AdvanceHighlight()
    {
        if (_shards.Count == 0 || _allShardsCollected) return;

        // No-op if the current shard is still alive and uncollected
        if (_highlightIndex >= 0 && _highlightIndex < _shards.Count)
        {
            var cur = _shards[_highlightIndex];
            if (cur != null && !cur.IsCollected) return;
        }

        // Current shard was collected — walk forward to next uncollected shard
        for (int step = 1; step <= _shards.Count; step++)
        {
            int candidate = (_highlightIndex + step) % _shards.Count;
            var s = _shards[candidate];
            if (s != null && !s.IsCollected)
            {
                _highlightIndex = candidate;
                s.SetHighlighted(true);
                return;
            }
        }
    }

    // When all tracks are already maxed there are no shards — the player triggers the
    // chord transition by hitting the central SuperNode body directly.
    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (_allShardsCollected) return;
        if (Time.time - _spawnTime < bodyGraceSeconds) return;

        var vehicle =
            (collision.collider != null ? collision.collider.GetComponentInParent<Vehicle>() : null)
            ?? (collision.rigidbody != null ? collision.rigidbody.GetComponent<Vehicle>() : null);
        if (vehicle == null) return;

        if (collision.relativeVelocity.magnitude < bodyMinImpactSpeed) return;

        if (_shards.Count > 0)
        {
            kiteSteering?.BoltNow((Vector2)vehicle.transform.position);
            return;
        }

        OnAllShardsCollected();
    }

    private void OnShardHit(SuperNodeShard shard)
    {
        if (shard.AssignedTrack != null)
        {
            shard.AssignedTrack.InstantFillAllBins(toMaxCapacity: true);
            if (!_collectedTracks.Contains(shard.AssignedTrack))
                _collectedTracks.Add(shard.AssignedTrack);

            if (_dustGen != null && drumTrack != null)
            {
                Vector2Int center = drumTrack.CellOf(shard.transform.position);
                Color      color  = shard.AssignedTrack.trackColor;
                float      dur    = drumTrack.GetTimeToLoopEnd();
                _pendingTints.Add((center, color));
                if (dur > 0.01f)
                    StartCoroutine(ExpandDustTint(center, color, dur));
                else
                    ApplyFullRadiusTint(center, color);
            }
        }
        _collectedCount++;
        if (_collectedCount >= _shards.Count)
            OnAllShardsCollected();
    }

    private IEnumerator ExpandDustTint(Vector2Int center, Color color, float duration)
    {
        float   elapsed    = 0f;
        int     cellRadius = Mathf.CeilToInt(dustTintRadius) + 1;
        Vector2 centerWorld = drumTrack.GridToWorldPosition(center);

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float   progress      = Mathf.Clamp01(elapsed / duration);
            float   currentRadius = dustTintRadius * progress;
            float   sqCurrent     = currentRadius * currentRadius;

            for (int dx = -cellRadius; dx <= cellRadius; dx++)
            for (int dy = -cellRadius; dy <= cellRadius; dy++)
            {
                var     cell  = new Vector2Int(center.x + dx, center.y + dy);
                Vector2 world = drumTrack.GridToWorldPosition(cell);
                if ((world - centerWorld).sqrMagnitude > sqCurrent) continue;
                if (_dustGen.TryGetDustAt(cell, out var dust) && dust != null)
                    dust.SetTint(color);
            }
            yield return null;
        }
    }

    private void ApplyFullRadiusTint(Vector2Int center, Color color)
    {
        if (_dustGen == null || drumTrack == null) return;
        int     cellRadius  = Mathf.CeilToInt(dustTintRadius) + 1;
        float   sqRadius    = dustTintRadius * dustTintRadius;
        Vector2 centerWorld = drumTrack.GridToWorldPosition(center);

        for (int dx = -cellRadius; dx <= cellRadius; dx++)
        for (int dy = -cellRadius; dy <= cellRadius; dy++)
        {
            var     cell  = new Vector2Int(center.x + dx, center.y + dy);
            Vector2 world = drumTrack.GridToWorldPosition(cell);
            if ((world - centerWorld).sqrMagnitude > sqRadius) continue;
            if (_dustGen.TryGetDustAt(cell, out var dust) && dust != null)
                dust.SetTint(color);
        }
    }

    private void OnAllShardsCollected()
    {
        _allShardsCollected = true;

        if (_rb != null) _rb.angularVelocity = 0f;
        if (kiteSteering != null) kiteSteering.enabled = false;
        particles?.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        UpdateBodyCollider();

        // Stage the profile swap now so HarmonyDirector's own OnLoopBoundary handler
        // commits it (and regenerates note assignments) at the next boundary — before
        // this node destroys itself in that same boundary callback.
        if (_alternateProg != null)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            _gfm?.harmony?.SetActiveProfile(_alternateProg, applyImmediately: false);
        }
    }

    private void OnLoopBoundary()
    {
        if (!_sawFirstBoundary)
            _sawFirstBoundary = true;

        AdvanceHighlight();

        if (_allShardsCollected || despawnOnNextBoundary)
        {
            // Snap all expanding tint animations to their full radius
            foreach (var (center, color) in _pendingTints)
                ApplyFullRadiusTint(center, color);
            _pendingTints.Clear();

            RevertCollectedShards();
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            _gfm?.controller?.UpdateVisualizer();
            FireResolvedOnce();
            var explode = GetComponent<Explode>();
            if (explode != null) explode.Permanent();
            else Destroy(gameObject);
        }
    }

    private void RevertCollectedShards()
    {
        foreach (var track in _collectedTracks)
        {
            if (track == null) continue;
            if (!_snapshotMult.TryGetValue(track, out int snap)) continue;
            track.InstantCollapseToLoopMultiplier(snap);
        }
    }

    private void OnDestroy()
    {
        foreach (var shard in _shards)
            if (shard != null) Destroy(shard.gameObject);
        _shards.Clear();
    }

    private void FireResolvedOnce()
    {
        if (_resolvedFired) return;
        _resolvedFired = true;
        OnResolved?.Invoke();
    }
}
