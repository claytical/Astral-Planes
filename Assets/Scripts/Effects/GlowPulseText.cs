using UnityEngine;
using TMPro;
[RequireComponent(typeof(TextMeshProUGUI))]
public class GlowPulseText : MonoBehaviour
{
    public float minGlow = 0.2f;
    public float maxGlow = 1.2f;
    public float speed = 2f;
    public string glowProperty = "_GlowPower"; // Some shaders might use "_GlowStrength"

    // NOTE (VARIABLE_AUDIT): the fontMaterial grab was commented out, so this is never assigned and
    // the component is inert. Start used to NRE on it. Either restore
    // `_textMaterial = GetComponent<TextMeshProUGUI>().fontMaterial;` or remove the component from scenes.
    private Material _textMaterial;
    private float _timer;

    void Update()
    {
        if (_textMaterial == null || !_textMaterial.HasProperty(glowProperty)) return;

        _timer += Time.deltaTime * speed;
        float glow = Mathf.Lerp(minGlow, maxGlow, (Mathf.Sin(_timer) + 1f) / 2f);
        _textMaterial.SetFloat(glowProperty, glow);
    }
}