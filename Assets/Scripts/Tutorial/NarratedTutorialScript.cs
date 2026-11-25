using System;
using UnityEngine;

public enum TutorialGate
{
    None,
    CarveComplete,
    TentacleAppeared,
    StarArmed,
    StarPoked,
    DiscoveryBurst,
    NoteCaught,
    NotePlaced,
    RingCompleted,
    RecordAccessible,
    RecordCollected,
}

[Serializable]
public class TutorialStage
{
    public string id;
    [Tooltip("Lines shown in order. All but the last auto-advance after holdSeconds; the last stays until the gate fires (or forever if gate is None).")]
    public string[] lines;
    [Tooltip("JSON name of a TutorialGate (e.g. \"CarveComplete\"); resolved into 'gate' at load.")]
    public string gateName;
    [NonSerialized] public TutorialGate gate;
    [Tooltip("Index of a line that becomes a Now!/Here! placement-window cue loop while the player carries a note. -1 = none.")]
    public int cueLineIndex = -1;
    [Tooltip("Optional replacement lines used when the active vehicle(s) are Cozy Co-Pilot (auto placement). Empty = use 'lines'. The co-pilot fires gates by itself, so these always play through even when the gate fires early.")]
    public string[] linesAuto;
    [Tooltip("Cue line index within linesAuto. -1 = none (auto placement has no release window).")]
    public int cueLineIndexAuto = -1;
    [Tooltip("Line that zooms the camera in on the docked record; the line waits until the camera has arrived. -1 = none.")]
    public int spotlightLine = -1;
    [Tooltip("Line that releases the record spotlight (the camera pulls back as it appears). -1 = when the stage ends.")]
    public int spotlightEndLine = -1;
    [Tooltip("Line shown first; then the camera zooms in on the record, holds, and pulls back before the next line. -1 = none.")]
    public int spotlightDuringLine = -1;
}

[Serializable]
public class TutorialInterruption
{
    public string[] lines;
}

/// <summary>
/// Runtime container for the narrated tutorial's dialogue and tuning. Dialogue lives in
/// Resources/Tutorial/NarratedTutorial.json (see <see cref="Load"/>); the field values below are
/// only the defaults for keys the JSON omits.
/// </summary>
public class NarratedTutorialScript : ScriptableObject
{
    public const string ResourcePath = "Tutorial/NarratedTutorial"; // Assets/Resources/Tutorial/NarratedTutorial.json

    [Tooltip("Scene path (from a root object) of the authored text panel; its child TMP_Text shows the lines. If missing, an overlay is generated.")]
    public string panelPath = "Canvas/UI/Tutorial";
    [Tooltip("Seconds each non-final line stays up before the next appears.")]
    public float holdSeconds = 2.75f;
    [Tooltip("A line stays up at least this long before a stage advance can replace it (commit-skip and interruptions still cut through).")]
    public float minLineSeconds = 2.5f;
    [Tooltip("Pause after the motif-start rings have disappeared before the first message appears.")]
    public float introSettleSeconds = 0.5f;
    [Tooltip("A commit press with no carried note skips the current delay, but only after the line has been up this long (stops accidental double-presses).")]
    public float skipMinLineSeconds = 0.4f;
    [Tooltip("spotlightDuringLine: seconds the line is up before the camera zooms in.")]
    public float spotlightLeadSeconds = 1.5f;
    [Tooltip("spotlightDuringLine: seconds the camera holds on the record before pulling back.")]
    public float spotlightHoldSeconds = 1.5f;
    [Tooltip("Vehicle-plow cells carved to satisfy the carve gate.")]
    public int carveCellThreshold = 25;

    [Header("Placement-window cue (Now! / Here!)")]
    [Tooltip("Words shown, alternating, when the note-release window opens. Index 1 also pulses the target placeholder.")]
    public string[] cueWindowTexts = { "Now!", "Here!" };
    [Tooltip("Minimum seconds the instruction line stays up before the first cue.")]
    public float cueMinLineSeconds = 5f;
    [Tooltip("Breather on the instruction line between cues, so back-to-back windows don't flicker.")]
    public float cueRepeatLineSeconds = 2f;
    [Tooltip("Safety cap on how long a cue word stays up if the window never closes.")]
    public float cueMaxWindowSeconds = 4f;

    [Header("Object highlight (expanding circle)")]
    public float highlightExpandSeconds = 0.7f;
    public float highlightHoldSeconds = 2.5f;
    public float highlightFadeSeconds = 0.6f;
    public Color highlightColor = new Color(1f, 0.95f, 0.7f, 1f);

    public TutorialStage[] stages;
    public TutorialInterruption discoveryEscaped;
    public TutorialInterruption noteDiscarded;
    [Tooltip("Shown when a vehicle runs dry in the tutorial motif and is refilled to half instead of exploding.")]
    public TutorialInterruption outOfEnergy;

    [Header("Control sandbox only (ControlSandbox.json)")]
    [Tooltip("Shown to returning players: how to skip the sandbox.")]
    public TutorialInterruption skipHint;
    [Tooltip("Shown once when someone has been charging into dust for a while without carving through.")]
    public TutorialInterruption stuckOnHardDust;

    /// <summary>The stage with this id, or null.</summary>
    public TutorialStage StageById(string id)
    {
        if (stages == null) return null;
        foreach (var st in stages)
            if (st != null && st.id == id) return st;
        return null;
    }

    /// <summary>
    /// Loads and validates the tutorial JSON. Returns null (after logging why) if it is missing or
    /// invalid; callers should then simply not run the tutorial.
    /// </summary>
    public static NarratedTutorialScript Load() => Load(ResourcePath);

    /// <summary>Loads any dialogue file in this schema (e.g. the control sandbox's).</summary>
    public static NarratedTutorialScript Load(string resourcePath)
    {
        var ta = Resources.Load<TextAsset>(resourcePath);
        if (ta == null)
        {
            Debug.LogError($"[NarratedTutorial] Missing dialogue file: Assets/Resources/{resourcePath}.json");
            return null;
        }

        var s = CreateInstance<NarratedTutorialScript>();
        s.hideFlags = HideFlags.DontSave;
        try
        {
            // Overwrite onto the pre-initialised instance so keys absent from the JSON keep their defaults.
            JsonUtility.FromJsonOverwrite(ta.text, s);
        }
        catch (ArgumentException e)
        {
            Debug.LogError($"[NarratedTutorial] Could not parse {resourcePath}.json: {e.Message}");
            return null;
        }

        return s.Resolve() ? s : null;
    }

    bool Resolve()
    {
        if (stages == null || stages.Length == 0)
        {
            Debug.LogError("[NarratedTutorial] The dialogue file has no stages.");
            return false;
        }

        for (int i = 0; i < stages.Length; i++)
        {
            var st = stages[i];
            string label = string.IsNullOrEmpty(st.id) ? $"#{i}" : st.id;

            if (string.IsNullOrEmpty(st.gateName)) st.gateName = nameof(TutorialGate.None);
            if (!Enum.TryParse(st.gateName, true, out st.gate) || !Enum.IsDefined(typeof(TutorialGate), st.gate))
            {
                Debug.LogError($"[NarratedTutorial] Stage '{label}' has unknown gateName '{st.gateName}'. " +
                               $"Valid: {string.Join(", ", Enum.GetNames(typeof(TutorialGate)))}.");
                return false;
            }
            if (st.lines == null || st.lines.Length == 0)
            {
                Debug.LogError($"[NarratedTutorial] Stage '{label}' has no lines.");
                return false;
            }
            if (st.cueLineIndex < -1 || st.cueLineIndex >= st.lines.Length)
            {
                Debug.LogWarning($"[NarratedTutorial] Stage '{label}' cueLineIndex {st.cueLineIndex} is outside its {st.lines.Length} lines; cue disabled.");
                st.cueLineIndex = -1;
            }
            if (st.spotlightLine < -1 || st.spotlightLine >= st.lines.Length)
            {
                Debug.LogWarning($"[NarratedTutorial] Stage '{label}' spotlightLine {st.spotlightLine} is outside its {st.lines.Length} lines; spotlight disabled.");
                st.spotlightLine = -1;
            }
            if (st.spotlightDuringLine < -1 || st.spotlightDuringLine >= st.lines.Length)
            {
                Debug.LogWarning($"[NarratedTutorial] Stage '{label}' spotlightDuringLine {st.spotlightDuringLine} is outside its {st.lines.Length} lines; disabled.");
                st.spotlightDuringLine = -1;
            }
            if (st.spotlightEndLine >= 0 && st.spotlightEndLine <= st.spotlightLine)
            {
                Debug.LogWarning($"[NarratedTutorial] Stage '{label}' spotlightEndLine {st.spotlightEndLine} must come after spotlightLine {st.spotlightLine}; releasing at stage end.");
                st.spotlightEndLine = -1;
            }
            if (st.linesAuto != null && st.linesAuto.Length > 0 &&
                (st.cueLineIndexAuto < -1 || st.cueLineIndexAuto >= st.linesAuto.Length))
            {
                Debug.LogWarning($"[NarratedTutorial] Stage '{label}' cueLineIndexAuto {st.cueLineIndexAuto} is outside its {st.linesAuto.Length} linesAuto; cue disabled.");
                st.cueLineIndexAuto = -1;
            }
        }

        if (cueWindowTexts == null || cueWindowTexts.Length == 0)
            cueWindowTexts = new[] { "Now!", "Here!" };

        return true;
    }
}
