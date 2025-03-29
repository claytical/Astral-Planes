using UnityEngine;

public class EvolvingObstacleSet : MonoBehaviour
{
    public GameObject[] evolvingObstacles;
    private int evolvingObstacleIndex = 0;

    public GameObject GetEvolvingObstacle()
    {
        GameObject go = evolvingObstacles[evolvingObstacleIndex];
        Debug.Log($"Evolving obstacle {evolvingObstacleIndex} at {transform.position}  {go.name}");
        evolvingObstacleIndex++;
        if (evolvingObstacleIndex >= evolvingObstacles.Length)
        {
            evolvingObstacleIndex = 0;
        }
        return go;
    }
    
}
