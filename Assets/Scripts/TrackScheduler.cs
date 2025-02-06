using System.Collections.Generic;
using UnityEngine;
using MidiPlayerTK;

public class TrackScheduler
{
    private MidiStreamPlayer instrumentPlayer;
    public List<MPTKEvent> midiEvents = new List<MPTKEvent>();
    private int instrumentChannel;
    private int instrumentPreset;
    private int instrumentBank;
    private int loopLengthTicks;

    
    public TrackScheduler(MidiStreamPlayer player, int channel, int loopLength, int assignedPreset, int assignedBank)
    {
        instrumentPlayer = player;
        instrumentChannel = channel;
        loopLengthTicks = loopLength;
        instrumentPreset = assignedPreset;
        instrumentBank = assignedBank;
    }

    public void SetInstrument()
    {
        instrumentPlayer.MPTK_Channels[instrumentChannel].ForcedPreset = instrumentPreset;
        instrumentPlayer.MPTK_Channels[instrumentChannel].ForcedBank = instrumentBank;
    }
    public void LoadMidiEvents(List<MPTKEvent> events)
    {
        if (events == null || events.Count == 0)
        {
            Debug.LogError("❌ ERROR: No MIDI events found for TrackScheduler.");
            return;
        }

        midiEvents = events;
        Debug.Log($"✅ Loaded {midiEvents.Count} MIDI events for {instrumentPlayer.name} on Channel {instrumentChannel}");
    }

    public void ProcessTrack(long masterTick)
    {
        long localTick = masterTick % loopLengthTicks;

        instrumentPlayer.MPTK_Channels[instrumentChannel].ForcedPreset = instrumentPreset;
        instrumentPlayer.MPTK_Channels[instrumentChannel].ForcedBank = instrumentBank;

        instrumentPlayer.MPTK_EnableChangeTempo = true;

        foreach (var midiEvent in midiEvents)
        {
            long normalizedTick = midiEvent.Tick % loopLengthTicks;

            // 🔍 Prevent duplicate notes by ensuring each note is played only once per loop
            if (normalizedTick == localTick)
            {
                //midiEvent.Played = true; // ✅ Ensure it’s not played again

                if (midiEvent.Command == MPTKCommand.NoteOn)
                {
                    midiEvent.Velocity = Mathf.Clamp(midiEvent.Velocity, 1, 127); // Ensure non-zero velocity
                    Debug.Log($"🔊 Playing Note {midiEvent.Value} on Channel {midiEvent.Channel} at tick {localTick}");
                    instrumentPlayer.MPTK_PlayEvent(midiEvent);
                }
            }
        }
    }


}
