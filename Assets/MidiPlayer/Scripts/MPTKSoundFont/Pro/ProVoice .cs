#define DEBUGPERF

using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MidiPlayerTK
{
    public partial class fluid_voice
    {
        bool pauseVoice = false;
        long pauseDuration;
        long pauseStartTick;
        long pauseEndTick;
        long pausePlayedTicks;
        float pauseRampCurrent;
        float pauseRampDelay;
        float pauseRampDelta;
        bool pauseDebug = false;

        // Uncomment only for checking the effective duration of the transition
        //DateTime startRamp;

        /// <summary>
        /// Pauses the current voice playback and applies a transition effect over the specified duration.
        /// </summary>
        /// <remarks>This method pauses the playback if it is not already paused, applying a transition
        /// effect over the specified duration. It records the playback position to allow resumption from the same
        /// point. If debugging is enabled, additional debug information is logged.</remarks>
        /// <param name="transitionDuration">The duration, in seconds, over which the transition effect is applied. Must be a non-negative value.</param>
        public void Pause(float transitionDuration)
        {
            if (!pauseVoice)
            {
                if (pauseStartTick == 0)
                    pausePlayedTicks = DateTime.UtcNow.Ticks - TimeAtStart;
                else
                    pausePlayedTicks += DateTime.UtcNow.Ticks - pauseEndTick;
                pauseStartTick = DateTime.UtcNow.Ticks;
                ApplyRampTransition(transitionDuration);
                pauseVoice = true;
                if (pauseDebug) DebugPauseVoice();
            }
        }

        private void ApplyRampTransition(float transitionDuration)
        {
            // Smooth target will be call 8 times by frame, frame duration is 10 ms, so each call must decrease current ramp for 10/8 ms
            pauseRampDelay = transitionDuration;
            pauseRampDelta = (float)synth.DeltaTimeAudioCall / (synth.DspBufferSize / synth.FLUID_BUFSIZE);
            pauseRampCurrent = pauseRampDelay;
            // Uncomment only for checking the effective duration of the transition
            //Debug.Log($"ApplyRampTransition wanted duration:{transitionDuration} ms Count:{(synth.DspBufferSize / synth.FLUID_BUFSIZE)} pauseRampDelay:{pauseRampDelay} pauseRampDelta:{pauseRampDelta}");
            //startRamp = DateTime.Now;
        }

        /// <summary>
        /// Resumes playback from a paused state, applying a transition effect over the specified duration.
        /// </summary>
        /// <remarks>This method resumes the playback if it is currently paused. The transition effect is
        /// applied to smoothly ramp up the audio from the paused state.</remarks>
        /// <param name="transitionDuration">The duration, in seconds, over which the transition effect is applied when resuming playback.</param>
        public void Resume(float transitionDuration)
        {
            if (pauseVoice)
            {
                pauseEndTick = DateTime.UtcNow.Ticks;
                pauseDuration += DateTime.UtcNow.Ticks - pauseStartTick;
                ApplyRampTransition(transitionDuration);
                pauseVoice = false;
                if (pauseDebug) DebugPauseVoice();
            }
        }

        /// <summary>
        /// Adjusts the playback pause state based on the current pause state and duration.
        /// </summary>
        /// <remarks>This method accounts for different pause states, including ongoing pause ramps and
        /// complete pauses, to determine the correct playback position. It ensures that the playback resumes correctly
        /// after a pause or continues without interruption if no pause is active.
        /// Warning: call for each buffer (64 bytes) at each audio frames. Keep it efficient.
        /// </remarks>
        /// <param name="tick">The current playback position in ticks.</param>
        /// <returns>The adjusted playback position in ticks. Returns <see langword="-1"/> if the voice is paused, indicating
        /// that the synthesis process should exit. If a pause ramp is in progress, returns the position adjusted by the
        /// pause duration.</returns>
        public long PauseApply(long tick)
        {
            if (pauseRampCurrent > 0f)
                // Pause or resume in progress (ramp)
                return tick - pauseDuration;

            if (pauseVoice)
                // Voice is paused, exit synth process
                return -1;
            else if (pauseDuration == 0)
                // Voice is playing without a delay
                return tick;
            else
                // Voice is playing with a delay after a pause 
                return tick - pauseDuration;
        }

        /// <summary>
        /// Smoothly transitions the amplitude towards a target value based on the current pause state.
        /// </summary>
        /// <remarks>This method uses linear interpolation to adjust the amplitude smoothly over time.  If
        /// the system is in a paused state, the amplitude transitions towards zero; otherwise, it transitions towards
        /// the specified target amplitude.
        /// Warning: call for each buffer (64 bytes) at each audio frames. Keep it efficient.
        /// </remarks>
        /// <param name="target_amp">The target amplitude to transition towards. Must be a non-negative value.</param>
        /// <returns>The smoothed amplitude value, transitioning towards the target amplitude.</returns>
        public float SmoothAmp(float target_amp)
        {
            pauseRampCurrent -= pauseRampDelta;
            float smooth;
            if (pauseVoice)
                // Go to low
                smooth = Mathf.Lerp(0f, target_amp, pauseRampCurrent / pauseRampDelay);
            else
                smooth = Mathf.Lerp(target_amp, 0f, pauseRampCurrent / pauseRampDelay);

            // Uncomment only for checking the effective duration of the transition
            // Debug.Log($"Ramp over DeltaTimeAudioCall:{synth.DeltaTimeAudioCall:F2} ramp:{(DateTime.Now-startRamp).TotalMilliseconds:F0} ms.");
            // Debug.Log($"SmoothAmp pauseRampCurrent:{pauseRampCurrent} Ratio:{(pauseRampCurrent / (float)pauseRampDelay):F2} target:{target_amp} --> smooth:{smooth}");
            return smooth;

        }
        private void DebugPauseVoice([CallerMemberName] string caller = "")
        {
            long remainingTicks = DurationTick - pausePlayedTicks;
            long elapsedTicks = DateTime.UtcNow.Ticks - TimeAtStart;
            string adsr = $"{volenv_section} count:{volenv_count} / {volenv_data[(int)volenv_section].count}";

            Debug.Log($"{caller} voice {id} Duration:{TimeSpan.FromTicks(DurationTick).TotalSeconds:F2} s. " +
                $"Played:{TimeSpan.FromTicks(pausePlayedTicks).TotalSeconds:F2} s. " +
                $"Remain:{TimeSpan.FromTicks(remainingTicks).TotalSeconds:F2} s. " +
                $"Paused:{TimeSpan.FromTicks(pauseDuration).TotalSeconds:F2} s. " +
                $"Elapse:{TimeSpan.FromTicks(elapsedTicks).TotalSeconds:F2} s. " +
                $"{adsr}");

            // {DateTime.UtcNow.ToLongTimeString()}
        }

        public void ResetPause()
        {
            pauseVoice = false;
            pauseDuration = 0;
            pauseStartTick = 0;
            pauseEndTick = 0;
            pausePlayedTicks = 0;
            pauseRampCurrent = 0;
            pauseRampDelay = 400;
            pauseRampDelta = 0;
        }
    }
}
