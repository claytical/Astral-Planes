using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One vehicle on the match results screen: ship icon with the final score underneath. The winner cycles through a rainbow.</summary>
public class VehicleResultView : MonoBehaviour
{
    [SerializeField] private Image icon;
    [SerializeField] private TMP_Text scoreText;
    [SerializeField] private float hueCyclesPerSecond = 0.5f;
    [Range(0f, 1f)] [SerializeField] private float saturation = 0.8f;
    [Range(0f, 1f)] [SerializeField] private float value = 1f;

    private bool _winner;

    public void Bind(Sprite sprite, Color tint, int score)
    {
        if (icon != null)
        {
            if (sprite != null) icon.sprite = sprite;
            icon.color = tint;
        }
        if (scoreText != null)
        {
            scoreText.text = score.ToString();
            scoreText.color = tint;
        }
    }

    public void SetWinner(bool winner) => _winner = winner;

    // Unscaled time: gameplay is frozen while results show.
    private void Update()
    {
        if (!_winner) return;
        var c = Color.HSVToRGB(Mathf.Repeat(Time.unscaledTime * hueCyclesPerSecond, 1f), saturation, value);
        if (icon != null) icon.color = c;
    }
}
