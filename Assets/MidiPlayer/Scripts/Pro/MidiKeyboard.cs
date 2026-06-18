using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace MidiPlayerTK
{
    /// @ingroup pro_midi_input
    /// <summary>
    /// Provides access to the native MidiKeyboard plugin to send and receive MIDI messages
    /// from desktop MIDI devices.
    /// <para>Version: Maestro Pro</para>
    /// <para>More information: https://paxstellar.fr/class-midikeyboard/</para>
    /// </summary>
    public class MidiKeyboard
    {
        /// <summary>
        /// Native plugin error return values (WinMM/MMSystem style).
        /// </summary>
        public enum PluginError
        {
            OK = 0,  // no error 
            UNSPECIFIED = 1,  // unspecified error 
            BADDEVICEID = 2,  // device ID out of range 
            DRIVERNOTENABLED = 3,  // driver failed enable 
            DEVICEALLOCATED = 4,  // device already allocated 
            INVALHANDLE = 5,  // device handle is invalid 
            NODRIVER = 6,  // no device driver present 
            NOMEM = 7,  // memory allocation error 
            NOTSUPPORTED = 8,  // function isn't supported 
            BADERRNUM = 9,  // error value out of range 
            INVALFLAG = 10, // invalid flag passed 
            INVALPARAM = 11, // invalid parameter passed 
            HANDLEBUSY = 12, // handle being used simultaneously on another thread (eg callback, 
            INVALIDALIAS = 13, // specified alias not found 
            BADDB = 14, // bad registry database 
            KEYNOTFOUND = 15, // registry key not found 
            READERROR = 16, // registry read error 
            WRITEERROR = 17, // registry write error 
            DELETEERROR = 18, // registry delete error 
            VALNOTFOUND = 19, // registry value not found 
            NODRIVERCB = 20, // driver does not call DriverCallback 
            MOREDATA = 21, // more data to be returned 
            LASTERROR = 21, // last error in range 
        }

        /// <summary>
        /// Event raised when an input MIDI message is available.
        /// @code
        /// if (enableRealTimeRead)
        /// {
        ///     MidiKeyboard.OnActionInputMidi += ProcessEvent;
        ///     MidiKeyboard.MPTK_SetRealTimeRead();
        /// }
        /// else
        /// {
        ///     MidiKeyboard.OnActionInputMidi -= ProcessEvent;
        ///     MidiKeyboard.MPTK_UnsetRealTimeRead();
        /// }
        /// @endcode
        /// </summary>
        [HideInInspector]
        public static event Action<MPTKEvent> OnActionInputMidi;// = (impactPoint) => { };

        //public static EventMidiClass OnEventInputMidi;

        private static string msgPluginsNotFound = "MidiKeyboard Plugin not found, please see here to setup https://paxstellar.fr/class-midikeyboard/";

        // V2.18.2 - more robust process for real-time MIDI input callback, with fallback to main thread dispatch when no synchronization context is available (eg in some Unity Editor contexts).
        private static SynchronizationContext unitySyncContext;
        private static MidiMsgDelegate realtimeMidiDelegate;
        private static volatile bool realtimeReadEnabled;
        private static readonly ConcurrentQueue<ulong> pendingRealtimeMessages = new ConcurrentQueue<ulong>();

        /// <summary>
        /// Clears the native MIDI input read queue.
        /// </summary>
        /// <returns>Implementation-specific status value from the native plugin.</returns>
        [DllImport("MidiKeyboard", EntryPoint = "MPTKClearReadQueue")]
        static public extern int MPTK_ClearReadQueue();

        /// <summary>
        /// Reads one MIDI message from the shared native input queue.
        /// </summary>
        /// <returns>A parsed <see cref="MPTKEvent"/> or null when the queue is empty.</returns>
        static public MPTKEvent MPTK_Read()
        {
            // Pop from the queue.
            ulong data = 0;
            try
            {
                data = _mptkRead();
            }
            catch (Exception)
            {
                Debug.LogWarning(msgPluginsNotFound);
                return null;
            }
            if (data == 0)
                // No more midi message, go out
                return null;
            else
                // Parse the message.
                return new MPTKEvent(data);
        }
        [DllImport("MidiKeyboard", EntryPoint = "MPTKRead")]
        static private extern ulong _mptkRead();

        /// <summary>
        /// Gets the number of MIDI messages waiting in the native read queue.
        /// </summary>
        /// <returns>Queue length.</returns>
        [DllImport("MidiKeyboard", EntryPoint = "MPTKSizeReadQueue")]
        static public extern int MPTK_SizeReadQueue();

        /// <summary>
        /// Gets the number of MIDI input devices currently detected by Windows (WinMM).
        /// </summary>
        /// <returns>Detected input device count.</returns>
        [DllImport("MidiKeyboard", EntryPoint = "MPTKCountInp")]
        static public extern int MPTK_CountInp();

        /// <summary>
        /// Gets the number of MIDI output devices currently detected by Windows (WinMM).
        /// </summary>
        /// <returns>Detected output device count.</returns>
        [DllImport("MidiKeyboard", EntryPoint = "MPTKCountOut")]
        static public extern int MPTK_CountOut();

        /*  *** exploration - not yet available for Unity (Windows and MacOS) ***
        /// <summary>
        /// Gets the number of MIDI input devices currently opened and tracked by the plugin.
        /// This differs from <see cref="MPTK_CountInp"/>, which returns the number of devices
        /// detected by Windows.
        /// </summary>
        [DllImport("MidiKeyboard", EntryPoint = "MPTKCountOpenInp")]
        static public extern int MPTK_CountOpenInp();
        */

        /// <summary>
        /// Gets the display name of a MIDI input device from its WinMM index.
        /// </summary>
        /// <param name="index">WinMM input device index.</param>
        /// <returns>Input device display name.</returns>
        static public string MPTK_GetInpName(int index)
        {
            return Marshal.PtrToStringAnsi(_mptkGetInpName(index));
        }
        [DllImport("MidiKeyboard", EntryPoint = "MPTKGetInpName")]
        static private extern System.IntPtr _mptkGetInpName(int index);

        /// <summary>
        /// Gets the display name of a MIDI output device from its WinMM index.
        /// </summary>
        /// <param name="index">WinMM output device index.</param>
        /// <returns>Output device display name.</returns>
        static public string MPTK_GetOutName(int index)
        {
            return Marshal.PtrToStringAnsi(_mptkGetOutName(index));
        }
        [DllImport("MidiKeyboard", EntryPoint = "MPTKGetOutName")]
        static private extern System.IntPtr _mptkGetOutName(int index);

        //
        // Write to a dedicated midi
        // -------------------------

        /// <summary>
        /// Opens a MIDI output device.
        /// </summary>
        /// <param name="index">WinMM output device index.</param>
        [DllImport("MidiKeyboard", EntryPoint = "MPTKOpenOut")]
        static public extern void MPTK_OpenOut(int index);

        /// <summary>
        /// Closes a MIDI output device.
        /// </summary>
        /// <param name="index">WinMM output device index.</param>
        [DllImport("MidiKeyboard", EntryPoint = "MPTKCloseOut")]
        static public extern void MPTK_CloseOut(int index);

        /// <summary>
        /// Sends a MIDI event to an output device. If the event has a delay, it is sent from a worker thread.
        /// </summary>
        /// <param name="evnt">MIDI event to send.</param>
        /// <param name="device">WinMM output device index.</param>
        static public void MPTK_PlayEvent(MPTKEvent evnt, int device)
        {
            ulong data = evnt.ToData();
            //Debug.Log($"Send {data:X}");
            if (evnt.Delay <= 0)
            {
                // for testing 0x00403C90
                _mptkWrite(device, data);
            }
            else
            {
                Thread thread = new Thread(() => delayedPlayThread(device, data, evnt.Delay));
                thread.Start();
            }
        }

        static private void delayedPlayThread(int device, ulong data, float delayMS)
        {
            TimeSpan time = TimeSpan.FromMilliseconds((double)delayMS);
            Thread.Sleep(time);
            //Debug.Log($"Delayed send {data:X}");
            _mptkWrite((int)device, (ulong)data);
        }
        // exemple 0x00403C90
        [DllImport("MidiKeyboard", EntryPoint = "MPTKWrite")]
        static private extern void _mptkWrite(int index, ulong data);

        //
        // Read from a dedicated midi - excluded from this version, rather use MPTKOpenAllInp
        //

        //[DllImport("MidiKeyboard", EntryPoint = "MPTKOpenInp")]
        //static public extern void MPTK_OpenInp(int index);

        //[DllImport("MidiKeyboard", EntryPoint = "MPTKCloseInp")]
        //static public extern void MPTK_CloseInp(int index);

        /// <summary>
        /// Opens all available MIDI input devices and refreshes the opened list on subsequent calls.
        /// </summary>
        /// <remarks>
        /// Hot-plug behavior depends on the Windows MIDI driver (WinMM). Some hardware/drivers,
        /// especially older USB-MIDI drivers, may not report disconnect/reconnect reliably.
        /// If a device is not detected after reconnect, call <see cref="MPTK_RebuildAllInp"/>.
        /// </remarks>
        [DllImport("MidiKeyboard", EntryPoint = "MPTKOpenAllInp")]
        static public extern void MPTK_OpenAllInp();

        /// <summary>
        /// Closes all MIDI input devices currently opened by the plugin.
        /// </summary>
        [DllImport("MidiKeyboard", EntryPoint = "MPTKCloseAllInp")]
        static public extern void MPTK_CloseAllInp();

        /// <summary>
        /// Forces a full rebuild of all opened MIDI input devices.
        /// </summary>
        /// <remarks>
        /// Use this as a recovery action when hot-plug is not detected correctly by some hardware/drivers.
        /// </remarks>
        [DllImport("MidiKeyboard", EntryPoint = "MPTKRebuildAllInp")]
        static public extern void MPTK_RebuildAllInp();

        /// <summary>
        /// Enables or disables filtering of MIDI system messages.
        /// </summary>
        /// <param name="exclude">If true, excludes messages with status/command &gt;= 0xF0 (default behavior).</param>
        static public void MPTK_ExcludeSystemMessage(bool exclude)
        {
            mptkExcludeSystemMessage(exclude);
        }
        [DllImport("MidiKeyboard", EntryPoint = "MPTKExcludeSystemMessage")]
        static private extern void mptkExcludeSystemMessage(bool exclude);

        /// <summary>
        /// Gets the current native plugin version string.
        /// </summary>
        /// <returns>Version string, or a fallback message if the plugin is not available.</returns>
        static public string MPTK_Version()
        {
            string version = msgPluginsNotFound;
            try
            {
                version = Marshal.PtrToStringAnsi(_mptkVersion());
            }
            catch (Exception)
            {
                version = msgPluginsNotFound;
            }
            return version;
        }
        [DllImport("MidiKeyboard", EntryPoint = "MPTKVersion")]
        static private extern System.IntPtr _mptkVersion();


        [DllImport("MidiKeyboard", EntryPoint = "MPTKIVersion")]
        static private extern int MPTK_iVersion();


        /// <summary>
        /// Enables real-time MIDI input notifications through a native callback.
        /// The <see cref="OnActionInputMidi"/> event is raised when a MIDI event is available.
        /// @code
        /// if (enableRealTimeRead)
        /// {
        ///     MidiKeyboard.OnActionInputMidi += ProcessEvent;
        ///     MidiKeyboard.MPTK_SetRealTimeRead();
        /// }
        /// else
        /// {
        ///     MidiKeyboard.OnActionInputMidi -= ProcessEvent;
        ///     MidiKeyboard.MPTK_UnsetRealTimeRead();
        /// }
        /// @endcode
        /// </summary>
        public static void MPTK_SetRealTimeRead()
        {
            // V2.18.2 replace
            // MPTKSetMidiMsgCB(new MidiMsgDelegate(MidiMsgCB));
            // With a more robust process for real-time MIDI input callback, with fallback to main thread dispatch when no synchronization context is available (eg in some Unity Editor contexts).
            try
            {
                realtimeReadEnabled = true;
                if (realtimeMidiDelegate == null)
                    realtimeMidiDelegate = new MidiMsgDelegate(MidiMsgCB);
                MPTKSetMidiMsgCB(realtimeMidiDelegate);
            }
            catch (Exception)
            {
                Debug.LogWarning(msgPluginsNotFound);
            }
        }

        // Set a CB to return Midi event
        public delegate void MidiMsgDelegate(ulong data);
        [DllImport("MidiKeyboard")]
        private static extern void MPTKSetMidiMsgCB(MidiMsgDelegate fp);

        private static void MidiMsgCB(ulong data)
        {
            if (!realtimeReadEnabled)
                return;

            if (unitySyncContext != null)
            {
                unitySyncContext.Post(_ => DispatchRealtimeMidi(data), null);
                return;
            }

            pendingRealtimeMessages.Enqueue(data);
        }

        /// <summary>
        /// Dispatches MIDI messages received from the native callback when no Unity synchronization
        /// context is available. Call this from the main thread (for example from Update).
        /// </summary>
        /// <param name="maxMessages">Maximum messages to dispatch in one call.</param>
        /// <returns>Number of dispatched messages.</returns>
        public static int MPTK_DispatchPendingRealtimeInput(int maxMessages = 256)
        {
            int dispatched = 0;
            while (dispatched < maxMessages && pendingRealtimeMessages.TryDequeue(out ulong queuedData))
            {
                DispatchRealtimeMidi(queuedData);
                dispatched++;
            }
            return dispatched;
        }

        private static void DispatchRealtimeMidi(ulong data)
        {
            MPTKEvent midievent = null;
            try
            {
                midievent = new MPTKEvent(data);
            }
            catch (Exception)
            {
                Debug.LogWarning(msgPluginsNotFound);
            }
            if (midievent != null)
            {
                try
                {
                    Action<MPTKEvent> action = OnActionInputMidi;
                    if (action != null)
                        action.Invoke(midievent);
                }
                catch (Exception ex)
                {
                    Debug.LogError("OnActionInputMidi: exception detected. Check the callback code");
                    Debug.LogException(ex);
                }
            }
        }

        /// <summary>
        /// Disables real-time MIDI input notifications from the native callback.
        /// This should be called before application shutdown (especially in the Unity Editor)
        /// to reduce the risk of crashes caused by stale callbacks.
        /// </summary>
        public static void MPTK_UnsetRealTimeRead()
        {
            try
            {
                // V2.18.2 
                // Add realtimeReadEnabled set to false to prevent processing of any pending callback messages after unsetting the native callback,
                // which may arrive before the native plugin fully unregisters the callback and cause null reference exceptions in the managed callback.
                // And dequeue...
                realtimeReadEnabled = false;
                MPTKUnsetMidiMsgCB();
                while (pendingRealtimeMessages.TryDequeue(out _)) { }
            }
            catch (Exception)
            {
                // Remove exception when plugin not found 
                //Debug.LogWarning($"MPTK_UnsetRealTimeRead {ex.Message}");
            }
        }
        [DllImport("MidiKeyboard")]
        private static extern void MPTKUnsetMidiMsgCB();


        private static void DebugCallBack(System.IntPtr n, int m)
        {
            Debug.Log($"DebugCallBack {Marshal.PtrToStringAnsi(n)} {m}");
        }
        // Set a CB to display information
        public delegate void DebugDelegate(System.IntPtr p1, int p2);
        [DllImport("MidiKeyboard")]
        private static extern void SetDebugCB(DebugDelegate fp);

        [DllImport("MidiKeyboard")]
        private static extern void UnsetDebugCB();

        /// <summary>
        /// Initializes the native MidiKeyboard plugin.
        /// This must be called before using any other plugin function.
        /// </summary>
        static public bool MPTK_Init()
        {
            try
            {
                unitySyncContext = SynchronizationContext.Current;
                if (MPTK_iVersion() >= 12)
                {
                    _mptkInit(159789);
                    return true;
                }
            }
            catch (Exception)
            {
            }
            Debug.LogWarning($"The MPTK MidiKeyboard Plugin version is incorrect or not found.");
            Debug.Log("Look here to get the most recent version of the MidiKeyboard plugin:");
            Debug.Log("https://paxstellar.fr/class-midikeyboard");
            return false;
        }
        [DllImport("MidiKeyboard", EntryPoint = "MPTKInit")]
        static private extern void _mptkInit(int sig);

        /// <summary>
        /// Gets the last native plugin status.
        /// The status is reset to OK after each call.
        /// </summary>
        static public PluginError MPTK_LastStatus
        {
            get
            {
                PluginError error;
                try
                {
                    error = (PluginError)mptkLastStatus();
                }
                catch
                {
                    error = PluginError.UNSPECIFIED;
                }
                return error;
            }
        }
        [DllImport("MidiKeyboard", EntryPoint = "MPTKLastStatus")]
        static private extern int mptkLastStatus();
    }
}





