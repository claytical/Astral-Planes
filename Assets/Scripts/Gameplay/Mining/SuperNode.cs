using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class SuperNode : MonoBehaviour
{
    [SerializeField] private SoloVoice soloVoice;
    [SerializeField] public DrumTrack drumTrack;
    [SerializeField] private bool despawnOnNextBoundary = true;
    [SerializeField] private GameObject shardPrefab;

    public System.Action OnResolved;

    private GameFlowManager _gfm;
    private bool _sawFirstBoundary;
    private bool _resolvedFired;

    private readonly List<SuperNodeShard>            _shards       = new();
    private readonly Dictionary<InstrumentTrack, int> _snapshotMult = new();
    private ChordProgressionProfile _alternateProg;
    private bool _allShardsCollected;
    private int  _collectedCount;

    private void Awake()
    {
        if (!soloVoice) soloVoice = FindAnyObjectByType<SoloVoice>();

        if (drumTrack == null)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            if (_gfm != null && _gfm.activeDrumTrack != null)
                drumTrack = _gfm.activeDrumTrack;
            else
                drumTrack = FindAnyObjectByType<DrumTrack>();
        }
    }

    public void Initialize(SoloVoice sv, DrumTrack drum,
                           InstrumentTrack initiatingTrack,
                           List<InstrumentTrack> shardTracks,
                           ChordProgressionProfile alternateProgression)
    {
        soloVoice      = sv;
        drumTrack      = drum;
        _alternateProg = alternateProgression;

        if (shardTracks == null || shardTracks.Count == 0) return;

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

            var go    = Instantiate(shardPrefab, transform.position, Quaternion.identity);
            var shard = go.GetComponent<SuperNodeShard>();
            if (shard == null)
            {
                Debug.LogError("[SuperNode] shardPrefab missing SuperNodeShard component.");
                Destroy(go);
                continue;
            }

            float angle = 2f * Mathf.PI * i / total;
            shard.Setup(track, transform, angle);
            _snapshotMult[track] = track.loopMultiplier;
            shard.OnHit += OnShardHit;
            _shards.Add(shard);
        }
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

    private void OnShardHit(SuperNodeShard shard)
    {
        shard.AssignedTrack?.InstantFillAllBins();
        _collectedCount++;
        if (_collectedCount >= _shards.Count)
            OnAllShardsCollected();
    }

    private void OnAllShardsCollected()
    {
        _allShardsCollected = true;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        Debug.Log($"[SuperNode] All shards collected. _alternateProg={((_alternateProg != null) ? _alternateProg.name : "NULL")} harmony={(_gfm?.harmony != null ? "found" : "NULL")}");

        if (_alternateProg != null)
        {
            _gfm?.harmony?.SetActiveProfile(_alternateProg, applyImmediately: false);
            Debug.Log($"[SuperNode] Staged alternate progression '{_alternateProg.name}' for next boundary.");
        }
    }

    private void OnLoopBoundary()
    {
        if (!_sawFirstBoundary)
            _sawFirstBoundary = true;

        if (_allShardsCollected || despawnOnNextBoundary)
        {
            RevertCollectedShards();
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            _gfm?.controller?.UpdateVisualizer();
            FireResolvedOnce();
            Destroy(gameObject);
        }
    }

    private void RevertCollectedShards()
    {
        foreach (var shard in _shards)
        {
            if (shard == null || !shard.IsCollected) continue;
            if (!_snapshotMult.TryGetValue(shard.AssignedTrack, out int snap)) continue;
            shard.AssignedTrack?.InstantCollapseToLoopMultiplier(snap);
        }
    }

    private void OnDestroy()
    {
        foreach (var shard in _shards)
            if (shard != null)
                Destroy(shard.gameObject);
        _shards.Clear();
    }

    private void FireResolvedOnce()
    {
        if (_resolvedFired) return;
        _resolvedFired = true;
        OnResolved?.Invoke();
    }
}
