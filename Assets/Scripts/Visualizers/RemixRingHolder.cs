using UnityEngine;
using System.Collections.Generic;

public class RemixRingHolder : MonoBehaviour
{
    [SerializeField] private List<SpriteRenderer> ringSprites;

    private float flipTimer = 0f;
    private float flipInterval = 0.5f;
    private List<Color> currentColors = new();

    void Update()
    {
        if (currentColors.Count <= 1) return;

        flipTimer += Time.deltaTime;
        if (flipTimer >= flipInterval)
        {
            flipTimer = 0f;
            Color last = currentColors[^1];
            currentColors.RemoveAt(currentColors.Count - 1);
            currentColors.Insert(0, last);

            for (int i = 0; i < currentColors.Count; i++)
            {
                ringSprites[i].color = currentColors[i];
            }
        }
    }
    
    public void CycleColors(List<Color> shiftedColors)
    {
        for (int i = 0; i < ringSprites.Count; i++)
        {
            if (i < shiftedColors.Count)
                ringSprites[i].color = shiftedColors[i];
        }
    }
    public void ActivateRing(MusicalRole role, Color color)
    {
        // Find the first disabled ring and use it
        for (int i = 0; i < ringSprites.Count; i++)
        {
            if (!ringSprites[i].enabled)
            {
                ringSprites[i].enabled = true;
                ringSprites[i].color = color;

                currentColors.Add(color);
                return;
            }
        }

        Debug.LogWarning("No available ring slots to activate new remix role.");
    }
    public void SetColor(Color newColor)
    {
        for (int i = 0; i < ringSprites.Count; i++)
        {
            if (ringSprites[i] != null)
            {
                if (ringSprites[i].enabled)
                {
                    ringSprites[i].color = newColor;
                }
            }
        }
    }

    public void ClearAllRings()
    {
        foreach (var ring in ringSprites)
        {
            ring.enabled = false;
        }
        currentColors.Clear();
        flipTimer = 0f;
    }


}