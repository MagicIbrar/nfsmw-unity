// FMV pictures (Core/Vp6Decoder): three R8 planes (Y full size, U and V half size) converted to RGB with the BT.601
// limited-range matrix [?] (the matrix RCMP's VP6 path uses is not decoded; BT.601 is the VP6 / FFmpeg default).
// The planes are uploaded in coded order, which for EA's VP6 is bottom-up, i.e. already Unity's texture orientation.
// Drawn with Graphics.DrawTexture(rect, Y, material) from the MoviePlayer's OnGUI; _Fade multiplies the picture (fade in).
Shader "NFSMW/MovieYUV"
{
    Properties
    {
        _MainTex ("Y", 2D) = "black" {}
        _UTex ("U", 2D) = "gray" {}
        _VTex ("V", 2D) = "gray" {}
        _Fade ("Fade", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Overlay" "IgnoreProjector" = "True" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex, _UTex, _VTex;
            float _Fade;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float y = tex2D(_MainTex, i.uv).r * 255.0 - 16.0;
                float u = tex2D(_UTex, i.uv).r * 255.0 - 128.0;
                float v = tex2D(_VTex, i.uv).r * 255.0 - 128.0;
                float3 rgb;
                rgb.r = 1.164383 * y + 1.596027 * v;
                rgb.g = 1.164383 * y - 0.391762 * u - 0.812968 * v;
                rgb.b = 1.164383 * y + 2.017232 * u;
                rgb = saturate(rgb / 255.0) * _Fade;
                #if !defined(UNITY_COLORSPACE_GAMMA)
                rgb = GammaToLinearSpace(rgb);
                #endif
                return float4(rgb, 1);
            }
            ENDCG
        }
    }
}
