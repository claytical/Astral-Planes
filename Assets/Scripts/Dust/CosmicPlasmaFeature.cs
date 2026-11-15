using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Metaball renderer for the Cosmic Plasma dust look (URP 2D renderer, Render Graph).
///
///  1. Field pass: draws every renderer whose shader has LightMode "PlasmaField" (Astral/CosmicPlasmaParticle)
///     on the dust layer(s) into a reduced-resolution HDR buffer, additively.
///  2. Composite pass: blurs + thresholds that buffer into one merged liquid surface and alpha-blends it over the scene.
///
/// The 2D renderer never draws "PlasmaField" passes itself, so dust particles using that shader only appear via this feature.
/// Add it to the renderer asset: "2D URP Renderer_Renderer" -> Add Renderer Feature -> Cosmic Plasma Feature.
/// </summary>
public class CosmicPlasmaFeature : ScriptableRendererFeature
{
    public enum DebugView { Off = 0, SolidTest = 1, FieldDensity = 2, FieldColor = 3 }

    [System.Serializable]
    public class Settings
    {
        [Header("Debug")]
        [Tooltip("SolidTest: flat magenta wash (proves the composite pass runs + is visible). FieldDensity: raw accumulated density as grey " +
                 "(proves the particles reach the field). FieldColor: tint channel.")]
        public DebugView debugView = DebugView.Off;
        [Tooltip("Log once per play session what the field pass sees (layer mask, camera).")]
        public bool logOnce = true;


        [Tooltip("Layers containing the plasma dust (the CosmicDust layer).")]
        public LayerMask layerMask = ~0;

        [Tooltip("Where the plasma is composited. AfterRenderingTransparents = over sprites (incl. vehicle); " +
                 "BeforeRenderingTransparents = under the 2D sprite pass if the renderer orders it that way.")]
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingTransparents;

        [Range(0.2f, 1f), Tooltip("Field buffer resolution relative to the camera. Lower = softer + cheaper.")]
        public float resolutionScale = 0.5f;

        [Header("Surface")]
        [Tooltip("Field density at which the liquid surface forms. Lower = blobs fuse more and grow; higher = separate, smaller blobs.")]
        [Min(0.001f)] public float threshold = 0.45f;
        [Tooltip("Softness of the surface edge (in field-density units).")]
        [Min(0.0001f)] public float edgeSoftness = 0.05f;
        [Range(0f, 1f), Tooltip("Opacity cap, so the vehicle can read through the plasma.")]
        public float maxAlpha = 0.75f;
        [Range(0f, 6f), Tooltip("Blur radius in field-buffer texels. More = smoother bridges between cells.")]
        public float blurRadius = 1f;

        [Header("Shading")]
        [Min(0.0001f), Tooltip("Density range over which the rim fades into the interior.")]
        public float rimWidth = 0.15f;
        [Range(0f, 2f)] public float rimStrength = 0.25f;
        [Range(0f, 2f)] public float coreBoost = 0.25f;
        public Color rimColor = Color.white;
        [Range(0.5f, 4f), Tooltip("Overall brightness multiplier on the plasma color.")]
        public float brightness = 1.6f;
        [Range(0f, 1f), Tooltip("Blend of the tint toward white. Dark role tints need some lift to glow.")]
        public float liftToWhite = 0f;
        [Range(0f, 2f)] public float saturation = 1.2f;
    }

    public Settings settings = new Settings();

    [Tooltip("Optional; falls back to Shader.Find. Assign it (or add the shader to Always Included Shaders) for player builds.")]
    public Shader compositeShader;

    Material _compositeMat;
    bool _warnedNoMat, _loggedAdd;
    FieldPass _fieldPass;
    CompositePass _compositePass;

    public override void Create()
    {
        if (compositeShader == null) compositeShader = Shader.Find("Hidden/Astral/CosmicPlasmaComposite");
        if (_compositeMat != null) CoreUtils.Destroy(_compositeMat);
        _compositeMat = compositeShader != null ? CoreUtils.CreateEngineMaterial(compositeShader) : null;

        _fieldPass = new FieldPass(settings) { renderPassEvent = settings.renderPassEvent };
        _compositePass = new CompositePass(settings, _compositeMat) { renderPassEvent = settings.renderPassEvent };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_compositeMat == null)
        {
            if (settings.logOnce && !_warnedNoMat)
            {
                _warnedNoMat = true;
                Debug.LogWarning("[CosmicPlasma] composite material is null (shader 'Hidden/Astral/CosmicPlasmaComposite' not found/failed?). No plasma will draw.");
            }
            return;
        }
        var cam = renderingData.cameraData.camera;
        if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
        if (settings.logOnce && !_loggedAdd)
        {
            _loggedAdd = true;
            Debug.Log($"[CosmicPlasma] AddRenderPasses for camera '{cam.name}' type={cam.cameraType} (enqueuing field + composite).");
        }

        _fieldPass.renderPassEvent = settings.renderPassEvent;
        _compositePass.renderPassEvent = settings.renderPassEvent + 1; // strictly after the field pass
        _fieldPass.Link(_compositePass);
        renderer.EnqueuePass(_fieldPass);
        renderer.EnqueuePass(_compositePass);
    }

    protected override void Dispose(bool disposing)
    {
        if (_compositeMat != null) CoreUtils.Destroy(_compositeMat);
        _compositeMat = null;
    }

    // ---------------------------------------------------------------- field pass
    class FieldPass : ScriptableRenderPass
    {
        static readonly ShaderTagId kTag = new ShaderTagId("PlasmaField");
        readonly Settings _s;
        CompositePass _composite;
        bool _logged;

        class PassData { public RendererListHandle list; }

        public FieldPass(Settings s) { _s = s; profilingSampler = new ProfilingSampler("Cosmic Plasma Field"); }
        public void Link(CompositePass c) { _composite = c; }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var cameraData = frameData.Get<UniversalCameraData>();
            var renderingData = frameData.Get<UniversalRenderingData>();

            var desc = cameraData.cameraTargetDescriptor;
            desc.msaaSamples = 1;
            desc.depthBufferBits = 0;
            desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat; // field sums exceed 1
            desc.width  = Mathf.Max(8, Mathf.RoundToInt(desc.width  * _s.resolutionScale));
            desc.height = Mathf.Max(8, Mathf.RoundToInt(desc.height * _s.resolutionScale));

            if (_s.logOnce && !_logged)
            {
                _logged = true;
                Debug.Log($"[CosmicPlasma] field pass running. camera='{cameraData.camera.name}' layerMask={_s.layerMask.value} " +
                          $"cullingMask={cameraData.camera.cullingMask} fieldRes={desc.width}x{desc.height} event={_s.renderPassEvent}");
            }

            TextureHandle field = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_CosmicPlasmaField", true,
                FilterMode.Bilinear, TextureWrapMode.Clamp);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Cosmic Plasma Field", out var data))
            {
                var sorting = new SortingSettings(cameraData.camera) { criteria = SortingCriteria.CommonTransparent };
                var drawing = new DrawingSettings(kTag, sorting) { perObjectData = PerObjectData.None };
                var filtering = new FilteringSettings(RenderQueueRange.all, _s.layerMask);
                var param = new RendererListParams(renderingData.cullResults, drawing, filtering);

                data.list = renderGraph.CreateRendererList(param);
                builder.UseRendererList(data.list);
                builder.SetRenderAttachment(field, 0);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                {
                    ctx.cmd.ClearRenderTarget(false, true, Color.clear);
                    ctx.cmd.DrawRendererList(d.list);
                });
            }

            _composite.Field = field;
            _composite.Texel = new Vector4(1f / desc.width, 1f / desc.height, desc.width, desc.height);
        }
    }

    // ------------------------------------------------------------ composite pass
    class CompositePass : ScriptableRenderPass
    {
        static readonly int ParamsId  = Shader.PropertyToID("_PlasmaParams");
        static readonly int Params2Id = Shader.PropertyToID("_PlasmaParams2");
        static readonly int RimId     = Shader.PropertyToID("_PlasmaRimColor");
        static readonly int Params3Id = Shader.PropertyToID("_PlasmaParams3");

        readonly Settings _s;
        readonly Material _mat;
        public TextureHandle Field;
        public Vector4 Texel = new Vector4(1f / 582f, 1f / 327f, 582f, 327f);
        static readonly int TexelId = Shader.PropertyToID("_PlasmaTexel");
        bool _loggedInvalid, _loggedRecord;

        class PassData { public TextureHandle src; public Material mat; }

        public CompositePass(Settings s, Material m) { _s = s; _mat = m; profilingSampler = new ProfilingSampler("Cosmic Plasma Composite"); }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            if (!Field.IsValid())
            {
                if (_s.logOnce && !_loggedInvalid) { _loggedInvalid = true; Debug.LogWarning("[CosmicPlasma] composite pass skipped: field texture handle invalid."); }
                return;
            }
            if (_s.logOnce && !_loggedRecord)
            {
                _loggedRecord = true;
                Debug.Log($"[CosmicPlasma] composite pass recording. debugView={_s.debugView} activeColorValid={resources.activeColorTexture.IsValid()}");
            }

            _mat.SetVector(ParamsId,  new Vector4(_s.threshold, _s.edgeSoftness, _s.maxAlpha, _s.blurRadius));
            _mat.SetVector(Params2Id, new Vector4(_s.rimWidth, _s.rimStrength, _s.coreBoost, _s.brightness));
            _mat.SetVector(Params3Id, new Vector4(_s.liftToWhite, _s.saturation, (float)_s.debugView, 0f));
            _mat.SetColor(RimId, _s.rimColor);
            _mat.SetVector(TexelId, Texel);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Cosmic Plasma Composite", out var data))
            {
                data.src = Field;
                data.mat = _mat;
                builder.UseTexture(Field);
                builder.SetRenderAttachment(resources.activeColorTexture, 0);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                {
                    Blitter.BlitTexture(ctx.cmd, d.src, new Vector4(1, 1, 0, 0), d.mat, 0);
                });
            }
        }
    }
}
