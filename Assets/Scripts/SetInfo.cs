using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MidiPlayerTK;

[System.Serializable]
public struct BreakableInfo
{
    public GameObject breakable;  // The breakable prefab
    public int weight;  // The weight associated with this breakable
}


public class SetInfo : MonoBehaviour
{
    public Transform AutoSpawnLocation;
    public GameObject lootLocation;
    public Transform[] lootLocations;
    public LootBox loot;
    public GameObject platforms;  // Single GameObject that holds all platform-related objects
    public BreakableInfo[] breakableInfos;  // Array of BreakableInfo to handle weighted breakable spawning
    public List<SetInfo> availableSets;
    public SequenceManager[] nextPossibleSequences;

    public int weight = 1;  // Weight of this set for weighted random selection
    
    public int breakablesRequiredToAdvance = 20; // Total breakables required for this set
    public int breakablesRequiredForLoot = 8;
    public int noteToSpawnOn = -1;
    public int trackToSpawnOn = -1;
    public float movingSpeed = .03f;
    
    private int spawnedBreakablesCount = 0; // Tracks spawned breakables
    private int breakablesCollectedOnSet = 0;
    private Transform[] spawnLocations;

    private ProceduralLevel level;
    private HashSet<int> occupiedLocations = new HashSet<int>();
    void Start()
    {
        
        Transform[] spawnFilter = AutoSpawnLocation.GetComponentsInChildren<Transform>();
        spawnLocations = spawnFilter.Where(t => t.position != Vector3.zero).ToArray();
        
        if (lootLocation)
        {
            lootLocations = lootLocation.GetComponentsInChildren<Transform>();
        }

        platforms.SetActive(true);  // Make sure platforms are active initially
        Debug.Log("SetInfo Successfully Initialized: " + gameObject.name);
    }
    public void SetLevel(ProceduralLevel l)
    {
        level = l;
        if(level?.midiSequencer != null)
        {
            level.midiSequencer.OnMidiEventPlayed += HandleMidiEvent;
            Debug.Log("Subscribing Set to Midi Events");
        }
    }
    private void HandleMidiEvent(MPTKEvent midiEvent, int trackIndex)
    {
//        Debug.Log($"Handling MIDI event in SetInfo: Note {midiEvent.Value}, Track {trackIndex}");

        // Route the event to platforms
        Platform[] platformArray = platforms.GetComponentsInChildren<Platform>();
        foreach (Platform platform in platformArray)
        {
            platform.HandleMidiEvent(midiEvent, trackIndex);
        }
    }
    public void SpawnBreakableAtMidiEvent()
    {
        if (spawnedBreakablesCount >= breakablesRequiredToAdvance)
        {
            Debug.Log("All breakables for this set have already been spawned.");
            return;
        }

        if (spawnLocations != null)
        {
            // Check if there are free spawn locations
            if (occupiedLocations.Count >= spawnLocations.Length)
            {
                Debug.Log("Nowhere to spawn breakable, waiting...");
                return;
            }

            // Find the next available location
            int spawnIndex = -1;
            for (int i = 0; i < spawnLocations.Length; i++)
            {
                if (!occupiedLocations.Contains(i) && IsLocationClear(spawnLocations[i].position))
                {
                    spawnIndex = i;
                    break;
                }
            }

            if (spawnIndex == -1)
            {
                Debug.Log("No clear spawn locations available, waiting...");
                return;
            }

            // Spawn the breakable
            GameObject breakableToSpawn = GetWeightedRandomBreakable();
            GameObject go = Instantiate(breakableToSpawn, spawnLocations[spawnIndex].position, Quaternion.identity, transform);

            // Access the Collectable component
            Collectable collectable = go.GetComponentInChildren<Collectable>();
            if (collectable != null)
            {
                Debug.Log("Adding Collectable Event");
                // Subscribe to the OnCollected event
//                collectable.OnCollected += () => Collected(go, spawnIndex);
            }

            Debug.Log($"{go.name} created at {go.transform.position} in response to MIDI NoteOn.");

            // Mark this location as occupied
            occupiedLocations.Add(spawnIndex);
            spawnedBreakablesCount++;
        }
    }

    // Method to handle collection and free up the location
    private void Collected(GameObject collectedItem, int spawnIndex)
    {
        Debug.Log("Collected Object");
        breakablesCollectedOnSet++;
        if (breakablesCollectedOnSet >= breakablesRequiredToAdvance)
        {
            level.TransitionToNextSet(SelectNextSet(), nextPossibleSequences[Random.Range(0, nextPossibleSequences.Length)]);
        }

        Destroy(collectedItem);
        occupiedLocations.Remove(spawnIndex);
        Debug.Log($"Spawn location {spawnIndex} is now free.");
    }


    //Next Level
    private SetInfo SelectNextSet()
    {
        if (availableSets.Count == 0)
            return null;

        int totalWeight = 0;
        foreach (var set in availableSets)
        {
            totalWeight += set.weight;
        }

        int randomValue = Random.Range(0, totalWeight);
        int cumulativeWeight = 0;

        foreach (var set in availableSets)
        {
            cumulativeWeight += set.weight;
            if (randomValue < cumulativeWeight)
            {
                return set;
            }
        }

        return availableSets[0]; // Fallback to the first set
    }

    private GameObject GetWeightedRandomBreakable()
    {
        if (breakableInfos == null || breakableInfos.Length == 0)
        {
            Debug.LogError("breakableInfos is null or empty!");
            return null;
        }

        int totalWeight = 0;

        // Calculate the total weight
        foreach (var breakableInfo in breakableInfos)
        {
            if (breakableInfo.weight < 0)
            {
                Debug.LogError("Weight cannot be negative. Defaulting to 0.");
                //breakableInfo.weight = 0;
            }
            totalWeight += breakableInfo.weight;
        }

        if (totalWeight == 0)
        {
            Debug.LogError("Total weight is zero! Returning the first breakable as fallback.");
            return breakableInfos[0].breakable;
        }

        int randomWeight = Random.Range(0, totalWeight);

        // Select a breakable based on the weighted random value
        foreach (var breakableInfo in breakableInfos)
        {
            if (randomWeight < breakableInfo.weight)
            {
                return breakableInfo.breakable;
            }
            randomWeight -= breakableInfo.weight;
        }

        // Fallback in case of any error, though this should never happen
        Debug.LogError("Unexpected error in weighted random selection! Returning first breakable.");
        return breakableInfos[0].breakable;
    }
    private bool IsLocationClear(Vector3 spawnPosition)
    {
        Collider2D[] colliders = Physics2D.OverlapCircleAll(spawnPosition, 0.5f); // Adjust the radius as needed
        foreach (Collider2D collider in colliders)
        {
            if (collider.GetComponent<Vehicle>())
            {
                return false; // Location is not clear, a vehicle is present
            }
        }
        return true; // Location is clear
    }


    public void MoveOffScreen(Vector3 offScreenPosition, float duration)
    {
        Platform[] platformArray = platforms.GetComponentsInChildren<Platform>();

        foreach (Platform platform in platformArray)
        {
            if (platform != null)
            {
                // Perform operations on myObject
                platform.MoveOffScreen(offScreenPosition, duration);
            }

        }
    }

    public void ResetPlatforms()
    {
        Platform[] platformArray = platforms.GetComponentsInChildren<Platform>();
        foreach (Platform platform in platformArray)
        {
            platform.ResetState();
        }
    }

    public void ExplodePlatforms()
    {
        if(level?.midiSequencer != null)
        {
            level.midiSequencer.OnMidiEventPlayed -= HandleMidiEvent;
        }
        Platform[] platformArray = platforms.GetComponentsInChildren<Platform>();
        foreach (Platform platform in platformArray)
        {
            if (platform.GetComponent<Explode>())
            {
                platform.GetComponent<Explode>().Temporary(2);
            }
        }
    }
}
