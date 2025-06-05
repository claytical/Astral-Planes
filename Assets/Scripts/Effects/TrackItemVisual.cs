using TMPro;
using UnityEngine;

public class TrackItemVisual : MonoBehaviour
{

    [Header("Function & Color")]
    public TrackModifierType itemType = TrackModifierType.Expansion;
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
        if (itemType == TrackModifierType.Drift)
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


    private void ApplyItemStyle()
    {
        circleRenderer.color = GetModifiedTrackColor(itemType, trackColor);

        switch (itemType)
        {
            case TrackModifierType.Contract:
                iconText.text = "⇣";
                iconText.color = Color.yellow;
                shouldPulse = true;
                break;

            case  TrackModifierType.Solo:
                iconText.text = "◎";
                iconText.color = Color.green;
                shouldPulse = true;
                break;

            case  TrackModifierType.Remix:
                iconText.text = "⟳";
                iconText.color = Color.cyan;
                shouldPulse = true;
                shouldRotate = true;
                break;

            case  TrackModifierType.Drift:
                iconText.text = "~";
                iconText.color = new Color(0.8f, 0.9f, 1f); // pale blue
                shouldPulse = true;
                shouldRotate = true;
                break;

            case  TrackModifierType.Magic:
                iconText.text = "✨";
                iconText.color = new Color(1f, 0.8f, 1f); // pink shimmer
                shouldPulse = true;
                shouldRotate = true;
                break;

            case  TrackModifierType.StructureShift:
                iconText.text = "#";
                iconText.fontSize = 2;
                iconText.color = Color.white;
                shouldPulse = true;
                break;
            
            case TrackModifierType.MoodShift:
                iconText.text = "~"; // visual wave
                iconText.color = new Color(1f, 0.7f, 0.9f); // pastel rose
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
                case  TrackModifierType.AntiNote:
                    main.startColor = Color.black;
                    break;
                case  TrackModifierType.Drift:
                    main.startColor = new Color(0.8f, 0.9f, 1f);
                    break;
                case  TrackModifierType.Magic:
                    main.startColor = new Color(1f, 0.7f, 1f);
                    break;
                case  TrackModifierType.MoodShift:
                    main.startColor = new Color(1f, 0.8f, 0.9f);
                    break;
                case TrackModifierType.StructureShift:
                    main.startColor = Color.cyan;
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
    
    private Color GetModifiedTrackColor(TrackModifierType type, Color baseColor)
    {
        switch (type)
        {
            case  TrackModifierType.AntiNote:
                return Color.Lerp(baseColor, Color.black, 0.3f);
            case  TrackModifierType.Remix:
                return Color.Lerp(baseColor, Color.cyan, 0.4f);
            case  TrackModifierType.Contract:
                return Color.Lerp(baseColor, Color.yellow, 0.3f);
            case  TrackModifierType.Solo:
                return Color.Lerp(baseColor, Color.green, 0.3f);
            case  TrackModifierType.Drift:
                return new Color(0.6f, 0.7f, 1f); // indigo blend
            case  TrackModifierType.Magic:
                return new Color(0.9f, 0.6f, 1f); // soft pink shimmer
            case  TrackModifierType.StructureShift:
                return Color.Lerp(baseColor, Color.cyan, 0.3f);
            case  TrackModifierType.MoodShift:
                return Color.Lerp(baseColor, Color.magenta, 0.3f);
            default:
                return baseColor;
        }
    }
}
