using System.Collections;
using UnityEngine;

public class Obstacle : MonoBehaviour
{
    public Vector2Int gridPosition;
    public Vector2 gridOrigin;
    public Vector2 cellSize;

    private int hitCount = 0;
    private int requiredHitsForBlackHole = 3;
    public float looseTime = 0f;
    public float looseEvolutionThreshold = 10f;
    public bool isLoose = false;
    private EvolvingObstacle parentEvolvingObstacle;
    private DrumTrack drumTrack;

    public GameObject blackHolePrefab; // ✅ Assign Black Hole Prefab in Inspector

    public void SetParentEvolvingObstacle(EvolvingObstacle parent)
    {
        parentEvolvingObstacle = parent;
    }

    public void SetDrumTrack(DrumTrack track)
    {
        drumTrack = track;
    }
    public void Age()
    {
        if (isLoose)
        {
            looseTime += Time.deltaTime;

            if (looseTime >= looseEvolutionThreshold)
            {
                FadeAway();
            }
        }
    }

    void OnCollisionEnter2D(Collision2D coll)
    {
        if (!isLoose)
        {
            isLoose = true;

            // ✅ Notify the EvolvingObstacle that it should be destroyed
            if (parentEvolvingObstacle != null)
            {
                parentEvolvingObstacle.OnObstacleKnockedLoose();
            }
        }

        if (coll.gameObject.GetComponent<Vehicle>() != null)
        {
            hitCount++;
            Debug.Log($"{gameObject.name} hit {hitCount}/{requiredHitsForBlackHole}");

            if (hitCount >= requiredHitsForBlackHole)
            {
                EvolveIntoBlackHole();
            }
        }
    }

    void EvolveIntoBlackHole()
    {
        Debug.Log($"Obstacle at {transform.position} has evolved into a Black Hole!");
        GameObject go = Instantiate(blackHolePrefab, transform.position, Quaternion.identity);
        Hazard hazard = go.GetComponent<Hazard>();
        if (hazard != null)
        {
            hazard.SetDrumTrack(drumTrack);
        }
        Destroy(gameObject); // ✅ Remove the obstacle after transformation
    }

    void FadeAway()
    {
        Debug.Log($"Obstacle at {transform.position} faded away.");
        Destroy(gameObject); // ✅ Remove if ignored
    }
}
