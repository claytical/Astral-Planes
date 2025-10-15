using UnityEngine;
using TMPro;
[RequireComponent(typeof(TextMeshProUGUI))]
public class GlowPulseText : MonoBehaviour
{
    public float minGlow = 0.2f;
    public float maxGlow = 1.2f;
    public float speed = 2f;
    public string glowProperty = "_GlowPower"; // Some shaders might use "_GlowStrength"

    private TextMeshProUGUI _tmpText;
    private Material _textMaterial;
    private float _timer;

    void Start()
    {
        _tmpText = GetComponent<TextMeshProUGUI>();
        _textMaterial = _tmpText.fontMaterial;

        if (!_textMaterial.HasProperty(glowProperty))
        {
            Debug.LogWarning($"GlowPulseText: Material does not contain property '{glowProperty}'.");
        }
    }

    void Update()
    {
        if (_textMaterial == null || !_textMaterial.HasProperty(glowProperty)) return;

        _timer += Time.deltaTime * speed;
        float glow = Mathf.Lerp(minGlow, maxGlow, (Mathf.Sin(_timer) + 1f) / 2f);
        _textMaterial.SetFloat(glowProperty, glow);
    }
}