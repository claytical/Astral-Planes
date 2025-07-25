using System.Collections;
using UnityEngine;

public class MineNode : MonoBehaviour
{
    public GameObject debrisPrefab;
    public SpriteRenderer coreSprite;
    public int maxStrength = 100;
    private int strength;
    private GameObject preloadedObject;
    private Vector3 originalScale;
    private bool objectRevealed = false;
    private Color? lockedColor;
    private Rigidbody2D rb;
    private MinedObject minedObject;
    private void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        originalScale = transform.localScale;
        strength = maxStrength;
    }

    public void Initialize(MinedObjectSpawnDirective directive)
    {
        Debug.Log("Initializing mine node with directive: " + directive);
        if (directive.minedObjectPrefab == null)
        {
            Debug.LogError("❌ MinedObject prefab not set in directive.");
            return;
        }

        GameObject obj = Instantiate(directive.minedObjectPrefab, transform.position, Quaternion.identity, transform);
        lockedColor = directive.displayColor;
        minedObject = obj.GetComponent<MinedObject>();
        obj.transform.SetParent(transform);
        Debug.Log($"Setting {obj} to inactive");
        obj.SetActive(false);
        Debug.Log($"Mined object {minedObject.name}");
        minedObject.Initialize(directive.minedObjectType, directive.assignedTrack, directive.noteSetSeries, directive.trackModifierType);
        minedObject.assignedTrack.drumTrack.RegisterMinedObject(minedObject);
        minedObject.assignedTrack.drumTrack.OccupySpawnGridCell(directive.spawnCell.x, directive.spawnCell.y, GridObjectType.Node);
        NoteSpawnerMinedObject spawner = obj.GetComponent<NoteSpawnerMinedObject>();
        if (spawner != null)
        {
            this.preloadedObject = spawner.ghostTrigger;
        }
        TrackUtilityMinedObject track = obj.GetComponent<TrackUtilityMinedObject>();
        if (track != null)
        {
            Debug.Log($"Assigning Track Utility Mined Object: {track.name} on {obj.name}");
            this.preloadedObject = obj;
            if (directive.remixUtility != null)
            {
                Debug.Log($"Applying Remix Utility with {directive.remixUtility}");
                track.ApplyRemixUtility(directive.remixUtility);                
            }
        }
        if (coreSprite != null && lockedColor.HasValue)
        {
            coreSprite.color = lockedColor.Value;
        }
    }

    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (objectRevealed) return;

        if (coll.gameObject.TryGetComponent<Vehicle>(out var vehicle))
        {
            strength -= vehicle.GetForceAsDamage();
            strength = Mathf.Max(0, strength); // Ensure it doesn’t go below 0

            float normalized = (float)strength / maxStrength; // [0, 1]
            float scaleFactor = Mathf.Lerp(0f, 1f, normalized); // Linear scale from 1 to 0

            StartCoroutine(ScaleSmoothly(originalScale * scaleFactor, 0.1f));
            if (strength <= 0)
            {
                if (preloadedObject != null)
                {
                    preloadedObject.transform.SetParent(null, true); // world position stays
                    preloadedObject.transform.localScale = originalScale;
                    preloadedObject.SetActive(true);
                }

                TriggerExplosion(); // destroy self, fade sprite, etc.
            }

            
        }
    }

    private void TriggerExplosion()
    {
        Debug.Log($"Triggering Explosion in Mine Node");
        //preloadedObject
        minedObject.assignedTrack.drumTrack.UnregisterMinedObject(minedObject);

        SpawnDebris();
        StartCoroutine(DelayedReveal(0.2f));
    }

    private IEnumerator DelayedReveal(float delay)
    {
        yield return new WaitForSeconds(delay);
        RevealPreloadedObject();
    }

    private void RevealPreloadedObject()
    {
        if (preloadedObject == null || preloadedObject.gameObject == null || objectRevealed)
        {
            Debug.LogWarning("⚠️ Preloaded object was destroyed before reveal.");
            return;
        }

        Debug.Log($"Reveal Preloaded Object: {preloadedObject.name}");

        preloadedObject.transform.SetParent(null);
        preloadedObject.transform.position = transform.position;
        preloadedObject.SetActive(true);

        if (preloadedObject.TryGetComponent(out MinedObject mined))
        {
            mined.EnableColliderAfterDelay(0.5f);
        }
        Destroy(gameObject);
        objectRevealed = true;
    }

    private void SpawnDebris()
    {
        for (int i = 0; i < 5; i++)
        {
            Vector3 offset = Random.insideUnitCircle * 0.5f;
            GameObject chunk = Instantiate(debrisPrefab, transform.position + offset, Quaternion.identity);
            chunk.transform.localScale = originalScale * 0.7f;
            if (chunk.TryGetComponent(out Rigidbody2D debrisRb))
                debrisRb.AddForce(Random.insideUnitCircle * 5f, ForceMode2D.Impulse);

            Destroy(chunk, 3f);
        }
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

        if (transform.localScale.magnitude <= 0.05f)
            TriggerExplosion();
    }
}
