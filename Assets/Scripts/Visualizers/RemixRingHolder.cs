using UnityEngine;
using System.Collections.Generic;

public class RemixRingHolder : MonoBehaviour
{
    [SerializeField] private List<SpriteRenderer> ringSprites;

    private float _flipTimer = 0f;
    private float _flipInterval = 0.5f;
    private List<Color> _currentColors = new();

    void Update()
    {
        if (_currentColors.Count <= 1) return;

        _flipTimer += Time.deltaTime;
        if (_flipTimer >= _flipInterval)
        {
            _flipTimer = 0f;
            Color last = _currentColors[^1];
            _currentColors.RemoveAt(_currentColors.Count - 1);
            _currentColors.Insert(0, last);

            for (int i = 0; i < _currentColors.Count; i++)
            {
                ringSprites[i].color = _currentColors[i];
            }
        }
    }
    public void SetColor(Color newColor)
    {
        foreach (var t in ringSprites)
        {
            if (t != null)
            {
                if (t.enabled)
                {
                    t.color = newColor;
                }
            }
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

                _currentColors.Add(color);
                return;
            }
        }

        Debug.LogWarning("No available ring slots to activate new remix role.");
    }
    public void ClearAllRings()
    {
        foreach (var ring in ringSprites)
        {
            ring.enabled = false;
        }
        _currentColors.Clear();
        _flipTimer = 0f;
    }


}