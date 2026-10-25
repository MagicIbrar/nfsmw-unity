// On-screen rain drops: speed.exe fx\rain_drop.efx (IDI_RAIN_DROP_FX, effect id 14), technique "filter", drawn by sub_6D4620.
//   states: FOGENABLE 0, COLORWRITEENABLE 7 (RGB), ALPHABLENDENABLE 1, SRCBLEND 5 (SRCALPHA), DESTBLEND 6 (INVSRCALPHA),
//           CULLMODE 1 (none), ZENABLE 0, ZWRITEENABLE 0; samplers CLAMP, linear
//   vs_1_1: oPos = v0 (clip space as given), oD0 = v1, oT0 = v2
//   ps_2_0: r0 = tex(displacemap, t0)
//           r0.x = r0.y * raindropoffset.z + raindropoffset.x
//           r0.y = r0.z * raindropoffset.w + raindropoffset.y
//           r0.w = r0.w * raindropalpha
//           r1 = tex(diffusemap, r0);  oC0 = (r1.rgb * 1.2, r0.w)
// diffusemap = a copy of the frame (sub_6D2CD0), displacemap = RAINDROPPC (GLOBALB). Per drop (Runtime/World/Rain.cs):
// raindropoffset = (x - s/4, y + s/4, s, -s), s = 0.12 size; raindropalpha = 3 timer/Maxtimer. Here both ride in the
// vertex stream (TEXCOORD1 = offset, TEXCOORD2.x = alpha) so all drops go in one draw [ours].
Shader "NFSMW/RainDrop"
{
    Properties
    {
        _DisplaceTex ("Displacement (RAINDROPPC)", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask RGB
        ZTest Always
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _DisplaceTex;
            sampler2D _MW_RainScreen;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 offset : TEXCOORD1; float2 alpha : TEXCOORD2; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 offset : TEXCOORD1; float alpha : TEXCOORD2; };

            v2f vert (appdata v)
            {
                v2f o;
                // D3D clip space (y up). Unity flips the projection when rendering into a texture: follow it [ours]
                o.pos = float4(v.vertex.x, v.vertex.y * _ProjectionParams.x, 0.5, 1);
                o.uv = v.uv;
                o.offset = v.offset;
                o.alpha = v.alpha.x;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float4 d = tex2D(_DisplaceTex, i.uv);
                float2 uv = float2(d.y * i.offset.z + i.offset.x, d.z * i.offset.w + i.offset.y);   // D3D texture space (v down)
                float a = d.w * i.alpha;
                float3 c = tex2D(_MW_RainScreen, float2(uv.x, 1 - uv.y)).rgb * 1.2;
                return saturate(float4(c, a));                                                      // 8-bit target
            }
            ENDCG
        }
    }
}
