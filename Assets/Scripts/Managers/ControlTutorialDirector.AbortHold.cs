public partial class ControlTutorialDirector
{
    // Authored in the Editor as a child of primaryPrefab's "Primary" sub-hierarchy (Control
    // Scheme.prefab) — NOT the "Mini" sub-hierarchy LocalPlayer.miniTutorialPrefab points to.
    // Discovered fresh each scene load in RebuildPrimaryInstanceForScene(). Deliberately not
    // routed through primaryInstance/ShowWaitingFor — ShowWaitingFor calls StopAllModes()
    // internally, which would kill the running Drift/Boost/Release tutorial coroutine on an
    // early release. AbortHoldPrompt is a separate component, so it never touches that state.
    private AbortHoldPrompt _abortHoldPrompt;

    public void BeginAbortHoldUI(string label, float holdDurationSeconds) => _abortHoldPrompt?.BeginHold(label, holdDurationSeconds);

    public void UpdateAbortHoldUI(float t01) => _abortHoldPrompt?.UpdateHold(t01);

    public void CancelAbortHoldUI() => _abortHoldPrompt?.CancelHold();

    public void EndAbortHoldUI() => _abortHoldPrompt?.EndHold();
}
