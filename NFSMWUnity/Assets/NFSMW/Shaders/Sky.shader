// The game's sky dome (SKYDOME, SKY_MIDDAY_A_*): unlit, no fog, drawn first behind everything.
Shader "NFSMW/Sky"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Tint;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
            #if UNITY_REVERSED_Z
                o.pos.z = o.pos.w * 1e-6;          // at the far end of the depth range, never clipped
            #else
                o.pos.z = o.pos.w * (1 - 1e-6);
            #endif
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                // the cap and cloud layers are white alpha masks tinted by the dome's vertex colours, blended in order
                return fixed4(t.rgb * i.color.rgb * 2 * _Tint.rgb, t.a * i.color.a);
            }
            ENDCG
        }
    }
}
