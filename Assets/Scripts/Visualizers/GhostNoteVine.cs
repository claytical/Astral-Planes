using System.Collections.Generic;
using UnityEngine;

public class GhostNoteVine : MonoBehaviour
{
    public LineRenderer vinePrefab;
    private List<LineRenderer> activeVines = new();
    private List<int> connectedSteps = new(); // 🔹 Needed for step matching
    [SerializeField] float growDuration = 0.5f;
    private float growTimer = 0f;
    private bool isGrowing = true;
    [SerializeField] float dissolveDuration = 0.75f;
    private float dissolveTimer = 0f;
    private bool isDissolving = false;
    private List<Vector3[]> vinePointPaths = new List<Vector3[]>(); // one set per vine
    private Color vineColor;
    private Collectable collectable;

    public bool IsHighlighted { get; private set; }

    void Start()
    {
        NoteVisualizer nv = GetComponentInParent<NoteVisualizer>();
    }
    void Update()
    {
        // inside GhostNoteVine.Update():
        if (isGrowing)
        {
            growTimer += Time.deltaTime;
            float t = Mathf.Clamp01(growTimer / growDuration);

            for (int i = 0; i < activeVines.Count; i++)
            {
                LineRenderer vine = activeVines[i];
                Vector3[] fullPath = vinePointPaths[i];
                int fullCount = fullPath.Length;

                int visibleCount = Mathf.Max(2, Mathf.FloorToInt(t * fullCount));
                Vector3[] visiblePoints = new Vector3[visibleCount];

                for (int j = 0; j < visibleCount; j++)
                    visiblePoints[j] = fullPath[j];

                vine.positionCount = visibleCount;
                vine.SetPositions(visiblePoints);
            }

            if (t >= 1f)
            {
                isGrowing = false;
            }
        }
    }
    void LateUpdate()
    {
        // 1) Grab your track & visualizer via the Collectable
        collectable = GetComponent<Collectable>();
        var track       = collectable.assignedInstrumentTrack;           // :contentReference[oaicite:0]{index=0}
        var nv          = track.controller.noteVisualizer;

        // 2) Recompute the current world‐space anchors for each connected step
        List<Vector3> anchors = nv.GetRibbonWorldPositionsForSteps(track, connectedSteps);  
        // :contentReference[oaicite:1]{index=1}

        int count = Mathf.Min(activeVines.Count, vinePointPaths.Count, anchors.Count);
        for (int i = 0; i < count; i++)
        {
            var vine = activeVines[i];
            var path = vinePointPaths[i];
            path[^1] = anchors[i];
            vine.positionCount = path.Length;
            vine.SetPositions(path);
        }

    }

    public void CreateVines(Vector3 ghostPos, List<Vector3> targetPositions, List<int> stepIndices)
    {
        // Remove any existing vines
        ClearVines();
        connectedSteps.Clear();
        // Find our NoteVisualizer top‐of‐canvas Y once
        var collectable = GetComponent<Collectable>();
        var track       = collectable.assignedInstrumentTrack;
        var nv          = track.controller.noteVisualizer;
        var nvRT        = nv.GetComponent<RectTransform>();

        Vector3[] nvCorners = new Vector3[4];
        nvRT.GetWorldCorners(nvCorners);
        float staticY = nvCorners[1].y + 0.1f; // just above the top of the canvas

        for (int i = 0; i < targetPositions.Count; i++)
        {
            Vector3 target  = targetPositions[i];
            float   ribbonY = target.y;  // final drop into ribbon mid‐line

            // Instantiate a new vine
            var vine = Instantiate(vinePrefab, transform);
            // Color the vine to match the track
            Color c = collectable.assignedInstrumentTrack.trackColor;
            vineColor = c;
            vine.startColor = vineColor;
            Vector3[] pts = new Vector3[]
            {
                ghostPos,
                new Vector3(ghostPos.x, staticY,    ghostPos.z),
                new Vector3(target.x,   staticY,    target.z),
                new Vector3(target.x,   ribbonY,    target.z)
            };

            vine.positionCount = pts.Length;
            vine.SetPositions(pts);
            
            // Register for animation updates if needed
            activeVines.Add(vine);
            connectedSteps.Add(stepIndices[i]);
            vinePointPaths.Add(pts);
        }
    }

    /// <summary>
    /// Highlights the *next* connected step within `windowSeconds` ahead of `loopElapsed`,
    /// fading from fully opaque to transparent as you approach the step.
    /// </summary>
    /// <summary>
    /// Highlights the *next* connected step within `windowSeconds` ahead of `loopElapsed`,
    /// fading from fully opaque to transparent as you approach the step.
    /// </summary>
    public void AnimateToNextStep(
        float loopElapsed,   // seconds into the drum loop [0…loopLength)
        float stepDuration,  // drumLoopLength / totalSteps
        float windowSeconds  // how many seconds ahead we’re looking
    )
    {
        // 1) Prep
        Collectable c = GetComponent<Collectable>();
        if (c == null) return;

        int   totalSteps = c.assignedInstrumentTrack.drumTrack.totalSteps;
        float loopLength = stepDuration * totalSteps;

        // 2) Clear any old highlight
        for (int i = 0; i < activeVines.Count; i++)
            ClearHighlight(i);

        // 3) Find the *smallest positive* delta to each future step
        float bestDelta = float.MaxValue;
        int   bestIdx   = -1;
        for (int i = 0; i < connectedSteps.Count; i++)
        {
            float stepPos = (connectedSteps[i] * stepDuration) % loopLength;
            // forward distance around the ring
            float delta   = (stepPos - loopElapsed + loopLength) % loopLength;
            if (delta <= windowSeconds && delta < bestDelta)
            {
                //Visual All Future Possible Notes
                FutureStep(i, bestDelta / windowSeconds);
                bestDelta = delta;
                bestIdx   = i;
            }
        }

        // 4) Highlight that one vine, with progress = timeLeft/window
        if (bestIdx >= 0)
        {
            float progress = Mathf.Clamp01(bestDelta / windowSeconds);
            HighlightStep(bestIdx, progress);
        }
    }
    
    public void HighlightStep(int vineIdx, float timeLeftNorm)
    {
        timeLeftNorm = Mathf.Clamp01(timeLeftNorm);

        LineRenderer lr = activeVines[vineIdx];

        // 🔹 WIDTH FLARE: narrower when timeLeft is small
        float topWidth    = 0.02f;
        float baseMax     = 0.2f; // customize as needed
        float bottomWidth = Mathf.Lerp(baseMax, topWidth, 1f - timeLeftNorm);

        AnimationCurve widthCurve = new AnimationCurve(
            new Keyframe(0f, topWidth),
            new Keyframe(0.9f, topWidth),
            new Keyframe(1f, bottomWidth)
        );
        lr.widthCurve = widthCurve;

        // 🔹 COLOR: Solid line for now (or add fade if needed)
        var g = new Gradient();
        g.mode = GradientMode.Fixed;
        g.SetKeys(
            new[] {
                new GradientColorKey(vineColor, 0f),
                new GradientColorKey(vineColor, 1f)
            },
            new[] {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 1f)
            }
        );
        lr.colorGradient = g;

        IsHighlighted = true;
    }
    public void FutureStep(int vineIdx, float timeLeftNorm)
    {
        timeLeftNorm = Mathf.Clamp01(timeLeftNorm);

        LineRenderer lr = activeVines[vineIdx];

        // 🔹 WIDTH FLARE: narrower when timeLeft is small
        float topWidth    = 0.02f;
        float baseMax     = 0.2f; // customize as needed
        float bottomWidth = Mathf.Lerp(baseMax, topWidth, 1f - timeLeftNorm);

        AnimationCurve widthCurve = new AnimationCurve(
            new Keyframe(0f, topWidth),
            new Keyframe(0.9f, topWidth),
            new Keyframe(1f, bottomWidth)
        );
        lr.widthCurve = widthCurve;

        // 🔹 COLOR: Solid line for now (or add fade if needed)
        var g = new Gradient();
        g.mode = GradientMode.Fixed;
        g.SetKeys(
            new[] {
                new GradientColorKey(vineColor, 0f),
                new GradientColorKey(vineColor, 1f)
            },
            new[] {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(.3f, 1f)
            }
        );
        lr.colorGradient = g;

        IsHighlighted = false;
    }


    public void ClearHighlight(int step)
    {
        IsHighlighted = false;

        Gradient g = new Gradient();
        g.SetKeys(
            new[] {
                new GradientColorKey(vineColor, 0f),
                new GradientColorKey(vineColor, 1f)
            },
            new[] {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(0f, 1f)
            }
        );
        activeVines[step].colorGradient = g;

        activeVines[step].widthCurve = AnimationCurve.Constant(0f, 1f, 0.02f); // thin fallback
    }
    public void ClearVines()
    {
        // Destroy all old LineRenderers
        foreach (var vine in activeVines)
            Destroy(vine.gameObject);

        activeVines.Clear();
        connectedSteps.Clear();
        vinePointPaths.Clear();

        // Restart your grow / dissolve state
        isGrowing     = true;
        growTimer     = 0f;
        isDissolving  = false;
        dissolveTimer = 0f;
    }
    
    
}
