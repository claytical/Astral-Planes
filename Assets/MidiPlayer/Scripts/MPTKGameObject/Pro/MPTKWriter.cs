using MPTK.NAudio.Midi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MidiPlayerTK
{
    /// <summary>
    /// Create, build, write, import, and play MIDI from script. See full examples in:
    /// @li  TestMidiGenerator.cs for MIDI creation examples. 
    /// @li  TinyMidiSequencer.cs for a light sequencer.
    /// @li  MidiEditorProWindow.cs.cs for a full MIDI editor.
    /// @note
    /// Methods such as MPTK_AddxxxxMilli will be deprecated in a future version.\n
    /// MIDI events can be added either with tick-based positions (AddNote) or millisecond-based positions (AddNoteMilli).
    /// For consistency, the recommended approach is to use tick-based methods and convert with #ConvertTickToMilli when needed.
    /// More information here: https://paxstellar.fr/class-MPTKWriter/
    ///
    /// @version 
    ///     Maestro Pro 
    ///
    /// @snippet TestMidiGenerator.cs ExampleFullMidiFileWriter
    /// </summary>
    /// @ingroup mptkwriter_management
    public class MPTKWriter
    {

        /// @name Initialization and Import
        /// @ingroup mptkwriter_init_import
        /// @brief Initialize, reset, and merge event lists into the writer.
        /// @details
        /// Includes constructor defaults, full reset logic, and list import/merge behavior
        /// with DTPQN conversion and post-import timing recalculation.
        /// @{

        /// <summary>@brief
        /// Creates an empty MPTKWriter with default or custom MIDI header values.\n
        /// Default:\n
        /// @li Delta ticks per quarter note = 240\n
        /// @li MIDI file type = 1\n
        /// @li Tempo = 120 BPM\n
        /// @snippet MidiEditorProWindow.cs.cs ExampleInitMidiFileWriter
        /// 
        /// </summary>
        /// <param name="deltaTicksPerQuarterNote">Delta ticks per quarter note, default is 240. See #DeltaTicksPerQuarterNote.</param>
        /// <param name="midiFileType">Type of MIDI format. Must be 0 or 1. Default is 1.</param>
        /// <param name="bpm">Initial tempo in beats per minute. Default is 120.</param>
        public MPTKWriter(int deltaTicksPerQuarterNote = 240, int midiFileType = 1, int bpm = 120, int channelcount = 16)
        {
            MPTK_MidiEvents = new List<MPTKEvent>();
            DeltaTicksPerQuarterNote = deltaTicksPerQuarterNote;
            //MPTK_NumberBeatsMeasure = 4;
            MidiFileType = midiFileType;
            ChannelCount = channelcount;
            MPTK_TempoMap = new List<MPTKTempo>();
            MPTK_SignMap = new List<MPTKSignature>();

            _bpm = bpm;
        }

        /// <summary>@brief
        /// Removes all MIDI events and restores default attributes:
        /// @li MPTK_DeltaTicksPerQuarterNote = 240
        /// @li MPTK_MidiFileType = 1
        /// @li Tempo = 120
        /// </summary>
        public void Clear()
        {
            if (MPTK_TrackStat != null)
                MPTK_TrackStat.Clear();
            //MPTK_DeltaTicksPerQuarterNote = 240;
            MidiFileType = 1;
            _bpm = 120;
            MPTK_MidiEvents.Clear();
            MPTK_TempoMap.Clear();
            MPTK_SignMap.Clear();
        }

        /// <summary>@brief
        /// Imports a list of MPTKEvent instances.\n
        /// @details
        /// Multiple imports can be chained to merge events from different sources.\n
        /// @li The first import defines #DeltaTicksPerQuarterNote.
        /// @li Subsequent imports convert tick and duration values using the DTPQN ratio between source and destination.
        /// @li Real time, measure, and beat are recalculated for the full event list at the end with #CalculateTiming().

        /// Example from MIDI Generator
        /// @snippet TestMidiGenerator.cs ExampleMIDIImport
        /// \n
        /// Example from MIDI Join And Import
        /// @snippet MidiJoinAndImport.cs ExampleMIDIImportAndPlay
        /// \n
        /// </summary>
        /// <param name="midiEventsToInsert">List of MPTKEvent instances to insert.</param>
        /// <param name="deltaTicksPerQuarterNote">
        /// DTPQN value of the source events.\n
        /// @li If #MPTK_MidiEvents is empty, this value becomes #DeltaTicksPerQuarterNote.\n
        /// @li If events already exist, source timing is converted to the writer DTPQN.\n
        /// </param>
        /// <param name="position">Tick position for insertion: -1 to append, 0 for beginning, or any tick within the sequence.</param>
        /// <param name="name">Optional sequence name (sets #MidiName).</param>
        /// <param name="logDebug">Enable debug logs.</param>
        /// <returns>True if import succeeds.</returns>
        public bool ImportFromEventsList(List<MPTKEvent> midiEventsToInsert, int deltaTicksPerQuarterNote, long position = -1, string name = null, bool logPerf = false, bool logDebug = false)
        {
            bool ok = false;
            try
            {

                if (!string.IsNullOrEmpty(name))
                    MidiName = name;

                if (logDebug) Debug.Log($"***** MPTK_ImportFromEventsList to {name}");

                if (deltaTicksPerQuarterNote <= 0)
                    throw new MaestroException($"deltaTicksPerQuarterNote cannot be < 0, found {deltaTicksPerQuarterNote}");

                System.Diagnostics.Stopwatch watch = null;
                if (logPerf)
                {
                    watch = new System.Diagnostics.Stopwatch(); // High resolution time
                    watch.Start();
                }

                // MPTK_Events is instancied with the instance of the class, so no worry, can't be null (check just in case)
                if (MPTK_MidiEvents.Count == 0)
                {
                    // No event, add at beginning
                    position = 0;
                    // when no event already exist, take the DTPQN in parameters
                    DeltaTicksPerQuarterNote = deltaTicksPerQuarterNote;
                    if (logDebug) Debug.Log($"Set MPTK_DeltaTicksPerQuarterNote from paremeter: {DeltaTicksPerQuarterNote}");
                }

                float ratioDTPQN = 1f;
                long shiftTick = 0;
                //float shiftTime = 0f;

                if (deltaTicksPerQuarterNote != DeltaTicksPerQuarterNote)
                    ratioDTPQN = (float)DeltaTicksPerQuarterNote / (float)deltaTicksPerQuarterNote;

                if (logDebug)
                {
                    Debug.Log($"Count events in source={MPTK_MidiEvents.Count} MPTK_DeltaTicksPerQuarterNote={DeltaTicksPerQuarterNote}");
                    Debug.Log($"Count events to import={midiEventsToInsert.Count} DTPQN={deltaTicksPerQuarterNote}");
                    Debug.Log($"ratio DTPQN = {ratioDTPQN}");
                }

                if (position == 0)
                {
                    // Insert at the beguining, get event information from the last MIDI event to insert
                    // ---------------------------------------------------------------------------------
                    if (logDebug) Debug.Log("Insert at the beguining");

                    if (ratioDTPQN != 1f)
                    {
                        if (logDebug) Debug.Log("Convert imported events to the DTPQN original");

                        // DTPQN conversion (real time will be recalculated at the end of the full MIDI events list)
                        midiEventsToInsert.ForEach(midiEvent =>
                        {
                            midiEvent.Tick = (long)(midiEvent.Tick * ratioDTPQN + 0.5f);
                            //midiEvent.RealTime = midiEvent.RealTime * ratioDTPQN;
                            midiEvent.Length = (int)(midiEvent.Length * ratioDTPQN + 0.5f);
                        }
                        );
                    }

                    if (MPTK_MidiEvents.Count != 0)
                    {
                        // Convert existing events (but no change of the DTPQN)
                        MPTKEvent insert = midiEventsToInsert.Last();
                        shiftTick = insert.Tick + insert.Length; // only noteon have a length, be careful with endtrack at the last position 
                        //shiftTime = insert.RealTime;
                        if (logDebug) Debug.Log($"Shift {MPTK_MidiEvents.Count} source events, shift tickFromTime={shiftTick}");

                        // time shift source event  (real time will be recalculated at the end of the full MIDI events list)
                        MPTK_MidiEvents.ForEach(midiEvent =>
                        {
                            midiEvent.Tick += shiftTick;
                            // No change on Length (tickFromTime duration) as the original DTPQN is not modified
                            //midiEvent.RealTime += shiftTime;
                        }
                        );
                    }
                    else if (logDebug) Debug.Log("No events in source, no time shifting");

                    // Insert at beguining
                    MPTK_MidiEvents.InsertRange(0, midiEventsToInsert);
                }
                else if (position < 0 || position >= MPTK_MidiEvents.Last().Tick)
                {
                    // Append at the end (or after !), get event information from the last MIDI event of the source
                    // -------------------------------------------------------------------------------
                    if (logDebug)
                        if (position < 0)
                            Debug.Log("Append at the end of the source");
                        else
                            Debug.Log("Append after the end of the source");

                    MPTKEvent insert = MPTK_MidiEvents.Last();
                    shiftTick = insert.Tick + insert.Length;  // only noteon have a length, be careful with endtrack at the last position 
                    if (position > 0)
                        shiftTick += (long)(position * ratioDTPQN + 0.5f);

                    //shiftTime = insert.RealTime;
                    if (logDebug) Debug.Log($"Shift {midiEventsToInsert.Count} imported events, shift tickFromTime={shiftTick}");
                    if (logDebug) AddText(0, insert.Tick, MPTKMeta.TextEvent, "Insert MIDI");

                    // shift event to append + DTPQN conversion  (real time will be recalculated at the end of the full MIDI events list)
                    midiEventsToInsert.ForEach(midiEvent =>
                    {
                        midiEvent.Tick = (long)(midiEvent.Tick * ratioDTPQN + 0.5f) + shiftTick;
                        //midiEvent.RealTime = midiEvent.RealTime * ratioDTPQN + shiftTime;
                        midiEvent.Length = (int)(midiEvent.Length * ratioDTPQN + 0.5f);
                    }
                    );


                    // Append, works also if there is no event in the source
                    MPTK_MidiEvents.AddRange(midiEventsToInsert);
                }
                else
                {
                    // Insert anywhere inside the source MIDI events!!!
                    // Get event information from the last MIDI event of the source
                    // ------------------------------------------------------------
                    if (logDebug) Debug.Log($"Insert at tickFromTime position {position}");

                    int indexToInsert = MidiLoad.MPTK_SearchEventFromTick(MPTK_MidiEvents, position);
                    if (logDebug) Debug.Log($"Insert at index position {indexToInsert}");
                    if (indexToInsert < 0)
                        Debug.Log($"Not possible to insert at position tickFromTime {position}");
                    else
                    {
                        //// insert after the group with the same tickFromTime in origine
                        //while (index < MPTK_MidiEvents.Count && MPTK_MidiEvents[index].Tick >= position)
                        //    index++;
                        //Debug.Log($"Insert corrected {index} for tickFromTime {position}");

                        // get  event information from the MIDI event where to insert
                        MPTKEvent sourceEvent = MPTK_MidiEvents[indexToInsert];
                        shiftTick = sourceEvent.Tick + sourceEvent.Length;
                        //shiftTime = sourceEvent.RealTime;

                        if (logDebug) Debug.Log($"Shift {midiEventsToInsert.Count} imported events, shift tickFromTime={shiftTick}");

                        // Time shift event to insert + DTPQN conversion  (real time will be recalculated at the end of the full MIDI events list)
                        midiEventsToInsert.ForEach(midiEvent =>
                        {
                            midiEvent.Tick = (long)(midiEvent.Tick * ratioDTPQN + 0.5f) + shiftTick;
                            //midiEvent.RealTime = midiEvent.RealTime * ratioDTPQN + shiftTime;
                            midiEvent.Length = (int)(midiEvent.Length * ratioDTPQN + 0.5f);
                        }
                        );

                        // Insert at the position found
                        MPTK_MidiEvents.InsertRange(indexToInsert, midiEventsToInsert);

                        // Correct events after (time shift of the source events), take tickFromTime of the last events inserted
                        MPTKEvent insert = midiEventsToInsert.Last();
                        shiftTick = insert.Tick;
                        //shiftTime = insert.RealTime;

                        if (logDebug) Debug.Log($"Shift {midiEventsToInsert.Count} source events from postiion {indexToInsert}, shift tickFromTime={shiftTick} ");

                        for (int index = indexToInsert + midiEventsToInsert.Count; index < MPTK_MidiEvents.Count; index++)
                        {
                            // No change on Length (tickFromTime duration) as the original DTPQN is not modified
                            MPTK_MidiEvents[index].Tick += shiftTick;
                            //MPTK_MidiEvents[index].RealTime += shiftTime;
                        }
                    }
                    //MPTK_MidiEvents.RemoveRange(index, MPTK_MidiEvents.Count - index);
                    ok = true;
                }

                // Calculate real time, measure and beat for each events
                // -----------------------------------------------------
                CalculateTiming(logPerf: logPerf);

                if (logPerf)
                {
                    Debug.Log($"MPTK_ImportFromEventsList {watch.ElapsedMilliseconds} ms {watch.ElapsedTicks} timer ticks");
                    watch.Stop();
                }
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return ok;
        }

        /// @}


        /// @name Tempo and Time Conversion
        /// @ingroup mptkwriter_tempo
        /// @brief Tempo state, tempo/signature maps, and tick/millisecond conversion helpers.
        /// @details
        /// This section defines the timing model used by the writer.
        /// It includes DTPQN, current tempo, cached tempo/signature maps,
        /// and helper methods to convert absolute positions and durations
        /// between ticks and milliseconds.
        /// @{

        /// <summary>@brief
        /// Delta Ticks Per Quarter Note (DTPQN) represents the number of ticks in one quarter note.\n
        /// For example, with 96 ticks per quarter note, an eighth note has a duration of 48 ticks.\n
        /// In a MIDI file, this value is found in the MIDI header and remains constant for the entire file.\n
        /// More information: https://paxstellar.fr/2020/09/11/midi-timing/\n
        /// </summary>
        public int DeltaTicksPerQuarterNote;

        //private int _bpm;
        // version 2.10.0 was an int, becomes a double
        private double _bpm;

        /// <summary>@brief
        /// Gets the current tempo value in microseconds per quarter note: 60 * 1000 * 1000 / BPM.\n
        /// @details
        /// Tempo in a MIDI file is stored in microseconds per quarter note.
        /// To convert this to BPM, use #MPTKEvent.QuarterPerMicroSecond2BeatPerMinute.\n
        /// This value can change while generating MIDI when #AddTempoChange is called.\n
        /// More information: https://paxstellar.fr/2020/09/11/midi-timing/
        /// </summary>
        public int MicrosecondsPerQuaterNote
        {
            get { return _bpm > 0 ? (int)(60 * 1000 * 1000 / _bpm) : 0; }
        }


        /// <summary>@brief
        /// Gets the current tempo in BPM. Updated by #AddTempoChange and #AddBPMChange.\n
        /// https://en.wikipedia.org/wiki/Tempo
        /// </summary>
        public double CurrentTempo => _bpm;

        /// <summary>@brief
        /// List of tempo segments derived from #MPTK_MidiEvents.
        /// Rebuild with MPTKTempo.CalculateMap when events change.\n
        /// See example:
        /// @snippet TestMidiGenerator.cs ExampleCalculateMaps
        /// @version 2.10.0
        /// </summary>
        public List<MPTKTempo> MPTK_TempoMap;

        /// <summary>@brief
        /// List of time-signature segments derived from #MPTK_MidiEvents.
        /// Rebuild with MPTKSignature.CalculateMap when events change.\n
        /// See example:
        /// @snippet TestMidiGenerator.cs ExampleCalculateMaps
        /// @version 2.10.0
        /// </summary>
        public List<MPTKSignature> MPTK_SignMap;


        /// <summary>@brief
        /// Gets the current duration, in milliseconds, of one MIDI tick (relative to the current tempo).\n
        /// @details
        /// This depends on #CurrentTempo and #DeltaTicksPerQuarterNote.\n
        /// PulseLength = 60000 / CurrentTempo / DeltaTicksPerQuarterNote
        /// </summary>
        public float PulseLenght { get { return _bpm > 0 && DeltaTicksPerQuarterNote > 0 ? (60000f / (float)_bpm) / (float)DeltaTicksPerQuarterNote : 0f; } }

        /// <summary>@brief
        /// Converts an absolute tick position to time in milliseconds.\n
        /// </summary>
        /// <param name="tick">Absolute tick position.</param>
        /// <param name="indexTempo">Optional index in #MPTK_TempoMap for this position. If -1, the map is recalculated and the segment is searched automatically.</param>
        /// <returns>Absolute time in milliseconds.</returns>
        public float ConvertTickToMilli(long tick, int indexTempo = -1)
        {
            if (indexTempo < 0)
            {
                MPTKTempo.CalculateMap(DeltaTicksPerQuarterNote, MPTK_MidiEvents, MPTK_TempoMap);
                indexTempo = MPTKTempo.FindSegment(MPTK_TempoMap, tick, fromIndex: 0);
            }
            return (float)MPTK_TempoMap[indexTempo].CalculateTime(tick);
        }

        /// <summary>@brief
        /// Converts a time position in milliseconds to an absolute tick position.\n
        /// </summary>
        /// <param name="time">Time position in milliseconds</param>
        /// <param name="indexTempo">Optional index in #MPTK_TempoMap for this position. If -1, the map is recalculated and the segment is searched automatically.</param>
        /// <returns>Absolute tick position.</returns>
        public long ConvertMilliToTick(float time, int indexTempo = -1)
        {
            if (indexTempo < 0)
            {
                MPTKTempo.CalculateMap(DeltaTicksPerQuarterNote, MPTK_MidiEvents, MPTK_TempoMap);
                indexTempo = MPTKTempo.FindSegment(MPTK_TempoMap, time, fromIndex: 0);
            }
            //Debug.Log($"Index:{indexTempo} Tick:{MPTK_TempoMap[indexTempo].CalculatelTick(time)} at time {time}");
            return MPTK_TempoMap[indexTempo].CalculatelTick(time);
        }

        /// <summary>@brief
        /// Converts a tick duration to a millisecond duration using the current tempo.\n
        /// @note Previous calls to #AddBPMChange and #AddTempoChange have a direct impact on this calculation.
        /// </summary>
        /// <param name="tick">Duration in ticks.</param>
        /// <returns>Duration in milliseconds.</returns>
        public long DurationTickToMilli(long tick)
        {
            return (long)(tick * PulseLenght + 0.5f);
        }

        /// <summary>@brief
        /// Converts a millisecond duration to a tick duration using the current tempo.\n
        /// @note Previous calls to #AddBPMChange and #AddTempoChange have a direct impact on this calculation.
        /// </summary>
        /// <param name="time">Duration in milliseconds.</param>
        /// <returns>Duration in ticks.</returns>
        public long DurationMilliToTick(float time)
        {
            return PulseLenght > 0d ? (long)(time / PulseLenght + 0.5f) : 0L;
        }
        /// @}

        /// @name Sequence State and Event Collections
        /// @ingroup mptkwriter_state
        /// @brief Core state for the current MIDI sequence and generated event list.
        /// @details
        /// This section stores global sequence metadata (name, MIDI format, channels),
        /// accessors for the generated event list, and convenience properties such as
        /// last event and last tick position.
        /// @{

        /// <summary>@brief
        /// Gets the number of tracks. This value is available only when CreateTracksStat() has been called.
        /// There is no longer a track-count limit as of V2.9.0.
        /// </summary>
        public int TrackCount { get { return MPTK_TrackStat?.Count ?? 0; } }

        public int ChannelCount = 16;

        /// <summary>@brief
        /// Gets the MIDI file type of the loaded MIDI (0, 1, or 2).
        /// </summary>
        public int MidiFileType;

        /// <summary>@brief
        /// Name of this MIDI sequence.
        /// </summary>
        public string MidiName;

        /// <summary>@brief
        /// Gets all created MIDI events.
        /// @code
        /// midiFileWriter.MPTK_MidiEvents.ForEach(midiEvent =>
        /// {
        ///     midiEvent.Tick += shiftTick;
        ///     midiEvent.RealTime += shiftTime;
        /// });
        /// @endcode
        /// </summary>
        public List<MPTKEvent> MPTK_MidiEvents;

        /// <summary>@brief
        /// Last MIDI event in the created MIDI events list. It is not always the last sound played, especially if a previous event lasts longer.
        /// @note Returns null if no MIDI event is found.
        /// </summary>
        public MPTKEvent MPTK_LastEvent => MPTK_MidiEvents == null || MPTK_MidiEvents.Count == 0 ? null : MPTK_MidiEvents[MPTK_MidiEvents.Count - 1];

        /// <summary>@brief
        /// Last tick position including event duration.
        /// @note This is not always the last audible sound. A previous event may still ring longer.
        /// </summary>
        public long TickLast => MPTK_LastEvent == null ? 0 : MPTK_LastEvent.Tick + MPTK_LastEvent.Length;

        // @cond NODOC
        // Not yet mature to be published.
        // Track information, built with CreateTracksStat. It's a dictionary with the track number as a key and the item holds some information about the track.
        public Dictionary<long, MPTKStat> MPTK_TrackStat;
        // @endcond

        /// <summary>@brief
        /// Number of MIDI events in #MPTK_MidiEvents.
        /// </summary>
        public int CountEvent
        {
            get { return MPTK_MidiEvents == null ? 0 : MPTK_MidiEvents.Count; }
        }
        
        /// @}



        /*
        /// <summary>@brief
        /// 
        /// </summary>
        /// <param name="frequency">
        ///     With frequency \n
        ///         @li  >  0 insert Game Event each "frequency" bar (1, 2, 3, ...)
        ///         @li  =  0 insert nothing but removes each Game Event if remove=True
        ///         @li  = -1 insert Game Event each Half
        ///         @li  = -2 insert Game Event each Beat
        ///         @li  = -3 insert Game Event each Eighth
        /// </param>
        /// <param name="info">
        ///     Free text to identify your Game Event. The ful text received in the callback will be:\n
        ///     MPTK_[info]_[bar]_[half]_[quarter]_[eighth]
        ///     Split the string to retrieve each part string[] id = info.Split('_');
        ///     id[1] will contain your specific info
        ///     NON plutot faire des parametres specifiques dans le callback
        /// </param>
        /// <param name="remove"></param>
        /// <param name="fromTick"></param>
        /// <param name="toTick"></param>
        /// <param name="logDebug"></param>
        /// <returns></returns>
        public bool MPTK_InsertGameEvent(int frequency, string info = null, bool remove = false, long fromTick = 0, long toTick = -1, bool logDebug = false)
        {
            string data = "THExxQUICKxxBROWNxxFOX";

            data.Split(new string[] { "xx" }, StringSplitOptions.None);
            return true;
        }
        */

        /// @name Loading Existing MIDI Content
        /// @ingroup mptkwriter_loading
        /// @brief Load sequence data from file system or MidiDB.
        /// @details
        /// These methods replace the current in-memory event list with events loaded
        /// from an external source and restore timing header values such as DTPQN.
        /// @{

        /// <summary>@brief
        /// Loads a MIDI file from the OS file system (behavior may depend on the OS).
        /// </summary>
        /// <param name="filename">Full path to the MIDI file.</param>
        /// <returns>True if loading succeeds.</returns>
        public bool LoadFromFile(string filename)
        {
            bool ok = false;
            try
            {
                MidiLoad midiLoad = new MidiLoad();
                //midiLoad.MPTK_KeepNoteOff = true;
                if (midiLoad.MPTK_LoadFile(filename)) // corrected in 2.89.5 MPTK_Load --> MPTK_LoadFile (pro)
                {
                    MPTK_MidiEvents = midiLoad.MPTK_MidiEvents;
                    // Added in 2.89.5
                    DeltaTicksPerQuarterNote = midiLoad.MPTK_DeltaTicksPerQuarterNote;
                    ok = true;
                }
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return ok;
        }

        /// <summary>@brief
        /// Loads a MIDI from MPTK MidiDB into this writer.
        /// Existing events are replaced.
        /// If you add events afterward, sort the list before writing.
        /// @snippet TestMidiGenerator.cs ExampleMidiWileWriterLoadMidi
        /// </summary>
        /// <param name="indexMidiDb">Index in the MidiDB list.</param>
        /// <returns>True if loading succeeds.</returns>
        public bool LoadFromMidiDB(int indexMidiDb)
        {
            bool ok = false;
            try
            {
                if (MidiPlayerGlobal.CurrentMidiSet.MidiFiles != null && MidiPlayerGlobal.CurrentMidiSet.MidiFiles.Count > 0)
                {
                    if (indexMidiDb >= 0 && indexMidiDb < MidiPlayerGlobal.CurrentMidiSet.MidiFiles.Count - 1)
                    {
                        string midiname = MidiPlayerGlobal.CurrentMidiSet.MidiFiles[indexMidiDb];
                        TextAsset mididata = Resources.Load<TextAsset>(Path.Combine(MidiPlayerGlobal.MidiFilesDB, midiname));
                        MidiLoad midiLoad = new MidiLoad();
                        //midiLoad.MPTK_KeepNoteOff = true;
                        midiLoad.MPTK_Load(mididata.bytes);
                        // Corrected with version 2.10.1 - Delta ticks per quarter was lost when MIDI loaded in MidiFileWriter
                        DeltaTicksPerQuarterNote = midiLoad.MPTK_DeltaTicksPerQuarterNote;
                        MPTK_MidiEvents = midiLoad.MPTK_MidiEvents;
                        ok = true;
                    }
                    else
                        Debug.LogWarning("Index is out of the MidiDb list");
                }
                else
                    Debug.LogWarning("No MidiDb defined");
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return ok;
        }

        /// @}

        /// @name Track Statistics
        /// @ingroup mptkwriter_stats
        /// @brief Build and maintain per-track counters derived from event content.
        /// @details
        /// Track statistics are used by export/diagnostic operations to summarize
        /// event distribution by track (count, notes, preset changes).
        /// @{

        /// <summary>
        /// Builds track statistics (`MPTK_TrackStat`) from events in `MPTK_MidiEvents`.
        /// The dictionary key is the track number, and each value contains aggregated counters.
        /// </summary>
        /// <returns>Track statistics dictionary.</returns>
        /// <exception cref="MaestroException"></exception>
        public Dictionary<long, MPTKStat> CreateTracksStat()
        {
            if (MPTK_MidiEvents == null)
                throw new MaestroException("MPTK_Events is null");
            foreach (MPTKEvent midiEvent in MPTK_MidiEvents)
                UpdateStatTrack(midiEvent);
            return MPTK_TrackStat;
        }

        private void UpdateStatTrack(MPTKEvent midiEvent)
        {
            if (MPTK_TrackStat == null)
                MPTK_TrackStat = new Dictionary<long, MPTKStat>();

            if (!MPTK_TrackStat.ContainsKey(midiEvent.Track))
                MPTK_TrackStat[midiEvent.Track] = new MPTKStat();

            MPTK_TrackStat[midiEvent.Track].CountAll++;

            if (midiEvent.Command == MPTKCommand.NoteOn) MPTK_TrackStat[midiEvent.Track].CountNote++;
            if (midiEvent.Command == MPTKCommand.PatchChange) MPTK_TrackStat[midiEvent.Track].CountPreset++;
        }
        /// @}

        /// @name MIDI Event Creation API
        /// @ingroup mptkwriter_creation
        /// @brief Add musical, control, and meta events to the sequence.
        /// @details
        /// This section is the main authoring API for building MIDI content:
        /// notes, chords, program/controller changes, pitch wheel, tempo/signature,
        /// and text meta events.
        /// @{

        /// <summary>@brief
        /// Adds an event directly from an MPTKEvent instance.\n
        /// @details
        /// The following fields must be set in the input event:
        /// - MPTKEvent.Track
        /// - MPTKEvent.Channel
        /// - MPTKEvent.Command
        /// - MPTKEvent.Tick
        /// @note
        /// Other fields depend on the command type; see MidiPlayerTK.MPTKCommand.\n
        /// For example, MPTKEvent.Length must be set when MPTKEvent.Command is MPTKCommand.NoteOn.
        /// </summary>
        /// <param name="mptkEvent">Event to add.</param>
        /// <returns>The same event instance, or null on error.</returns>
        public MPTKEvent AddRawEvent(MPTKEvent mptkEvent)
        {
            try
            {
                if (mptkEvent == null) throw new MaestroException($"mptkEvent is null");
                //  if (mptkEvent.Channel < 0 || mptkEvent.Channel > 15) throw new MaestroException($"The channel must be >= 0 and <= 15, found {mptkEvent.Channel}");
                if (mptkEvent.Tick < 0) throw new MaestroException($"Position (tickFromTime or time) cannot be negative, found {mptkEvent.Tick}");
                if (mptkEvent.Track < 0) throw new MaestroException($"The number of the track ({mptkEvent.Track}) cannot be negative.");
                if (mptkEvent.Track == 0 && (
                        mptkEvent.Command == MPTKCommand.NoteOn ||
                        mptkEvent.Command == MPTKCommand.NoteOff ||
                        mptkEvent.Command == MPTKCommand.KeyAfterTouch ||
                        mptkEvent.Command == MPTKCommand.ControlChange ||
                        mptkEvent.Command == MPTKCommand.PatchChange ||
                        mptkEvent.Command == MPTKCommand.ChannelAfterTouch ||
                        mptkEvent.Command == MPTKCommand.PitchWheelChange)
                    )
                {
                    throw new MaestroException($"MIDI events based on channel (noteon, noteoff, patch change ...) cannot be defined on track 0.");
                }
                MPTK_MidiEvents.Add(mptkEvent);
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return mptkEvent;
        }


        /// <summary>@brief
        /// Adds a NoteOn event at an absolute tick position.\n
        /// A corresponding NoteOff is generated automatically when length is greater than 0.\n
        /// If length is less than 0, no automatic NoteOff is created.
        /// </summary>
        /// <param name="track">Track for this event (do not add to track 0)</param>
        /// <param name="tick">Absolute tick position for this event.</param>
        /// <param name="channel">Channel must be in the range 0-15</param>
        /// <param name="note">Note must be in the range 0-127</param>
        /// <param name="velocity">Velocity must be in the range 0-127.</param>
        /// <param name="length">Duration in ticks. If less than 0, you must add NoteOff manually with #AddOff.</param>
        /// <returns>The MIDI event created, or null on error.</returns>
        public MPTKEvent AddNote(int track, long tick, int channel, int note, int velocity, int length)
        {
            MPTKEvent mptkEvent = null;
            try
            {
                if (velocity < 0 || velocity > 127)
                {
                    throw new MaestroException($"Velocity must be >= 0 and <= 127, found {velocity}.");
                }

                if (length < 0)
                    // duration not specifed, set a default of a quarter (not taken into account by the synth). A next note off event will whange this duration.
                    mptkEvent = AddRawEvent(new MPTKEvent() { Track = track, Tick = tick, Channel = channel, Command = MPTKCommand.NoteOn, Value = note, Velocity = velocity, Duration = -1, Length = -1 });
                else
                {
                    long duration_ms = DurationTickToMilli(length);
                    mptkEvent = AddRawEvent(new MPTKEvent() { Track = track, Tick = tick, Channel = channel, Command = MPTKCommand.NoteOn, Value = note, Velocity = velocity, Duration = duration_ms, Length = length });
                    // It's better to create note-off when saving the MIDI file
                    // MPTK don't use note-off but the duration of the event
                    // But they are mandatory for the MIDI file norm .
                    // AddRawEvent(new MptkEvent() { Track = track, Tick = tickFromTime + length, Channel = channel, Command = MPTKCommand.NoteOff, Value = note, Velocity = 0 });
                }
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return mptkEvent;
        }


        /// <summary>@brief
        /// Adds a silence placeholder.\n
        /// @note
        /// MIDI has no native "silent note". This method simulates silence with a NoteOn event at velocity 1.\n
        /// A NoteOn velocity of 0 is interpreted as NoteOff by the MIDI standard.
        /// </summary>
        /// <param name="track">Track for this event (do not add to track 0)</param>
        /// <param name="tick">Tick time for this event.</param>
        /// <param name="channel">Channel must be in the range 0-15</param>
        /// <param name="length">Duration in ticks.</param>
        /// <returns>The MIDI event created, or null on error.</returns>
        public MPTKEvent AddSilence(int track, long tick, int channel, int length)
        {
            MPTKEvent mptkEvent = null;
            try
            {
                mptkEvent = new MPTKEvent() { Track = track, Tick = tick, Channel = channel, Command = MPTKCommand.NoteOn, Value = 0, Velocity = 1, Length = length };
                AddRawEvent(mptkEvent);
                // It's better to create note-off when saving the MIDI file
                // MPTK don't use note-off but the duration of the event
                // But they are mandatory for the MIDI file norm .
                // AddRawEvent(new MptkEvent() { Track = track, Tick = tickFromTime + duration, Channel = channel, Command = MPTKCommand.NoteOff, Value = 0 });
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return mptkEvent;

        }

        /// <summary>@brief
        /// Adds a note-off event.\n
        /// It must always follow the corresponding NoteOn, on the same channel.
        /// </summary>
        /// <param name="track">Track for this event (do not add to track 0)</param>
        /// <param name="tick">Absolute tick position for this event.</param>
        /// <param name="channel">Channel must be in the range 0-15</param>
        /// <param name="note">Note must be in the range 0-127</param>
        /// <returns>The matching NoteOn event that was updated, or null if not found/error.</returns>
        public MPTKEvent AddOff(int track, long tick, int channel, int note)
        {
            MPTKEvent mptkEvent = null;
            try
            {
                bool found = false;
                for (int index = MPTK_MidiEvents.Count - 1; index >= 0; index--)
                {
                    mptkEvent = MPTK_MidiEvents[index];
                    if (mptkEvent.Channel == channel && mptkEvent.Command == MPTKCommand.NoteOn && mptkEvent.Value == note)
                    {
                        int length = Convert.ToInt32(tick - mptkEvent.Tick);
                        if (length > 0)
                        {
                            found = true;
                            mptkEvent.Length = length;
                            mptkEvent.Duration = DurationTickToMilli(length);
                            break;
                        }
                    }
                }
                if (!found)
                    Debug.LogWarning($"No NoteOn found corresponding to this NoteOff: track={track} channel={channel} tickFromTime={tick} note={note}");
                //AddRawEvent(new MptkEvent() { Track = track, Tick = tickFromTime, Channel = channel, Command = MPTKCommand.NoteOff, Value = note });
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return mptkEvent;

        }


        /// <summary>@brief
        /// Adds a chord built from a scale range.
        /// @snippet TestMidiGenerator.cs ExampleMidiWriterBuildChordFromRange
        /// </summary>
        /// <param name="track">Track for this event (do not add to track 0)</param>
        /// <param name="tick">Start tick for all generated notes.</param>
        /// <param name="channel">MIDI channel (0-15).</param>
        /// <param name="scale">Scale definition (see MPTKScaleLib).</param>
        /// <param name="chord">Chord builder settings (see MPTKChordBuilder).</param>
        public void AddChordFromScale(int track, long tick, int channel, MPTKScaleLib scale, MPTKChordBuilder chord)
        {
            try
            {
                chord.MPTK_BuildFromRange(scale);
                foreach (MPTKEvent evnt in chord.Events)
                    AddNote(track, tick, channel, evnt.Value, evnt.Velocity, (int)ConvertMilliToTick(evnt.Duration));
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
        }

        /// <summary>@brief
        /// Adds a chord from a chord library.
        /// @snippet TestMidiGenerator.cs ExampleMidiWriterBuildChordFromLib
        /// </summary>
        /// <param name="track">Track for this event (do not add to track 0)</param>
        /// <param name="tick">Start tick for all generated notes.</param>
        /// <param name="channel">MIDI channel (0-15).</param>
        /// <param name="chordName">Chord name (see #MPTKChordName).</param>
        /// <param name="chord">Chord builder settings (see MPTKChordBuilder).</param>
        public void AddChordFromLib(int track, long tick, int channel, MPTKChordName chordName, MPTKChordBuilder chord)
        {
            try
            {
                chord.MPTK_BuildFromLib(chordName);
                foreach (MPTKEvent evnt in chord.Events)
                    AddNote(track, tick, channel, evnt.Value, evnt.Velocity, (int)ConvertMilliToTick(evnt.Duration));
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
        }

        /// <summary>@brief
        /// Adds a preset change.
        /// </summary>
        /// <param name="track">Track for this event (do not add to track 0)</param>
        /// <param name="tick">Tick time for this event</param>
        /// <param name="channel">Channel must be in the range 0-15</param>
        /// <param name="preset">Preset (program/patch) must be in the range 0-127</param>
        /// <returns>Return the MIDI event created or null if error</returns>
        public MPTKEvent AddChangePreset(int track, long tick, int channel, int preset)
        {
            MPTKEvent mptkEvent = null;
            try
            {
                mptkEvent = new MPTKEvent() { Track = track, Tick = tick, Channel = channel, Command = MPTKCommand.PatchChange, Value = preset };
                AddRawEvent(mptkEvent);
                //AddEvent(track, new PatchChangeEvent(absoluteTime, channel + 1, preset));
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return mptkEvent;
        }


        /// <summary>@brief
        /// Adds a Channel After-Touch event.
        /// </summary>
        /// <param name="track">Track for this event (do not add to track 0)</param>
        /// <param name="tick">Tick time for this event</param>
        /// <param name="channel">Channel must be in the range 0-15</param>
        /// <param name="afterTouchPressure">After-touch pressure from 0 to 127</param>
        /// <returns>Return the MIDI event created or null if error</returns>
        public MPTKEvent AddChannelAfterTouch(int track, long tick, int channel, int afterTouchPressure)
        {
            MPTKEvent mptkEvent = null;
            try
            {
                mptkEvent = new MPTKEvent() { Track = track, Tick = tick, Channel = channel, Command = MPTKCommand.ChannelAfterTouch, Value = afterTouchPressure };
                return AddRawEvent(mptkEvent);
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return null;
        }


        /// <summary>@brief
        /// Creates a general Control Change event (CC).
        /// </summary>
        /// <param name="track">Track for this event (do not add to track 0)</param>
        /// <param name="tick">Tick time for this event</param>
        /// <param name="channel">Channel must be in the range 0-15</param>
        /// <param name="controller">The MIDI Controller. See #MPTKController</param>
        /// <param name="controllerValue">Controller value</param>
        /// <returns>Return the MIDI event created or null if error</returns>
        public MPTKEvent AddControlChange(int track, long tick, int channel, MPTKController controller, int controllerValue)
        {
            try
            {
                MPTKEvent mptkEvent = new MPTKEvent() { Track = track, Tick = tick, Channel = channel, Command = MPTKCommand.ControlChange, Controller = controller, Value = controllerValue };
                return AddRawEvent(mptkEvent);
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return null;
        }


        /// <summary>@brief
        /// Creates a Pitch Wheel event from a normalized value.\n
        /// pitchWheel:
        /// @li  0.0 = minimum (MIDI value 0)
        /// @li  0.5 = center (MIDI value 8192)
        /// @li  1.0 = maximum (MIDI value 16383)
        /// </summary>
        /// <param name="track">Track for this event (do not add to track 0)</param>
        /// <param name="tick">Tick time for this event</param>
        /// <param name="channel">Channel must be in the range 0-15</param>
        /// <param name="pitchWheel">Normalized pitch-wheel value in range [0..1].</param>
        /// <returns>The MIDI event created, or null on error.</returns>
        public MPTKEvent AddPitchWheelChange(int track, long tick, int channel, float pitchWheel)
        {
            try
            {
                int pitch = (int)Mathf.Lerp(0f, 16383f, pitchWheel); // V2.88.2 range normalized from 0 to 1
                                                                     //Debug.Log($"{pitchWheel} --> {pitch}");
                return AddRawEvent(new MPTKEvent() { Track = track, Tick = tick, Channel = channel, Command = MPTKCommand.PitchWheelChange, Value = pitch });
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return null;
        }

        /// <summary>@brief
        /// Adds a tempo change to the MIDI stream. There is no channel parameter because a tempo change is applied to all tracks and channels.\n
        /// Subsequent duration/time conversions use the new BPM value.
        /// @note 
        /// #MPTK_TempoMap is not updated automatically; call #CalculateTiming to rebuild maps.
        /// </summary>
        /// <param name="track">Track for this event (it is good practice to use track 0 for this event).</param>
        /// <param name="tick">Absolute tick position for this event.</param>
        /// <param name="bpm">Quarters per minute.</param>
        /// <returns>The MIDI event created, or null on error.</returns>
        public MPTKEvent AddBPMChange(int track, long tick, int bpm)
        {
            if (bpm <= 0)
            {
                Debug.LogWarning("AddBPMChange: BPM must > 0");
                return null;
            }
            try
            {
                _bpm = bpm; // MPTK_MicrosecondsPerQuaterNote is calculated from the bpm
                            //Value contains new Microseconds Per Beat Note and Duration contains new tempo (quarter per minute).
                return AddRawEvent(new MPTKEvent() { Track = track, Tick = tick, Command = MPTKCommand.MetaEvent, Meta = MPTKMeta.SetTempo, Value = MicrosecondsPerQuaterNote/*, Duration = _bpm*/ });
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return null;
        }

        /// <summary>@brief
        /// Adds a tempo change to the MIDI stream in microseconds per quarter note.\n
        /// There is no channel parameter because a tempo change is applied to all tracks and channels.\n
        /// Subsequent duration/time conversions use the new tempo value.
        /// @note 
        /// #MPTK_TempoMap is not updated automatically. See example:
        /// @snippet TestMidiGenerator.cs ExampleCalculateMaps
        /// </summary>
        /// <param name="track">Track for this event (it is good practice to use track 0 for this event).</param>
        /// <param name="tick">Absolute tick position for this event.</param>
        /// <param name="microsecondsPerQuarterNote">Tempo in microseconds per quarter note.</param>
        /// <returns>The MIDI event created, or null on error.</returns>
        public MPTKEvent AddTempoChange(int track, long tick, int microsecondsPerQuarterNote)
        {
            if (microsecondsPerQuarterNote <= 0)
            {
                Debug.LogWarning("AddBPMChange: Microseconds Per Quarter Note must > 0");
                return null;
            }
            try
            {
                _bpm = MPTKEvent.QuarterPerMicroSecond2BeatPerMinute(microsecondsPerQuarterNote);
                //Value contains new Microseconds Per Beat Note and Duration contains new tempo (quarter per minute).
                return AddRawEvent(new MPTKEvent() { Track = track, Tick = tick, Command = MPTKCommand.MetaEvent, Meta = MPTKMeta.SetTempo, Value = microsecondsPerQuarterNote,/* Duration = _bpm*/ });
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return null;
        }


        /// <summary>@brief
        /// Creates a TimeSignature meta event (optional).
        /// The internal sequencer default is 4,2,24,32.
        /// No channel is required because time signature applies globally.
        /// More information: https://paxstellar.fr/2020/09/11/midi-timing/
        /// @note 
        /// MPTK_SignMap is not updated. See example:
        /// @snippet TestMidiGenerator.cs ExampleCalculateMaps
        /// </summary>
        /// <param name="track">Track for this event (it is good practice to use track 0 for this event).</param>
        /// <param name="tick">Absolute tick position where the event is added.</param>
        /// <param name="numerator">Numerator, beats per measure. Will be MPTKSignature.NumberBeatsMeasure in #MPTK_SignMap</param>
        /// <param name="denominator">Denominator, beat unit: 1 means 2, 2 means 4 (crochet), 3 means 8 (quaver), 4 means 16, ...</param>
        /// <param name="ticksInMetronomeClick">Ticks in Metronome Click. Set to 24 for a standard value.</param>
        /// <param name="no32ndNotesInQuarterNote">Number of 32nd notes per quarter note. Standard MIDI value is usually 8.</param>
        /// <returns>The MIDI event created, or null on error.</returns>
        public MPTKEvent AddTimeSignature(int track, long tick, int numerator = 4, int denominator = 2, int ticksInMetronomeClick = 24, int no32ndNotesInQuarterNote = 32)
        {
            try
            {
                //MPTK_NumberBeatsMeasure = numerator;
                // if Meta = TimeSignature,
                //      Value contains the numerator (number of beats in a bar) and the denominator (Beat unit: 1 means 2, 2 means 4 (crochet), 3 means 8 (quaver), 4 means 16, ...)
                return AddRawEvent(new MPTKEvent()
                {
                    Track = track,
                    Tick = tick,
                    Command = MPTKCommand.MetaEvent,
                    Meta = MPTKMeta.TimeSignature,
                    Value = MPTKEvent.BuildIntFromBytes((byte)numerator, (byte)denominator, (byte)ticksInMetronomeClick, (byte)no32ndNotesInQuarterNote),
                    Length = ticksInMetronomeClick,
                });

            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return null;
        }


        /// <summary>@brief
        /// Creates a MIDI text event.
        /// @snippet TestMidiGenerator.cs ExampleCreateMeta
        /// </summary>
        /// <param name="track">Track for this event (good practice: track 0).</param>
        /// <param name="tick">Absolute tick position of this event.</param>
        /// <param name="typeMeta">Meta-event type.</param>
        /// <param name="text">The text associated with this MIDI event.</param>
        /// <returns>The MIDI event created, or null on error.</returns>
        public MPTKEvent AddText(int track, long tick, MPTKMeta typeMeta, string text)
        {
            try
            {
                switch (typeMeta)
                {
                    case MPTKMeta.TextEvent:
                    case MPTKMeta.Copyright:
                    case MPTKMeta.DeviceName:
                    case MPTKMeta.Lyric:
                    case MPTKMeta.ProgramName:
                    case MPTKMeta.SequenceTrackName:
                    case MPTKMeta.Marker:
                    case MPTKMeta.TrackInstrumentName:
                        return AddRawEvent(new MPTKEvent() { Track = track, Tick = tick, Command = MPTKCommand.MetaEvent, Meta = typeMeta, Info = text });
                    default:
                        throw new Exception($"AddText need a meta event type for text. {typeMeta} is not correct.");
                }
            }

            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return null;
        }

        /// <summary>@brief
        /// If true, META text (for example lyrics) is written with UTF-8 encoding. Default is false.\n
        /// Standard MIDI text meta events are ASCII. With this extension, you can also store and display\n
        /// non-ASCII characters (Korean, Chinese, Japanese, accented Latin characters, and more).\n
        /// To enable reading UTF8 characters from an MPTK MIDI player, set MidiFilePlayer#MPTK_ExtendedText to true.\n
        /// @warning This is an MPTK extension of the MIDI standard, so if you read your MIDI file with another application, you may not find your original text.
        /// @version 2.11.3
        /// </summary>
        public bool ExtendedText { get; set; }


        /// @}

        /// @name Event List Maintenance and Timing Recalculation
        /// @ingroup mptkwriter_maintenance
        /// @brief Modify event collections and recompute sorted/timed state.
        /// @details
        /// Use these methods to delete subsets of events, apply stable sorting,
        /// and rebuild real-time/measure/beat values after content changes.
        /// @{

        /// <summary>@brief
        /// Deletes all MIDI events on this channel.
        /// </summary>
        /// <param name="channel">Channel to remove (0-15).</param>
        public void DeleteChannel(int channel)
        {
            if (MPTK_MidiEvents != null)
                for (int index = 0; index < MPTK_MidiEvents.Count;)
                {
                    if (MPTK_MidiEvents[index].Channel == channel)
                        MPTK_MidiEvents.RemoveAt(index);
                    else
                        index++;
                }
        }

        /// <summary>@brief
        /// Deletes all MIDI events on this track.
        /// </summary>
        /// <param name="track">Track index to remove.</param>
        public void DeleteTrack(int track)
        {
            if (MPTK_MidiEvents != null)
                for (int index = 0; index < MPTK_MidiEvents.Count;)
                {
                    if (MPTK_MidiEvents[index].Track == track)
                        MPTK_MidiEvents.RemoveAt(index);
                    else
                        index++;
                }
        }

        /// <summary>@brief
        /// Sorts events in MPTK_MidiEvents in place by ascending tickFromTime position.
        /// Priority is applied for 'bank change' and 'preset change' within a group of events at the same position (but 'end track' is placed at the end of the group).
        /// </summary>
        /// <param name="logPerf">Enable performance logging.</param>
        public void StableSortEvents(bool logPerf = false)
        {
            if (MPTK_MidiEvents != null)
            {
                System.Diagnostics.Stopwatch watch = null;
                if (logPerf)
                {
                    watch = new System.Diagnostics.Stopwatch(); // High resolution time
                    watch.Start();
                }

                // Sort with priority of bank change, preset change, meta end
                MPTK_MidiEvents.Sort((x, y) => x.Compare(y));

                //before 2.14.1 MidiLoad.Sort(MPTK_MidiEvents, 0, MPTK_MidiEvents.Count - 1, new MidiLoad.MidiEventComparer());
                if (logPerf)
                {
                    Debug.Log($"Stable sort time {watch.ElapsedMilliseconds} {watch.ElapsedTicks}");
                    watch.Stop();
                }
            }
            else
                Debug.LogWarning("MPTKWriter - MPTK_SortEvents - MPTK_MidiEvents is null");
        }

        /// <summary>@brief
        /// Calculates real-time position, measure, and beat for each event.
        /// @li Calculate #MPTK_TempoMap with #MPTKTempo.MPTK_CalculateMap
        /// @li Calculate #MPTK_SignMap with  #MPTKSignature.MPTK_CalculateMap
        /// @li Calculates time and duration of each event from tick values and the tempo map.
        /// @li Calculates measure and beat using the time-signature map.
        /// @version 2.10.0
        /// @snippet TestMidiGenerator.cs ExampleCalculateMaps
        /// </summary>
        /// <param name="logPerf">Enable performance logging.</param>
        /// <param name="logDebug">Enable detailed debug logging.</param>
        public void CalculateTiming(bool logPerf = false, bool logDebug = false)
        {
            if (MPTK_MidiEvents != null)
            {
                System.Diagnostics.Stopwatch watch = null;
                if (logPerf)
                {
                    watch = new System.Diagnostics.Stopwatch(); // High resolution time
                    watch.Start();
                }

                // Default are defined in MPTK_CalculateMap
                //if (MPTK_TempoMap.Count == 0)
                //{
                //    MPTKTempo.DeltaTicksPerQuarterNote = MPTK_DeltaTicksPerQuarterNote;
                //    // Create in the map a default tempo, set to 120 by default (500 000 microseconds)
                //    MPTK_TempoMap.Add(new MPTKTempo(index: MPTK_TempoMap.Count, fromTick: 0, microsecondsPerQuarterNote: MptkEvent.BeatPerMinute2QuarterPerMicroSecond(120),
                //        pulse: (double)MptkEvent.BeatPerMinute2QuarterPerMicroSecond(120) / (double)MPTK_DeltaTicksPerQuarterNote / 1000d));
                //}
                //if (MPTK_SignMap.Count == 0)
                //{
                //    // Create in the map a default signature
                //    MPTK_SignMap.Add(new MPTKSignature(index: 0));
                //}

                // New with 2.10.0
                MPTKTempo.CalculateMap(DeltaTicksPerQuarterNote, MPTK_MidiEvents, MPTK_TempoMap);
                MPTKSignature.CalculateMap(DeltaTicksPerQuarterNote, MPTK_MidiEvents, MPTK_SignMap);
                MPTKSignature.CalculateMeasureBoundaries(MPTK_SignMap);

                int indexEvent = 0;
                int indexTempo = 0;
                int indexSign = 0;
                if (logDebug) Debug.Log($"CalculateTiming index:{indexTempo} {MPTK_TempoMap[indexTempo]} {MPTK_SignMap[indexSign]} (init)");
                foreach (MPTKEvent mptkEvent in MPTK_MidiEvents)
                {
                    mptkEvent.Index = indexEvent++;

                    indexTempo = MPTKTempo.FindSegment(MPTK_TempoMap, mptkEvent.Tick, fromIndex: indexTempo);
                    MPTKTempo tempoMap = MPTK_TempoMap[indexTempo];
                    mptkEvent.RealTime = (float)tempoMap.CalculateTime(mptkEvent.Tick);
                    if (mptkEvent.Command == MPTKCommand.NoteOn && mptkEvent.Duration > -1)
                        mptkEvent.Duration = (long)(mptkEvent.Length * tempoMap.Pulse);

                    indexSign = MPTKSignature.FindSegment(MPTK_SignMap, mptkEvent.Tick, fromIndex: indexSign);
                    MPTKSignature signMap = MPTK_SignMap[indexSign];
                    mptkEvent.Measure = signMap.TickToMeasure(mptkEvent.Tick);
                    mptkEvent.Beat = signMap.CalculateBeat(mptkEvent.Tick, mptkEvent.Measure);
                    if (logDebug) Debug.Log($"CalculateTiming index:{indexTempo} {MPTK_TempoMap[indexTempo]} {MPTK_SignMap[indexSign]}");
                }
                if (logPerf)
                {
                    Debug.Log($"CalculateTiming {watch.ElapsedMilliseconds} ms {watch.ElapsedTicks} timer ticks");
                    watch.Stop();
                }
            }
            else
                Debug.LogWarning("MPTKWriter - CalculateTiming - MPTK_MidiEvents is null");
        }
        /// @}

        /// @name Export and Diagnostics
        /// @ingroup mptkwriter_export
        /// @brief Export MIDI files and inspect generated output/logs.
        /// @details
        /// This section converts MPTK events to NAudio structures, writes files
        /// to disk or MidiDB, and provides detailed logging helpers for validation.
        /// @{

        /// <summary>@brief
        /// Writes a MIDI file to an OS folder.
        /// @snippet TestMidiGenerator.cs ExampleMIDIWriteAndPlay
        /// </summary>
        /// <param name="filename">Output file path.</param>
        /// <returns>True if writing succeeds.</returns>
        public bool WriteToFile(string filename)
        {
            bool ok = false;
            try
            {
                if (ChannelCount > 16)
                {
                    Debug.LogWarning("MPTKWriter - Writing MIDI file with more than 16 channels is not possible. Operation canceled.");
                }
                else
                if (MPTK_MidiEvents != null && MPTK_MidiEvents.Count > 0)
                {
                    MidiFile midiToSave = BuildNAudioMidi();
                    // NAudio don't create noteoff associated to noteon! they need to be added if they are missing
                    MidiFile.Export(filename, midiToSave.Events);
                    ok = true;
                }
                else
                    Debug.LogWarning("MPTKWriter - Write - MidiEvents is null or empty.");
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return ok;
        }

        /// <summary>@brief
        /// Writes a MIDI file to MidiDB.\n
        /// To be used only in Edit mode, not in a standalone application.\n
        /// A call to AssetDatabase.Refresh() is required after the file has been added to the Resources folder.
        /// </summary>
        /// <param name="filename">Filename of the MIDI file, without folder or extension.</param>
        /// <returns>True if writing succeeds.</returns>
        public bool WriteToMidiDB(string filename)
        {
            bool ok = false;
            try
            {
                if (Application.isEditor)
                {
                    string filenameonly = Path.GetFileNameWithoutExtension(filename) + ".bytes";
                    // Build path to midi folder 
                    string pathMidiFile = Path.Combine(Application.dataPath, MidiPlayerGlobal.PathToMidiFile);
                    string filepath = Path.Combine(pathMidiFile, filenameonly);
                    //Debug.Log(filepath);
                    WriteToFile(filepath);
                    // To be review, can't access class in the editor project ...
                    //MidiPlayerTK.ToolsEditor.CheckMidiSet();
                    string filenoext = Path.GetFileNameWithoutExtension(filename);
                    if (!MidiPlayerGlobal.CurrentMidiSet.MidiFiles.Contains(filenoext))
                    {
                        Debug.Log($"Add MIDI '{filenoext}' to MidiDB");
                        MidiPlayerGlobal.CurrentMidiSet.MidiFiles.Add(filenoext);
                        MidiPlayerGlobal.CurrentMidiSet.MidiFiles.Sort();
                        MidiPlayerGlobal.CurrentMidiSet.Save();
                    }

                    ok = true;
                }
                else
                    Debug.LogWarning("WriteToMidiDB can be call only in editor mode not in a standalone application");
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return ok;
        }

        /// <summary>@brief
        /// Builds an NAudio MidiFile object from #MPTK_MidiEvents.
        /// #WriteToMidiDB and #WriteToFile call this method before exporting.
        /// </summary>
        /// <returns>NAudio MidiFile instance.</returns>
        public MidiFile BuildNAudioMidi()
        {
            MidiFile naudioMidi = new MidiFile(MidiFileType, DeltaTicksPerQuarterNote, extendedText: ExtendedText);

            if (MPTK_TrackStat == null)
                CreateTracksStat();

            foreach (int track in MPTK_TrackStat.Keys)
            {
                if (MPTK_TrackStat[track].CountAll == 0)
                    Debug.LogWarning($"BuildNAudioMidi - Track {track} is empty");
                else
                {
                    bool endTrack = false;
                    naudioMidi.Events.AddTrack();
                    long endLastEvent = 0;
                    long prevAbsEvent = 0;
                    //Debug.Log($"Build track {track}");
                    foreach (MPTKEvent mptkEvent in MPTK_MidiEvents)
                    {
                        if (mptkEvent.Track == track)
                        {
                            MidiEvent naudioEvent = null;
                            MidiEvent naudioNoteOff = null;
                            //MidiEvent naudioNoteOff = null;
                            try
                            {
                                switch (mptkEvent.Command)
                                {
                                    case MPTKCommand.NoteOn:
                                        if (mptkEvent.Length < 0)
                                            Debug.LogWarning($"BuildNAudioMidi - NoteOn with negative duration not processed. NoteOff Missing? {mptkEvent}");
                                        else
                                            naudioEvent = new NoteOnEvent(mptkEvent.Tick, mptkEvent.Channel + 1, mptkEvent.Value, mptkEvent.Velocity, (int)mptkEvent.Length);
                                        // noteoff are already created if event has been added with AddNote but not if loaded with MidiLoad and KeepNoteOff is false.
                                        // NAudio don't create noteoff associated to noteon! they need to be added if they are missing
                                        // Can be added now, the events will be sorted by NAudio (MergeSort)
                                        naudioNoteOff = new NoteEvent(mptkEvent.Tick + mptkEvent.Length, mptkEvent.Channel + 1, MidiCommandCode.NoteOff, mptkEvent.Value, 0);

                                        break;
                                    //case MPTKCommand.NoteOff:
                                    //    if (!addNoteOffAuto)
                                    //        // Noteoff are added only if automatic note off creation is off.
                                    //        naudioEvent = new NoteEvent(mptkEvent.Tick, mptkEvent.Channel + 1, MidiCommandCode.NoteOff, mptkEvent.Value, 0);
                                    //    break;
                                    case MPTKCommand.PatchChange:
                                        naudioEvent = new PatchChangeEvent(mptkEvent.Tick, mptkEvent.Channel + 1, mptkEvent.Value);
                                        break;
                                    case MPTKCommand.ControlChange:
                                        naudioEvent = new ControlChangeEvent(mptkEvent.Tick, mptkEvent.Channel + 1, (MidiController)mptkEvent.Controller, mptkEvent.Value);
                                        break;
                                    case MPTKCommand.ChannelAfterTouch:
                                        naudioEvent = new ChannelAfterTouchEvent(mptkEvent.Tick, mptkEvent.Channel + 1, mptkEvent.Value);
                                        break;
                                    case MPTKCommand.KeyAfterTouch:
                                        // Not processed by NAudio
                                        // naudioEvent = new KeyAfterTouchEvent(mptkEvent.Tick, mptkEvent.Channel + 1, mptkEvent.Value);
                                        break;
                                    case MPTKCommand.MetaEvent:
                                        switch (mptkEvent.Meta)
                                        {
                                            case MPTKMeta.SetTempo:
                                                // mptkEvent.Value = microsecondsPerQuarterNote 
                                                naudioEvent = new TempoEvent(mptkEvent.Value, mptkEvent.Tick);
                                                break;
                                            case MPTKMeta.TimeSignature:
                                                // - The fourth byte is the numerator of the time signature and has values between 0x00 and 0xFF (0 and 255).
                                                // - The fifth byte is the power to which the number 2 must be raised to obtain the time signature denominator.
                                                //   Thus, if the fifth byte is 0, the denominator is 20 = 1, denoting whole notes.If the fifth byte is 1, the denominator is 21 = 2 denoting half notes, and so on.
                                                // - The sixth byte of the message defines a metronome pulse in terms of the number of MIDI clock ticks per click.
                                                //   Assuming 24 MIDI clocks per quarter note, if the value of the sixth byte is 48, the metronome will click every two quarter notes, or in other words, every half-note.
                                                // - The seventh byte defines the number of 32nd notes per beat (no32ndNotesInQuarterNote).
                                                //   This byte is usually 8 as there is usually one quarter note per beat and one quarter note contains eight 32nd notes.
                                                //   It does not affect at what time events are sent (so a pure playback program will ignore this message), but how notes are displayed.
                                                //   For example, if the header says there are 100 ticks per beat, and the time signature has the default of 8 32th notes per beat,
                                                //   then a note-on / note - off pair with a distance of 100 ticks is displayed as a quarter note.
                                                //   If you change the time signature to 32 32th notes per beat, then a length of 100 ticks corresponds to a whole note.
                                                // https://www.recordingblogs.com/wiki/midi-time-signature-meta-message#:~:text=Assuming%2024%20MIDI%20clocks%20per%20quarter%20note%2C%20if,and%20one%20quarter%20note%20contains%20eight%2032nd%20notes.
                                                naudioEvent = new TimeSignatureEvent(mptkEvent.Tick,
                                                    MPTKEvent.ExtractFromInt((uint)mptkEvent.Value, 0),
                                                    MPTKEvent.ExtractFromInt((uint)mptkEvent.Value, 1),
                                                    MPTKEvent.ExtractFromInt((uint)mptkEvent.Value, 2),
                                                    MPTKEvent.ExtractFromInt((uint)mptkEvent.Value, 3));
                                                break;
                                            case MPTKMeta.KeySignature:
                                                naudioEvent = new KeySignatureEvent(
                                                    MPTKEvent.ExtractFromInt((uint)mptkEvent.Value, 0),
                                                    MPTKEvent.ExtractFromInt((uint)mptkEvent.Value, 1), mptkEvent.Tick);
                                                break;
                                            case MPTKMeta.EndTrack:
                                                // v2.9.0 - don't add endtrack, they are automatically processed by Maestro
                                                Debug.LogWarning($"It's useless to add an endtrack. Maestro will automatically process it. Track:{track}");
                                                // naudioMidi.Events.AddEvent(new MetaEvent(MetaEventType.EndTrack, 0, mptkEvent.Tick), track);
                                                // End track, no more event will be added after this event for this track
                                                endTrack = true;
                                                break;
                                            case MPTKMeta.Marker:
                                            case MPTKMeta.MidiChannel:
                                            case MPTKMeta.MidiPort:
                                            case MPTKMeta.SmpteOffset:
                                            case MPTKMeta.CuePoint:
                                                // Not processed by Maestro
                                                break;

                                            default:
                                                if (mptkEvent.Info != null)
                                                    naudioEvent = new TextEvent(mptkEvent.Info, (MetaEventType)mptkEvent.Meta, mptkEvent.Tick, ExtendedText);
                                                else
                                                    Debug.LogWarning($"This Meta MIDI event is not processed by Maestro: {mptkEvent.Meta}");
                                                break;
                                        }
                                        break;
                                    case MPTKCommand.PitchWheelChange:
                                        naudioEvent = new PitchWheelChangeEvent(mptkEvent.Tick, mptkEvent.Channel + 1, mptkEvent.Value);
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"Can't build event {mptkEvent} {ex}");
                            }
                            try
                            {
                                if (naudioEvent != null)
                                {
                                    naudioEvent.DeltaTime = (int)(naudioEvent.AbsoluteTime - prevAbsEvent);
                                    prevAbsEvent = naudioEvent.AbsoluteTime;
                                    naudioMidi.Events.AddEvent(naudioEvent, track);
                                    //Debug.Log($"   Add event {naudioEvent}");

                                    if (endLastEvent < naudioEvent.AbsoluteTime)
                                    {
                                        endLastEvent = naudioEvent.AbsoluteTime;
                                        // v2.9.0 - there is always a noteoff with noteon
                                        //if (naudioEvent.CommandCode == MidiCommandCode.NoteOn)
                                        //    // A noteoff event will be created, so time of last event will be more later
                                        //    endLastEvent += naudioEvent.DeltaTime;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"Can't add event {mptkEvent} {ex}");
                            }
                            try
                            {
                                if (naudioNoteOff != null)
                                {
                                    naudioNoteOff.DeltaTime = (int)(naudioNoteOff.AbsoluteTime - prevAbsEvent);
                                    prevAbsEvent = naudioNoteOff.AbsoluteTime;
                                    naudioMidi.Events.AddEvent(naudioNoteOff, track);
                                    if (endLastEvent < naudioNoteOff.AbsoluteTime)
                                    {
                                        endLastEvent = naudioNoteOff.AbsoluteTime;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"Can't add noteoff event {mptkEvent} {ex}");
                            }
                        }

                        if (endTrack)
                            // exit loop on each events for this track
                            break;
                    } // foreach event

                    if (!endTrack)
                    {
                        try
                        {
                            //Debug.Log($"Close track {track} at {endLastEvent}");
                            naudioMidi.Events.AddEvent(new MetaEvent(MetaEventType.EndTrack, 0, endLastEvent), track);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"Can't add end track event {ex}");
                        }
                    }
                }
            } // foreach track

            //naudioMidi.Events.MidiFileType = MPTK_MidiFileType;
            //naudioMidi.Events.PrepareForExport();

            return naudioMidi;
        }

        /// <summary>@brief
        /// Logs writer state and MIDI events.
        /// </summary>
        /// <returns>True when logging completes without exception.</returns>
        public bool LogWriter()
        {
            bool ok = false;
            //
            // REWRITED with 2.9.0
            // 
            try
            {
                if (MPTK_MidiEvents != null && MPTK_MidiEvents.Count > 0)
                {
                    Debug.Log($"<b>---------------- MPTKWriter: LogWriter ----------------</b>");
                    Debug.Log($"<b>MPTK_DeltaTicksPerQuarterNote: {DeltaTicksPerQuarterNote}</b>");
                    Debug.Log($"<b>MPTK_TrackCount: {TrackCount}</b>");
                    LogTrackStat();

                    LogTempoMap();
                    LogSignMap();

                    Debug.Log($"<b>MIDI events: {MPTK_MidiEvents.Count}</b>");
                    if (MPTK_MidiEvents.Count < 10000)
                        foreach (MPTKEvent tmidi in MPTK_MidiEvents)
                            Debug.Log("   " + tmidi.ToString());
                    else
                    {
                        Debug.Log("<b>*** Log only the 10.000 last MIDI events ***</b>");
                        for (int i = MPTK_MidiEvents.Count - 10000; i < MPTK_MidiEvents.Count; i++)
                            Debug.Log("   " + MPTK_MidiEvents[i].ToString());
                    }
                    Debug.Log("--------------------------------------------------------------");
                }
                else
                    Debug.LogWarning("MPTKWriter - LogWriter - MidiEvents is null or empty");


            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return ok;
        }

        /// <summary>@brief
        /// Logs information about the tempo map.
        /// </summary>
        public void LogTempoMap()
        {
            if (MPTK_TempoMap != null)
            {
                Debug.Log($"<b>MPTK_TempoMap: {MPTK_TempoMap.Count}</b>");
                MPTK_TempoMap.ForEach(t =>
                {
                    Debug.Log("   " + t);
                });
            }
        }

        /// <summary>@brief
        /// Logs information about the signature map.
        /// </summary>
        public void LogSignMap()
        {
            if (MPTK_SignMap != null)
            {
                Debug.Log($"<b>MPTK_SignMap: {MPTK_SignMap.Count}</b>");
                MPTK_SignMap.ForEach(t =>
                {
                    Debug.Log("   " + t);
                });
            }
        }

        /// <summary>@brief
        /// Logs information about track statistics.
        /// </summary>
        public void LogTrackStat()
        {
            if (MPTK_TrackStat != null)
                foreach (int track in MPTK_TrackStat.Keys)
                {
                    if (MPTK_TrackStat[track].CountAll != 0)
                    {
                        Debug.Log($"   Track: {track,-2}\tCount event: {MPTK_TrackStat[track].CountAll,-3}\tPreset Change: {MPTK_TrackStat[track].CountPreset,-2}\tNote: {MPTK_TrackStat[track].CountNote}");
                    }
                }
            else
                Debug.Log($"   No track stat available, call CreateTracksStat() before.");
        }


        /// <summary>@brief
        /// Logs raw NAudio MIDI events built from the current writer state.
        /// </summary>
        /// <returns>True when logging completes without exception.</returns>
        public bool LogRaw()
        {
            bool ok = false;
            //
            // REWRITED with 2.9.0
            // 
            try
            {
                if (MPTK_MidiEvents != null && MPTK_MidiEvents.Count > 0)
                {
                    MidiFile midifile = BuildNAudioMidi();

                    Debug.Log($"---------------- MPTKWriter: LogRaw ----------------");
                    Debug.Log($"MidiFileType: {midifile.Events.MidiFileType}");
                    Debug.Log($"Tracks Count: {midifile.Tracks}");

                    if (midifile.Events.MidiFileType == 0 && midifile.Tracks > 1)
                    {
                        throw new ArgumentException("Can't export more than one track to a type 0 file");
                    }

                    for (int track = 0; track < midifile.Events.Tracks; track++)
                    {
                        IList<MidiEvent> eventList = midifile.Events[track];

                        long absoluteTime = midifile.Events.StartAbsoluteTime;

                        // use a stable sort to preserve ordering of MIDI events whose 
                        // absolute times are the same
                        //MergeSort.Sort(eventList, new MidiEventComparer());
                        if (eventList.Count > 0)
                        {
                            // TBN Change - error if no end track
                            Debug.Assert(MidiEvent.IsEndTrack(eventList[eventList.Count - 1]), "Exporting a track with a missing end track");
                        }
                        foreach (var midiEvent in eventList)
                        {
                            string info = $"   Track:{track} {midiEvent}";
                            if (midiEvent.CommandCode == MidiCommandCode.NoteOn)
                            {
                                NoteOnEvent ev = (NoteOnEvent)midiEvent;
                                if (ev.OffEvent != null)
                                    info += $" NoteOff at:  {ev.AbsoluteTime}";
                            }
                            Debug.Log(info);
                        }

                    }

                    //foreach (IList<MidiEvent> track in midifile.Events)
                    //{
                    //    foreach (MidiEvent nAudioMidievent in track)
                    //    {
                    //        string sEvent = nAudioMidievent.ToString();  //MidiScan.ConvertnAudioEventToString(nAudioMidievent, indexTrack);
                    //        if (sEvent != null)
                    //            Debug.Log("   " + sEvent);
                    //    }
                    //    indexTrack++;
                    //}

                    Debug.Log($"--------------------------------------------------------------");
                }
                else
                    Debug.LogWarning("MPTKWriter - LogRaw - MidiEvents is null or empty");


            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return ok;
        }
        /// @}

        private static bool Test(string source, string target)
        {
            bool ok = false;
            try
            {
                MidiFile midifile = new MidiFile(source);
                MidiFile.Export(target, midifile.Events);
                ok = true;
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return ok;
        }
    }
}
