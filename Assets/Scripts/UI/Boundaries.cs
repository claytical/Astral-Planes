using UnityEngine;

public class Boundaries : MonoBehaviour
{
    public BoxCollider2D topBoundary;
    public BoxCollider2D bottomBoundary;
    public BoxCollider2D leftBoundary;
    public BoxCollider2D rightBoundary;
    public Camera mainCamera;
    public GameObject warpPrefab;

    [Header("Vehicle Wrap Feel")]
    [Range(0.05f, 0.95f)]
    [SerializeField] private float commitDepth01 = 0.55f;
    [SerializeField] private float minCommitSpeed = 2.25f;
    [SerializeField] private float outwardSpeedBleed = 18f;
    [SerializeField] private float returnToPlaySpeed = 8f;
    [SerializeField] private float innerSettleBuffer = 0.08f;
    [SerializeField] private float settleSpeedThreshold = 1.0f;

    [Header("Wrap")]
    [SerializeField] private bool wrapEnabled = true;

    public static bool WrapEnabled { get; private set; } = true;
    public static void SetWrapEnabled(bool enabled) => WrapEnabled = enabled;

    void Awake()
    {
        if (GameFlowManager.VerboseLogging) Debug.Log($"[BOUNDARIES] Awake on {gameObject.name}");
    }

    void OnEnable()
    {
        if (GameFlowManager.VerboseLogging) Debug.Log($"[BOUNDARIES] OnEnable on {gameObject.name}");
    }

    void Start()
    {
        if (mainCamera == null)
            mainCamera = Camera.main;

        float screenHalfHeight = mainCamera.orthographicSize;
        float screenHalfWidth  = screenHalfHeight * mainCamera.aspect;

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

        if (bottomBoundary != null)
        {
            bottomBoundary.transform.position =
                new Vector3(0f, -screenHalfHeight - (bottomBoundary.size.y * 0.5f), 0f);
            bottomBoundary.size =
                new Vector2(screenHalfWidth * 2f, bottomBoundary.size.y);
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[BOUNDARIES] Boundaries constructed");

        WrapEnabled = wrapEnabled;

        AddWrap(leftBoundary,   BoundaryWrap.WrapAxis.Horizontal, BoundaryWrap.BoundarySide.Left,   rightBoundary);
        AddWrap(rightBoundary,  BoundaryWrap.WrapAxis.Horizontal, BoundaryWrap.BoundarySide.Right,  leftBoundary); 
        // AddWrap(topBoundary,    BoundaryWrap.WrapAxis.Vertical,   BoundaryWrap.BoundarySide.Top,    bottomBoundary);
        // AddWrap(bottomBoundary, BoundaryWrap.WrapAxis.Vertical,   BoundaryWrap.BoundarySide.Bottom, topBoundary);
        StartCoroutine(AlignBottomToNoteVisualizerWhenReady());
    }

    private void AddWrap(
        BoxCollider2D boundary,
        BoundaryWrap.WrapAxis axis,
        BoundaryWrap.BoundarySide side,
        BoxCollider2D opposite)
    {
        if (boundary == null || opposite == null) return;

        var wrap = boundary.gameObject.GetComponent<BoundaryWrap>()
                   ?? boundary.gameObject.AddComponent<BoundaryWrap>();

        wrap.warpPrefab = warpPrefab;
        wrap.axis = axis;
        wrap.side = side;
        wrap.oppositeBoundary = opposite;
    }
    private System.Collections.IEnumerator AlignBottomToNoteVisualizerWhenReady()
    {
        while (GameFlowManager.Instance == null ||
               GameFlowManager.Instance.noteViz == null)
        {
            yield return null;
        }

        yield return null;
        if (GameFlowManager.VerboseLogging) Debug.Log("[BOUNDARIES] Aligning to NoteViz");
        AlignBottomToVisualizer(GameFlowManager.Instance.noteViz);
        // Re-lock DrumTrack play area now that Canvas layout is final and the anchor Y is valid.
        GameFlowManager.Instance?.activeDrumTrack?.RefreshPlayAreaLock();
    }

    private void AlignBottomToVisualizer(NoteVisualizer viz)
    {
        if (!bottomBoundary || viz == null || mainCamera == null)
            return;

        float screenHalfHeight = mainCamera.orthographicSize;
        float screenHalfWidth  = screenHalfHeight * mainCamera.aspect;

        float thickness = bottomBoundary.size.y;
        if (thickness <= 0f) thickness = 1f;

        float topY = viz.GetTopWorldY();
        if (GameFlowManager.VerboseLogging) Debug.Log($"[BOUNDARIES] NoteViz at {topY}");

        float centerY = topY - (thickness * 0.5f);

        bottomBoundary.transform.position =
            new Vector3(0f, centerY, 0f);
        bottomBoundary.size =
            new Vector2(screenHalfWidth * 2f, thickness);
    }
}