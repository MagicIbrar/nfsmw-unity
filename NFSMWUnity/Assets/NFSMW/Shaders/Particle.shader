// Particles and skid marks (Ecstasy/EmitterSystem.cpp Render, World/Skids.cpp). fx\Particles.efx (PE resource
// IDI_PARTICLES_FX, confirmed 2026-10-24) technique 0 is ps_1_1 'mul_x2 r0, t0, v0': texture x vertex colour x 2,
// alpha included, saturated. Blend from the texture's AlphaBlendType (1 blend, 2 additive).
// particles.efx technique 0 (IDI_PARTICLES_FX, vs_1_1: oPos, oD0 = vertex colour, oT0 = uv, oT1 = 0) has no fog and the
// PC draws particles with fixed-function fog off, so nothing here is fogged (effects.md §6). _Additive is kept for the
// callers but no longer used.
// Shadow techniques (world_render.md §13): technique 1 (shadowLevel 1, ps_2_0 texldp 1 tap) multiplies rgb by
// saturate(shadow + 0.5) (def c0 0.5; technique 2 is the ATI shadowLevel 4 form, cmp + 0.3); alpha is untouched. The render
// list gives a particle draw the technique by its view depth as any world draw (MWShadow.cginc: level 0 beyond 400 m).
Shader "NFSMW/Particle"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 10
        _Additive ("Additive (unused)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }
        Blend [_SrcBlend] [_DstBlend]
        ZWrite Off
        Offset -1, -2
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "MWShadow.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; float4 shadow : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.shadow = float4(wpos, MWShadowLevel(wpos));   // [ours] the particle's own position stands for the draw's bbox centre
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float4 c = saturate(tex2D(_MainTex, i.uv) * i.color * 2);   // mul_x2 r0, t0, v0
                if (i.shadow.w > 0.5)
                {
                    // technique 1: one texldp shadow tap, add_sat 0.5, rgb only (no InvShadowStrength / edge fade in this effect)
                    c.rgb *= saturate(MWSunShadowTap(i.shadow.xyz, 1) + 0.5);
                }
                return c;
            }
            ENDCG
        }
    }
}
