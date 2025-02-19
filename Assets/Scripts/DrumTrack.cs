using UnityEngine;
using MidiPlayerTK;
using System.Collections.Generic;
using System.Collections;

public class DrumTrack : MonoBehaviour
{
    public MidiFilePlayer drums;
    public GameObject beatPrefab; // Prefab for visualization
    public Color activeColor = Color.yellow;
    public Color noteColor = Color.red;
    public float baseBeatSpeed = 1f; // ✅ Base speed (adjustable per pattern)
    public float beatSpeedMultiplier = 1f; // ✅ Multiplier for speed per pattern
    private int lastTick = 0; // ✅ Track MIDI tick progress
    public float beatMoveSpeed = 5f; // ✅ Speed at which beats move down
    public int cyclesToSwitchPattern = 4; // ✅ Number of full cycles before switching pattern
    private int completedCycles = 0; // ✅ Tracks how many times beats have restarted
    private int cycleCount = 0;
    private float screenTopY = 10f; // ✅ Adjust based on your screen position
    private float screenBottomY = -10f; // ✅ Adjust based on your screen position
    private List<GameObject> spawnedBeats = new List<GameObject>();
    private int lastIndex = -1;
    private int totalSteps = 64; // Number of steps in the loop
    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    private float stepWidth; // Dynamic width per step
    private bool isSwitching = false; // ✅ Prevent multiple switches at once
    public int loopCounter = 0;
    private float lastDrumTime = 0f;

    public AudioSource drumAudioSource;
    public float drumLoopBPM = 120f;
    public float loopLengthInSeconds;
    public float startDspTime;
    private void Update()
    {
        if (drumAudioSource == null) return;
        float currentTime = drumAudioSource.time;
        if(currentTime < lastDrumTime)
        {

            loopCounter++;
        }
        lastDrumTime = currentTime;
    }

    IEnumerator InitializeDrumLoop()
    {
        // ✅ Wait until the AudioSource has a valid clip
        while (drumAudioSource.clip == null)
        {
            Debug.Log("Waiting for drum audio clip to load...");
            yield return null; // Wait until the next frame
        }

        loopLengthInSeconds = drumAudioSource.clip.length;
        drumAudioSource.loop = true; // ✅ Ensure the loop setting is applied
        drumAudioSource.Play();
        startDspTime = (float)AudioSettings.dspTime;
        Debug.Log($"Drum loop initialized with length {loopLengthInSeconds} seconds.");
    }

    void Start()
    {
        if (drumAudioSource == null)
        {
            Debug.LogError("DrumTrack: No AudioSource assigned!");
            return;
        }

        StartCoroutine(InitializeDrumLoop()); // ✅ Ensure it loads properly
    }


    void SpawnBeatObject(int startStep, int durationSteps)
    {
        if (startStep < 0 || startStep >= totalSteps) return;

        // Instantiate prefab
        GameObject beat = Instantiate(beatPrefab, this.transform);

        // **Calculate the correct position**
        float startX = screenMinX + (startStep * stepWidth); // Align start position
        float width = durationSteps * stepWidth; // Scale width dynamically

        beat.transform.position = new Vector3(startX + (width / 2), 0, 0); // Centered correctly

        // **Change color to indicate it's a note**
        SpriteRenderer sprite = beat.GetComponent<SpriteRenderer>();
        if (sprite != null)
            sprite.color = noteColor;

        // **Resize BoxCollider2D (if exists) to match size**
        BoxCollider2D collider = beat.GetComponent<BoxCollider2D>();
        if (collider != null)
            collider.size = new Vector2(width, collider.size.y);

        spawnedBeats.Add(beat);
    }



    float GetSpeedForPattern(string patternName)
    {
        switch (patternName)
        {
            case "FastPattern":
                return 1.5f; // ✅ Faster beats
            case "SlowPattern":
                return 0.75f; // ✅ Slower beats
            default:
                return 1f; // ✅ Default speed
        }
    }


    void HighlightCurrentStep(int activeIndex)
    {
        foreach (var beat in spawnedBeats)
        {
            float beatX = beat.transform.position.x;
            float activeX = screenMinX + (activeIndex * stepWidth) + (stepWidth / 2); // Center alignment

            bool isActive = Mathf.Abs(beatX - activeX) < (stepWidth * 0.5f);
            SpriteRenderer sprite = beat.GetComponent<SpriteRenderer>();
            if (sprite != null)
                sprite.color = isActive ? activeColor : noteColor;
        }
    }

}
