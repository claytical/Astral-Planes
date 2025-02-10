using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using MidiPlayerTK;

public class ProceduralLevel : MonoBehaviour
{
    public SetInfo currentSet;
    public Loot[] availableLoot;

    public RemixManager remix;
    public GameObject LevelFailPanel;
    public GameObject patterns;
    public Text failureMessage;
    public ParkingLot lot;
    public GameObject ProgressPanel;
    public AudioSource effectsAudio;
    
    public GameObject portalPrefab;
    public GameObject newPlane;

    public int setsBeforePortal = 3;
    private int completedSets = 0;

    public float initialSpawnWaitTime = 1f;
    public float waitTimeIncrement = 0.5f;
    public float inactivityTime = 10f;  // Time in seconds to check for inactivity
    public GameObject specialItemPrefab;  // Prefab of the special item to spawn
    public CanvasMeter breakablesInSet;
    public CanvasMeter breakablesCollected;
    public ParticleSystem breakableMeterFX;


   
    private bool allBreakablesSpawned;
    private float currentSpawnWaitTime;
    private List<Vehicle> vehicles;

    private void Start()
    {
        // Initialize the first set
        if (currentSet)
        {
            currentSet.SetLevel(this);
            currentSet.gameObject.SetActive(true);

        }
        else
        {
            Debug.LogError("No set assigned to start the level.");
        }
    }

    private void HandleMidiEventPlayed(MPTKEvent midiEvent, int trackIndex)
    {
        string trackType = trackIndex == -1 ? "Drum" : $"Track {trackIndex}";
        Debug.Log($"{trackType}: Note {midiEvent.Value} at Tick {midiEvent.Tick}, Velocity: {midiEvent.Velocity}");
        //spawn on single note or all notes of a selected track
        if(currentSet.trackToSpawnOn == trackIndex && midiEvent.Value == currentSet.noteToSpawnOn || currentSet.trackToSpawnOn == trackIndex && currentSet.noteToSpawnOn == -1)
        {
            currentSet.SpawnBreakableAtMidiEvent();
            
        }

    }

    private void SpawnPortal()
    {
        if (portalPrefab != null)
        {
            Instantiate(portalPrefab, Vector3.zero, Quaternion.identity);
            Debug.Log("Portal spawned.");
        }
        else
        {
            Debug.LogWarning("Portal prefab is not assigned.");
        }
    }

    


    private void AttachAllPlatforms()
    {
        Platform[] platforms = Resources.FindObjectsOfTypeAll<Platform>();
        for(int i = 0; i < platforms.Length; i++)
        {
            platforms[i].AttachLevel(this);
        }
    }


    public void Play()
    {


        // Find all vehicles in the scene

        vehicles = new List<Vehicle>(FindObjectsByType<Vehicle>(FindObjectsSortMode.None));

        if (vehicles.Count == 0)
        {
            Debug.LogWarning("No vehicles found in the scene.");
            return;
        }

        if (currentSet == null)
        {
            Debug.LogError("Set is not assigned in ProceduralLevel.");
            return;
        }
        AttachAllPlatforms();
        currentSet.gameObject.SetActive(true);
        Debug.Log("Configuring Set: " + currentSet.name);


    }


    public void NextPlane(Transform transform)
    {
        GameObject go = Instantiate(newPlane);
        go.transform.position = transform.position;
    }

    public void RemovePlatforms()
    {
        currentSet.ExplodePlatforms();
    }

    private void SpawnSpecialItem()
    {
        if (specialItemPrefab != null)
        {
            Vector3 spawnPosition = new Vector3(0, 5, 0); // Example position, adjust as needed
            Instantiate(specialItemPrefab, spawnPosition, Quaternion.identity);
        }
        else
        {
            Debug.LogWarning("Special item prefab is not assigned.");
        }
    }
}
