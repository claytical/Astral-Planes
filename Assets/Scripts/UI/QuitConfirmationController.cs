using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Non-pausing "Quit the game?" overlay, opened/closed by GlobalEscapeQuitListener (a sibling
// component on the same persistent prefab) on a keyboard Escape tap. Lives on a
// DontDestroyOnLoad prefab instantiated once by GameFlowManager, so it works in every scene —
// unlike SettingsPanelController, which is Main.unity-only.
//
// Same shell/fade shape as SettingsPanelController: a CanvasGroup fade, no Time.timeScale
// pausing, and its own raw-Keyboard polling for navigation/activation rather than relying on
// InputSystemUIInputModule's Submit/Cancel routing — that module's actions asset reference is
// missing/dangling in the scenes that have it, so Submit/Cancel/Point can't be trusted here.
public class QuitConfirmationController : MonoBehaviour
{
    // GlobalEscapeQuitListener checks this before deciding an Escape tap should open the
    // modal (so a second tap while it's already open falls through to Toggle()'s close path
    // instead), and other pollers can use it to avoid double-handling input, mirroring
    // SettingsPanelController.IsOpen / LivingScorePanel.IsAwaitingChoice.
    public static bool IsOpen { get; private set; }

    private static int _closedFrame = -1;
    private static bool JustClosedThisFrame => Time.frameCount == _closedFrame;
    public static bool ShouldSuppressInput => IsOpen || JustClosedThisFrame;

    [SerializeField] private CanvasGroup group;
    [SerializeField] private float fadeSeconds = 0.2f;
    [SerializeField] private GameObject quitButton;
    [SerializeField] private GameObject cancelButton;
    [SerializeField] private GameObject defaultSelected; // Cancel — destructive choice isn't pre-highlighted

    private Coroutine _fadeRoutine;
    private bool _isOpen;

    private void Awake()
    {
        if (group != null)
        {
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }
    }

    private void OnDestroy()
    {
        IsOpen = false;
    }

    public void Toggle()
    {
        if (_isOpen) Close();
        else Open();
    }

    public void Open()
    {
        _isOpen = true;
        IsOpen = true;

        if (EventSystem.current != null && defaultSelected != null)
            EventSystem.current.SetSelectedGameObject(defaultSelected);

        if (group == null) return;
        group.interactable = true;
        group.blocksRaycasts = true;
        StartFade(1f);
    }

    public void Close()
    {
        _isOpen = false;
        IsOpen = false;
        _closedFrame = Time.frameCount;

        if (group == null) return;
        group.interactable = false;
        group.blocksRaycasts = false;
        StartFade(0f);
    }

    private void StartFade(float target)
    {
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeTo(target, fadeSeconds));
    }

    private IEnumerator FadeTo(float target, float duration)
    {
        float from = group.alpha;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            group.alpha = Mathf.Lerp(from, target, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }
        group.alpha = target;
    }

    private void Update()
    {
        if (!_isOpen || Keyboard.current == null || EventSystem.current == null) return;

        bool navPressed =
            Keyboard.current.tabKey.wasPressedThisFrame ||
            Keyboard.current.leftArrowKey.wasPressedThisFrame ||
            Keyboard.current.rightArrowKey.wasPressedThisFrame ||
            Keyboard.current.upArrowKey.wasPressedThisFrame ||
            Keyboard.current.downArrowKey.wasPressedThisFrame;

        if (navPressed)
        {
            var current = EventSystem.current.currentSelectedGameObject;
            var next = current == quitButton ? cancelButton : quitButton;
            EventSystem.current.SetSelectedGameObject(next != null ? next : defaultSelected);
        }

        if (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame)
        {
            var selected = EventSystem.current.currentSelectedGameObject;
            if (selected != null)
                ExecuteEvents.Execute(selected, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        }
    }

    public void OnQuitClicked() => GameFlowManager.Instance?.QuitApplication();

    public void OnCancelClicked() => Close();
}
