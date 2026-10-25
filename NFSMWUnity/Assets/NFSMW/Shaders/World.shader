// World geometry (solid effect id 0), after speed.exe's fx\world.efx technique 0 (vs_1_1 + ps_1_1, disassembled):
//   vertex: MWLighting.cginc (oD0 prelit x ambient x extinction, oD1 sun N.L, oT2 in-scatter)
//   pixel:  r0.rgb = t0 * v0; r0.rgb = 2 * (t0 * v1 + r0) (mad_x2); r0.a = t0.a * v0.a; r0.rgb += t2
// i.e. 2 * tex * (prelit + sun) + in-scatter. The shadow-map techniques 1-6 (ps 0x9F84 1 tap, 0x8DC0 4 taps) multiply
// the sun term only by the shadow factor f of MWShadow.cginc: 2 * tex * (prelit + sun * f) + in-scatter
// (world_render.md §13). Until the lighting is published (_MW_Enabled = 0, editor tools) the old texture x vertex
// colour x _VertexScale look.
// The ShadowCaster pass is off on every world material except the copies WorldShadows.cs gives the exe's casters
// (instances flagged 0x40000000, smackables); the exe draws them into its shadow map with worldnofog (alpha-tested).
// WorldReflect / WorldNormalMap / GlossyWindow / Tree fall back to it for their own casters.
Shader "NFSMW/World"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        _VertexScale ("Vertex color scale (unlit fallback)", Float) = 2
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0   // two-sided: world parts mix windings
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
        Cull [_Cull]
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

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff, _VertexScale;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 prelit : COLOR0;       // oD0
                half3 sun : COLOR1;          // oD1
                half3 inscatter : TEXCOORD1; // oT2
                float4 shadow : TEXCOORD2;   // world position, w = the draw's shadow level
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.shadow = float4(wpos, MWShadowLevel(unity_ObjectToWorld._m03_m13_m23));
                if (_MW_Enabled < 0.5)
                {
                    o.prelit = half4(v.color.rgb * _VertexScale, v.color.a);
                    o.sun = 0;
                    o.inscatter = 0;
                    return o;
                }
                float3 n = UnityObjectToWorldNormal(v.normal);
                MWLight l = MWLightVertex(wpos, n, v.color);
                o.prelit = l.prelit;
                o.sun = l.sun;
                o.inscatter = l.inscatter;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                clip(tex.a * i.prelit.a - _Cutoff);
                if (_MW_Enabled < 0.5) return fixed4(tex.rgb * i.prelit.rgb, 1);
                float f = MWSunShadow(i.shadow.xyz, i.shadow.w);
                // the exe renders into an 8-bit target: the result clamps at 1
                float3 c = saturate(2 * (tex.rgb * i.sun * f + tex.rgb * i.prelit.rgb) + i.inscatter);
                return fixed4(c, 1);
            }
            ENDCG
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            Cull [_Cull]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff;
            struct v2f { V2F_SHADOW_CASTER; float2 uv : TEXCOORD1; float alpha : TEXCOORD2; };
            v2f vert (appdata_full v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.alpha = v.color.a;
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }
            float4 frag (v2f i) : SV_Target
            {
                // worldnofog in the shadow slot keeps the material's alpha test (texture alpha x vertex alpha)
                if (_Cutoff >= 0) clip(tex2D(_MainTex, i.uv).a * i.alpha - _Cutoff);
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }
}
