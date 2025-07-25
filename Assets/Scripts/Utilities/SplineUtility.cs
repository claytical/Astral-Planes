using UnityEngine;
using System.Collections.Generic;

public static class SplineUtility
{
    /// <summary>
    /// Evaluate Catmull-Rom spline position between p1 and p2 using p0 and p3 as context.
    /// t should be between 0 and 1.
    /// </summary>
    public static Vector3 EvaluateCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        return 0.5f * (
            2f * p1 +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * (t * t) +
            (-p0 + 3f * p1 - 3f * p2 + p3) * (t * t * t)
        );
    }

    /// <summary>
    /// Generates a smooth Catmull-Rom path sampled across segments.
    /// </summary>
    public static List<Vector3> GenerateSplinePath(List<Vector3> controlPoints, int samplesPerSegment = 10)
    {
        var path = new List<Vector3>();

        if (controlPoints == null || controlPoints.Count < 4)
        {
            Debug.LogWarning("SplineUtility: At least 4 points are needed for spline interpolation.");
            return path;
        }

        for (int i = 1; i < controlPoints.Count - 2; i++)
        {
            for (int j = 0; j < samplesPerSegment; j++)
            {
                float t = j / (float)samplesPerSegment;
                Vector3 point = EvaluateCatmullRom(
                    controlPoints[i - 1],
                    controlPoints[i],
                    controlPoints[i + 1],
                    controlPoints[i + 2],
                    t
                );
                path.Add(point);
            }
        }

        // Optionally add the final control point
        path.Add(controlPoints[controlPoints.Count - 2]);

        return path;
    }

    /// <summary>
    /// Expands a list of waypoints by adding mirrored edge points to support full spline evaluation.
    /// </summary>
    public static List<Vector3> PadControlPoints(List<Vector3> points)
    {
        if (points.Count < 2) return points;

        Vector3 first = points[0];
        Vector3 second = points[1];
        Vector3 last = points[points.Count - 1];
        Vector3 secondLast = points[points.Count - 2];

        Vector3 pre = first - (second - first);
        Vector3 post = last + (last - secondLast);

        var padded = new List<Vector3> { pre };
        padded.AddRange(points);
        padded.Add(post);
        return padded;
    }
}
