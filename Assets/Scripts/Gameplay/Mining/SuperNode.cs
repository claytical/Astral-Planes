using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class SuperNode : MonoBehaviour
{
    [SerializeField] private SoloVoice soloVoice;
    [SerializeField] public  DrumTrack drumTrack;
    [SerializeField] private SuperNodeKiteSteering kiteSteering;
    [SerializeField] private ParticleSystem        particles;

    [Header("Body")]
    [SerializeField] private float bodyMinImpactSpeed  = 2.0f;
    [SerializeField] private float bodyGraceSeconds    = 0.15f;
    [SerializeField] private float dimmedAlpha         = 0.25f;
    [SerializeField] private float groupRotationDegPerSec = 30f;

    [Header("Shards")]
    [SerializeField] private GameObject shardPrefab;
    [SerializeField] private GameObject trackNodePrefab;
    [SerializeField] private int        ascendLoopsOverride = 3;

    public System.Action OnResolved;

    private GameFlowManager _gfm;
    private bool            _resolvedFired;
    private float           _spawnTime;
    private float           _difficulty01;

    private readonly List<SuperNodeShard> _shards = new();
    private int _pendingChaseCount;

    private Rigidbody2D  _rb;
    private SpriteRenderer _sr;
    private Collider2D   _bodyCollider;

    private void Awake()
    {
        _spawnTime = Time.time;
        _rb        = GetComponent<Rigidbody2D>();
        _sr        = GetComponent<SpriteRenderer>();
        _bodyCollider = GetComponent<Collider2D>();

        if (!soloVoice)    soloVoice    = FindAnyObjectByType<SoloVoice>();
        if (!kiteSteering) kiteSteering = GetComponent<SuperNodeKiteSteering>();
        if (!particles)    particles    = GetComponentInChildren<ParticleSystem>();

        if (drumTrack == null)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            drumTrack = _gfm?.activeDrumTrack ?? FindAnyObjectByType<DrumTrack>();
        }
    }

    public void Initialize(SoloVoice sv, DrumTrack drum,
                           InstrumentTrack initiatingTrack,
                           List<InstrumentTrack> shardTracks,
                           ChordProgressionProfile alternateProgression,
                           float difficulty01)
    {
        soloVoice     = sv;
        drumTrack     = drum;
        _difficulty01 = difficulty01;

        SpawnShards(shardTracks);

        if (_shards.Count > 0)
            _shards[0].SetHighlighted(true, instant: true);
    }

    private void SpawnShards(List<InstrumentTrack> tracks)
    {
        if (shardPrefab == null || tracks == null || tracks.Count == 0) return;

        // Base sorting order from the body's sprite renderer so shards render above it.
        int baseSortOrder = _sr != null ? _sr.sortingOrder + 1 : 1;

        int count = tracks.Count;
        for (int i = 0; i < count; i++)
        {
            float angle = 2f * Mathf.PI * i / count;
            var go = Instantiate(shardPrefab, transform);
            var shard = go.GetComponent<SuperNodeShard>();
            if (shard == null)
            {
                Debug.LogError("[SuperNode] shardPrefab missing SuperNodeShard component.");
                Destroy(go);
                continue;
            }
            // Each shard gets a distinct sorting order so all are visible when overlapping.
            var sr = go.GetComponentInChildren<SpriteRenderer>();
            if (sr != null) sr.sortingOrder = baseSortOrder + i;

            shard.Setup(tracks[i], angle);
            _shards.Add(shard);
        }
    }

    private void FixedUpdate()
    {
        if (_rb == null || _pendingChaseCount > 0) return;
        if (_shards.Count > 0)
            _rb.angularVelocity = groupRotationDegPerSec;
        else
            _rb.angularVelocity = 0f;
    }

    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (Time.time - _spawnTime < bodyGraceSeconds) return;
        if (_pendingChaseCount > 0) return;

        var vehicle =
            (collision.collider != null ? collision.collider.GetComponentInParent<Vehicle>() : null)
            ?? (collision.rigidbody != null ? collision.rigidbody.GetComponent<Vehicle>() : null);
        if (vehicle == null) return;

        if (collision.relativeVelocity.magnitude < bodyMinImpactSpeed) return;

        if (_shards.Count == 0)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            _gfm?.controller?.CheckAndTriggerAllTracksMaxed();
            FireResolvedOnce();
            Despawn();
            return;
        }

        EjectCurrentShard(collision.GetContact(0).point);
    }

    private void EjectCurrentShard(Vector2 ejectOrigin)
    {
        if (_shards.Count == 0) return;

        var shard = _shards[0];
        _shards.RemoveAt(0);

        var track = shard.AssignedTrack;
        Vector2 spawnPos = shard.transform.position;
        Destroy(shard.gameObject);

        DimBody();

        if (trackNodePrefab == null)
        {
            Debug.LogError("[SuperNode] trackNodePrefab is null.");
            RestoreBodyOrDespawn();
            return;
        }

        var go = Instantiate(trackNodePrefab, spawnPos, Quaternion.identity);
        var node = go.GetComponent<SuperNodeTrackNode>();
        if (node == null)
        {
            Debug.LogError("[SuperNode] trackNodePrefab missing SuperNodeTrackNode component.");
            Destroy(go);
            RestoreBodyOrDespawn();
            return;
        }

        _pendingChaseCount++;
        node.Setup(track, _difficulty01, drumTrack);
        node.OnResolved = OnTrackNodeResolved;
    }

    private void OnTrackNodeResolved(bool wasCaught)
    {
        _pendingChaseCount--;

        if (_shards.Count > 0)
        {
            RestoreBody();
            _shards[0].SetHighlighted(true, instant: true);
        }
        else if (_pendingChaseCount <= 0)
        {
            FireResolvedOnce();
            Despawn();
        }
    }

    private void DimBody()
    {
        if (_sr != null)
        {
            var c = _sr.color;
            c.a = dimmedAlpha;
            _sr.color = c;
        }
        if (_bodyCollider != null)
            _bodyCollider.enabled = false;
        if (_rb != null)
            _rb.angularVelocity = 0f;
        if (kiteSteering != null)
            kiteSteering.enabled = false;
    }

    private void RestoreBody()
    {
        if (_sr != null)
        {
            var c = _sr.color;
            c.a = 1f;
            _sr.color = c;
        }
        if (_bodyCollider != null)
            _bodyCollider.enabled = true;
        if (kiteSteering != null)
            kiteSteering.enabled = true;
    }

    private void RestoreBodyOrDespawn()
    {
        if (_shards.Count > 0)
        {
            RestoreBody();
            _shards[0].SetHighlighted(true, instant: true);
        }
        else
        {
            FireResolvedOnce();
            Despawn();
        }
    }

    private void Despawn()
    {
        particles?.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var explode = GetComponent<Explode>();
        if (explode != null) explode.Permanent();
        else Destroy(gameObject);
    }

    private void FireResolvedOnce()
    {
        if (_resolvedFired) return;
        _resolvedFired = true;
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        _gfm?.controller?.UpdateVisualizer();
        OnResolved?.Invoke();
    }
}
