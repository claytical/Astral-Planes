using TMPro;
using UnityEngine;

public class TrackItemVisual : MonoBehaviour
{
    public enum ItemType { Expansion, AntiNote, Clear, Shoft }

    [Header("Function & Color")]
    public ItemType itemType = ItemType.Expansion;
    public Color trackColor = Color.cyan;

    [Header("Base Animation")]
    public float baseScale = 1f;
    public float pulseAmplitude = 0.1f;
    public float pulseSpeed = 2f;
    public float hoverAmplitude = 0.1f;
    public float hoverSpeed = 1f;
    public float rotationSpeed = 20f;

    [Header("Particles")]
    public ParticleSystem glowEffect;
    public ParticleSystem burstEffect;
    public bool tintParticlesToTrackColor = true;
    public bool burstOnStart = false;

    private SpriteRenderer circleRenderer;
    private TextMeshPro iconText;
    private Vector3 originalPosition;
    private bool shouldPulse;
    private bool shouldHover = true;
    private bool shouldRotate = true;

    void Start()
    {
        originalPosition = transform.position;
        CreateCircle();
        CreateIcon();
        ApplyItemStyle();
        SetupParticles();
    }

    void Update()
    {
        if (shouldPulse)
        {
            float scale = baseScale + Mathf.Sin(Time.time * pulseSpeed) * pulseAmplitude;
            transform.localScale = new Vector3(scale, scale, 1f);
        }

        if (shouldHover)
        {
            float offsetY = Mathf.Sin(Time.time * hoverSpeed) * hoverAmplitude;
            transform.position = originalPosition + new Vector3(0f, offsetY, 0f);
        }

        if (shouldRotate)
        {
            transform.Rotate(Vector3.forward, rotationSpeed * Time.deltaTime);
        }
        if (itemType == ItemType.Shoft)
        {
            float hue = Mathf.PingPong(Time.time * 0.2f, 1f);
            circleRenderer.color = Color.HSVToRGB(hue, 0.6f, 1f);
        }

    }

    private void CreateCircle()
    {
        GameObject circleObj = new GameObject("Circle");
        circleObj.transform.SetParent(transform, false);

        circleRenderer = circleObj.AddComponent<SpriteRenderer>();
        circleRenderer.sprite = Resources.Load<Sprite>("Circle");
        circleRenderer.color = trackColor;
        circleRenderer.sortingOrder = 0;
    }

    private void CreateIcon()
    {
        GameObject iconObj = new GameObject("Icon");
        iconObj.transform.SetParent(transform, false);

        iconText = iconObj.AddComponent<TextMeshPro>();
        iconText.fontSize = 4;
        iconText.color = Color.white;
        iconText.alignment = TextAlignmentOptions.Center;
        iconText.text = "?";
        iconText.enableAutoSizing = true;
        iconText.rectTransform.sizeDelta = new Vector2(1, 1);
        iconText.sortingOrder = 1;
    }
    private void ApplyItemStyle()
    {
        circleRenderer.color = GetModifiedTrackColor(itemType, trackColor);

        switch (itemType)
        {
            case ItemType.Expansion:
                iconText.text = "+";
                shouldPulse = true;
                break;

            case ItemType.AntiNote:
                iconText.text = "×";
                shouldPulse = false;
                break;

            case ItemType.Clear:
                iconText.text = "−";
                shouldPulse = false;
                break;

            case ItemType.Shoft:
                iconText.text = "?";
                iconText.color = new Color(1f, 0.9f, 0.6f); // soft glowing off-white
                shouldPulse = true;
                shouldRotate = true;
                break;
        }
    }

    private void SetupParticles()
    {
        if (glowEffect == null)
            glowEffect = GetComponentInChildren<ParticleSystem>();

        if (glowEffect != null)
        {
            var main = glowEffect.main;

            switch (itemType)
            {
                case ItemType.Shoft:
                    main.startColor = Color.Lerp(trackColor, Color.magenta, 0.8f);
                    break;
                case ItemType.AntiNote:
                    main.startColor = Color.black;
                    break;
                default:
                    if (tintParticlesToTrackColor)
                        main.startColor = trackColor;
                    break;
            }

            glowEffect.Play();
        }

        if (burstOnStart && burstEffect != null)
        {
            burstEffect.Emit(15);
        }
    }

    public void PlayBurstAndDestroy()
    {
        if (glowEffect != null) glowEffect.Stop();
        if (burstEffect != null) burstEffect.Emit(25);

        Destroy(gameObject, 0.5f);
    }
    private Color GetModifiedTrackColor(ItemType type, Color baseColor)
    {
        switch (type)
        {
            case ItemType.Expansion:
                return baseColor; // Use track color directly
            case ItemType.AntiNote:
                return Color.Lerp(baseColor, Color.black, 0.3f);
            case ItemType.Clear:
                return Color.Lerp(baseColor, Color.gray, 0.5f);
            case ItemType.Shoft:
                return Color.Lerp(baseColor, Color.magenta, 0.5f);
            default:
                return baseColor;
        }
    }

}
