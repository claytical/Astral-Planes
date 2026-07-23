using UnityEngine;

// Frees a StarPool spawn-grid cell when its owning PhaseStar GameObject is destroyed.
internal sealed class StarDestroyRelay : MonoBehaviour
{
    private Vector2Int _cell;
    private DrumTrack _drum;

    public void Init(Vector2Int cell, DrumTrack drum)
    {
        _cell = cell;
        _drum = drum;
    }

    private void OnDestroy()
    {
        try { _drum?.FreeSpawnCell(_cell.x, _cell.y); } catch { }
    }
}
