using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidiPlayerTK;
using System;

public class LoopManager : MonoBehaviour
{
    public List<MidiStreamPlayer> instrumentPlayers;
    public MidiStreamPlayer drums;
    private List<TrackScheduler> trackSchedulers = new List<TrackScheduler>();
    public SequenceManager startingSequence;

    private long totalTicks;
    private long currentTick;
    private float nextTickTime;
    private int bpm;
    int frameCount = 0;
    void Start()
    {
        InitializeTrackSchedulers();
    }

    private void InitializeTrackSchedulers()
    {
        trackSchedulers.Clear();

        if (instrumentPlayers == null || instrumentPlayers.Count == 0)
        {
            Debug.LogError("❌ ERROR: No MidiStreamPlayers assigned in LoopManager!");
            return;
        }

        for (int i = 0; i < instrumentPlayers.Count; i++)
        {
            if (instrumentPlayers[i] == null)
            {
                Debug.LogError($"❌ ERROR: MidiStreamPlayer at index {i} is NULL in LoopManager!");
                continue;
            }

            int assignedChannel = i + 1;
            trackSchedulers.Add(new TrackScheduler(instrumentPlayers[i], assignedChannel, 256, startingSequence.midiInstruments[i].preset, startingSequence.midiInstruments[i].bank));

            Debug.Log($"✅ Created TrackScheduler for {instrumentPlayers[i].name} on Channel {assignedChannel}");
        }
        LoadSequence(startingSequence);

    }

    public void LoadSequence(SequenceManager sequence)
    {
        if (sequence == null)
        {
            Debug.LogError("❌ ERROR: `SequenceManager` is NULL in LoadSequence!");
            return;
        }

        Debug.Log("🔍 Loading sequence " + sequence.name + " has " + sequence.midiInstruments.Count + " instruments.");
        currentTick = 0;
        bpm = sequence.BPM;

        for (int i = 0; i < sequence.midiInstruments.Count; i++)
        {
            var midiInstrument = sequence.midiInstruments[i];

            if (midiInstrument.filePlayer == null || instrumentPlayers[i] == null)
            {
                Debug.LogError($"❌ ERROR: Missing filePlayer or instrument for index {i}");
                continue;
            }

            int assignedChannel = i + 1;
            midiInstrument.filePlayer.MPTK_Volume = midiInstrument.volume;


            Debug.Log("🔍 Assigned Channel " + assignedChannel + " for " + midiInstrument.name);
            StartCoroutine(WaitForMPTKLoad(midiInstrument.filePlayer, assignedChannel, midiInstrument.preset, midiInstrument.bank));

            StartCoroutine(WaitForMidiEvents(midiInstrument.filePlayer, trackSchedulers[i], assignedChannel));
        }

        StartCoroutine(WaitAndCalculateTotalTicks());
    }


    private IEnumerator WaitForMPTKLoad(MidiFilePlayer filePlayer, int assignedChannel, int preset, int bank)
    {
        Debug.Log("Waiting for MPTKLoad...");
        filePlayer.MPTK_Load();

        while (filePlayer.MPTK_Channels == null)
        {
            yield return null;  // Wait for the next frame
        }

        // Wait until the assigned channel is properly initialized
        // Now it's safe to assign the channel values
       
        Debug.Log("✅ MPTK_Load complete for channel " + assignedChannel);
        filePlayer.MPTK_Channels[assignedChannel].ForcedPreset = preset;
        filePlayer.MPTK_Channels[assignedChannel].ForcedBank = bank;

    }
    // 🔥 New Coroutine to Ensure Ticks Are Calculated AFTER Events Load
    private IEnumerator WaitAndCalculateTotalTicks()
    {
        Debug.Log("⏳ Waiting for MIDI events to finish loading before calculating totalTicks...");

        yield return new WaitForSeconds(0.5f); // Small delay to ensure MIDI events are loaded

        CalculateTotalTicks();
        nextTickTime = Time.time;
    }

    private IEnumerator WaitForMidiEvents(MidiFilePlayer filePlayer, TrackScheduler scheduler, int assignedChannel)
    {
        Debug.Log($"⏳ Waiting for MIDI events to load for {filePlayer.name}...");

        yield return new WaitUntil(() => filePlayer.MPTK_MidiEvents != null && filePlayer.MPTK_MidiEvents.Count > 0);

        Debug.Log($"✅ MIDI events loaded for {filePlayer.name}, {filePlayer.MPTK_MidiEvents.Count} events found.");

        if (scheduler == null)
        {
            Debug.LogError($"❌ ERROR: Scheduler for {filePlayer.name} is NULL!");
            yield break;
        }

        List<MPTKEvent> correctedMidiEvents = new List<MPTKEvent>();
    
        foreach (var midiEvent in filePlayer.MPTK_MidiEvents)
        {
            if (midiEvent.Channel == 0)
            {
//                midiEvent.Channel = assignedChannel;
                Debug.Log($"Note On: {midiEvent.Value}, Duration: {midiEvent.Duration} ms");
            }
            correctedMidiEvents.Add(midiEvent);
        }

        scheduler.LoadMidiEvents(correctedMidiEvents);

    }
    IEnumerator StopNoteAfterDuration(MPTKEvent noteEvent)
    {
        yield return new WaitForSeconds(noteEvent.Duration / 1000f); // Convert ticks to seconds
        noteEvent.Command = MPTKCommand.NoteOff;  // 🔥 Manually send Note-Off
        Debug.Log($"🛑 Stopping Note {noteEvent.Value} after {noteEvent.Duration} ms");
        instrumentPlayers[noteEvent.Channel].MPTK_PlayEvent(noteEvent); // Send Note-Off event
    }
    private void CalculateTotalTicks()
    {
        totalTicks = 0;

        if (trackSchedulers == null || trackSchedulers.Count == 0)
        {
            Debug.LogError("❌ ERROR: `trackSchedulers` is NULL or EMPTY!");
            return;
        }

        bool foundEvents = false; // ✅ Track if any events are found

        foreach (var scheduler in trackSchedulers)
        {
            if (scheduler == null)
            {
                Debug.LogError("❌ ERROR: A TrackScheduler is NULL!");
                continue;
            }

            foreach (var midiEvent in scheduler.midiEvents)
            {
                if (midiEvent.Tick > totalTicks)
                {
                    totalTicks = midiEvent.Tick;
                    foundEvents = true;
                }
            }
        }

        if (!foundEvents)
        {
            Debug.LogWarning("⚠️ WARNING: No MIDI events found! Setting default totalTicks = 128.");
            totalTicks = 128; // ✅ Set a fallback value if no events are found
        }

        Debug.Log($"✅ Calculated totalTicks: {totalTicks}");
    }


    void Update()
    {

        if (totalTicks == 0)
        {
            Debug.LogWarning("⚠️ totalTicks is 0. Skipping tick processing.");
            return;
        }

        float tickDuration = (60f / bpm) / totalTicks;
        if (Time.time >= nextTickTime)
        {
            ProcessTick(currentTick);
            currentTick = (currentTick + 1) % totalTicks;
            nextTickTime += tickDuration;
        }
    }



    private void ProcessTick(long tick)
    {
        foreach (var scheduler in trackSchedulers)
        {
            scheduler.ProcessTrack(tick); // ✅ Pass `long` instead of `int`
        }
    }
}
