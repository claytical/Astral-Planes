using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

[RequireComponent(typeof(LineRenderer))]
public class FractureLine : MonoBehaviour
{
    private List<Vector3> basePoints;
    private Transform targetTransform; // the moving dark star
    private LineRenderer lineRenderer;

    public void Initialize(List<Vector3> points, Transform darkStar)
    {
        lineRenderer = GetComponent<LineRenderer>();
        if (lineRenderer == null) return;

        lineRenderer.positionCount = points.Count;
        lineRenderer.SetPositions(points.ToArray());

        targetTransform = darkStar;
    }

    private void Update()
    {
        if (lineRenderer == null || basePoints == null || targetTransform == null) return;

        for (int i = 0; i < basePoints.Count; i++)
        {
            lineRenderer.SetPosition(i, basePoints[i]);
        }

        // Final point always follows the current position of the Dark Star
//        lineRenderer.SetPosition(basePoints.Count, targetTransform.position);
    }

    public float GetClosestDistance(Vector3 point)
    {
        float minDist = float.MaxValue;
        foreach (var segmentPoint in basePoints)
        {
            float dist = Vector3.Distance(point, segmentPoint);
            if (dist < minDist) minDist = dist;
        }
        return minDist;
    }

    public void RetreatAndDestroy()
    {
        // Optional: animate retraction here
        Destroy(gameObject);
    }
}
