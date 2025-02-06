using UnityEngine;
using MidiPlayerTK;
using System.Collections.Generic;
using System.Collections;

public class DrumTrack : MonoBehaviour
{
    public MidiFilePlayer drums;
    public GameObject beatPrefab; // Prefab for visualization
    //public Transform beatParent; // Parent for instantiated beats
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
    private int totalSteps = 32; // Number of steps in the loop
    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    private float stepWidth; // Dynamic width per step
    public List<string> drumPatterns; // ✅ Store MIDI file names instead of TextAssets
    private Queue<string> patternQueue = new Queue<string>(); // ✅ Queue for upcoming MIDI switches
    private bool isSwitching = false; // ✅ Prevent multiple switches at once

    void Start()
    {
        stepWidth = (screenMaxX - screenMinX) / totalSteps; // Correct step spacing
        AnalyzeMidiForNotes();
    }
    public void QueueDrumPattern(string midiName)
    {
        if (!patternQueue.Contains(midiName))
        {
            patternQueue.Enqueue(midiName);
            Debug.Log($"Queued new drum pattern: {midiName}");
        }
    }
    void MoveBeatsDownward()
    {
        for (int i = 0; i < spawnedBeats.Count; i++)
        {
            GameObject beat = spawnedBeats[i];
            beat.transform.position += Vector3.down * beatMoveSpeed * Time.deltaTime;

            if (beat.transform.position.y < screenBottomY)
            {
                RestartBeat(beat);
            }
        }
    }


    void AnalyzeMidiForNotes()
    {
        foreach (GameObject obj in spawnedBeats)
        {
            if (obj != null)
                Destroy(obj);
        }
        spawnedBeats.Clear();

        if (drums == null) return;

        MidiLoad midiData = drums.MPTK_Load();
        if (midiData == null) return;

        long totalTicks = drums.MPTK_TickLast;

        foreach (MPTKEvent midiEvent in midiData.MPTK_MidiEvents)
        {
            if (midiEvent.Command == MPTKCommand.NoteOn && midiEvent.Channel == 9) // Drum channel
            {
                int startStep = SnapToClosestStep(midiEvent.Tick, totalTicks);
                int endStep = SnapToClosestStep(midiEvent.Tick + midiEvent.Duration, totalTicks);
                int durationSteps = Mathf.Max(1, endStep - startStep); // Ensure at least 1 step width

                SpawnBeatObject(startStep, durationSteps);
            }
        }
    }

    void SpawnBeatObject(int startStep, int durationSteps)
    {
        if (startStep < 0 || startStep >= totalSteps) return;

        // Instantiate prefab
        GameObject beat = Instantiate(beatPrefab, this.transform);

        // **Calculate the correct position**
        float startX = screenMinX + (startStep * stepWidth); // Align start position
        float width = durationSteps * stepWidth; // Scale width dynamically

        // **Position & Scale**
        beat.transform.position = new Vector3(startX + (width / 2), 0, 0); // Centered correctly
 //       beat.transform.localScale = new Vector3(width, beat.transform.localScale.y, beat.transform.localScale.z);

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

    void Update()
    {
        if (drums != null && spawnedBeats.Count > 0)
        {
            long currentTick = drums.MPTK_TickCurrent;
            long totalTicks = drums.MPTK_TickLast;

            if (totalTicks > 0)
            {
                int currentIndex = SnapToClosestStep(currentTick, totalTicks);

                if (currentIndex != lastIndex)
                {
                    HighlightCurrentStep(currentIndex);
                    lastIndex = currentIndex;
                }

                // ✅ Switch pattern at end of loop if a new one is queued
                if (currentTick < 10 && patternQueue.Count > 0 && !isSwitching)
                {
                    StartCoroutine(SwitchPattern());
                }
            }
            MoveBeatsDownward();
        }
    }
    IEnumerator SwitchPattern()
    {
        isSwitching = true;

        if (patternQueue.Count > 0)
        {
            string newPattern = patternQueue.Dequeue();
            Debug.Log($"Seamlessly switching to new drum pattern: {newPattern}");

            // ✅ Preload the new pattern
            drums.MPTK_MidiName = newPattern;
            drums.MPTK_Load();

            // ✅ Ensure current loop fully plays out
            long lastTick = drums.MPTK_TickLast;
            yield return new WaitUntil(() => drums.MPTK_TickCurrent >= lastTick - 5);

            // ✅ Play new pattern at the correct timing
            drums.MPTK_Play();

            // ✅ Adjust beat speed
            beatSpeedMultiplier = GetSpeedForPattern(newPattern);

            Debug.Log($"Switched to {newPattern} with speed multiplier {beatSpeedMultiplier}");
        }

        isSwitching = false;
    }

    void RestartBeat(GameObject beat)
    {
        beat.transform.position = new Vector3(beat.transform.position.x, screenTopY, beat.transform.position.z);

        completedCycles++;
        cycleCount = completedCycles;
        Debug.Log("CYCLES: " + completedCycles + " / "  + cyclesToSwitchPattern + " / "+ cycleCount);
        if (completedCycles >= cyclesToSwitchPattern)
        {
            Debug.Log("RESET CYCLE");

            completedCycles = 0; // ✅ Reset count
            QueueDrumPattern(drumPatterns[cycleCount % drumPatterns.Count]);
            StartCoroutine(SwitchPattern()); // ✅ Change drum pattern
        }
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

    int SnapToClosestStep(long currentTick, long totalTicks)
    {
        float stepSize = totalTicks / (float)totalSteps;
        int snappedStep = Mathf.RoundToInt(currentTick / stepSize);
        return Mathf.Clamp(snappedStep, 0, totalSteps - 1);
    }
}
