// Fullscreen composite for the Cosmic Plasma dust look. Reads the accumulated field written by
// CosmicPlasmaParticle (rgb = sum tint*density, a = sum density), blurs it, thresholds it into one liquid surface,
// and alpha-blends the result over the camera color.
Shader "Hidden/Astral/CosmicPlasmaComposite"
{
    Properties { }

    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "PlasmaComposite"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off ZTest Always Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _PlasmaParams;   // x: threshold, y: edge softness, z: max alpha, w: blur radius (texels)
            float4 _PlasmaParams2;  // x: rim width, y: rim strength, z: core boost, w: brightness multiplier
            float4 _PlasmaParams3;  // x: lift toward white (0..1), y: saturation, z: debug view (0 off, 1 solid, 2 density, 3 color)
            float4 _PlasmaRimColor;
            float4 _PlasmaTexel;    // xy: 1/fieldWidth, 1/fieldHeight (set from C#; avoids relying on Blit.hlsl declaring a texel-size uniform)

            float4 SampleField(float2 uv, float2 texel, float radius)
            {
                // 9-tap blur; the field is half-res + bilinear so this is soft without extra passes.
                float4 s = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv) * 0.25;
                const float2 o[8] = { float2(1,0), float2(-1,0), float2(0,1), float2(0,-1),
                                      float2(0.7,0.7), float2(-0.7,0.7), float2(0.7,-0.7), float2(-0.7,-0.7) };
                [unroll] for (int i = 0; i < 8; i++)
                    s += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + o[i] * texel * radius) * 0.09375;
                return s;
            }

            float h21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float vn(float2 p)
            {
                float2 i = floor(p); float2 f = frac(p); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(h21(i), h21(i + float2(1,0)), f.x), lerp(h21(i + float2(0,1)), h21(i + float2(1,1)), f.x), f.y);
            }
            float swirl(float2 p, float t) { return vn(p + float2(t, t * 0.6)) * 0.6 + vn(p * 2.3 - float2(t * 0.7, t)) * 0.4; }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float2 texel = _PlasmaTexel.xy;

                // Debug views (set from CosmicPlasmaFeature.Settings.debugView)
                if (_PlasmaParams3.z > 0.5 && _PlasmaParams3.z < 1.5) return half4(1, 0, 1, 0.5);
                if (_PlasmaParams3.z > 1.5 && _PlasmaParams3.z < 2.5)
                {
                    float dd = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv).a;
                    return half4(dd, dd, dd, 1);                        // raw density, unblurred, unthresholded, opaque
                }
                if (_PlasmaParams3.z > 2.5)
                {
                    float4 fc = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv);
                    return half4(fc.rgb / max(fc.a, 0.0001), fc.a > 0.0001 ? 1 : 0);
                }

                float4 f = SampleField(uv, texel, _PlasmaParams.w);
                float density = f.a;

                float thr = _PlasmaParams.x;
                float soft = max(0.0001, _PlasmaParams.y);
                float mask = smoothstep(thr - soft, thr + soft, density);
                if (mask <= 0.0001) return half4(0, 0, 0, 0);

                half3 tint = f.rgb / max(density, 0.0001);

                // How deep inside the surface we are: 0 at the edge, 1 deep inside.
                float depth = saturate((density - thr) / max(0.0001, _PlasmaParams2.x * 4.0));
                float rim = (1.0 - depth) * mask * _PlasmaParams2.y;
                float core = depth * _PlasmaParams2.z;

                // Saturation / optional lift toward white (default 0: keep role colors).
                half lum = dot(tint, half3(0.299, 0.587, 0.114));
                tint = lerp(half3(lum, lum, lum), tint, _PlasmaParams3.y);
                tint = lerp(tint, half3(1, 1, 1), _PlasmaParams3.x);

                // Interior swirl: animated brightness only (never touches the mask, so the footprint keeps matching colliders).
                float2 su = uv * float2(_ScreenParams.x / _ScreenParams.y, 1.0) * 6.0;
                float sw = swirl(su, _Time.y * 0.4);
                float shade = 0.85 + 0.3 * sw;

                // Rim brightens the surface in its own tint (not pure white) so hue survives.
                half3 col = tint * _PlasmaParams2.w * shade * (1.0 + core) + tint * _PlasmaRimColor.rgb * rim;

                // Soft tone-map: boosts compress instead of clipping to white.
                col = 1.0 - exp(-col * 1.4);
                return half4(col, mask * _PlasmaParams.z);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
