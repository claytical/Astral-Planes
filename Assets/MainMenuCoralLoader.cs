using System.Collections.Generic;
using UnityEngine;

public class MainMenuCoralLoader : MonoBehaviour
{
    public GameObject coralVisualizerPrefab; // Prefab with CoralVisualizer on it
    public Transform container;              // Where to parent instantiated corals
    public Vector3 spacing = new Vector3(3f, 0, 0); // Offset each coral

    void Start()
    {
        ConstellationMemoryStore.Clear();
        LoadCoralHistory();
    }

    void LoadCoralHistory()
    {
        List<List<PhaseSnapshot>> allSessions = ConstellationMemoryStore.LoadAllSessionsFromDisk();

        if (allSessions == null || allSessions.Count == 0)
        {
            Debug.LogWarning("⚠️ No coral sessions found — skipping visualization.");
            return;
        }

        for (int i = 0; i < allSessions.Count; i++)
        {
            List<PhaseSnapshot> session = allSessions[i];

            if (session == null || session.Count == 0)
            {
                Debug.LogWarning($"⚠️ Session {i + 1} is empty — skipping.");
                continue;
            }

            GameObject sessionGroup = new GameObject($"CoralSession_{i + 1}");
            sessionGroup.transform.SetParent(container, false);
            sessionGroup.transform.localPosition = i * spacing;

            GameObject coralObj = Instantiate(coralVisualizerPrefab, sessionGroup.transform);
            coralObj.name = "CoralVisualizer";

            CoralVisualizer visualizer = coralObj.GetComponent<CoralVisualizer>();
            if (visualizer != null)
            {
                visualizer.GenerateCoralFromSnapshots(session);
            }
        }

        Debug.Log($"🌿 Loaded {allSessions.Count} coral sessions");
    }

}