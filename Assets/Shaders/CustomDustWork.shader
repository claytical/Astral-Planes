Shader "Custom/CosmicDustWork"
{
    Properties
    {
        _MainTex   ("Texture", 2D) = "white" {}
        _RoleColor ("Role Color", Color) = (1,1,1,1)
        _Work      ("Work (-1 deny .. +1 boost)", Range(-1,1)) = 0
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Lighting Off
        ZWrite Off
        Cull Off
        Blend One OneMinusSrcAlpha   // premultiplied-style blend (works well for particles/sprites)

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            fixed4 _RoleColor;
            float _Work;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                fixed4 color  : COLOR;     // sprite/particle vertex color (we keep this white in code)
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                fixed4 col : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = TRANSFORM_TEX(v.uv, _MainTex);
                o.col = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);

                // Base hue from role.
                fixed3 baseRgb = _RoleColor.rgb;

                // Work mapping:
                //  +Work : brighten toward white (boosting works; magnitude = effectiveness)
                //  -Work : darken toward black (denial; magnitude = severity)
                float wPos = saturate(_Work);
                float wNeg = saturate(-_Work);

                fixed3 boosted = lerp(baseRgb, fixed3(1,1,1), lerp(0.25, 0.85, wPos));
                fixed3 denied  = lerp(baseRgb, fixed3(0,0,0), lerp(0.15, 0.80, wNeg));

                fixed3 rgb = baseRgb;
                rgb = lerp(rgb, boosted, wPos);
                rgb = lerp(rgb, denied,  wNeg);

                // Alpha comes from RoleColor.a * texture alpha.
                float a = saturate(_RoleColor.a * tex.a);

                // Premultiply.
                fixed4 outCol;
                outCol.rgb = rgb * a;
                outCol.a   = a;

                // Also allow vertex alpha (particle fade, sprite fade) to participate.
                outCol *= i.col;

                return outCol;
            }
            ENDCG
        }
    }
}