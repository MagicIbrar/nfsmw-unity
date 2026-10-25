// The sun sprite (sub_6D04E0, NFSMW_RE/specs/world_render.md §5): screen-space quads drawn with fx\fe.efx technique 0
// (vs_1_1 + ps_1_1: rgb = 2 * tex * vertex colour, alpha = tex.a * vertex alpha), Z disabled (D3DRS_ZENABLE 0 around
// the draw), CULLMODE none, FECOLORWRITEFLAG 0xF (RGBA: the alpha feeds the overbright mask). The sun textures are
// AlphaBlendType 2 (SRCALPHA / ONE) in InGameA. Vertices arrive in clip space (Runtime/Sky/SunRender.cs).
Shader "NFSMW/Sun"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ZTest Always
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = float4(v.vertex.xy, 0.5, 1);
                o.pos.y *= _ProjectionParams.x;          // into a flipped render target on D3D-style platforms
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                return fixed4(saturate(2 * t.rgb * i.color.rgb), t.a * i.color.a);   // mul r0, t0, v0; add r0.xyz, r0, r0
            }
            ENDCG
        }
    }
}
