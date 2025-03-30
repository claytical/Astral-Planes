using UnityEngine;
public enum SpawnerPhase
{
    Intro,
    GrooveStart,
    BeatDrop,
    InstrumentChoice,
    Reharmonize,
    Spaceout,
    Finale
}

public class MineNodeSpawnerSet : MonoBehaviour
{
    public GameObject[] mineNodes;
    private int mineNodeIndex = 0;

    public GameObject GetMineNode()
    {
        GameObject go = mineNodes[mineNodeIndex];
        Debug.Log($"Mining node {mineNodeIndex} at {transform.position}  {go.name}");
        mineNodeIndex++;
        if (mineNodeIndex >= mineNodes.Length)
        {
            mineNodeIndex = 0;
        }
        return go;
    }
    
}
