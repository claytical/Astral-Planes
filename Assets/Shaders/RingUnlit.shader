Shader "Astral Planes/RingUnlit"
{
    Properties
    {
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _Color ("Color (Legacy)", Color) = (1, 1, 1, 1)
        // Defaults reproduce the previously-hardcoded "Blend SrcAlpha OneMinusSrcAlpha" exactly,
        // so any material that doesn't set these (e.g. RingLine.mat) is visually unchanged.
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5   // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 6   // OneMinusSrcAlpha
        // LineRenderer-only: fades alpha across the strip's width (UV.y) for a soft/glowing edge.
        // 0 = off (default); MeshRenderer Fill rings never opt in, so they're unaffected.
        _EdgeSoftness ("Edge Softness (LineRenderer only)", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Transparent"
            "RenderType"     = "Transparent"
        }

        Blend [_SrcBlend] [_DstBlend]
        ZWrite Off
        Cull Off

        Pass
        {
            // Universal2D is the LightMode the URP 2D Renderer's draw pass looks for.
            // Without it, a custom mesh shader falls back to SRPDefaultUnlit, which the
            // 2D render-graph path draws unreliably (renders in Scene view, not Game view).
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _Color;
                float  _EdgeSoftness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half4  color       : TEXCOORD0;
                float2 uv          : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                // Vertex color (default white) × _BaseColor drives the final pixel.
                // MeshRenderer rings have no vertex color → white → output equals _BaseColor.
                // LineRenderer contours receive lr.startColor/endColor as vertex color → tints output.
                OUT.color = IN.color * _BaseColor;
                // LineRenderer auto-generates UVs: U along the line's length, V across its width
                // (0→1). MeshRenderer Fill meshes have their own UVs but never set _EdgeSoftness
                // above 0, so this is unused for them.
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 col = IN.color;
                if (_EdgeSoftness > 0.0001)
                {
                    // Distance from the strip's center line: 1 at center, 0 at either edge.
                    half centerDist = 1.0 - abs(IN.uv.y * 2.0 - 1.0);
                    col.a *= smoothstep(0.0, _EdgeSoftness, centerDist);
                }
                return col;
            }
            ENDHLSL
        }
    }
}
