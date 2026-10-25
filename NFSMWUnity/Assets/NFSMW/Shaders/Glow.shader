// Additive light column (event markers): bright at the rim and the base, fading upward and close to the camera,
// so standing inside one tints the view only lightly.
Shader "NFSMW/Glow"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.75, 0.15, 0.5)
        _NearFade ("Fade-in distance", Float) = 18
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha One
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _NearFade;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD0; float3 normal : TEXCOORD1; float height : TEXCOORD2; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.height = saturate((v.vertex.y + 1) * 0.5);   // primitive cylinder: -1 (base) .. 1 (top)
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 view = normalize(_WorldSpaceCameraPos - i.world);
                float rim = 1 - abs(dot(view, normalize(i.normal)));
                float a = _Color.a * (0.25 + rim * rim) * pow(1 - i.height, 1.5);
                a *= saturate(distance(_WorldSpaceCameraPos, i.world) / _NearFade);
                return fixed4(_Color.rgb, a);
            }
            ENDCG
        }
    }
}
