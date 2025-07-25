using System.Collections.Generic;
using UnityEngine;

public class CoralVisualizer : MonoBehaviour
{
    [Header("Prefab Parts")]
    public GameObject branchPrefab;
    public GameObject tendrilPrefab;

    [Header("Coral Settings")]
    public float branchLength = 2f;
    public float branchSpreadAngle = 45f;
    public float tendrilSpacing = 0.3f;
    public float rotationSpeed = 5f;

    private List<GameObject> spawnedParts = new();
    public List<PhaseSnapshot> snapshots = new();
    private Transform coralRoot;     // Optional: parent transform for hierarchy

    public Vector3 coralOrigin = new Vector3(-10f, -3f, 0f); // Move off center if needed
    public float branchScale = 5f; // Amplify size of visuals
    
    void Awake()
    {
        coralRoot = new GameObject("CoralRoot").transform;
        coralRoot.SetParent(this.transform, false);
    }

public void GenerateCoralFromSnapshots(List<PhaseSnapshot> snapshots)
{
    ClearExisting();
    if (snapshots == null || snapshots.Count == 0) return;

    float heightStep = (branchLength / 10f) * branchScale;
    float angleJitter = branchSpreadAngle / 4f;
    float horizontalSpacing = branchScale;

    for (int i = 0; i < snapshots.Count; i++)
    {
        var snapshot = snapshots[i];
        var notes = snapshot.collectedNotes;
        if (notes == null || notes.Count == 0) continue;

        // 🌱 Create branch GameObject
        GameObject branch = Instantiate(branchPrefab, coralRoot);
        float xOffset = i * horizontalSpacing;
        Vector3 branchBaseWorld = coralOrigin + new Vector3(xOffset, 0, 0);
        branch.transform.position = branchBaseWorld; // world position
        branch.transform.localRotation = Quaternion.identity;

        LineRenderer lr = branch.GetComponent<LineRenderer>();
        if (lr == null) continue;

        lr.positionCount = notes.Count + 1;
        lr.useWorldSpace = false;

        List<Vector3> points = new();
        Vector3 pos = Vector3.zero;
        Vector3 dir = Vector3.up;

        points.Add(pos);

        for (int j = 0; j < notes.Count; j++)
        {
            float angleOffset = Random.Range(-angleJitter, angleJitter);
            dir = Quaternion.Euler(0, 0, angleOffset) * dir.normalized;
            pos += dir * heightStep;
            points.Add(pos);

            // 🌸 Tendril in world space at correct location
            Vector3 worldTendrilPos = branch.transform.TransformPoint(pos);
            GameObject tendril = Instantiate(tendrilPrefab, worldTendrilPos, Quaternion.identity, coralRoot);
            float tendrilScale = Mathf.Lerp(0.1f, 0.3f, notes[j].velocity / 127f);
            tendril.transform.localScale = Vector3.one * tendrilScale;
            SetColor(tendril, notes[j].trackColor);
            spawnedParts.Add(tendril);
        }

        for (int p = 0; p < points.Count; p++)
        {
            lr.SetPosition(p, points[p]);
        }

        // 🎨 Style the branch
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new GradientColorKey[] { new GradientColorKey(snapshot.color, 0f), new GradientColorKey(snapshot.color, 1f) },
            new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.1f, 1f) }
        );
        lr.colorGradient = gradient;

        AnimationCurve widthCurve = new AnimationCurve();
        widthCurve.AddKey(0f, 0.15f);
        widthCurve.AddKey(1f, 0.02f);
        lr.widthCurve = widthCurve;

        spawnedParts.Add(branch);
    }
}


    private void SetColor(GameObject obj, Color color)
    {
        if (obj.TryGetComponent<SpriteRenderer>(out var sr))
        {
            sr.color = color;
        }
        else if (obj.TryGetComponent<MeshRenderer>(out var mr))
        {
            mr.material.color = color;
        }

        if (obj.TryGetComponent<ParticleSystem>(out var ps))
        {
            ParticleSystem.MainModule main = ps.main;
            main.startColor = color;
        }
    }

    private void ClearExisting()
    {
        foreach (var obj in spawnedParts)
        {
            if (obj != null) Destroy(obj);
        }
        spawnedParts.Clear();
    }

    private void Update()
    {
        //transform.Rotate(Vector3.forward * rotationSpeed * Time.deltaTime);
    }
}
