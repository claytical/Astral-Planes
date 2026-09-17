using UnityEngine;

// Ownership: GameFlowManager.QuitFlow may mutate only the lifecycle of the persistent
// quit-confirmation modal (instantiating it once under this object's DontDestroyOnLoad root)
// and the one call site that actually exits the application. It knows nothing about how the
// modal decides to open itself (see GlobalEscapeQuitListener) — only how to spawn it.
public partial class GameFlowManager
{
    [Header("Quit Confirmation")]
    [Tooltip("Persistent modal (Canvas + GlobalEscapeQuitListener + QuitConfirmationController) " +
             "instantiated once under this object's DontDestroyOnLoad root, so a keyboard Escape " +
             "tap can open a quit-application confirmation from any scene.")]
    [SerializeField] private GameObject quitConfirmationPrefab;

    private QuitConfirmationController _quitConfirmation;

    private void SetupQuitConfirmation()
    {
        if (_quitConfirmation != null) return;
        if (quitConfirmationPrefab == null)
        {
            Debug.LogWarning("[GameFlowManager] No quitConfirmationPrefab assigned — Escape-to-quit will not be available.", this);
            return;
        }

        var instance = Instantiate(quitConfirmationPrefab);
        DontDestroyOnLoad(instance);
        _quitConfirmation = instance.GetComponent<QuitConfirmationController>();
    }

    // Single call site for a real desktop application exit — mirrors QuitToSelection() being the
    // single call site for the "return to ship-select" terminal flow.
    public void QuitApplication()
    {
        Application.Quit();
    }
}
