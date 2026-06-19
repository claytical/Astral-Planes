using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerSelect : MonoBehaviour
{
    public Image planeIcon;
    public VehicleStatDisplay statDisplay;
    public GameObject controlsAndStats;
    public GameObject tutorialControls;
    public Image border;

    private int _selectedColor = 0;
    private Hangar _hangar;
    private GameObject _chosenPlane;
    private string _currentShipName;

    void Start()
    {
        _hangar = FindAnyObjectByType<Hangar>();
        if (_hangar?.planeSelection != null)
        {
            transform.SetParent(_hangar.planeSelection.transform);
            transform.localScale = Vector3.one;
        }

        if (_hangar)
        {
            AssignFirstAvailablePlane();
            SetVehicleStats();
        }
        else
        {
            Debug.LogError("❌ Hangar not found.");
        }
    }

    private void AssignFirstAvailablePlane()
    {
        _chosenPlane = _hangar.FirstAvailablePlane();
        SetCurrentShipName(_chosenPlane.name.Replace("(Clone)", "").Trim());

        if (_chosenPlane == null)
        {
            Debug.LogWarning("No available planes to assign.");
            return;
        }

        ApplyVisuals();
        _hangar.MarkPlaneInUse(_chosenPlane, true);
    }
    private void SetVehicleStats()
    {
        if (_chosenPlane && _chosenPlane.TryGetComponent(out Vehicle vehicle))
        {
            SetStats(vehicle);
        }
    }
    private void SetStats(Vehicle vehicle)
    {
        if (statDisplay == null) return;

        ShipMusicalProfile profile = vehicle.profile;
        float speed, agility, power, fuel;

        if (profile != null)
        {
            speed   = profile.arcadeMaxSpeed / 20f;
            agility = profile.directionalAuthority01;
            power   = profile.plowChipAmount
                      * (profile.plowHalfWidthCells * 2 + 1)
                      * (1f + profile.carveResistanceBypass01)
                      / 15f;
            fuel    = (profile.capacity / profile.burnRate) / 700f;
        }
        else
        {
            speed   = vehicle.arcadeMaxSpeed / 20f;
            agility = 0.5f;
            power   = 0.5f;
            fuel    = vehicle.capacity / 50f;
        }

        statDisplay.SetStats(
            Mathf.Clamp01(speed),
            Mathf.Clamp01(agility),
            Mathf.Clamp01(power),
            Mathf.Clamp01(fuel)
        );
    }

    private void ApplyVisuals()
    {
        SpriteRenderer sr = _chosenPlane.GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            planeIcon.sprite = sr.sprite;
        }
    }
    public void Confirm()
    {
        controlsAndStats.SetActive(false);
        planeIcon.transform.parent.gameObject.SetActive(false);
        border.enabled = false;
    }

    public void NextVehicle()
    {
        if (_chosenPlane != null)
            _hangar.MarkPlaneInUse(_chosenPlane, false);

        int currentIndex = System.Array.IndexOf(_hangar.planes, _chosenPlane);
        int nextIndex = _hangar.NextAvailablePlane(currentIndex);
        _chosenPlane = _hangar.planes[nextIndex];
        SetCurrentShipName(_chosenPlane.name.Replace("(Clone)", "").Trim());
        ApplyVisuals();
        SetVehicleStats();
        _hangar.MarkPlaneInUse(_chosenPlane, true);
    }
    public void PreviousVehicle()
    {
        if (_chosenPlane != null)
            _hangar.MarkPlaneInUse(_chosenPlane, false);

        int currentIndex = System.Array.IndexOf(_hangar.planes, _chosenPlane);
        int prevIndex = _hangar.PreviousAvailableVehicle(currentIndex);
        _chosenPlane = _hangar.planes[prevIndex];
        SetCurrentShipName(_chosenPlane.name.Replace("(Clone)", "").Trim());

        ApplyVisuals();
        SetVehicleStats();
        _hangar.MarkPlaneInUse(_chosenPlane, true);
    }
    public GameObject GetChosenPlane()
    {
        return _chosenPlane;
    }
    public string GetCurrentShipName()
    {
        return _currentShipName; // or however you're storing it internally
    }
    private void SetCurrentShipName(string name)
    {
        _currentShipName = name;
    }


}
