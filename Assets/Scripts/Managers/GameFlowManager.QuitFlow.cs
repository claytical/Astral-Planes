using UnityEngine;

// Ownership: GameFlowManager.QuitFlow owns attaching the keyboard Escape-tap quit listener to this
// DontDestroyOnLoad object (so it works in every scene) and the one call site that actually exits
// the application. The prompt itself is QuitPrompt, shown through the generic ModalMenu.
public partial class GameFlowManager
{
    private void SetupQuitConfirmation()
    {
        if (GetComponent<GlobalEscapeQuitListener>() == null)
            gameObject.AddComponent<GlobalEscapeQuitListener>();
    }

    // Single call site for a real desktop application exit — mirrors QuitToSelection() being the
    // single call site for the "return to ship-select" terminal flow.
    public void QuitApplication() => QuitPrompt.QuitNow();
}
