using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Serialization;
using TMPro;

public class PlayerStats : MonoBehaviour
{
    public Image vehicleIcon;
    public Fuel fuel;

    [FormerlySerializedAs("abortHoldPrompt")]
    [SerializeField] private AbortHoldPrompt abortHoldPromptPrefab;
    private AbortHoldPrompt _abortHoldPrompt;
    private Vehicle _currentVehicle;
    private Quaternion _baseRotation;

    [Header("Competitive Mode (shown only while competitive)")]
    [Tooltip("The 'Score' and 'Current Round' parents in the prefab.")]
    [SerializeField] private GameObject[] competitiveRoots;
    [SerializeField] private TMP_Text totalText;
    [Tooltip("Optional 'Current Round' parent. Shown only while a multiplier burst is active; if it is also listed in Competitive Roots this overrides it.")]
    [SerializeField] private GameObject currentRoundRoot;
    [Tooltip("Multiplier parent; shown only while a multiplier burst is active.")]
    [SerializeField] private GameObject multiplierRoot;
    [SerializeField] private TMP_Text multiplierAmountText;
    [SerializeField] private TMP_Text noteScoreText;

    [Header("Competitive Pulse")]
    [Tooltip("Peak scale of a score/multiplier text when its value changes.")]
    [SerializeField, Min(1f)] private float pulseScale = 1.25f;
    [SerializeField, Min(0.01f)] private float pulseDuration = 0.25f;

    private PlayerStatsTracking _tracking;
    private SessionStateCoordinator _session;

    private int _lastTotal = -1, _lastRoundScore = -1, _lastMultiplier = -1;
    private Vector3 _totalRest, _roundScoreRest, _multiplierRest;
    private Coroutine _totalPulse, _roundScorePulse, _multiplierPulse;

    private void Awake()
    {
        _baseRotation = transform.localRotation; // preserves the prefab's authored rest tilt
        if (abortHoldPromptPrefab != null && fuel != null)
            _abortHoldPrompt = Instantiate(abortHoldPromptPrefab, fuel.transform, false);
        if (totalText != null) _totalRest = totalText.rectTransform.localScale;
        if (noteScoreText != null) _roundScoreRest = noteScoreText.rectTransform.localScale;
        if (multiplierAmountText != null) _multiplierRest = multiplierAmountText.rectTransform.localScale;
    }

    private void Update()
    {
        if (fuel == null) return;
        transform.localRotation = _baseRotation * Quaternion.Euler(0f, 0f, fuel.ShakeDegreesZ);
    }

    public void BeginAbortHold(string label, float holdDurationSeconds) => _abortHoldPrompt?.BeginHold(label, holdDurationSeconds);
    public void UpdateAbortHold(float t01) => _abortHoldPrompt?.UpdateHold(t01);
    public void CancelAbortHold() => _abortHoldPrompt?.CancelHold();
    public void EndAbortHold() => _abortHoldPrompt?.EndHold();

    public void SetStats(Vehicle vehicle)
    {
        // Store the vehicle ID and reference to the vehicle instance
        _currentVehicle = vehicle;
        if (vehicle != null)
        {
            vehicleIcon.sprite = vehicle.GetBaseSprite();
            UpdateFuel(_currentVehicle.capacity, _currentVehicle.capacity);
        }
        else {Debug.LogWarning("There is no vehicle attached to this object.");}
        
        if (GameFlowManager.VerboseLogging) Debug.Log("Energy collected should be displayed.");
    }
    public void UpdateFuel(float currentEnergy, float maxEnergy, float drainWeight01 = 0f)
    {
        float ratio = currentEnergy / maxEnergy;
        fuel.UpdateFuelUI(ratio, drainWeight01);
    }
    public void SetColor(Color color)
    {
        vehicleIcon.color = color;
    }

    /// <summary>Binds the competitive fields to a player's tracking and to the session mode. Safe to call repeatedly.</summary>
    public void BindCompetitive(PlayerStatsTracking tracking)
    {
        UnbindCompetitive();
        _lastTotal = _lastRoundScore = _lastMultiplier = -1;
        _tracking = tracking;
        if (_tracking != null) _tracking.CompetitiveChanged += RefreshCompetitive;

        _session = GameFlowManager.Instance != null ? GameFlowManager.Instance.SessionState : null;
        if (_session != null)
        {
            _session.ModeChanged += OnModeChanged;
            _session.PlayersChanged += RefreshCompetitive;
        }
        RefreshCompetitive();
    }

    private void UnbindCompetitive()
    {
        if (_tracking != null) _tracking.CompetitiveChanged -= RefreshCompetitive;
        if (_session != null)
        {
            _session.ModeChanged -= OnModeChanged;
            _session.PlayersChanged -= RefreshCompetitive;
        }
        _tracking = null;
        _session = null;
    }

    private static void SetActiveIfChanged(GameObject go, bool active)
    {
        if (go != null && go.activeSelf != active) go.SetActive(active);
    }

    private void OnModeChanged(GameMode _) => RefreshCompetitive();

    private void OnDestroy() => UnbindCompetitive();

    private void RefreshCompetitive()
    {
        bool show = _session != null && _session.IsCompetitive;
        if (competitiveRoots != null)
            foreach (var root in competitiveRoots)
                if (root != null && root.activeSelf != show) root.SetActive(show);
        // Round fields are per player: they open on that player's first pickup from a discovery
        // burst and close when the burst clears. Only the multiplier holder also sees the multiplier.
        bool roundActive = show && _tracking != null && _tracking.RoundActive;
        bool hasMultiplier = roundActive && _tracking.ActiveMultiplier > 0;
        SetActiveIfChanged(currentRoundRoot, roundActive);
        SetActiveIfChanged(multiplierRoot, hasMultiplier);
        if (noteScoreText != null) SetActiveIfChanged(noteScoreText.gameObject, roundActive);

        if (!show || _tracking == null) return;

        // The first value after a bind (or round open) is shown without a pulse only for the total;
        // round fields start at -1 so the first award of a round pulses.
        if (totalText != null)
        {
            int total = _tracking.CompetitiveTotal;
            totalText.text = total.ToString();
            if (_lastTotal >= 0 && total != _lastTotal) Pulse(totalText, _totalRest, ref _totalPulse);
            _lastTotal = total;
        }

        if (!roundActive)
        {
            _lastRoundScore = _lastMultiplier = -1;
            return;
        }

        if (noteScoreText != null)
        {
            int score = _tracking.RoundNoteScore;
            noteScoreText.text = score.ToString();
            if (score != _lastRoundScore) Pulse(noteScoreText, _roundScoreRest, ref _roundScorePulse);
            _lastRoundScore = score;
        }

        if (multiplierAmountText != null && hasMultiplier)
        {
            int mult = _tracking.ActiveMultiplier;
            multiplierAmountText.text = mult.ToString();
            if (mult != _lastMultiplier) Pulse(multiplierAmountText, _multiplierRest, ref _multiplierPulse);
            _lastMultiplier = mult;
        }
        else
        {
            _lastMultiplier = -1;
        }
    }

    private void Pulse(TMP_Text text, Vector3 rest, ref Coroutine handle)
    {
        if (!isActiveAndEnabled || !text.gameObject.activeInHierarchy) return;
        if (handle != null) StopCoroutine(handle);
        handle = StartCoroutine(PulseRoutine(text.rectTransform, rest));
    }

    private System.Collections.IEnumerator PulseRoutine(RectTransform target, Vector3 rest)
    {
        Vector3 peak = rest * pulseScale;
        for (float t = 0f; t < pulseDuration; t += Time.unscaledDeltaTime)
        {
            float envelope = Mathf.Sin(Mathf.PI * (t / pulseDuration));
            target.localScale = Vector3.LerpUnclamped(rest, peak, envelope);
            yield return null;
        }
        target.localScale = rest;
    }

}
