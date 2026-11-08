using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

public partial class LocalPlayer
{
    public AudioClip confirmFx;

    private bool _isReady;
    private bool _isInContinueWindow;
    private bool _isRespawning;

    public bool IsReady
    {
        get => _isReady;
        set => _isReady = value;
    }

    /// <summary>Competitive modes: permanently out after dying (no continue window, no respawn).</summary>
    public bool IsEliminated { get; private set; }

    public bool IsInContinueWindow => _isInContinueWindow;
    public bool IsRespawning => _isRespawning;

    [SerializeField] private ContinueCountdown continueCountdownPrefab;
    private ContinueCountdown _continueCountdown;
    private const float ContinueWindowSeconds = 30f;

    private void HandleConfirm()
    {
        // Selection/Choose and Play/Choose are both hard-bound to raw buttonSouth for every
        // controller brand (see AstralPlanes.inputactions) — unlike Unity's semantic UI
        // Submit/Cancel, which the settings panel's buttons actually respond to and which can
        // resolve to a different physical button per brand. A South press meant only to confirm
        // that panel must not also fall through to whatever this raw action drives here (menu
        // confirm, ship-select, restart, etc.) — same guard role as LivingScorePanel.ShouldSuppressConfirm.
        // ShouldSuppressInput (not just IsOpen) also covers the same frame the panel just closed
        // on this exact press.
        if (SettingsPanelController.ShouldSuppressInput) return;

        if (_isReady &&
            GameFlowManager.Instance != null &&
            GameFlowManager.Instance.CurrentState == GameState.Selection &&
            ControlTutorialDirector.Instance != null &&
            ControlTutorialDirector.Instance.IsPrimaryTutorialRunning)
        {
            ControlTutorialDirector.Instance.SkipCurrentTutorialStep();
            return;
        }

        if (_isReady &&
            GameFlowManager.Instance != null &&
            GameFlowManager.Instance.CurrentState == GameState.Selection &&
            GameFlowManager.Instance.SessionState.AwaitingModeConfirm)
        {
            _sfxSource?.PlayOneShot(confirmFx);
            GameFlowManager.Instance.ConfirmModeSelection();
            return;
        }

        OnChooseConfirm?.Invoke();

        if (_isReady ||GameFlowManager.Instance == null) return;

        switch (GameFlowManager.Instance.CurrentState)
        {
            case GameState.Selection:
                _sfxSource?.PlayOneShot(confirmFx);
                playerVehicle = _selection.GetChosenPlane();
                _selection.Confirm();
                SetColor();
                _isReady = true;
                IsReady = true;
                if (ControlTutorialDirector.Instance != null)
                    ControlTutorialDirector.Instance.Mini_Clear(this);
                GameFlowManager.Instance.CheckAllPlayersReady();
                break;

            case GameState.Playing:
                if (_isInContinueWindow)
                {
                    _isInContinueWindow = false;
                    if (_continueCountdown != null)
                    {
                        Destroy(_continueCountdown.gameObject);
                        _continueCountdown = null;
                    }
                    _isRespawning = true;
                    CreatePlayerSelect();
                }
                else if (_isRespawning)
                {
                    playerVehicle = _selection.GetChosenPlane();
                    if (playerVehicle == null)
                    {
                        // No vehicle left to claim (hangar exhausted, or lost the
                        // race for the last one against another player's continue).
                        // End this gamepad's run instead of launching with nothing.
                        _isRespawning = false;
                        if (_selection != null)
                        {
                            Destroy(_selection.gameObject);
                            _selection = null;
                        }
                        GameFlowManager.Instance?.CheckAllPlayersOutOfEnergy();
                        break;
                    }

                    _sfxSource?.PlayOneShot(confirmFx);
                    _selection.Confirm();
                    SetColor();
                    _isRespawning = false;
                    _isReady = true;
                    Launch();
                }
                break;

            case GameState.GameOver:
                // While the Living Score publish-consent prompt is open, this same raw
                // Choose/South press is the one reliable, controller-agnostic "confirm" in the
                // game (the EventSystem's own Submit routing doesn't reach it — see
                // LivingScorePanel.ConfirmCurrentSelection) — so drive the prompt's currently
                // selected button from it directly instead of restarting the run out from under it.
                if (LivingScorePanel.IsAwaitingChoice)
                {
                    LivingScorePanel.ConfirmCurrentSelection();
                    break;
                }
                if (LivingScorePanel.ShouldSuppressConfirm) break;
                _sfxSource?.PlayOneShot(confirmFx);
                Restart();
                break;
        }
    }

    public void ResetReady()
    {
        IsEliminated = false;
        _isReady = false;
        IsReady = false;
        _isInContinueWindow = false;
        _isRespawning = false;
    }

    public void OnVehicleDied()
    {
        _isReady = false;
        Hangar.Instance?.MarkVehiclePermanentlyUsed(playerVehicle);
        plane = null;
        _launched = false;
        _launchStarted = false;

        if (GameFlowManager.Instance?.SessionState?.IsCompetitive == true)
        {
            IsEliminated = true;
            return;
        }

        // Fully chained ?. — SessionState/Players being null (not just Instance) must not throw
        // here: this runs synchronously on whatever call stack killed the vehicle, which can be
        // a bomb's Detonate() drain loop, and an unguarded NRE there would abort cleanup.
        int playerCount = GameFlowManager.Instance?.SessionState?.Players?.Count ?? 1;
        bool isMultiplayer = playerCount >= 2;
        bool vehiclesAvailable = Hangar.Instance?.HasAvailableVehicles() ?? false;

        if (isMultiplayer && vehiclesAvailable)
            StartCoroutine(ContinueWindowRoutine());
    }

    private IEnumerator ContinueWindowRoutine()
    {
        _isInContinueWindow = true;
        _playerInput.SwitchCurrentActionMap("Selection");

        if (continueCountdownPrefab != null && _ui != null)
        {
            _continueCountdown = Instantiate(continueCountdownPrefab, _ui.transform);
            var rect = _continueCountdown.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            _continueCountdown.Show(ContinueWindowSeconds);
        }

        float remaining = ContinueWindowSeconds;
        while (_isInContinueWindow && remaining > 0f)
        {
            remaining -= Time.deltaTime;
            _continueCountdown?.UpdateCountdown(remaining);
            yield return null;
        }

        if (_isInContinueWindow) // timer expired — player chose not to continue
        {
            _isInContinueWindow = false;
            if (_continueCountdown != null)
            {
                Destroy(_continueCountdown.gameObject);
                _continueCountdown = null;
            }
            GameFlowManager.Instance?.CheckAllPlayersOutOfEnergy();
        }
    }

    private void Restart()
    {
        _isReady = false;

        if (_selection != null)
        {
            Destroy(_selection.gameObject);
        }

        GetComponent<PlayerInput>().SwitchCurrentActionMap("Selection");
        Destroy(gameObject);
        GameFlowManager.Instance.QuitToSelection();
    }
}
