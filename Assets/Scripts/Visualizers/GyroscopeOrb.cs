using System.Collections;
using UnityEngine;

// =========================================================================
//  GyroscopeOrb
//
//  Assembles a MotifRopeRingApplicator into a gyroscope-like ball by
//  distributing each ring's orientation uniformly across the sphere using
//  the golden-angle (Fibonacci lattice) method.
//
//  Works for any ring count:
//    1 ring  → identity (XZ plane)
//    2 rings → two poles (north / south hemisphere)
//    3 rings → evenly spread ~120° apart on sphere
//    4+ rings → Fibonacci-sphere lattice distribution
//
//  Usage:
//    gyroscopeOrb.Build(snapshot);
//    gyroscopeOrb.BuildInstant(snapshot);
//    StartCoroutine(gyroscopeOrb.FadeOutAndClear(duration));
//    gyroscopeOrb.Clear();
//
//  The MotifRopeRingApplicator on this (or a child) GO is driven directly.
//  The applicator's ringOrientations array is populated before Apply is called,
//  so each ring's local Z-axis points to the distributed normal — meaning
//  the ring lies in its own XZ plane, which is perpendicular to that normal.
// =========================================================================
public class GyroscopeOrb : MonoBehaviour
{
    [Tooltip("The MotifRopeRingApplicator to drive. Must be assigned in the Inspector.")]
    public MotifRopeRingApplicator applicator;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Build the gyroscope orb with staggered draw-in animation.
    /// Ring orientations are distributed uniformly on a sphere.
    /// </summary>
    public void Build(MotifSnapshot snapshot)
    {
        if (applicator == null || snapshot == null) return;

        int n = MotifRingMeshGenerator.CountRings(snapshot);
        applicator.ringOrientations = ComputeOrientations(n);
        applicator.AnimateApply(snapshot);
    }

    /// <summary>
    /// Build the gyroscope orb instantly with no draw-in animation.
    /// </summary>
    public void BuildInstant(MotifSnapshot snapshot)
    {
        if (applicator == null || snapshot == null) return;

        int n = MotifRingMeshGenerator.CountRings(snapshot);
        applicator.ringOrientations = ComputeOrientations(n);
        applicator.Apply(snapshot);
    }

    /// <summary>
    /// Fade all rings out and destroy them. Delegates to the applicator.
    /// Safe to fire-and-forget: StartCoroutine(gyroscopeOrb.FadeOutAndClear(dur)).
    /// </summary>
    public IEnumerator FadeOutAndClear(float duration)
    {
        if (applicator != null)
            yield return applicator.FadeOutAndClear(duration);
    }

    /// <summary>Destroy all ring GameObjects immediately.</summary>
    public void Clear() => applicator?.Clear();

    // ── Orientation distribution ─────────────────────────────────────────────

    // Distributes N ring-normal vectors uniformly on a sphere using the
    // golden-angle (Fibonacci lattice) method, then converts each normal to a
    // Quaternion that rotates the ring's local Y-axis to point along that normal.
    //
    // Rings lie in their local XZ plane; their local Y is their normal.
    // So Quaternion.FromToRotation(Vector3.up, normal) orients the ring correctly.
    //
    // The golden-angle spiral avoids the clustering that occurs at poles with naive
    // uniform-azimuth sampling: each ring's inclination is uniform in cos(θ) (i.e.
    // area-uniform on the sphere surface) and azimuth advances by the golden angle
    // (~137.5°) to maximise angular separation between successive rings.
    private static Quaternion[] ComputeOrientations(int n)
    {
        if (n <= 0) return System.Array.Empty<Quaternion>();

        const float GoldenRatio = 1.6180339887f;
        var orientations = new Quaternion[n];

        for (int i = 0; i < n; i++)
        {
            // t goes from 0 (top of sphere) to 1 (bottom) as i increases
            float t           = n > 1 ? (float)i / (n - 1) : 0f;
            float inclination = Mathf.Acos(Mathf.Clamp(1f - 2f * t, -1f, 1f)); // 0 → π
            float azimuth     = 2f * Mathf.PI * i / GoldenRatio;                 // golden angle spiral

            var normal = new Vector3(
                Mathf.Sin(inclination) * Mathf.Cos(azimuth),
                Mathf.Cos(inclination),
                Mathf.Sin(inclination) * Mathf.Sin(azimuth));

            // Rotate ring so its local Y (the ring's normal) points along `normal`
            orientations[i] = Quaternion.FromToRotation(Vector3.up, normal);
        }

        return orientations;
    }
}
