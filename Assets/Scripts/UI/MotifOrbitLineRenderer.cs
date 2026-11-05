using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws PhaseLibraryCarousel's orbit path as pooled world-space polylines.
/// </summary>
public class MotifOrbitLineRenderer : MonoBehaviour
{
    [Tooltip("Same material as the ring glyph contour lines.")]
    [SerializeField] private Material lineMaterial;

    [SerializeField] private Color lineColor = Color.white;
    [SerializeField] private float lineWidth = 0.06f;
    [SerializeField] private int sortingOrder = -1;

    private readonly List<LineRenderer> _pool = new();
    private int _used;

    public void Clear()
    {
        for (int i = 0; i < _pool.Count; i++) _pool[i].gameObject.SetActive(false);
        _used = 0;
    }

    public void DrawPolyline(IReadOnlyList<Vector3> points, float alpha)
    {
        if (points.Count < 2) return;
        var lr = Next(alpha);
        lr.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++) lr.SetPosition(i, points[i]);
    }

    private LineRenderer Next(float alpha)
    {
        if (_used == _pool.Count) _pool.Add(CreateLine());
        var lr = _pool[_used++];
        lr.gameObject.SetActive(true);
        var c = lineColor;
        c.a *= alpha;
        lr.startColor = c;
        lr.endColor   = c;
        return lr;
    }

    private LineRenderer CreateLine()
    {
        var go = new GameObject("OrbitLine");
        go.transform.SetParent(transform, worldPositionStays: false);
        var lr = go.AddComponent<LineRenderer>();
        if (lineMaterial != null) lr.material = lineMaterial;
        lr.useWorldSpace     = true;
        lr.widthMultiplier   = lineWidth;
        lr.numCornerVertices = 4;
        lr.numCapVertices    = 4;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        lr.sortingOrder      = sortingOrder;
        return lr;
    }
}
