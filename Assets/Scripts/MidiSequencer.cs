using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidiPlayerTK;

[System.Serializable]
public class DrumEvent
{
    public int tick; // The tick position of the event
    public int note; // MIDI note (e.g., 36 for kick drum, 38 for snare)
    public int velocity = 100; // Velocity (default: 100)
    public int duration = 500; // Duration in milliseconds (default: 500)
}

public class MidiSequencer : MonoBehaviour
{
    public float bpm = 150f; // Tempo in beats per minute (set in script)
    private float nextTickTime; // Next time to play notes
    public MidiStreamPlayer drums;
    public List<MidiStreamPlayer> instrumentPlayers; // Stream players for instruments
    public SequenceManager startingSequence;
    //public List<SequenceManager> sequences; // File players for loading MIDI files
    public List<List<MPTKEvent>> instrumentTracks; // Parsed note events for each instrument
    public List<DrumEvent> kickDrums = new List<DrumEvent>();
    public List<DrumEvent> snareDrums = new List<DrumEvent>();
    public List<DrumEvent> hiHats = new List<DrumEvent>();
    private int currentTick; // Current tick (step) in the sequence
    private int totalTicks; // Total ticks for looping (calculated based on longest track)
//    private int sequenceIndex = 0;
    private bool nextSequence = false;
//    private int nextSequenceIndex = -1;
    private SequenceManager nextSequenceManager;

    public delegate void MidiEventPlayedHandler(MPTKEvent midiEvent, int trackIndex);
    public event MidiEventPlayedHandler OnMidiEventPlayed;
    void Start()
    {
        instrumentTracks = new List<List<MPTKEvent>>();
        AddDrumEvent(kickDrums, 0, 36,  10);

        // Calculate seconds per beat based on the tempo
        nextTickTime = Time.time;
        if(startingSequence)
        {
            LoadSequence(startingSequence);
        }
    }

    public void DrumPattern_Heartbeat()
    {
//0
        AddDrumEvent(kickDrums, 0, 36, 40);
        AddDrumEvent(kickDrums, 1, 36, 50);
//2
        AddDrumEvent(kickDrums, 8, 36, 40);
        AddDrumEvent(kickDrums, 9, 36, 50);
//3
        AddDrumEvent(kickDrums, 16, 36, 40);
        AddDrumEvent(kickDrums, 17, 36, 50);
//4
        AddDrumEvent(kickDrums, 24, 36, 40);
        AddDrumEvent(kickDrums, 25, 36, 50);
    }

    public void DrumPattern_SkipKick()
    {
//0, 
        AddDrumEvent(kickDrums, 0, 36, 85);
//        AddDrumEvent(kickDrums, 8, 36, 85);
//        AddDrumEvent(kickDrums, 10, 36, 85);
//       AddDrumEvent(kickDrums, 14, 36, 85);
        AddDrumEvent(kickDrums, 16, 36, 85);
        AddDrumEvent(kickDrums, 18, 36, 85);
        AddDrumEvent(kickDrums, 20, 36, 85);

        AddDrumEvent(kickDrums, 24, 36, 85);



        AddDrumEvent(snareDrums, 4, 40, 85);
        AddDrumEvent(snareDrums, 12, 40, 85);
        AddDrumEvent(snareDrums, 20, 40, 85);
        AddDrumEvent(snareDrums, 28, 40, 85);


        /*

        AddDrumEvent(snareDrums, 4, 36, 85);

        //8,10,12,14
        AddDrumEvent(snareDrums, 8, 69, 85);
        AddDrumEvent(snareDrums, 12, 36, 85);


        //3     16, 18, 20, 22
        AddDrumEvent(kickDrums, 16, 36, 85);
        AddDrumEvent(kickDrums, 18, 36, 85);
        AddDrumEvent(snareDrums, 20, 36, 85);
        AddDrumEvent(kickDrums, 22, 36, 85);

//4        24, 26, 28, 30, 32

        AddDrumEvent(kickDrums, 26, 36, 85);
        AddDrumEvent(snareDrums, 28, 36, 85);
        AddDrumEvent(snareDrums, 30, 37, 85);

        */
    }
    private void CalculateTotalTicks()
    {
        // Manually set the totalTicks for the global sequence
        totalTicks = 32; // Fixed to the drum track's pattern length

        Debug.Log($"Global totalTicks set to: {totalTicks}");
    }

    public void SetNextSequence(SequenceManager ns)
    {
        nextSequence = true;
        nextSequenceManager = ns;
    }
    public void LoadSequence(SequenceManager sequence)
    {
        // Clear previous tracks
        instrumentTracks.Clear();
        currentTick = 0;
        bpm = sequence.BPM;
        // Get the SequenceManager from the parent object

        // Parse and assign instruments dynamically
        foreach (var midiInstrument in sequence.midiInstruments)
        {
            if (midiInstrument.filePlayer != null && midiInstrument.instrumentIndex >= 0 && midiInstrument.instrumentIndex < instrumentPlayers.Count)
            {
                midiInstrument.filePlayer.MPTK_Volume = midiInstrument.volume;
                // Configure the MidiStreamPlayer for this instrument
                StartCoroutine(ConfigureMidiStreamPlayer(instrumentPlayers[midiInstrument.instrumentIndex], midiInstrument));

                // Parse MIDI events for this instrument
                List<MPTKEvent> trackEvents = ParseMidiEvents(midiInstrument.filePlayer, midiInstrument.instrumentIndex);
                instrumentTracks.Add(trackEvents);

             Debug.Log($"Loaded instrument {midiInstrument.instrumentIndex}: Preset {midiInstrument.preset}, Channel {midiInstrument.instrumentIndex}, Events: {trackEvents.Count}");
            }
            else
            {
                // Skip unassigned or invalid instruments
                instrumentTracks.Add(null);
                Debug.Log($"Bypassed instrument at index {midiInstrument.instrumentIndex}");
            }
        }

        GenerateDrumPattern(sequence.drumPattern);
        drums.MPTK_Volume = sequence.drumVolume;

        CalculateTotalTicks();
        // Initialize timing
        nextTickTime = Time.time;

        Debug.Log($"Sequence loaded with {totalTicks} total ticks.");
    }

    private void GenerateDrumPattern(SequenceManager.DrumPatternType patternType)
    {
        kickDrums.Clear();
        snareDrums.Clear();
        hiHats.Clear();

        switch (patternType)
        {
            case SequenceManager.DrumPatternType.Silence:
                AddDrumEvent(kickDrums, 0, 36, 0);
                break;

            case SequenceManager.DrumPatternType.Heartbeat:
                DrumPattern_Heartbeat();
                break;
            case SequenceManager.DrumPatternType.SkipKick:
                DrumPattern_SkipKick();
                break;
            case SequenceManager.DrumPatternType.Rock:
                AddDrumEvent(kickDrums, 0, 36);
                AddDrumEvent(snareDrums, 4, 38);
                AddDrumEvent(kickDrums, 8, 36);
                AddDrumEvent(snareDrums, 12, 38);
                AddDrumEvent(hiHats, 2, 42);
                AddDrumEvent(hiHats, 6, 42);
                AddDrumEvent(hiHats, 10, 42);
                AddDrumEvent(hiHats, 14, 42);
                break;

            case SequenceManager.DrumPatternType.Custom:
                Debug.Log("Custom pattern selected. Add your custom logic here.");
                break;

            default:
                Debug.LogWarning("Unknown drum pattern type!");
                break;
        }

        Debug.Log($"Drum pattern generated: {patternType}");
    }

    private IEnumerator ConfigureMidiStreamPlayer(MidiStreamPlayer player, SequenceManager.MidiInstrument midiInstrument)
{
    // Wait until MPTK_Channels is initialized
    while (player.MPTK_Channels == null || player.MPTK_Channels.Length == 0)
    {
        yield return null; // Wait for the next frame
    }

    // Set the preset and bank for the assigned channel
    if (midiInstrument.instrumentIndex >= 0 && midiInstrument.instrumentIndex < player.MPTK_Channels.Length)
    {
        player.MPTK_Channels[midiInstrument.instrumentIndex].PresetNum = midiInstrument.preset;
        player.MPTK_Channels[midiInstrument.instrumentIndex].BankNum = midiInstrument.bank;
        Debug.Log($"Configured player for instrument {midiInstrument.instrumentIndex} with preset {midiInstrument.preset} and bank {midiInstrument.bank}");
    }
    else
    {
        Debug.LogError($"Invalid channel index {midiInstrument.instrumentIndex} or uninitialized MPTK_Channels.");
    }
}

    private List<MPTKEvent> ParseMidiEvents(MidiFilePlayer midiFilePlayer, int assignedChannel)
    {
        List<MPTKEvent> trackEvents = new List<MPTKEvent>();
        midiFilePlayer.MPTK_PlayOnStart = false;
        midiFilePlayer.MPTK_Load();

        foreach (var midiEvent in midiFilePlayer.MPTK_MidiEvents)
        {
            if (midiEvent.Command == MPTKCommand.NoteOn) // Only consider NoteOn events
            {
                midiEvent.Channel = assignedChannel;
                trackEvents.Add(new MPTKEvent()
                {
                    Command = midiEvent.Command,
                    Value = midiEvent.Value,
                    Channel = assignedChannel,
                    Velocity = midiEvent.Velocity,
                    Duration = midiEvent.Duration
                });
            }
        }

        return trackEvents;
    }

    void Update()
    {
        // Synchronize playback based on tempo
        if (Time.time >= nextTickTime)
        {
            PlayNotesAtCurrentTick();
            PlayDrumTrackAtCurrentTick();
            currentTick++;

            // Loop back to the beginning
            if (currentTick >= totalTicks)
            {
                currentTick = 0;
                Debug.Log("Looping back to start. Total Ticks: " + totalTicks);
            }

            nextTickTime += (60f / bpm) / (totalTicks / 16f);
            if(nextSequence)
            {
                
                Debug.Log("Switching to Sequence #" + nextSequenceManager.name);


                if (!nextSequenceManager)
                {
                    Debug.Log("Invalid Sequence Index");
                }
                else
                {
                    
                    LoadSequence(nextSequenceManager);
                    nextSequence = false;

                }

            }
        }
    }
    private void PlayDrumTrackAtCurrentTick()
    {
        // Play kick drum events
        foreach (var drum in kickDrums)
        {
            if (drum.tick == currentTick % 32) // Assuming a 16-tick loop
            {

                var kickEvent = new MPTKEvent()
                {
                    Command = MPTKCommand.NoteOn,
                    Value = drum.note,
                    Channel = 9,
                    Velocity = drum.velocity,
                    Duration = drum.duration
                };

                drums.MPTK_PlayEvent(kickEvent);
                OnMidiEventPlayed?.Invoke(kickEvent, -1); // Use -1 for drum events

            }
        }

        // Play snare drum events
        foreach (var drum in snareDrums)
        {
            if (drum.tick == currentTick % 32)
            {
                var snareEvent = new MPTKEvent()
                {
                    Command = MPTKCommand.NoteOn,
                    Value = drum.note,
                    Channel = 9,
                    Velocity = drum.velocity,
                    Duration = drum.duration
                };

                drums.MPTK_PlayEvent(snareEvent);
                OnMidiEventPlayed?.Invoke(snareEvent, -1);
            }
        }

        // Play hi-hat events
        foreach (var drum in hiHats)
        {
            if (drum.tick == currentTick % 32)
            {
                var hiHatEvent = new MPTKEvent()
                {
                    Command = MPTKCommand.NoteOn,
                    Value = drum.note,
                    Channel = 9,
                    Velocity = drum.velocity,
                    Duration = drum.duration
                };

                drums.MPTK_PlayEvent(hiHatEvent);
                OnMidiEventPlayed?.Invoke(hiHatEvent, -1);
            }
        }
    }
    private List<int> GenerateHiHatHits(int paceLevel, int totalTicks)
    {
        List<int> hiHatHits = new List<int>();

        // Calculate the step size based on the pace level
        int step = (int)Mathf.Max(1, totalTicks / Mathf.Pow(2, paceLevel - 1));

        // Add hit positions by stepping through the total ticks
        for (int i = 0; i < totalTicks; i += step)
        {
            hiHatHits.Add(i);
        }

        return hiHatHits;
    }
    public void UpdateDrumTrack(int paceLevel, int totalTicks)
    {
        List<int> hiHatHits = GenerateHiHatHits(paceLevel, totalTicks);

        // Clear existing hi-hat events
        hiHats.Clear();

        // Add new hi-hat events
        foreach (int tick in hiHatHits)
        {
            hiHats.Add(new DrumEvent()
            {
                tick = tick,
                note = 42, // MIDI note for closed hi-hat
                velocity = 100,
                duration = 100 // Arbitrary duration
            });
        }

        Debug.Log($"Updated drum track for pace level {paceLevel}.");
    }


    // Add a drum event
    public void AddDrumEvent(List<DrumEvent> drumList, int tick, int note, int velocity = 100, int duration = 500)
    {
        drumList.Add(new DrumEvent() { tick = tick, note = note, velocity = velocity, duration = duration });
        Debug.Log($"Added drum event at tick {tick} with note {note}");
    }

    // Remove a drum event
    public void RemoveDrumEvent(List<DrumEvent> drumList, int tick)
    {
        drumList.RemoveAll(drum => drum.tick == tick);
        Debug.Log($"Removed drum event(s) at tick {tick}");
    }
    private void PlayNotesAtCurrentTick()
    {
        // Loop through each track in instrumentTracks
        for (int i = 0; i < instrumentTracks.Count; i++)
        {
            // Ensure the track is valid and contains events
            if (instrumentTracks[i] != null && currentTick < instrumentTracks[i].Count)
            {
                // Get the event for the current tick
                var noteEvent = instrumentTracks[i][currentTick];
                if (noteEvent != null)
                {
                    // Play the event on the corresponding MidiStreamPlayer
                    instrumentPlayers[i].MPTK_PlayEvent(noteEvent);
                    OnMidiEventPlayed?.Invoke(noteEvent, i);
                }
            }
        }
    }

}
