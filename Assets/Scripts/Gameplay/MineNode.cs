using System.Collections;
using UnityEngine;
using UnityEngine.Serialization;
public class MineNode : MonoBehaviour
{
    public GameObject[] minedPrefabs; // ✅ Array of possible rewards
    public SpriteRenderer coreSprite;
    public MinedObjectType minedObjectType; // Assigned on spawn
    public GameObject debris;
    public int strength = 100;
    public int maxStrength = 400; // ✅ Maximum strength when fully grown
    public float looseTime = 0f;
    public float looseEvolutionThreshold = 10f;
    public bool isLoose = false;
    private MineNodeSpawner parentNodeSpawner;
    private DrumTrack drumTrack;

    private bool spawnedObject = false;
    private Vector3 originalScale;
    private float maxScaleMultiplier = 4f; // ✅ Maximum growth scale
    private float minScaleFactor = 0.5f; //Minimum size of node
    private float scaleGrowthDuration = 100f; // ✅ Time period for max scale up
    private float currentGrowthMultiplier = 1f; // ✅ Tracks how big it got before mined

    private void Start()
    {        
        if (coreSprite == null)
        {
            coreSprite = GetComponentInChildren<SpriteRenderer>();
        }

        ApplyVisualStyle(minedObjectType);
        originalScale = transform.localScale;
        StartCoroutine(ScaleUpOverTime()); // ✅ Start the scale-up process
    }
    public void ApplyVisualStyle(MinedObjectType type)
    {
        switch (MinedObject.GetCategory(type))
        {
            case MinedObjectCategory.NoteSpawner:
                ApplyNoteSpawnerStyle();
                break;

            case MinedObjectCategory.TrackUtility:
                ApplyUtilityStyle(type);
                break;

            case MinedObjectCategory.NoteModifier:
                ApplyModifierStyle(type);
                break;

            default:
                ApplyDefaultStyle();
                break;
        }
    }

    private void ApplyNoteSpawnerStyle()
    {
        coreSprite.color = new Color(0.18f, 0.22f, 0.25f); // #2E3A40
        AddGlowEffect("#00FFC8", 0.3f); // teal glow
        //AddIcon("♪", new Color(0.9f, 1f, 1f)); // optional overlay
    }

    private void ApplyUtilityStyle(MinedObjectType type)
    {
        coreSprite.color = new Color(0.216f, 0.278f, 0.310f); // #2E3A40
        
        // Tint based on utility type (AntiNote, Shoft, etc.)
        switch (type)
        {
            case MinedObjectType.AntiNoteSpawner:
                coreSprite.color = Color.Lerp(coreSprite.color, Color.black, 0.3f);
                break;

            case MinedObjectType.TrackClear:
                coreSprite.color = Color.Lerp(coreSprite.color, Color.gray, 0.5f);
                break;

            case MinedObjectType.LoopExpansion:
                break;

            case MinedObjectType.Shoft:
                coreSprite.color = Color.Lerp(coreSprite.color, Color.magenta, 0.5f);
                break;

            default:
                break;
        }
    }

    private void ApplyModifierStyle(MinedObjectType type)
    {
        // Similar logic if you decide to distinguish modifiers
        coreSprite.color = new Color(0.3f, 0.3f, 0.3f); // muted modifier style
    }

    private void ApplyDefaultStyle()
    {
        coreSprite.color = new Color(0.2f, 0.2f, 0.2f); // fallback
    }

    private void AddGlowEffect(string hexColor, float alpha = 0.3f)
    {
        if (coreSprite == null) return;
        Color glow = ColorUtility.TryParseHtmlString(hexColor, out var c) ? c : Color.cyan;
        glow.a = alpha;

        // Apply via material, outline, or visual script
        // Placeholder:
        coreSprite.material.SetColor("_GlowColor", glow);
    }

    private void AddIcon(string symbol, Color color)
    {
        // You could spawn a child TextMeshPro icon here
        GameObject iconObj = new GameObject("Icon");
        iconObj.transform.SetParent(coreSprite.transform);
        iconObj.transform.localPosition = Vector3.zero;

        var tmp = iconObj.AddComponent<TMPro.TextMeshPro>();
        tmp.text = symbol;
        tmp.fontSize = 4;
        tmp.color = color;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
    }
    public void SetParentEvolvingObstacle(MineNodeSpawner parent)
    {
        parentNodeSpawner = parent;
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
            if (parentNodeSpawner != null)
            {
                parentNodeSpawner.OnObstacleKnockedLoose();
            }
        }

        Vehicle vehicle = coll.gameObject.GetComponent<Vehicle>();
        if (vehicle != null)
        {
            int damageToNode = vehicle.GetForceAsDamage();

            // ✅ Ensure final hits are impactful
            if (strength <= 50)
            {
                strength = 0;
            }
            else
            {
                strength -= damageToNode;
            }

            Debug.Log($"Hit with {damageToNode}, strength now at {strength}");

            // ✅ Calculate scale relative to remaining strength
            float scaleFactor = Mathf.Max(minScaleFactor, ((float)strength / maxStrength) * maxScaleMultiplier);
            StartCoroutine(ScaleSmoothly(originalScale * scaleFactor, 0.1f));

            if (strength <= 0 || transform.localScale.magnitude <= minScaleFactor * originalScale.magnitude)
            {
                TriggerExplosion();

            }
        }
    }
    void OnDestroy()
    {
        if (drumTrack != null)
        {
            drumTrack.UnregisterMineNode(this);
        }
    }
    private void TriggerExplosion()
    {
        Vector3 currentScale = transform.localScale;

        // Spawn debris chunks with reduced scale
        for (int i = 0; i < 5; i++)
        {
            Vector3 spawnOffset = (Vector3)UnityEngine.Random.insideUnitCircle * 0.5f;
            GameObject debrisChunk = Instantiate(debris, transform.position + spawnOffset, Quaternion.identity);

            float debrisScaleFactor = 1f; // Adjust this to taste (30% of node size)
            debrisChunk.transform.localScale = currentScale * debrisScaleFactor;

            Rigidbody2D rb = debrisChunk.GetComponent<Rigidbody2D>();
            if (rb)
            {
                rb.AddForce(UnityEngine.Random.insideUnitCircle * 5f, ForceMode2D.Impulse);
            }
            Destroy(debrisChunk, 3f); // 💥 Auto-destroy debris after 3 seconds
        }

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
        if (spawnedObject) return;

        int rewardIndex = Mathf.FloorToInt((currentGrowthMultiplier / maxScaleMultiplier) * (minedPrefabs.Length - 1));
//        GameObject chosenPrefab = minedPrefabs[rewardIndex];
        GameObject chosenPrefab = minedPrefabs[Random.Range(0, minedPrefabs.Length)];
        GameObject go = Instantiate(chosenPrefab, transform.position, Quaternion.identity);
        MinedObject minedObject = go.GetComponent<MinedObject>();
        Debug.Log($"Spawned mined prefab: {go.name}");
        foreach (var comp in go.GetComponents<MonoBehaviour>())
        {
            Debug.Log($"  → Component: {comp.GetType().Name}");
        }
        if (minedObject != null)
        {
            // ✅ Handle NoteSpawnerMinedObject logic
            NoteSpawnerMinedObject noteSpawner = go.GetComponent<NoteSpawnerMinedObject>();
            if (noteSpawner != null)
            {
                // Assign track based on role
                InstrumentTrack track = drumTrack.trackController.FindRandomTrackByRole(noteSpawner.musicalRole);
                NoteSet selected = noteSpawner.noteSetSeries?.GetRandomOrCuratedNoteSet();
                if (track != null)
                {
                    noteSpawner.Initialize(track, selected); // internally assigns track + noteset
                    minedObject.sprite.color = track.trackColor;
                }
                else
                {
                    Debug.LogWarning($"Track does not exist.");
                }
            }

            // ✅ Handle TrackUtilityMinedObject logic
            TrackUtilityMinedObject utilityItem = go.GetComponent<TrackUtilityMinedObject>();
            if (utilityItem != null)
            {
                InstrumentTrack track = drumTrack.trackController.FindTrackByRole(utilityItem.targetRole);
                if (track != null)
                {
                    Debug.Log($"Assigning track {track.assignedRole} to utility item {go.name}");
                    utilityItem.Initialize(track);
                    minedObject.sprite.color = track.trackColor;
                }
                else
                {
                    Debug.LogWarning($"❌ No track found for role: {utilityItem.targetRole}");
                }
            }


            // ✅ Delay collider so player doesn't accidentally collect immediately
            minedObject.EnableColliderAfterDelay(0.5f);
        }

        Destroy(gameObject); // ✅ Self-destruct obstacle
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
