using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public enum CoralState { Drawing, Rendered }

[System.Serializable]
public class CoralStyleProfile
{
    [Header("Scale")]
    public float baseBranchLength = 1.0f;
    public float velocityToLength = 0.01f;     // extra length per summed velocity
    public float densityToWidth  = 0.004f;     // width per note on a track

    [Header("Geometry")]
    public int   pointsPerNote    = 2;         // curve resolution
    public float jitterAngleDeg   = 10f;
    public float arcBendPerNote   = 4f;        // degrees of arc per note

    [Header("Drawing Animation")]
    public float growSecondsPerTrack = 1.25f;
    public AnimationCurve growCurve = AnimationCurve.EaseInOut(0,0, 1,1);

    [Header("Styling")]
    public Gradient fallbackGradient;
}

public class CoralVisualizer : MonoBehaviour
{
    [Header("Prefabs (optional)")]
    public GameObject tendrilPrefab;        // optional markers at segment nodes

    [Header("Rendering")]
    public Material lineMaterial;
    public float    minWidth = 0.02f;
    public float    maxWidth = 0.25f;

    [Header("Layout")]
    public Vector3  origin = Vector3.zero;
    public float    trackAngularSpread = 60f; // degrees separating track stems
    public float    radialSpacing      = 0.0f;

    [Header("Style")]
    public CoralStyleProfile style = new CoralStyleProfile();

    private readonly List<LineRenderer> _lines = new();     // 0..3 tracks
    private readonly List<GameObject>   _tendrils = new();  // spawned VFX
    private Transform _root;

    void Awake()
    {
        _root = new GameObject("CoralRoot").transform;
        _root.SetParent(transform, false);
        EnsureLineCount(4);
    }

    void EnsureLineCount(int count)
    {
        while (_lines.Count < count)
        {
            var go = new GameObject($"TrackLine_{_lines.Count}");
            go.transform.SetParent(_root, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.material = lineMaterial;
            lr.useWorldSpace = false;
            lr.widthMultiplier = 1f;
            lr.numCapVertices = 6;
            lr.numCornerVertices = 4;
            _lines.Add(lr);
        }
        for (int i = _lines.Count - 1; i >= count; i--)
        {
            if (_lines[i] != null) Destroy(_lines[i].gameObject);
            _lines.RemoveAt(i);
        }
    }

    private void Clear()
    {
        foreach (var lr in _lines) if (lr) { lr.positionCount = 0; }
        foreach (var t in _tendrils) if (t) Destroy(t);
        _tendrils.Clear();
    }

    public void RenderPhaseCoral(PhaseSnapshot snapshot, CoralState state)
    {
        Clear();
        if (snapshot == null || snapshot.CollectedNotes == null || snapshot.CollectedNotes.Count == 0) return;

        // 1) Bucket notes by track color (up to 4 groups), stable by hue.
        var groups = GroupNotesIntoTracks(snapshot);

        // 2) Build static geometry for each track.
        var trackCurves = new List<Vector3[]>();
        var widths      = new List<float>();
        var gradients   = new List<Gradient>();

        for (int i = 0; i < 4; i++)
        {
            var notes = groups.ElementAtOrDefault(i);
            var curve = BuildTrackCurve(snapshot, notes, i);
            var width = ComputeTrackWidth(notes);
            var grad  = BuildGradient(notes, snapshot.Color);
            trackCurves.Add(curve);
            widths.Add(width);
            gradients.Add(grad);
        }

        // 3) Apply to LineRenderers
        EnsureLineCount(4);
        for (int i = 0; i < 4; i++)
        {
            var lr = _lines[i];
            lr.positionCount = trackCurves[i].Length;
            lr.SetPositions(trackCurves[i]);
            lr.colorGradient = gradients[i];
            lr.widthCurve = new AnimationCurve(
                new Keyframe(0f, Mathf.Clamp(widths[i], minWidth, maxWidth)),
                new Keyframe(1f, Mathf.Clamp(widths[i]*0.4f, minWidth*0.5f, maxWidth))
            );
        }

        // 4) Animate or freeze
        switch (state)
        {
            case CoralState.Drawing:
                StopAllCoroutines();
                StartCoroutine(AnimateGrowth(trackCurves));
                break;
            case CoralState.Rendered:
                // static; optionally sprinkle tendrils sparsely
                SpawnSparseTendrils(trackCurves, groups);
                break;
        }
    }

 private List<List<PhaseSnapshot.NoteEntry>> GroupNotesIntoTracks(PhaseSnapshot s)
{
    // Map quantized hue -> list
    var buckets = new Dictionary<float, List<PhaseSnapshot.NoteEntry>>();

    // Defensive: empty result with 4 buckets
    var empty = new List<List<PhaseSnapshot.NoteEntry>>(4);
    empty.Add(new List<PhaseSnapshot.NoteEntry>());
    empty.Add(new List<PhaseSnapshot.NoteEntry>());
    empty.Add(new List<PhaseSnapshot.NoteEntry>());
    empty.Add(new List<PhaseSnapshot.NoteEntry>());

    if (s == null || s.CollectedNotes == null || s.CollectedNotes.Count == 0)
        return empty;

    for (int i = 0; i < s.CollectedNotes.Count; i++)
    {
        PhaseSnapshot.NoteEntry n = s.CollectedNotes[i];
        float h, ss, vv;
        Color.RGBToHSV(n.TrackColor, out h, out ss, out vv);
        float key = Mathf.Round(h * 48f) / 48f; // quantize

        List<PhaseSnapshot.NoteEntry> list;
        if (!buckets.TryGetValue(key, out list))
        {
            list = new List<PhaseSnapshot.NoteEntry>();
            buckets[key] = list;
        }
        list.Add(n);
    }

    // Sort keys by hue, then by group size desc
    var keys = new List<float>(buckets.Keys);
    keys.Sort(); // hue ascending

    // Build groups up to 4, preferring larger groups when hues collide
    var result = new List<List<PhaseSnapshot.NoteEntry>>(4);
    for (int k = 0; k < keys.Count && result.Count < 4; k++)
    {
        var group = buckets[keys[k]];
        // insert by size (desc)
        int insertAt = result.Count;
        for (int r = 0; r < result.Count; r++)
        {
            if (group.Count > result[r].Count) { insertAt = r; break; }
        }
        result.Insert(insertAt, group);
    }

    while (result.Count < 4) result.Add(new List<PhaseSnapshot.NoteEntry>());
    return result;
}

    private Vector3[] BuildTrackCurve(PhaseSnapshot snapshot, List<PhaseSnapshot.NoteEntry> notes, int trackIndex)
    { 
        int nCount = notes?.Count ?? 0; 
        if (nCount == 0) {
            // Keep an empty line if this track has no notes.
            return new Vector3[0];
        }
        // Deterministic order: turn at each collected-note boundary.
        var ordered = notes.OrderBy(n => n.Step).ToList();
        // One straight segment per note (plus the starting point).
        // Total length scales with note count; per-segment length is uniform.
        float totalLength = Mathf.Max(0.0001f, style.baseBranchLength) * nCount;
        float segLen      = totalLength / nCount;
        // Initial direction is up; apply a small bend at each note.
        // We alternate left/right using the index to produce visible “kinks”.
        // arcBendPerNote acts as the maximum turn per segment (in degrees).
        float currentAngleDeg = 90f; // straight up in local space
        
        var pts = new Vector3[nCount + 1]; Vector3 p = origin + Vector3.up * radialSpacing; 
        pts[0] = p;
        for (int i = 0; i < nCount; i++) {
            // Small deterministic bend per note boundary:
            // alternate sign to visibly differentiate segments.
            float sign = (i % 2 == 0) ? 1f : -1f; 
            float jitter = (style.jitterAngleDeg > 0f) ? (sign * style.jitterAngleDeg * 0.15f) : 0f;
            currentAngleDeg += sign * style.arcBendPerNote + jitter; 
            // Advance along the new direction by one segment length.
            var dir = Quaternion.Euler(0f, 0f, currentAngleDeg) * Vector3.up; 
            p += dir.normalized * segLen; 
            pts[i + 1] = p;
        }
        return pts;
    }

    private float ComputeTrackWidth(List<PhaseSnapshot.NoteEntry> notes)
    {
        int n = notes?.Count ?? 0;
        return Mathf.Clamp(minWidth + n * style.densityToWidth, minWidth, maxWidth);
    }
    private Gradient BuildGradient(List<PhaseSnapshot.NoteEntry> notes, Color fallbackPhaseColor)
    {
        var g = new Gradient();
        if (notes != null && notes.Count > 0)
        {
            Color track = AverageTrackColor_NoLinq(notes);
            var colors = new GradientColorKey[2];
            colors[0] = new GradientColorKey(track, 0f);
            colors[1] = new GradientColorKey(fallbackPhaseColor, 1f);

            var alphas = new GradientAlphaKey[2];
            alphas[0] = new GradientAlphaKey(1f, 0f);
            alphas[1] = new GradientAlphaKey(0.9f, 1f);

            g.SetKeys(colors, alphas);
        }
        else
        {
            var colors = new GradientColorKey[2];
            colors[0] = new GradientColorKey(fallbackPhaseColor, 0f);
            colors[1] = new GradientColorKey(fallbackPhaseColor, 1f);

            var alphas = new GradientAlphaKey[2];
            alphas[0] = new GradientAlphaKey(0.6f, 0f);
            alphas[1] = new GradientAlphaKey(0.2f, 1f);

            g.SetKeys(colors, alphas);
        }
        return g;
    }

    private Color AverageTrackColor_NoLinq(List<PhaseSnapshot.NoteEntry> notes)
    {
        if (notes == null || notes.Count == 0) return Color.white;
        float r = 0f, g = 0f, b = 0f, a = 0f;
        int c = notes.Count;
        for (int i = 0; i < c; i++)
        {
            Color col = notes[i].TrackColor;
            r += col.r; g += col.g; b += col.b; a += col.a;
        }
        return new Color(r / c, g / c, b / c, a / c);
    }

    private IEnumerator AnimateGrowth(List<Vector3[]> curves)
    {
        // progressive per-track growth; stagger slightly
        for (int ti = 0; ti < _lines.Count; ti++)
        {
            var lr = _lines[ti];
            var pts = curves[ti];
            if (pts.Length == 0) continue;

            float T = Mathf.Max(0.05f, style.growSecondsPerTrack);
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / T;
                float u = Mathf.Clamp01(style.growCurve.Evaluate(t));

                int cut = Mathf.Max(2, Mathf.RoundToInt(Mathf.Lerp(2, pts.Length, u)));
                lr.positionCount = cut;
                for (int i = 0; i < cut; i++) lr.SetPosition(i, pts[i]);

                yield return null;
            }

            // optional tendrils at major note “beats”
            SpawnSparseTendrils(new List<Vector3[]>{ pts }, null, ti);
        }
    }

    private void SpawnSparseTendrils(IReadOnlyList<Vector3[]> trackCurves,
                                     List<List<PhaseSnapshot.NoteEntry>> groups,
                                     int onlyTrack = -1)
    {
        if (tendrilPrefab == null) return;

        for (int ti = 0; ti < trackCurves.Count; ti++)
        {
            if (onlyTrack >= 0 && ti != onlyTrack) continue;

            var pts = trackCurves[ti];
            if (pts.Length < 4) continue;

            int marks = Mathf.Clamp(pts.Length/6, 1, 6);
            for (int m = 1; m <= marks; m++)
            {
                int idx = Mathf.RoundToInt(m * (pts.Length-1) / (float)(marks+1));
                var go = Instantiate(tendrilPrefab, _root);
                go.transform.localPosition = pts[idx];
                _tendrils.Add(go);

                // tint roughly to track color
                if (groups != null && groups[ti].Count > 0)
                {
                    var c = AverageTrackColor_NoLinq(groups[ti]);
                    SetColor(go, c);
                }
            }
        }
    }

    private void SetColor(GameObject obj, Color c)
    {
        if (obj.TryGetComponent<SpriteRenderer>(out var sr)) sr.color = c;
        else if (obj.TryGetComponent<MeshRenderer>(out var mr)) mr.material.color = c;
        if (obj.TryGetComponent<ParticleSystem>(out var ps))
        {
            var main = ps.main; main.startColor = c;
        }
    }
}
