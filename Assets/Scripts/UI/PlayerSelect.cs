using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerSelect : MonoBehaviour
{
    public Image planeIcon;
    public Fuel fuel;
    public Color[] planeColors;
    public GameObject controlsAndStats;
    public Image border;

    private int selectedColor = 0;
    private Hangar hangar;
    private GameObject chosenPlane;

    void Start()
    {
        hangar = FindAnyObjectByType<Hangar>();
        transform.SetParent(hangar.setupScreen.transform);
        transform.localScale = Vector3.one;

        if (hangar)
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
        chosenPlane = hangar.FirstAvailablePlane();
        SetCurrentShipName(chosenPlane.name.Replace("(Clone)", "").Trim());

        if (chosenPlane == null)
        {
            Debug.LogWarning("No available planes to assign.");
            return;
        }

        ApplyVisuals();
        hangar.MarkPlaneInUse(chosenPlane, true);
    }

    private void ApplyVisuals()
    {
        SpriteRenderer sr = chosenPlane.GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            planeIcon.sprite = sr.sprite;
            planeIcon.color = planeColors[selectedColor];
        }
        else
        {
            Debug.LogWarning("Selected plane has no SpriteRenderer.");
        }
    }

    private void SetVehicleStats()
    {
        if (chosenPlane && chosenPlane.TryGetComponent(out Vehicle vehicle))
        {
            SetStats(vehicle);
        }
    }

    public void SetStats(Vehicle vehicle)
    {
        float capacity = vehicle.capacity;
        float maxCapacity = hangar.GetMaxCapacity();

        if (fuel != null && maxCapacity > 0)
        {
            float ratio = Mathf.Clamp01(capacity / maxCapacity);
            fuel.UpdateFuelUI(ratio);
        }
        else
        {
            fuel?.UpdateFuelUI(1f); // fallback
        }
    }

    public void SetVehicleIconColor(Color color)
    {
        planeIcon.color = color;
    }

    public void NextColor()
    {
        selectedColor = (selectedColor + 1) % planeColors.Length;
        SetVehicleIconColor(planeColors[selectedColor]);
    }

    public void PreviousColor()
    {
        selectedColor = (selectedColor - 1 + planeColors.Length) % planeColors.Length;
        SetVehicleIconColor(planeColors[selectedColor]);
    }

    public void NextVehicle()
    {
        Debug.Log("Next Vehicle");
        if (chosenPlane != null)
            hangar.MarkPlaneInUse(chosenPlane, false);

        int currentIndex = System.Array.IndexOf(hangar.planes, chosenPlane);
        int nextIndex = hangar.NextAvailablePlane(currentIndex);
        chosenPlane = hangar.planes[nextIndex];
        SetCurrentShipName(chosenPlane.name.Replace("(Clone)", "").Trim());
        ApplyVisuals();
        SetVehicleStats();
        hangar.MarkPlaneInUse(chosenPlane, true);
    }

    public void PreviousVehicle()
    {
        if (chosenPlane != null)
            hangar.MarkPlaneInUse(chosenPlane, false);

        int currentIndex = System.Array.IndexOf(hangar.planes, chosenPlane);
        int prevIndex = hangar.PreviousAvailableVehicle(currentIndex);
        chosenPlane = hangar.planes[prevIndex];
        SetCurrentShipName(chosenPlane.name.Replace("(Clone)", "").Trim());

        ApplyVisuals();
        SetVehicleStats();
        hangar.MarkPlaneInUse(chosenPlane, true);
    }

    public GameObject GetChosenPlane()
    {
        return chosenPlane;
    }
    
    public string GetCurrentShipName()
    {
        return currentShipName; // or however you're storing it internally
    }

    private string currentShipName;

    public void SetCurrentShipName(string name)
    {
        currentShipName = name;
    }

    public void Confirm()
    {
        controlsAndStats.SetActive(false);
        planeIcon.transform.parent.gameObject.SetActive(false);
        border.enabled = false;
    }
}
