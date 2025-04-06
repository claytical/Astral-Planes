using UnityEngine;
public enum SpawnerPhase
{
    Establish,     // replaces Intro, GrooveStart
    Evolve,        // replaces InstrumentChoice, Reharmonize
    Intensify,     // replaces Buildup
    Release,       // replaces GrooveDrop, Finale
    WildCard,       // replaces Experimental
    Pop
}

public class MineNodeSpawnerSet : MonoBehaviour
{
    public GameObject[] mineNodes;
    private int mineNodeIndex = 0;

    public GameObject GetMineNode()
    {
        int index = Random.Range(0, mineNodes.Length);
        return mineNodes[index];
    }
    
}
