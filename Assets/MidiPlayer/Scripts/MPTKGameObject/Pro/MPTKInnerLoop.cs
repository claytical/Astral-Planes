using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Scripting;
using static MidiPlayerTK.MPTKInnerLoop;

namespace MidiPlayerTK
{
    /// @ingroup midifileplayer_pro
    /// <summary>
    /// MIDI inner-loop settings for MidiFilePlayer and MidiExternalPlayer [Pro].\n
    /// See MidiFilePlayer.MPTK_InnerLoop and MidiExternalPlayer.MPTK_InnerLoop. See example:\n
    /// @snippet TestInnerLoop.cs ExampleMidiInnerLoop
    /// </summary>
    public class MPTKInnerLoop
    {
        /// <summary>@brief
        /// Enable log messages.
        /// </summary>
        public bool Log;

        /// <summary>@brief
        /// Loop phase action sent to #OnEventInnerLoop.
        /// </summary>
        public enum InnerLoopPhase
        {
            /// <summary>
            /// Start the loop.
            /// </summary>
            Start,

            /// <summary>
            /// Resume the loop.
            /// </summary>
            Resume,

            /// <summary>
            /// Exit the loop.
            /// </summary>
            Exit
        }

        /// <summary>@brief
        /// Callback triggered when an inner-loop event occurs.
        /// Parameters:
        ///     - InnerLoopPhase  current loop phase
        ///     - long            current player tick (MPTK_TickPlayer)
        ///     - long            loop end tick (End)
        ///     - long            loop count (Count)
        /// Return:
        ///     - boolean         true to continue looping, false to exit.
        /// @note 
        ///     - This callback runs on the MIDI thread, not on the Unity thread.
        ///     - It is not possible to call Unity APIs (except Debug.Log).
        ///     - This runs on a managed thread context, so your script fields remain accessible.
        /// </summary>
        public Func<InnerLoopPhase, long, long, int, bool> OnEventInnerLoop;

        /// <summary>@brief
        /// Enable or disable the loop. Default is false.
        /// </summary>
        public bool Enabled;

        /// <summary>@brief
        /// Becomes true when the loop has finished or when OnEventInnerLoop returns false.\n
        /// Can also be set to true to stop looping.
        /// </summary>
        public bool Finished;

        /// <summary>@brief
        /// Tick position where the loop begins when MIDI starts. The sequencer jumps immediately to this position.\n
        /// If #Start > #Resume, the loop begins at #Resume. Default is 0.
        /// </summary> 
        public long Start;

        /// <summary>@brief
        /// Tick position where the loop resumes when MidiLoad.MPTK_TickPlayer >= #End. Default is 0.
        /// See also MidiFilePlayer.MPTK_RawSeek.
        /// </summary>
        public long Resume;

        /// <summary>@brief
        /// Tick position that triggers a restart at #Resume (when MidiLoad.MPTK_TickPlayer >= #End). Default is 0.
        /// </summary>
        public long End;

        /// <summary>@brief
        /// Maximum number of loop iterations, including the first pass. When #Count >= #Max, the sequencer continues with MIDI events after tick #End.\n
        /// Set to 0 for an infinite loop. Default is 0.
        /// </summary>
        public int Max;

        /// <summary>@brief
        /// Current loop count. Default is 0.
        /// </summary>
        public int Count;

        [Preserve]
        public MPTKInnerLoop()
        {
        }

        /// <summary>@brief
        /// Resets inner-loop state.
        ///     Enabled = false, Finished = false
        ///     Start = 0,  Resume = 0,  End = 0
        ///     Count = 0
        /// </summary>
        public void Clear()
        {
            //Debug.Log("MPTKInnerLoop Clear");
            Enabled = false;
            Finished = false;
            Start = 0;
            Resume = 0;
            End = 0;
            Count = 0;
        }

        public override string ToString()
        {
            return $"MPTKInnerLoop Enabled:{Enabled} Finished:{Finished} Start:{Start} Resume:{Resume} End:{End} Count:{Count}/{Max}";
        }
    }
}
