using UnityEngine;
using TMPro;

[RequireComponent(typeof(TextMeshProUGUI))]
public class GlowPulseText : MonoBehaviour
{
    public float minGlow = 0.2f;
    public float maxGlow = 1.2f;
    public float speed = 2f;
    public string glowProperty = "_GlowPower"; // Some shaders might use "_GlowStrength"

    private TextMeshProUGUI tmpText;
    private Material textMaterial;
    private float timer;

    void Start()
    {
        tmpText = GetComponent<TextMeshProUGUI>();
        textMaterial = tmpText.fontMaterial;

        if (!textMaterial.HasProperty(glowProperty))
        {
            Debug.LogWarning($"GlowPulseText: Material does not contain property '{glowProperty}'.");
        }
    }

    void Update()
    {
        if (textMaterial == null || !textMaterial.HasProperty(glowProperty)) return;

        timer += Time.deltaTime * speed;
        float glow = Mathf.Lerp(minGlow, maxGlow, (Mathf.Sin(timer) + 1f) / 2f);
        textMaterial.SetFloat(glowProperty, glow);
    }
}