using UnityEngine;

public class Boundaries : MonoBehaviour
{
    public BoxCollider2D topBoundary;
    public BoxCollider2D bottomBoundary;
    public BoxCollider2D leftBoundary;
    public BoxCollider2D rightBoundary;
    public Camera mainCamera;
    public DrumTrack track;
    void Awake()
    {
        Debug.Log($"[BOUNDARIES] Awake on {gameObject.name}");
    }

    void OnEnable()
    {
        Debug.Log($"[BOUNDARIES] OnEnable on {gameObject.name}");
    }

    void Start()
    {
        if (mainCamera == null)
            mainCamera = Camera.main;

        // --- existing camera-based setup (safe at Start) ---
        float screenHalfHeight = mainCamera.orthographicSize;
        float screenHalfWidth  = screenHalfHeight * mainCamera.aspect;

        // You probably already have something like this in your file;
        // leave your top/left/right logic as-is.

        if (topBoundary != null)
        {
            topBoundary.transform.position =
                new Vector3(0f, screenHalfHeight + (topBoundary.size.y * 0.5f), 0f);
            topBoundary.size =
                new Vector2(screenHalfWidth * 2f, topBoundary.size.y);
        }

        if (leftBoundary != null)
        {
            leftBoundary.transform.position =
                new Vector3(-screenHalfWidth - (leftBoundary.size.x * 0.5f), 0f, 0f);
            leftBoundary.size =
                new Vector2(leftBoundary.size.x, screenHalfHeight * 2f);
        }

        if (rightBoundary != null)
        {
            rightBoundary.transform.position =
                new Vector3(screenHalfWidth + (rightBoundary.size.x * 0.5f), 0f, 0f);
            rightBoundary.size =
                new Vector2(rightBoundary.size.x, screenHalfHeight * 2f);
        }

        // Bottom: temporary camera-based placement so we have *something*
        if (bottomBoundary != null)
        {
            bottomBoundary.transform.position =
                new Vector3(0f, -screenHalfHeight - (bottomBoundary.size.y * 0.5f), 0f);
            bottomBoundary.size =
                new Vector2(screenHalfWidth * 2f, bottomBoundary.size.y);
        }
        Debug.Log($"[BOUNDARIES] Boundaries constructed");
        // ✅ Defer precise alignment to when NoteVisualizer is ready
        StartCoroutine(AlignBottomToNoteVisualizerWhenReady());
    }

    private System.Collections.IEnumerator AlignBottomToNoteVisualizerWhenReady()
    {
        Debug.Log("[BOUNDARIES] Align bottom to note visualizer");
        // Wait until we have a GameFlowManager and a NoteVisualizer
        while (GameFlowManager.Instance == null ||
               GameFlowManager.Instance.noteViz == null)
        {
            yield return null;
        }
        Debug.Log("[BOUNDARIES] NoteViz present");

        // Give layout a frame to settle after noteViz.Initialize()
        yield return null;
        Debug.Log("[BOUNDARIES] Aligning to NoteViz");

        AlignBottomToVisualizer(GameFlowManager.Instance.noteViz);
    }

    private void AlignBottomToVisualizer(NoteVisualizer viz)
    {
        if (!bottomBoundary || viz == null || mainCamera == null)
            return;

        // Recompute camera extents here so we don’t depend on Start() locals
        float screenHalfHeight = mainCamera.orthographicSize;
        float screenHalfWidth  = screenHalfHeight * mainCamera.aspect;

        // Use the *current* thickness of the collider as the vertical size
        float thickness = bottomBoundary.size.y;
        if (thickness <= 0f) thickness = 1f;

        // NoteVisualizer gives us the *top edge* Y in world space
        float topY = viz.GetTopWorldY();
        Debug.Log($"[BOUNDARIES] NoteViz at {topY}");

        // BoxCollider2D.position is its CENTER, so:
        //   centerY = topY - (thickness / 2)
        float centerY = topY - (thickness * 0.5f);

        bottomBoundary.transform.position =
            new Vector3(0f, centerY, 0f);
        bottomBoundary.size =
            new Vector2(screenHalfWidth * 2f, thickness);
    }
}
