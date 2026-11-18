using System.Collections.Generic;
using UnityEngine;

// One polyline of a ring glyph (MotifRingGlyphGenerator). Survivor of the removed 2D glyph
// generator (MotifGlyphGenerator.cs), which declared it alongside its own now-deleted types.
public class GlyphPolyline
{
    public string        LayerName;    // e.g. "Bass_Body", "Harmony_Branch_0"
    public MusicalRole?  Role;
    public List<Vector2> Points;
    public float         LineWidth;
    public Color         LineColor;
    public int           SortOrder;   // lower = drawn first (behind)
    public int           BinIndex = -1; // ring bin index; -1 for non-ring glyphs
}
