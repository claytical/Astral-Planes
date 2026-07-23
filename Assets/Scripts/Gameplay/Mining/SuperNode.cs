using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class SuperNode : MonoBehaviour
{
    [SerializeField] public  DrumTrack     drumTrack;

    [Header("Track Nodes")]
    [SerializeField] private GameObject trackNodePrefab;
    [SerializeField] private float      spawnRadius = 1f;

    public System.Action OnResolved;

    private GameFlowManager _gfm;
    private bool            _resolvedFired;
    private float           _difficulty01;
    private int             _pendingChaseCount;

    private void Awake()
    {

        if (drumTrack == null)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            drumTrack = _gfm != null ? _gfm.ResolveDrumTrack() : FindAnyObjectByType<DrumTrack>();
        }
    }

    public void Initialize(SoloVoice sv, DrumTrack drum,
                           InstrumentTrack initiatingTrack,
                           List<InstrumentTrack> shardTracks,
                           ChordProgressionProfile alternateProgression,
                           float difficulty01)
    {
        drumTrack     = drum;
        _difficulty01 = difficulty01;

        SpawnAllTrackNodes(shardTracks);

        if (_pendingChaseCount == 0)
        {
            FireResolvedOnce();
            Despawn();
        }
    }

    private void SpawnAllTrackNodes(List<InstrumentTrack> tracks)
    {
        if (trackNodePrefab == null || tracks == null || tracks.Count == 0) return;

        int count = tracks.Count;
        for (int i = 0; i < count; i++)
        {
            float angle = 2f * Mathf.PI * i / count;
            Vector2 offset   = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * spawnRadius;
            Vector2 spawnPos = (Vector2)transform.position + offset;

            var go = Instantiate(trackNodePrefab, spawnPos, Quaternion.identity);
            var node = go.GetComponent<SuperNodeTrackNode>();
            if (node == null)
            {
                Debug.LogError("[SuperNode] trackNodePrefab missing SuperNodeTrackNode component.");
                Destroy(go);
                continue;
            }

            _pendingChaseCount++;
            node.Setup(tracks[i], _difficulty01, drumTrack);
            node.OnResolved = OnTrackNodeResolved;
        }
    }

    private void OnTrackNodeResolved(bool wasCaught)
    {
        _pendingChaseCount--;
        if (_pendingChaseCount <= 0)
        {
            FireResolvedOnce();
            Despawn();
        }
    }

    private void Despawn()
    {
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
