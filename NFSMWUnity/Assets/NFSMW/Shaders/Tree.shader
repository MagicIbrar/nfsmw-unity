// Trees, bushes and tree lines (solid effect id 6): speed.exe fx\tree.efx (IDI_TREE_FX) technique 0 "world"
// (vs_1_1 + ps_1_1, CULLMODE none; NFSMW_RE/specs/world_render.md §2). It differs from world.efx in two ways:
// the extinction multiplies the SUN term (world.efx: the prelit term) and the alpha is the texture's alone.
//   vs: oD1 = (Ambient.rgb * Ambient.w, 1) * vertex colour          (prelit, not extinguished)
//       oD0 = (min(ext, 1) * sat(N.L) * DiffuseColour.rgb, sat(N.L) * DiffuseColour.w); oT2 = in-scatter
//   ps: r0 = t0 * v1; r0.rgb = 2 (t0 * v0 + r0) (mad_x2); r0.a = t0.a; r0.rgb += t2
Shader "NFSMW/Tree"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(-1, 1)) = 0.5
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
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "MWLighting.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 sun : COLOR0;          // oD0
                half4 prelit : COLOR1;       // oD1
                half3 inscatter : TEXCOORD1; // oT2
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 n = UnityObjectToWorldNormal(v.normal);
                float3 ext, ins;
                MWFogTerms(wpos, ext, ins);
                float ndl = saturate(dot(_MW_LightDir.xyz, n));
                o.sun = saturate(float4(ext * ndl * _MW_Diffuse.rgb, ndl * _MW_Diffuse.w));
                o.prelit = saturate(float4(_MW_Ambient.rgb * _MW_Ambient.w, 1) * v.color);
                o.inscatter = ins;
                if (_MW_Enabled < 0.5) { o.prelit = half4(v.color.rgb, v.color.a); o.sun = 0; o.inscatter = 0; }
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float4 tex = tex2D(_MainTex, i.uv);
                clip(tex.a - _Cutoff);
                float3 c = saturate(2 * (tex.rgb * i.sun.rgb + tex.rgb * i.prelit.rgb) + i.inscatter);
                return fixed4(c, tex.a);
            }
            ENDCG
        }
    }
    Fallback "NFSMW/World"
}
