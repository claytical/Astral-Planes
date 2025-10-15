using UnityEngine;

public class Boundaries : MonoBehaviour
{
    public BoxCollider2D topBoundary;
    public BoxCollider2D bottomBoundary;
    public BoxCollider2D leftBoundary;
    public BoxCollider2D rightBoundary;
    public Camera mainCamera;
    public DrumTrack track;
    void Start()
    {
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
        }

        AdjustBoundaries();
    }

    void AdjustBoundaries()
    {
        if (mainCamera == null)
        {
            Debug.LogError("No camera assigned to BoundaryAdjuster.");
            return;
        }

        float screenHalfHeight = mainCamera.orthographicSize;
        float screenHalfWidth = screenHalfHeight * mainCamera.aspect;

        // 🔹 Force the scale of colliders to (1,1,1) to prevent unintended stretching
        if (topBoundary != null) topBoundary.transform.localScale = Vector3.one;
        if (bottomBoundary != null) bottomBoundary.transform.localScale = Vector3.one;
        if (leftBoundary != null) leftBoundary.transform.localScale = Vector3.one;
        if (rightBoundary != null) rightBoundary.transform.localScale = Vector3.one;

        float boundaryThickness = 1f; // Keep it small so it's not huge

        // ✅ Adjust Top Boundary
        if (topBoundary != null)
        {
            topBoundary.transform.position = new Vector3(0, screenHalfHeight + (boundaryThickness / 2), 0);
            topBoundary.size = new Vector2(screenHalfWidth * 2, boundaryThickness);
        }

        // ✅ Adjust Bottom Boundary
        if (bottomBoundary != null)
        {
            if (track != null && GameFlowManager.Instance.controller != null && GameFlowManager.Instance.controller.noteVisualizer != null)
            {
                float bottomY = GameFlowManager.Instance.controller.noteVisualizer.GetTopWorldY();
                bottomBoundary.transform.position = new Vector3(0, bottomY - (boundaryThickness / 2), 0);
            }
            bottomBoundary.size = new Vector2(screenHalfWidth * 2, boundaryThickness);
        }

        // ✅ Adjust Left Boundary
        if (leftBoundary != null)
        {
            leftBoundary.transform.position = new Vector3(-screenHalfWidth - (boundaryThickness / 2), 0, 0);
            leftBoundary.size = new Vector2(boundaryThickness, screenHalfHeight * 2);
        }

        // ✅ Adjust Right Boundary
        if (rightBoundary != null)
        {
            rightBoundary.transform.position = new Vector3(screenHalfWidth + (boundaryThickness / 2), 0, 0);
            rightBoundary.size = new Vector2(boundaryThickness, screenHalfHeight * 2);
        }
    }

}