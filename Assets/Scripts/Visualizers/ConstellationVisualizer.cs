using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class ConstellationVisualizer : MonoBehaviour
{
    [Header("Star Settings")]
    public GameObject starPrefab;
    public float baseRadius = 2.5f;
    public float ringSpacing = 1.5f;
    public float rotationSpeed = 5f;
    public float pulseSpeed = 2f;
    public float maxScale = 1.3f;

    private List<GameObject> instantiatedStars = new();

    public void GenerateFromPhases(List<PhaseSnapshot> phases)
    {
        ClearConstellation();

        int ringCount = phases.Count;
        float angleOffset = 0f;

        float scaleFactor = CalculateVisualScale(phases);

        for (int i = 0; i < ringCount; i++)
        {
            PhaseSnapshot phase = phases[i];

            int starCount = Mathf.Max(1, phase.CollectedNotes.Count);
            float angleStep = 360f / starCount;

            for (int j = 0; j < starCount; j++)
            {
                float radius = (baseRadius + (i * ringSpacing)) * scaleFactor;

                var note = phase.CollectedNotes[j];

                float angle = angleOffset + j * angleStep;
                
                Vector3 pos = transform.position + Quaternion.Euler(0, 0, angle) * Vector3.up * radius;

                GameObject star = Instantiate(starPrefab, pos, Quaternion.identity, transform);

                float size = Mathf.Lerp(0.4f, 1f, Mathf.InverseLerp(60f, 127f, note.Velocity));
                star.transform.localScale = Vector3.one * Mathf.Max(size, 0.2f);

                var sr = star.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
//TODO: Check Colors
                    //                    sr.color = note.trackColor;
                }

                instantiatedStars.Add(star);
                star.AddComponent<ConstellationStarPulse>().Init(pulseSpeed, maxScale, rotationSpeed, transform.position);
            }

            angleOffset += 20f; // slight twist for mandala feel
        }
    }

    private float CalculateVisualScale(List<PhaseSnapshot> phases)
    {
        Camera cam = Camera.main;
        if (cam == null) return 1f;

        float screenHeight = 2f * cam.orthographicSize;
        float screenWidth = screenHeight * cam.aspect;

        float targetRadius = Mathf.Min(screenWidth, screenHeight) * 0.4f;
        float visualExtent = baseRadius + (phases.Count * ringSpacing);

        return targetRadius / Mathf.Max(visualExtent, 0.001f);
    }

    public void ClearConstellation()
    {
        foreach (var star in instantiatedStars)
        {
            if (star != null) Destroy(star);
        }
        instantiatedStars.Clear();
    }
}

public class ConstellationStarPulse : MonoBehaviour
{
    private float pulseSpeed;
    private float maxScale;
    private float rotationSpeed;
    private Vector3 center;
    private Vector3 basePos;
    private float baseScale;

    public void Init(float _pulseSpeed, float _maxScale, float _rotationSpeed, Vector3 _center)
    {
        pulseSpeed = _pulseSpeed;
        maxScale = _maxScale;
        rotationSpeed = _rotationSpeed;
        center = _center;
        basePos = transform.position;
        baseScale = transform.localScale.x;
    }

    void Update()
    {
        float scale = baseScale + Mathf.Sin(Time.time * pulseSpeed) * 0.1f;
        transform.localScale = Vector3.one * scale;

        transform.RotateAround(center, Vector3.forward, rotationSpeed * Time.deltaTime);
    }
}
