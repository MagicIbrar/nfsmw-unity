// The road reflection view's world effects (NFSMW_RE/specs/world_render.md §4). eViewPlatInterface::Render (0x6DA9B0)
// swaps a material's effect for every render target slot other than the player view and the shadow map: world.efx (0)
// and GlossyWindow (5) -> worldnofog.efx (8), worldreflect (1) and worldnormalmap (3) -> worldnormalmapnofog.efx (30).
//   worldnofog.efx technique 0 "world" (IDI_WORLDNOFOG_FX, vs_1_1 + ps_1_1): oD0 = vertex colour, oT0 = uv;
//       ps: mul_x2 r0.xyz, t0, v0 + mul r0.w, t0.w, v0.w
//   worldnormalmapnofog.efx has only "world_fixed" (fixed function): stage 0 COLOROP MODULATE2X (TEXTURE, DIFFUSE),
//       ALPHAOP MODULATE (TEXTURE, DIFFUSE), stage 1 DISABLE, lighting off - the same maths.
// So the reflected scenery is unlit and unfogged: 2 x texture x vertex colour, alpha = texture alpha x vertex alpha.
// The blend / alpha test / cull state is the material's (its TextureInfo BlendState), copied by RoadReflection.cs.
Shader "NFSMW/WorldNoFog"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff (<0 off)", Float) = -1
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
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "IgnoreProjector" = "True" }
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
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                fixed a = t.a * i.color.a;
                if (_Cutoff >= 0) clip(a - _Cutoff);
                return fixed4(saturate(2 * t.rgb * i.color.rgb), a);
            }
            ENDCG
        }
    }
}
