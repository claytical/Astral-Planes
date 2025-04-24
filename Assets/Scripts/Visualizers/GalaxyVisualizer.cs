using System.Collections.Generic;
using UnityEngine;

public class GalaxyVisualizer : MonoBehaviour
{
    [Header("Prefabs & References")]
    public GameObject centralStarPrefab;
    public GameObject planetPrefab;
    public DrumTrack drumTrack;

    [Header("Galaxy Layout")]
    public float systemSpacing = 6f;      // Spiral spacing between solar systems
    public float clusterScale = 1.5f;     // Size scale of each solar system

    [Header("Planet Motion")]
    public float baseOrbitSpeed = 10f;
    public float orbitRadiusStep = 0.5f;

    private float systemIndex = 0f;

    public void AddSnapshot(PhaseSnapshot snapshot)
    {
        // Determine position in galaxy spiral
        Vector3 systemCenter = GetSpiralPosition(systemIndex++);
        GameObject star = Instantiate(centralStarPrefab, systemCenter, Quaternion.identity, transform);

        // Star color = phase color (faint glow)
        var starRenderer = star.GetComponentInChildren<SpriteRenderer>();
        if (starRenderer != null)
        {
            Color c = snapshot.color;
            c.a = 0.5f;
            starRenderer.color = c;
        }

        // Setup ranges for normalization
        int totalSteps = 64;
        int minNote = 36;
        int maxNote = 84;

        for (int i = 0; i < snapshot.collectedNotes.Count; i++)
        {
            var note = snapshot.collectedNotes[i];

            float normalizedStep = Mathf.InverseLerp(0, totalSteps, note.step);
            float normalizedPitch = Mathf.InverseLerp(minNote, maxNote, note.note);

            float orbitRadius = (i + 1) * orbitRadiusStep * clusterScale;
            float speed = baseOrbitSpeed * 0.5f * (0.5f + normalizedPitch); // Higher pitch → faster orbit

            GameObject planet = Instantiate(planetPrefab, systemCenter, Quaternion.identity, transform);
            planet.transform.localScale = Vector3.one * Mathf.Lerp(0.1f, 0.3f, note.velocity / 127f); // Scale by velocity

            // Planet color = track color
            var r = planet.GetComponentInChildren<SpriteRenderer>();
            if (r != null)
            {
                Color pc = note.trackColor;
                pc.a = 0.3f;
                r.color = pc;
            }

            // Add orbital motion
            var orbit = planet.AddComponent<PlanetOrbit>();
            orbit.center = star.transform;
            orbit.orbitRadius = orbitRadius;
            orbit.orbitSpeed = speed;
        }
    }

    private Vector3 GetSpiralPosition(float index)
    {
        float angle = index * 137.5f * Mathf.Deg2Rad;
        float radius = Mathf.Sqrt(index) * systemSpacing;

        return new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);
    }

    public void ClearGalaxy()
    {
        foreach (Transform child in transform)
        {
            Destroy(child.gameObject);
        }

        systemIndex = 0;
    }
}
