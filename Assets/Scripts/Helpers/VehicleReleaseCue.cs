using System;
using UnityEngine;
public class VehicleReleaseCue : MonoBehaviour
{
    [Header("Ring")]
    public SpriteRenderer ringSprite;       // radial fill shader, or swap for UI Image
    public float ringFillLerpSpeed = 8f;

    [Header("Beat Dots")]
    public GameObject dotPrefab;            // small circle sprite
    public float dotRadius = 0.5f;
    public Color dotActiveColor  = Color.white;
    public Color dotSpentColor   = new Color(1,1,1,0.15f);

    private float _targetFill;
    private GameObject[] _dots = Array.Empty<GameObject>();
    private int _totalBeats;

    // Called every frame from Vehicle (passes gap-normalized pulse01)
    public void SetFill(float pulse01) => _targetFill = pulse01;

    // Called on each step tick
    public void SetBeatsRemaining(int remaining, int total)
    {
        if (total != _totalBeats) RebuildDots(total);
        for (int i = 0; i < _dots.Length; i++)
        {
            var sr = _dots[i].GetComponent<SpriteRenderer>();
            // Dots light up from the right as beats are consumed
            sr.color = (i >= _dots.Length - remaining) ? dotActiveColor : dotSpentColor;
        }
    }

    private void RebuildDots(int count)
    {
        foreach (var d in _dots) Destroy(d);
        _totalBeats = count;
        _dots = new GameObject[count];
        for (int i = 0; i < count; i++)
        {
            float angle = (360f / count) * i - 90f;
            var pos = (Vector3)(dotRadius * new Vector2(
                Mathf.Cos(angle * Mathf.Deg2Rad),
                Mathf.Sin(angle * Mathf.Deg2Rad)));
            _dots[i] = Instantiate(dotPrefab, transform.position + pos,
                                   Quaternion.identity, transform);
        }
    }

    private void Update()
    {
        if (ringSprite == null) return;
        // Drive a _Fill property on a radial fill material, or scale a arc mesh
        float fill = Mathf.Lerp(ringSprite.material.GetFloat("_Fill"),
                                _targetFill, ringFillLerpSpeed * Time.deltaTime);
        ringSprite.material.SetFloat("_Fill", fill);

        // Hide everything when idle
        bool active = _targetFill > 0.01f;
        ringSprite.enabled = active;
        foreach (var d in _dots) d.SetActive(active);
    }
}