using TMPro;
using UnityEngine;

public class ContinueCountdown : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI countdownText;
    [SerializeField] private TextMeshProUGUI promptText;
    [SerializeField] private string promptMessage = "CONTINUE?";

    public void Show(float totalSeconds)
    {
        gameObject.SetActive(true);
        if (promptText) promptText.text = promptMessage;
        UpdateCountdown(totalSeconds);
    }

    public void UpdateCountdown(float remaining)
    {
        if (countdownText) countdownText.text = Mathf.CeilToInt(remaining).ToString();
    }

    public void Hide()
    {
        gameObject.SetActive(false);
    }
}
