// Glossy windows and facades (solid effect id 5): speed.exe fx\GlossyWindow.efx (IDI_GLOSSYWINDOW_FX) technique 0
// "world" (vs_1_1 + ps_1_1, CULLMODE none; NFSMW_RE/specs/world_render.md §2). CPU setup 0x6D7400: WindowReflection =
// the WINDOWREFLECTION texture (GLOBAL\InGameA.bun, 256x128 DXT3; sampler WRAP, LOD bias -1), DiffuseColour,
// AmbientColour. The gloss mask is the diffuse alpha.
//   vs: R = 2 (N.L) N - L; s = sat(R . normalize(eye - v)); oT2 = s^10           (specular, as a clamped texcoord)
//       oD0 = sat(N.L) * DiffuseColour                                            (sun, not extinguished)
//       oD1 = (min(ext, 1) * Ambient.rgb * Ambient.w * vc.rgb, vc.a); oT3 = in-scatter
//       oT1 = (0.5 uv.x + 0.5 viewNormal.x, 0.5 uv.y)  (viewNormal.x = N . WorldView column 0)
//   ps: r0 = 2 t0.a t2; r0 += t0 v0; r0 = 2 (t0 v1 + r0) (mad_x2); r0 += t0.a t1; r0.a = t0.a; r0 += t3
//       => 2 tex (prelit + sun) + 4 a spec + a WindowReflection + in-scatter
// Shadow techniques (shadowLevel 1 ps 0x6C44 / 2 ps_3_0 0x5A38): (tex * sun + 2 a spec) * f (MWShadow.cginc,
// world_render.md §13).
Shader "NFSMW/GlossyWindow"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _WindowReflection ("WINDOWREFLECTION", 2D) = "black" {}
        _Cutoff ("Alpha cutoff", Range(-1, 1)) = -1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 0
        [Enum(UnityEngine.Rendering.BlendOp)] _BlendOp ("Blend op", Float) = 0
        _ZWrite ("ZWrite", Float) = 1
        _OffsetFactor ("Depth offset factor", Float) = 0
        _OffsetUnits ("Depth offset units", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
        Cull [_Cull]
        Blend [_SrcBlend] [_DstBlend]
        BlendOp [_BlendOp]
        ZWrite [_ZWrite]
        Offset [_OffsetFactor], [_OffsetUnits]
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "MWLighting.cginc"
            #include "MWShadow.cginc"

            sampler2D _MainTex, _WindowReflection;
            float4 _MainTex_ST;
            float _Cutoff;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 uvRefl : TEXCOORD1;   // oT1
                half3 spec : TEXCOORD2;      // oT2
                half3 inscatter : TEXCOORD3; // oT3
                half3 sun : COLOR0;          // oD0
                half4 prelit : COLOR1;       // oD1
                float4 shadow : TEXCOORD4;   // world position, w = shadow level
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.shadow = float4(wpos, MWShadowLevel(unity_ObjectToWorld._m03_m13_m23));
                float3 n = UnityObjectToWorldNormal(v.normal);
                float3 L = _MW_LightDir.xyz;
                float ndl = dot(n, L);
                float3 R = 2 * ndl * n - L;
                float s = saturate(dot(R, normalize(_WorldSpaceCameraPos - wpos)));
                float s2 = s * s, s8 = s2 * s2; s8 *= s8;
                o.spec = s2 * s8;                                                   // s^10
                o.sun = saturate(saturate(ndl) * _MW_Diffuse.rgb);
                float3 ext, ins;
                MWFogTerms(wpos, ext, ins);
                o.prelit = saturate(float4(ext * _MW_Ambient.rgb * _MW_Ambient.w * v.color.rgb, v.color.a));
                o.inscatter = ins;
                // WorldView column 0 = the view-space x of the normal (Unity view space keeps x)
                float nvx = mul((float3x3)UNITY_MATRIX_V, n).x;
                o.uvRefl = float2(0.5 * v.uv.x + 0.5 * nvx, 0.5 * v.uv.y);
                if (_MW_Enabled < 0.5) { o.prelit = half4(v.color.rgb, v.color.a); o.sun = 0; o.spec = 0; o.inscatter = 0; }
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float4 tex = tex2D(_MainTex, i.uv);
                clip(tex.a - _Cutoff);
                float3 refl = tex2Dbias(_WindowReflection, float4(i.uvRefl, 0, -1)).rgb;
                float f = MWSunShadow(i.shadow.xyz, i.shadow.w);
                float3 c = 2 * (tex.rgb * i.prelit.rgb + (tex.rgb * i.sun + 2 * tex.a * i.spec) * f);
                c += tex.a * refl * (_MW_Enabled > 0.5 ? 1 : 0) + i.inscatter;
                return fixed4(saturate(c), tex.a);
            }
            ENDCG
        }
    }
    Fallback "NFSMW/World"
}
