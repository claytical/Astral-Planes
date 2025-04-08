using System.Collections.Generic;
using UnityEngine;

public class ConstellationVisualizer : MonoBehaviour
{
    public DrumTrack drumTrack;
    [Header("Galaxy Layout")]
    public float galaxySpreadRadius = 5f;      // Distance between clusters
    public float clusterScale = 0.25f;         // Shrinks individual snapshots

    [Header("Visual Settings")]
    public GameObject starPrefab; // Small glowing sprite
    public Material lineMaterial;
    public float stepScale = 0.2f;
    public float noteScale = 0.2f;
    public float zSpacing = 1.0f;

    private List<GameObject> visualizedStars = new();
    private List<LineRenderer> lines = new();
    private float currentZIndex = 0f;

    public void VisualizeSnapshots(List<PhaseSnapshot> snapshots)
    {
        ClearVisuals();

        for (int i = 0; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i];
            float zOffset = i * zSpacing;

            foreach (var (step, note, velocity) in snapshot.collectedNotes)
            {
                Vector3 pos = new Vector3(step * stepScale, note * noteScale, zOffset);
                pos += transform.position; // ⬅️ Ensures it's relative to visualizer's origin

                GameObject star = Instantiate(starPrefab, pos, Quaternion.identity, transform);
                var spriteRenderers = star.GetComponentsInChildren<SpriteRenderer>();
                foreach (var renderer in spriteRenderers)
                {
                    if (renderer.name.Contains("Overlay"))
                    {
                        renderer.color = snapshot.color * new Color(1f, 1f, 1f, 0.75f); // semi-transparent glow
                    }
                    else
                    {
                        renderer.color = snapshot.color;
                    }
                }

                visualizedStars.Add(star);
            }

            // Optional: draw connections between stars in this snapshot
            if (snapshot.collectedNotes.Count > 1)
            {
                LineRenderer line = new GameObject("ConstellationLine").AddComponent<LineRenderer>();
                line.transform.SetParent(transform, false);
                line.material = lineMaterial;
                line.positionCount = snapshot.collectedNotes.Count;
                line.widthMultiplier = 0.05f;
                line.startColor = line.endColor = snapshot.color;

                for (int j = 0; j < snapshot.collectedNotes.Count; j++)
                {
                    var (step, note, _) = snapshot.collectedNotes[j];
                    line.SetPosition(j, new Vector3(step * stepScale, note * noteScale, zOffset));
                }

                lines.Add(line);
            }
        }
    }
    private Vector2 GetGalaxyClusterPosition(float index)
    {
        float angle = index * 137.5f * Mathf.Deg2Rad; // Spiral layout using golden angle
        float radius = Mathf.Sqrt(index) * galaxySpreadRadius;

        return new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
    }

    public void AddSnapshot(PhaseSnapshot snapshot)
    {
        float zOffset = currentZIndex * zSpacing;
        currentZIndex += 1f;

        int totalSteps = 64; // or fetch from snapshot if dynamic
        int minNote = 36;
        int maxNote = 84;

// Pick a unique offset for this snapshot
        Vector2 clusterCenter = GetGalaxyClusterPosition(currentZIndex);
        currentZIndex += 1f;
        
        foreach (var (step, note, velocity) in snapshot.collectedNotes)
        {
            float tX = Mathf.InverseLerp(0, totalSteps, step);
            float tY = Mathf.InverseLerp(minNote, maxNote, note);

            // Position inside the cluster (centered around 0)
            float localX = (tX - 0.5f) * clusterScale;
            float localY = (tY - 0.5f) * clusterScale;

            Vector3 pos = new Vector3(clusterCenter.x + localX, clusterCenter.y + localY, 0f);

            GameObject star = Instantiate(starPrefab, pos, Quaternion.identity, transform);
            var sr = star.GetComponent<SpriteRenderer>();
            if (sr != null) sr.color = snapshot.color;

            visualizedStars.Add(star);
        }



        // Optional: draw connections
        if (snapshot.collectedNotes.Count > 1)
        {
            LineRenderer line = new GameObject("ConstellationLine").AddComponent<LineRenderer>();
            line.transform.SetParent(transform, false);
            line.material = lineMaterial;
            line.positionCount = snapshot.collectedNotes.Count;
            line.widthMultiplier = 0.05f;
            line.startColor = line.endColor = snapshot.color;

            for (int i = 0; i < snapshot.collectedNotes.Count; i++)
            {
                var (step, note, _) = snapshot.collectedNotes[i];

                float tX = Mathf.InverseLerp(0, totalSteps, step);
                float tY = Mathf.InverseLerp(minNote, maxNote, note);

                float localX = (tX - 0.5f) * clusterScale;
                float localY = (tY - 0.5f) * clusterScale;

                Vector3 pos = new Vector3(clusterCenter.x + localX, clusterCenter.y + localY, 0f);
                line.SetPosition(i, pos);
            }

            lines.Add(line);
        }
    }

    public void ClearVisuals()
    {
        foreach (var star in visualizedStars)
            Destroy(star);
        visualizedStars.Clear();

        foreach (var line in lines)
            Destroy(line.gameObject);
        lines.Clear();
    }
}
