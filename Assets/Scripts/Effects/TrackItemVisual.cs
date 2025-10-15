using Gameplay.Mining;
using TMPro;
using UnityEngine;

namespace Effects
{
    public class TrackItemVisual : MonoBehaviour
    {

        [Header("Function & Color")]
        public TrackModifierType itemType = TrackModifierType.Remix;
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

        private SpriteRenderer _circleRenderer;
        private TextMeshPro _iconText;
        private Vector3 _originalPosition;
        private bool _shouldPulse;
        private bool _shouldHover = true;
        private bool _shouldRotate = true;

        void Start()
        {
            _originalPosition = transform.position;
            CreateCircle();
            ApplyItemStyle();
            SetupParticles();
        }

        void Update()
        {
            if (_shouldPulse)
            {
                float scale = baseScale + Mathf.Sin(Time.time * pulseSpeed) * pulseAmplitude;
                transform.localScale = new Vector3(scale, scale, 1f);
            }

            if (_shouldHover)
            {
                float offsetY = Mathf.Sin(Time.time * hoverSpeed) * hoverAmplitude;
                transform.position = _originalPosition + new Vector3(0f, offsetY, 0f);
            }

            if (_shouldRotate)
            {
                transform.Rotate(Vector3.forward, rotationSpeed * Time.deltaTime);
            }
            if (itemType == TrackModifierType.RootShift)
            {
                float hue = Mathf.PingPong(Time.time * 0.2f, 1f);
                _circleRenderer.color = Color.HSVToRGB(hue, 0.6f, 1f);
            }

        }

        private void CreateCircle()
        {
            GameObject circleObj = new GameObject("Circle");
            circleObj.transform.SetParent(transform, false);

            _circleRenderer = circleObj.AddComponent<SpriteRenderer>();
            _circleRenderer.sprite = Resources.Load<Sprite>("Circle");
            _circleRenderer.color = trackColor;
            _circleRenderer.sortingOrder = 0;
        }
        
        private void ApplyItemStyle()
        {
            _circleRenderer.color = GetModifiedTrackColor(itemType, trackColor);

            switch (itemType)
            {
                case TrackModifierType.RhythmStyle:
                    _shouldPulse = true;
                    _shouldRotate = false;
                    break;
                case  TrackModifierType.Remix:
                    _shouldPulse = false;
                    _shouldRotate = true;
                    break;

                case  TrackModifierType.RootShift:
                    _shouldPulse = true;
                    _shouldRotate = true;
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
                    case  TrackModifierType.RootShift:
                        main.startColor = new Color(0.8f, 0.9f, 1f);
                        break;
                    case  TrackModifierType.RhythmStyle:
                        main.startColor = new Color(1f, 0.7f, 1f);
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
                case  TrackModifierType.Remix:
                    return Color.Lerp(baseColor, Color.cyan, 0.4f);
                case  TrackModifierType.RhythmStyle:
                    return Color.Lerp(baseColor, Color.green, 0.3f);
                case  TrackModifierType.RootShift:
                    return Color.Lerp(baseColor, Color.magenta, 0.3f);
                default:
                    return baseColor;
            }
        }
    }
}
