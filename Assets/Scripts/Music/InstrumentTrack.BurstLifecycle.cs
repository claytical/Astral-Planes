using System;
using UnityEngine;

public partial class InstrumentTrack
{
    private void HookCollectableDestroyHandler(Collectable c)
    {
        if (_destroyHandlers.TryGetValue(c, out var old) && old != null)
        {
            c.OnDestroyed -= old;
            _destroyHandlers.Remove(c);
        }
        Action handler = () => OnCollectableDestroyed(c);
        _destroyHandlers[c] = handler;
        c.OnDestroyed += handler;
    }

    private void PlaceAndBindPlaceholderMarker(NoteVisualizer nv, Collectable c, int absStep, int burstId)
    {
        if (nv == null) return;
        var markerGO = nv.PlacePersistentNoteMarker(this, absStep, lit: false, burstId);
        if (!markerGO) return;
        var tag = markerGO.GetComponent<MarkerTag>() ?? markerGO.AddComponent<MarkerTag>();
        tag.track         = this;
        tag.step          = absStep;
        tag.burstId       = burstId;
        tag.isPlaceholder = true;
        var ml = markerGO.GetComponent<MarkerLight>() ?? markerGO.AddComponent<MarkerLight>();
        ml.SetGrey(new Color(1f, 1f, 1f, 0.25f));
        c.BindMarkerAtSpawn(markerGO.transform, absStep);
    }
}
