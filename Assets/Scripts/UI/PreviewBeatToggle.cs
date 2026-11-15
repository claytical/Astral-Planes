using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player-facing "beat" toggle for the PhaseLibraryCarousel: plays a medium/high-intensity drum
/// loop from the selected motif under its ring preview (RingPreviewPlayer.SetBeat). The loop starts
/// on the preview's next bar and is re-picked for each motif while the beat stays on.
/// Intended to become an unlockable item; for now it is always available on the keyboard.
/// </summary>
public class PreviewBeatToggle : MonoBehaviour
{
    [SerializeField] private RingPreviewPlayer previewPlayer;
    [SerializeField] private Key toggleKey = Key.B;

    private bool _on;

    private void Awake()
    {
        if (previewPlayer == null) previewPlayer = GetComponent<RingPreviewPlayer>();
    }

    // Unlockable gate: replace with the unlock/progression check once the item exists.
    protected virtual bool IsUnlocked() => true;

    private void Update()
    {
        // Same modal guard as PhaseLibraryCarousel.Update: the settings panel owns input while open.
        if (ModalMenu.ShouldSuppressInput) return;
        if (previewPlayer == null || !IsUnlocked()) return;

        var kb = Keyboard.current;
        if (kb == null || !kb[toggleKey].wasPressedThisFrame) return;

        _on = !_on;
        previewPlayer.SetBeat(_on);
    }
}
