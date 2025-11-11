using System.Collections.Generic;
using UnityEngine;

public class EndSceneManager : MonoBehaviour
{ 
    public CoralVisualizer coralVisualizer;
    void Start()
    {
        List<PhaseSnapshot> data = ConstellationMemoryStore.RetrieveSnapshot();
        Debug.Log($"{data.Count} data points");
        if (data != null && data.Count > 0)
        {
//            coralVisualizer.Gener
            // ✅ Save the session to disk
            ConstellationMemoryStore.SaveSessionToDisk(data);
        }
    }
}
 
