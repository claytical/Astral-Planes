using UnityEngine;
using MidiPlayerTK;
using System.Collections.Generic;

public enum DrumPatternType
{
    Heartbeat,
    Silence,
    SkipKick,
    Rock,
    Custom
}
[System.Serializable]
public class DrumTrackScheduler
{
    private MidiStreamPlayer drumPlayer;
    private List<MPTKEvent> drumEvents = new List<MPTKEvent>();
    private int loopLengthTicks = 32;

    public DrumTrackScheduler(MidiStreamPlayer player)
    {
        if (player == null)
        {
            Debug.LogError("DrumTrackScheduler: MidiStreamPlayer is NULL! Assign a valid player.");
            return;
        }

        drumPlayer = player;
        loopLengthTicks = 32;
    }
    public void LoadDrumPattern(DrumPatternType patternType)
    {
        if (drumPlayer == null)
        {
            Debug.LogError("DrumTrackScheduler: drumPlayer is NULL! Cannot load drum pattern.");
            return;
        }

        drumEvents.Clear();

        switch (patternType)
        {
            case DrumPatternType.Heartbeat:
                AddDrumHit(0, 36);
                AddDrumHit(8, 36);
                AddDrumHit(16, 36);
                AddDrumHit(24, 36);
                break;
            case DrumPatternType.SkipKick:
                AddDrumHit(0, 36);
                AddDrumHit(16, 36);
                AddDrumHit(24, 36);
                AddDrumHit(4, 40);
                AddDrumHit(12, 40);
                break;
            case DrumPatternType.Rock:
                AddDrumHit(0, 36);
                AddDrumHit(4, 38);
                AddDrumHit(8, 36);
                AddDrumHit(12, 38);
                break;
            case DrumPatternType.Silence:
            default:
                break;
        }
        // 🔥 Ensure `loopLengthTicks` is correctly set
        if (drumEvents.Count > 0)
        {
            loopLengthTicks = 32; // Or dynamically set based on drum pattern length
        }
        else
        {
            loopLengthTicks = 1; // 🔥 Prevent divide by zero error
        }
    }

    private void AddDrumHit(int tick, int note)
    {
        drumEvents.Add(new MPTKEvent
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = 9,
            Velocity = 100,
            Duration = 500,
            Tick = tick
        });
    }

    public void ProcessDrumTrack(int masterTick)
    {
        // 🔥 FIX: Prevent divide by zero error
        if (loopLengthTicks <= 0)
        {
            Debug.LogError("DrumTrackScheduler: loopLengthTicks is 0! Preventing division by zero.");
            return;
        }

        int localTick = masterTick % loopLengthTicks;

        foreach (var drumEvent in drumEvents)
        {
            if (drumEvent.Tick == localTick)
            {
                drumPlayer.MPTK_PlayEvent(drumEvent);
            }
        }
    }
}
