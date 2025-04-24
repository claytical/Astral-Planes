using System.Collections.Generic;
using UnityEngine;

public class EndSceneManager : MonoBehaviour
{
    public ConstellationVisualizer constellationVisualizer;

    void Start()
    {
        List<PhaseSnapshot> data = ConstellationMemoryStore.RetrieveSnapshot();
        Debug.Log($"{data.Count} data points");
        if (data != null && data.Count > 0)
        {
            Debug.Log($"Visualizing Stars");
            constellationVisualizer.GenerateFromPhases(data);
        }

        // Optional: Clear memory for replay safety
        ConstellationMemoryStore.Clear();
    }
}
 
