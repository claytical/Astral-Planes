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

    private void Awake()
    {
        _baseRotation = transform.localRotation; // preserves the prefab's authored rest tilt
        if (abortHoldPromptPrefab != null && fuel != null)
            _abortHoldPrompt = Instantiate(abortHoldPromptPrefab, fuel.transform, false);
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


}
