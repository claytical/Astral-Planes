using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Persistent Main-menu icon showing whatever the connected controller's Select/View/Share/Minus
// button looks like, signaling "press this to open the settings panel." Independent of
// SettingsPanelController's open/closed state — refreshes unconditionally on device change.
//
// Only shown once TelemetryConsent.HasDecided — before the player's first Living Score choice
// exists (no publish_consent.json yet), there's nothing for this panel to let them revisit, so
// advertising it here would be misleading. Checked every frame (not just on enable) since the
// player can make that first choice directly from this panel while still on this same screen.
public class SelectGlyphHint : MonoBehaviour
{
    [SerializeField] private Image glyphImage;
    [SerializeField] private TMP_Text label;

    private void OnEnable()
    {
        InputSystem.onDeviceChange += OnDeviceChange;
        Refresh();
    }

    private void OnDisable()
    {
        InputSystem.onDeviceChange -= OnDeviceChange;
    }

    private void Update()
    {
        // ControllerGlyphProvider.Instance (on SteamManager.prefab) may not have run its own
        // Awake() yet the moment this OnEnable()'d — cross-object Awake order isn't guaranteed —
        // so keep retrying until a sprite actually resolves, rather than staying blank forever.
        if (glyphImage != null && glyphImage.sprite == null) Refresh();
        else UpdateVisibility();
    }

    private void OnDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (change == InputDeviceChange.Added || change == InputDeviceChange.Removed
            || change == InputDeviceChange.UsageChanged)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        UpdateVisibility();
        if (glyphImage == null) return;
        var sprite = ControllerGlyphProvider.Instance?.GetOpenSettingsGlyph();
        glyphImage.sprite = sprite;
    }

    private void UpdateVisibility()
    {
        bool shouldShow = TelemetryConsent.HasDecided;
        if (glyphImage != null) glyphImage.enabled = shouldShow && glyphImage.sprite != null;
        if (label != null) label.enabled = shouldShow;
    }
}
