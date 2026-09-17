using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marker storage for NoteVisualizer. Keeps the original flat (track, step) -> Transform
/// map (used by callers that look up a single marker by key) alongside a per-track index,
/// so track-scoped scans (carry highlights, step search) don't have to filter every marker
/// across every active role/track every frame. All mutation goes through this class so the
/// two views can never drift apart — this is the structural fix for the O(all markers across
/// all roles) scans that used to run once per vehicle per frame.
/// </summary>
public sealed class NoteMarkerRegistry : IEnumerable<KeyValuePair<(InstrumentTrack, int), Transform>>
{
    private readonly Dictionary<(InstrumentTrack, int), Transform> _flat = new();
    private readonly Dictionary<InstrumentTrack, Dictionary<int, Transform>> _byTrack = new();

    public int Count => _flat.Count;
    public IEnumerable<(InstrumentTrack, int)> Keys => _flat.Keys;

    public Transform this[(InstrumentTrack track, int step) key]
    {
        get => _flat[key];
        set => Set(key, value);
    }

    public bool TryGetValue((InstrumentTrack, int) key, out Transform value) => _flat.TryGetValue(key, out value);

    /// <summary>
    /// O(1) access to just this track's markers, keyed by step. Returns false (with a null
    /// map) if the track has no markers at all. Callers must not mutate the returned map —
    /// go through this registry's indexer/Remove instead.
    /// </summary>
    public bool TryGetTrackMap(InstrumentTrack track, out Dictionary<int, Transform> map)
    {
        if (track == null) { map = null; return false; }
        return _byTrack.TryGetValue(track, out map);
    }

    private void Set((InstrumentTrack track, int step) key, Transform value)
    {
        _flat[key] = value;
        if (key.track == null) return;

        if (!_byTrack.TryGetValue(key.track, out var inner))
        {
            inner = new Dictionary<int, Transform>();
            _byTrack[key.track] = inner;
        }
        inner[key.step] = value;
    }

    public bool Remove((InstrumentTrack track, int step) key)
    {
        bool removed = _flat.Remove(key);
        if (key.track != null && _byTrack.TryGetValue(key.track, out var inner))
            inner.Remove(key.step);
        return removed;
    }

    public void Clear()
    {
        _flat.Clear();
        _byTrack.Clear();
    }

    /// <summary>
    /// Snapshots all entries into a caller-owned, grow-only buffer (resized only when it's
    /// too small), avoiding a fresh array allocation on every call the way ToArray() would.
    /// </summary>
    public int CopyToScratch(ref KeyValuePair<(InstrumentTrack, int), Transform>[] buffer)
    {
        int count = _flat.Count;
        if (buffer == null || buffer.Length < count)
            buffer = new KeyValuePair<(InstrumentTrack, int), Transform>[count];

        int i = 0;
        foreach (var kv in _flat)
            buffer[i++] = kv;
        return count;
    }

    // Returns Dictionary's own struct enumerator directly (no boxing/allocation), matching
    // Dictionary<>'s own zero-alloc foreach behavior.
    public Dictionary<(InstrumentTrack, int), Transform>.Enumerator GetEnumerator() => _flat.GetEnumerator();

    IEnumerator<KeyValuePair<(InstrumentTrack, int), Transform>>
        IEnumerable<KeyValuePair<(InstrumentTrack, int), Transform>>.GetEnumerator() => _flat.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _flat.GetEnumerator();
}
