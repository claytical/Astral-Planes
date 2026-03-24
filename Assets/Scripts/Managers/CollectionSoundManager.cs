using System.Collections;
using UnityEngine;

public enum SoundEffectPreset
{
    Aether = 1,
    Bloom = 10,
    Dust = 11,
    Boundary = 13
}

public class CollectionSoundManager : MonoBehaviour
{
    [Header("FX Voice (MidiVoice)")]
    [SerializeField] private MidiVoice fxVoice;

    [Header("FX Program Defaults")]
    [Tooltip("FX voice program preset (used for PlayEffect unless overridden).")]
    [SerializeField] private int defaultFxPreset = 12;

    // All presets assumed to be in Bank 0
    private const int fxBank = 0;

    public static CollectionSoundManager Instance;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (!fxVoice) fxVoice = GetComponent<MidiVoice>();

        BindFxVoiceAuthorityIfNeeded();
        StartCoroutine(WaitForSoundFontReady());
    }

    private IEnumerator WaitForSoundFontReady()
    {
        // We can’t see fxVoice’s MidiStreamPlayer directly unless you expose it.
        // So we gate on the global soundfont readiness only.
        while (!MidiPlayerTK.MidiPlayerGlobal.MPTK_SoundFontIsReady)
            yield return null;

        if (fxVoice == null)
        {
            Debug.LogWarning("[CollectionSoundManager] fxVoice (MidiVoice) missing; FX disabled.");
            yield break;
        }

        // Set default program for FX
        fxVoice.PlayOneShotMs127(60, 1, 1, defaultFxPreset, fxBank); // "poke" to force channel init
       // fxVoice.SetProgram((int)defaultFxPreset, fxBank);

        Debug.Log("✅ SoundFont ready. CollectionSoundManager FX voice initialized.");
    }

    // ============================================================
    // Public API
    // ============================================================
    

    public void PlayPhaseStarImpact(InstrumentTrack track, NoteSet previewSet, float forceVel01 = 0.75f)
    {
        if (fxVoice == null || track == null) return;

        int steps = Mathf.Max(1, track.GetTotalSteps());
        int note = previewSet != null
            ? previewSet.GetNoteForPhaseAndRole(track, Random.Range(0, steps))
            : Mathf.Clamp(track.lowestAllowedNote, track.lowestAllowedNote, track.highestAllowedNote);

        int vel = Mathf.RoundToInt(Mathf.Lerp(70, 120, Mathf.Clamp01(forceVel01)));

        // Use the track's instrument program for this impact (consistent timbre with role)
        fxVoice.PlayOneShotMs127(note, durationMs: 180, velocity127: vel, overridePreset: track.Preset, overrideBank: track.Bank);
    }
    
    private void BindFxVoiceAuthorityIfNeeded()
    {
        if (fxVoice == null) return;

        // Ensure MidiStreamPlayer is wired (common failure when fxVoice is a different object than the player).
        // If your fxVoice is on the same GO as MidiStreamPlayer, this resolves correctly.
        var player = fxVoice.GetComponent<MidiPlayerTK.MidiStreamPlayer>() ?? fxVoice.GetComponentInParent<MidiPlayerTK.MidiStreamPlayer>();
        if (player != null)
            fxVoice.SetMidiStreamPlayer(player); // add this setter to MidiVoice if you don't already have it

        // Ensure timing authority is wired (THIS is what was missing for Solo/FX).
        var gfm = GameFlowManager.Instance;
        if (gfm != null && gfm.activeDrumTrack != null)
            fxVoice.SetDrumTrack(gfm.activeDrumTrack);
    }
    

}
