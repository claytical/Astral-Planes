using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// =========================================================================
//  GlyphApplicator — wires GlyphOutput into Unity LineRenderers
//  Attach to the glyph parent GameObject.
// =========================================================================

public class GlyphApplicator : MonoBehaviour
{
    [Header("Species preset (editable in Inspector)")]
    public GlyphSpeciesParams SpeciesParams = new();

    [Header("Rendering")]
    [SerializeField] public Material LineMaterial;

    [Header("Runtime state")]
    public GlyphOutput LastOutput;

    private readonly List<LineRenderer> _renderers = new();

    public void Apply(MotifSnapshot snapshot)
    {
        ClearRenderers();

        LastOutput = MotifGlyphGenerator.GenerateGlyph(snapshot, SpeciesParams);

        // Sort by layer order (Groove behind Bass behind Harmony behind Lead)
        var sorted = LastOutput.Polylines.OrderBy(p => p.SortOrder).ToList();

        foreach (var poly in sorted)
        {
            var go = new GameObject(poly.LayerName);
            go.transform.SetParent(transform, worldPositionStays: false);

            var lr = go.AddComponent<LineRenderer>();
            if (LineMaterial != null) lr.material = LineMaterial;
            lr.useWorldSpace = false;
            lr.loop          = poly.LayerName.StartsWith("Bass_Body");
            lr.widthMultiplier = poly.LineWidth;
            lr.startColor    = poly.LineColor;
            lr.endColor      = poly.LineColor;
            lr.positionCount = poly.Points.Count;

            for (int i = 0; i < poly.Points.Count; i++)
                lr.SetPosition(i, new Vector3(poly.Points[i].x, poly.Points[i].y, 0f));

            _renderers.Add(lr);
        }
    }

    public void ClearRenderers()
    {
        foreach (Transform child in transform)
            Destroy(child.gameObject);
        _renderers.Clear();
    }

    public void Clear() => ClearRenderers();

    public void FitToPlayArea(float width, float height, float cx, float cy)
    {
        transform.position = new Vector3(cx, cy, transform.position.z);
        float scale = Mathf.Min(width, height);
        transform.localScale = new Vector3(scale, scale, 1f);
    }
}
