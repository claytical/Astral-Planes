using System.Collections;
using UnityEngine;

public class Obstacle : MonoBehaviour
{
    public GameObject[] minedPrefabs; // ✅ Array of possible rewards
    public GameObject debris;
    public int strength = 100;
    public int maxStrength = 400; // ✅ Maximum strength when fully grown
    public float looseTime = 0f;
    public float looseEvolutionThreshold = 10f;
    public bool isLoose = false;
    private EvolvingObstacle parentEvolvingObstacle;
    private DrumTrack drumTrack;

    private bool spawnedObject = false;
    private Vector3 originalScale;
    private float maxScaleMultiplier = 4f; // ✅ Maximum growth scale
    private float scaleGrowthDuration = 10f; // ✅ Time period for max scale up
    private float currentGrowthMultiplier = 1f; // ✅ Tracks how big it got before mined

    private void Start()
    {
        originalScale = transform.localScale;
        StartCoroutine(ScaleUpOverTime()); // ✅ Start the scale-up process
    }

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
            if (parentEvolvingObstacle != null)
            {
                parentEvolvingObstacle.OnObstacleKnockedLoose();
            }
        }

        Vehicle vehicle = coll.gameObject.GetComponent<Vehicle>();
        if (vehicle != null)
        {
            int damageToObstacle = vehicle.GetForceAsDamage();

            // ✅ Ensure final hits are impactful
            if (strength <= 20)
            {
                strength = 0;
            }
            else
            {
                strength -= damageToObstacle;
            }

            Debug.Log($"Hit with {damageToObstacle}, strength now at {strength}");

            // ✅ Calculate scale relative to remaining strength
            float scaleFactor = Mathf.Max(0.2f, ((float)strength / maxStrength) * maxScaleMultiplier);
            StartCoroutine(ScaleSmoothly(originalScale * scaleFactor, 0.1f));

            if (strength <= 0 || transform.localScale.magnitude <= 0.05f)
            {
                TriggerExplosion();

            }
        }
    }

    private void TriggerExplosion()
    {
        // ✅ Ensure explosion debris does not inherit obstacle's scale
        GameObject explosion = Instantiate(debris, transform.position, Quaternion.identity);

        // ✅ Release physics objects (debris) to knock the vehicle away
        for (int i = 0; i < 5; i++)
        {
            GameObject debrisChunk = Instantiate(debris, transform.position + (Vector3)UnityEngine.Random.insideUnitCircle * 0.5f, Quaternion.identity);
            debrisChunk.transform.localScale = Vector3.one;
            Rigidbody2D rb = debrisChunk.GetComponent<Rigidbody2D>();
            if (rb) rb.AddForce(UnityEngine.Random.insideUnitCircle * 5f, ForceMode2D.Impulse);
        }

        // ✅ Delay MinedObject activation to make sure it's visible after the explosion
        StartCoroutine(DelayedSpawnMinedObject(0.2f));
    }

    private IEnumerator DelayedSpawnMinedObject(float delay)
    {
        yield return new WaitForSeconds(delay);
        SpawnMinedObject(); // ✅ Wait before spawning to allow explosion effects
    }

    private IEnumerator ScaleSmoothly(Vector3 targetScale, float duration)
    {
        Vector3 initialScale = transform.localScale;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            transform.localScale = Vector3.Lerp(initialScale, targetScale, elapsed / duration);
            elapsed += Time.deltaTime;
            yield return null;
        }

        transform.localScale = targetScale;

        // ✅ If the object gets too small, force destruction
        if (transform.localScale.magnitude <= 0.05f)
        {
            TriggerExplosion();
        }
    }

    private IEnumerator ScaleUpOverTime()
    {
        float elapsed = 0f;
        while (elapsed < scaleGrowthDuration)
        {
            if (isLoose) yield break; // ✅ Stop scaling if knocked loose

            currentGrowthMultiplier = 1f + (elapsed / scaleGrowthDuration) * (maxScaleMultiplier - 1f);
            float newScaleFactor = Mathf.Min(currentGrowthMultiplier, maxScaleMultiplier);

            strength = Mathf.RoundToInt(maxStrength * (newScaleFactor / maxScaleMultiplier)); // ✅ Strength increases properly
            transform.localScale = originalScale * newScaleFactor;

            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    private void SpawnMinedObject()
    {
        if (!spawnedObject)
        {
            int rewardIndex = Mathf.FloorToInt((currentGrowthMultiplier / maxScaleMultiplier) * (minedPrefabs.Length - 1));
            GameObject chosenPrefab = minedPrefabs[rewardIndex];

            GameObject go = Instantiate(chosenPrefab, transform.position, Quaternion.identity);
            MinedObject minedObject = go.GetComponent<MinedObject>();

            if (minedObject != null)
            {
                Color color = minedObject.SetTracks(drumTrack);
                minedObject.sprite.color = color;

                // ✅ Tell the MinedObject to handle its own collider delay
                minedObject.EnableColliderAfterDelay(0.5f);
            }

            Destroy(gameObject); // ✅ Safe to destroy the obstacle now
        }
        spawnedObject = true;
    }


    private IEnumerator EnableColliderAfterDelay(Collider2D collider, float delay)
    {
        yield return new WaitForSeconds(delay);
        collider.enabled = true; // ✅ Enable collider after delay
    }

    void FadeAway()
    {
        Debug.Log($"Obstacle at {transform.position} faded away.");
        Destroy(gameObject);
    }
}
