using System.Collections.Generic;
using UnityEngine;

public class CoralGardenRenderer : MonoBehaviour
{
    [Header("Layout")]
    public int    columnsPerRow  = 8;       // max corals per row before wrap
    public Vector2 cellSize      = new Vector2(4f, 3f);
    public Vector2 patchGap      = new Vector2(2f, 3f); // extra space between session rows

    [Header("Coral")]
    public CoralVisualizer coralPrefab;
    public CoralState defaultState = CoralState.Rendered;

    private readonly List<CoralVisualizer> _spawned = new();

    public void Clear()
    {
        foreach (var c in _spawned) if (c) Destroy(c.gameObject);
        _spawned.Clear();
    }

    public void RenderGardenFromDisk()
    {
        Clear();
        var sessions = ConstellationMemoryStore.LoadAllSessionsFromDisk(); // List<List<PhaseSnapshot>>
        // Each inner list = one play session (patch).

        int row = 0;
        foreach (var session in sessions)
        {
            RenderPatch(session, row++);
        }
    }

    public void RenderPatch(List<PhaseSnapshot> snapshots, int rowIndex)
    {
        if (snapshots == null || snapshots.Count == 0 || coralPrefab == null) return;

        int col = 0;
        for (int i = 0; i < snapshots.Count; i++)
        {
            var snap = snapshots[i];
            var go = Instantiate(coralPrefab, transform);
            _spawned.Add(go);

            // Position in a grid; one “patch” per row
            int gridX = col % columnsPerRow;
            int gridY = col / columnsPerRow;

            var local =
                new Vector3(
                    gridX * cellSize.x,
                    -rowIndex * (cellSize.y + patchGap.y) - gridY * cellSize.y,
                    0f);

            go.transform.localPosition = local;

            // Optional: shift each coral’s local origin slightly right so stems don’t overlap
            go.origin = new Vector3(0.2f, 0f, 0f);
            go.RenderPhaseCoral(snap, defaultState);

            col++;
        }
    }
}
