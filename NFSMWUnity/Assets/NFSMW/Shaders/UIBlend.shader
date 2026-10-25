// FEng image with its texture's blend state (speed.exe 0x6C68B0, used by the FE ePoly path eViewPlatInterface::Render
// 0x6D1260): TextureInfo +0x56 AlphaBlendType 0 = blending off, 1 = SrcAlpha/InvSrcAlpha, 2 = SrcAlpha/One (additive),
// 3 = Zero/InvSrcColor, 5 = DestAlpha/InvDestAlpha, 6 = DestAlpha/One; +0x55 AlphaUsageType 1 = alpha test, ref 0x5F.
// Colour maths = fe.efx (IDI_FE_FX) ps_1_1: tex * vertex colour, rgb * 2 (the CPU halves the FEColor's rgb,
// FEngColorToEpolyColor 0x56FA90), alpha = tex.a * colour.a; here the halving and doubling cancel.
Shader "NFSMW/UIBlend"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _SrcBlend ("Src blend", Float) = 5
        _DstBlend ("Dst blend", Float) = 10
        _Cutoff ("Alpha test (0 = off)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend [_SrcBlend] [_DstBlend]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            float _Cutoff;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                if (_Cutoff > 0) clip(c.a - _Cutoff);
                return c;
            }
            ENDCG
        }
    }
}
