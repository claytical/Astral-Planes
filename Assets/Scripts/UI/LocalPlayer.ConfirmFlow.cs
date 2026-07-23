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

    public bool IsInContinueWindow => _isInContinueWindow;
    public bool IsRespawning => _isRespawning;

    [SerializeField] private ContinueCountdown continueCountdownPrefab;
    private ContinueCountdown _continueCountdown;
    private const float ContinueWindowSeconds = 30f;

    private void HandleConfirm()
    {
        if (_isReady &&
            GameFlowManager.Instance != null &&
            GameFlowManager.Instance.CurrentState == GameState.Selection &&
            ControlTutorialDirector.Instance != null &&
            ControlTutorialDirector.Instance.IsPrimaryTutorialRunning)
        {
            ControlTutorialDirector.Instance.SkipCurrentTutorialStep();
            return;
        }

        OnChooseConfirm?.Invoke();

        if (_isReady || GameFlowManager.Instance == null) return;

        switch (GameFlowManager.Instance.CurrentState)
        {
            case GameState.Selection:
                GetComponent<AudioSource>().PlayOneShot(confirmFx);
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

                    GetComponent<AudioSource>().PlayOneShot(confirmFx);
                    _selection.Confirm();
                    SetColor();
                    _isRespawning = false;
                    _isReady = true;
                    Launch();
                }
                break;

            case GameState.GameOver:
                GetComponent<AudioSource>().PlayOneShot(confirmFx);
                Restart();
                break;
        }
    }

    public void ResetReady()
    {
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

        int playerCount = GameFlowManager.Instance?.SessionState.Players.Count ?? 1;
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
