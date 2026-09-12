using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Non-pausing settings overlay, opened/closed by the Global/OpenSettings action (the gamepad
// Select/View/Share/Minus button). Lives in Main.unity only for v1 — the Global action map is
// enabled directly off the InputActionAsset (not through any PlayerInput), because Main.unity
// is the title/boot scene and often has zero LocalPlayer instances joined yet.
//
// Pure open/close/fade shell — content (currently a publish-consent panel, see
// PublishSettingsPanel.cs) is a child prefab that knows nothing about fading, and calls Close()
// on this component (found via GetComponentInParent) when the player makes a choice.
public class SettingsPanelController : MonoBehaviour
{
    // IntroInputHandler.OnAnyInput and PhaseLibraryCarousel.Update both poll for a raw
    // South/Submit press on the same title screen this panel overlays — they have no other way
    // to know it's up. Checked there so a Choose/South press meant for this panel's buttons
    // doesn't also fall through to them. Mirrors LivingScorePanel.IsAwaitingChoice's guard role
    // in TrackFinished.
    public static bool IsOpen { get; private set; }

    // Close() runs synchronously inside the same South-press callback that confirmed a choice
    // (OnConfirmPressed -> Button.OnClick -> Close()), clearing IsOpen before other pollers
    // (e.g. PhaseLibraryCarousel.Update) get to check it later in that same frame — the same
    // physical press would then read as "confirm" all over again on whatever's behind this
    // panel. Latching a frame number, exactly like LivingScorePanel's
    // _choiceMadeFrame/JustMadeChoiceThisFrame, keeps the guard up for the rest of that frame
    // regardless of callback/Update ordering.
    private static int _closedFrame = -1;
    private static bool JustClosedThisFrame => Time.frameCount == _closedFrame;
    public static bool ShouldSuppressInput => IsOpen || JustClosedThisFrame;

    [SerializeField] private InputActionAsset actions;
    [SerializeField] private CanvasGroup group;
    [SerializeField] private float fadeSeconds = 0.2f;

    private InputAction _openSettingsAction;
    private InputAction _confirmAction;
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

        if (actions == null)
        {
            Debug.LogWarning("[SettingsPanelController] No InputActionAsset assigned.", this);
            return;
        }

        _openSettingsAction = actions.FindActionMap("Global")?.FindAction("OpenSettings");
        if (_openSettingsAction == null)
        {
            Debug.LogWarning("[SettingsPanelController] Global/OpenSettings action not found.", this);
            return;
        }

        _openSettingsAction.started += OnOpenSettingsPressed;
        _openSettingsAction.Enable();

        // Choose/South is hard-bound to raw buttonSouth everywhere else in this game
        // (AstralPlanes.inputactions), independent of controller brand — but Unity's own
        // semantic UI Submit action (which the panel's buttons respond to natively) resolves to
        // a DIFFERENT physical button per brand (e.g. buttonEast on a Switch Pro Controller).
        // East already works via that native Submit routing; this manually submits the
        // currently-selected button on raw South too, so both act as confirm on every
        // controller. Same technique as LivingScorePanel.ConfirmCurrentSelection().
        _confirmAction = actions.FindActionMap("Global")?.FindAction("Confirm");
        if (_confirmAction != null)
        {
            _confirmAction.started += OnConfirmPressed;
            _confirmAction.Enable();
        }
    }

    private void OnDestroy()
    {
        IsOpen = false;
        if (_openSettingsAction != null)
            _openSettingsAction.started -= OnOpenSettingsPressed;
        if (_confirmAction != null)
            _confirmAction.started -= OnConfirmPressed;
    }

    private void OnOpenSettingsPressed(InputAction.CallbackContext ctx)
    {
        if (_isOpen) Close();
        else Open();
    }

    private void OnConfirmPressed(InputAction.CallbackContext ctx)
    {
        if (!_isOpen) return;
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected == null) return;
        ExecuteEvents.Execute(selected, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
    }

    private void Open()
    {
        _isOpen = true;
        IsOpen = true;

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
            elapsed += Time.deltaTime;
            group.alpha = Mathf.Lerp(from, target, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }
        group.alpha = target;
    }
}
