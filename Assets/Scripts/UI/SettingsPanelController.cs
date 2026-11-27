using UnityEngine;
using UnityEngine.InputSystem;

// Bootstrap for the Options menu, left on its Main.unity object so the scene wiring (the
// InputActionAsset reference) didn't have to change. All menu logic lives in OptionsMenu, which
// subscribes to Global/OpenSettings once and keeps working in every scene after this.
//
// The Global action map is enabled directly off the InputActionAsset (not through any
// PlayerInput), because Main.unity is the title/boot scene and often has zero LocalPlayers yet.
public class SettingsPanelController : MonoBehaviour
{
    [SerializeField] private InputActionAsset actions;

    private void Awake()
    {
        if (actions == null)
        {
            Debug.LogWarning("[SettingsPanelController] No InputActionAsset assigned.", this);
            return;
        }
        OptionsMenu.Install(actions);
    }
}
