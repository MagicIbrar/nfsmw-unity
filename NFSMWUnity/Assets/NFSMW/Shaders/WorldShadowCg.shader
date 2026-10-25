// speed.exe fx\shadow_cg.efx (IDI_SHADOW_CG_FX, effect 22), the baked current-gen shadow decals (NFSMW_RE/specs/
// world_render.md §1/§11). StuffScenery gives an instance flagged 0x100 (the SHD_*_ROAD/PROPS/TERRAIN layers) render flag
// 0x20000, and eViewPlatInterface::Render 0x6DABFC swaps its world.efx / worldnofog material for shadow_cg in every view.
// Technique "world" (shader = 1): vs_1_1 oPos, oD0 = vertex colour, oT0 = uv; ps_1_1 `tex t0; mul r0, t0, v0; mul r0, r0, c0;
// mul r0, r0, c1` with c0 = ShadowColor (semantic SHADOWCURRENTGEN_COLOR, parameter 74) and c1 = (1, 1, 1, 2):
//   rgb = tex.rgb x vc.rgb x ShadowColor.rgb,  a = 2 x tex.a x vc.a x ShadowColor.a
// ShadowColor is set per frame by the effect's slot-1 function sub_6C6630: rgb = [0x9018D0..D8] = (0.5, 0.5, 0.5),
// a = [0x9018CC] (1.0) x [0x8FAE68] (0.1 + 0.8 x sun shadow strength) in the player view (render-target slot 0; the other
// slots use [0x9018C8] = 2.0 and rgb 0.5). TimeOfDayDriver publishes it as _MW_ShadowCgColor. No fog (FOGENABLE 0), no
// lighting; the blend / alpha test / cull come from the material's BlendState (the SHD_ textures blend: SRCALPHA /
// INVSRCALPHA). So the baked shadows darken the road to half and fade with the sun's shadow strength (clouds, dusk).
Shader "NFSMW/WorldShadowCg"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 10
        [Enum(UnityEngine.Rendering.BlendOp)] _BlendOp ("Blend op", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "AlphaTest+20" "IgnoreProjector" = "True" }
        Blend [_SrcBlend] [_DstBlend]
        BlendOp [_BlendOp]
        ZWrite Off
        Offset -1, -2
        Cull [_Cull]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MW_ShadowCgColor;   // ShadowColor: (0.5, 0.5, 0.5, 1.0 x [0x8FAE68]); (0.5, 0.5, 0.5, 0) before the lighting runs

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
                fixed4 t = tex2D(_MainTex, i.uv) * i.color;          // mul r0, t0, v0
                t *= _MW_ShadowCgColor;                              // mul r0, r0, c0
                return fixed4(t.rgb, saturate(t.a * 2));             // mul r0, r0, c1 = (1, 1, 1, 2), ps_1_1 saturates
            }
            ENDCG
        }
    }
}
