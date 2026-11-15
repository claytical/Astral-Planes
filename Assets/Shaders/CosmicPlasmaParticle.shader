// Metaball "field" particle for the Cosmic Plasma dust look.
//
// This shader does NOT draw to the screen. Its only pass has LightMode "PlasmaField", which the 2D renderer never
// draws; CosmicPlasmaFeature draws it into an offscreen half-res buffer instead:
//     rgb = sum of (tint * density)      a = sum of density
// CosmicPlasmaComposite then blurs, thresholds and shades that buffer into one merged liquid surface.
//
// Vertex color is the role tint (CosmicDust.SetDustColorAllParticles), so all tint/charge/rainbow code keeps working.
Shader "Astral/CosmicPlasmaParticle"
{
    Properties
    {
        _FieldGain     ("Field Gain", Range(0.01,2)) = 1.0
        _NoiseScale    ("Noise Scale (world)", Float) = 1.2
        _NoiseSpeed    ("Noise Speed", Float) = 0.35
        _NoiseStrength ("Noise Strength", Range(0,1)) = 0.08
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
            "RenderPipeline"="UniversalPipeline"
            "PreviewType"="Plane"
        }

        Pass
        {
            Name "PlasmaField"
            Tags { "LightMode"="PlasmaField" }

            Blend One One      // accumulate the field
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half  _FieldGain;
                float _NoiseScale;
                float _NoiseSpeed;
                half  _NoiseStrength;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float2 worldXY : TEXCOORD1; half4 color : COLOR; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 wp = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(wp);
                OUT.uv = IN.uv;
                OUT.worldXY = wp.xy;
                OUT.color = IN.color;
                return OUT;
            }

            float hash21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float vnoise(float2 p)
            {
                float2 i = floor(p); float2 f = frac(p); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(hash21(i), hash21(i + float2(1,0)), f.x),
                            lerp(hash21(i + float2(0,1)), hash21(i + float2(1,1)), f.x), f.y);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 c = IN.uv * 2.0 - 1.0;
                float falloff = saturate(1.0 - dot(c, c));
                float k = falloff * falloff;                       // smooth, compact kernel: reaches exactly 0 at the quad edge

                // World-space lumpiness so the merged surface wobbles like liquid, continuous across cells.
                float n = vnoise(IN.worldXY * _NoiseScale + _Time.y * _NoiseSpeed);
                k *= 1.0 + (n - 0.5) * 2.0 * _NoiseStrength;

                // Collider-disabled / drained cells are mirrored to a ghost alpha (colliderDisabledAlpha 0.08, drained ~0.05) by
                // CosmicDust.SetTerrainColliderEnabled / ChipEnergy. Those cells are passable, so they must NOT draw plasma.
                // (Regrow cap 0.40 and normal tints 0.5+ stay solid.) This keeps "plasma visible" == "collider active".
                float solid = smoothstep(0.10, 0.20, IN.color.a);

                float d = max(0.0, k) * _FieldGain * solid;
                // Vertex alpha (charge / regrow-cap / collider-disabled) only DIMS the color (min 65%); it never changes density,
                // so the surface footprint stays identical to the cell's collider.
                half dim = lerp(0.65, 1.0, saturate(IN.color.a));
                return half4(IN.color.rgb * dim * d, d);   // rgb = tint-weighted; alpha channel = pure density
            }
            ENDHLSL
        }
    }
    FallBack Off
}
