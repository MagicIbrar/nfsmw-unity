// Blended world layers (TextureInfo AlphaBlendType 1 blend, 2 additive, 3 subtractive): baked shadows, overlays, neon and
// light glows, oil stains. Drawn over the surface below, depth biased, no depth write.
// _MWLit = 1 (world scenery materials, Runtime/World/WorldMaterials.cs): the exe draws blended world materials with the
// same world.efx technique 0 as opaque ones, only the BlendState differs (NFSMW_RE/specs/world_render.md §3):
// rgb = 2 tex (prelit + sun) + in-scatter, alpha = tex.a * vertex alpha. _MWLit = 0 (car shadow, skid marks, flares,
// particle fallback): the old unlit texture x vertex colour x _VertexScale, fogged to black.
// With _MWLit the world.efx shadow techniques apply too (sun term x f, MWShadow.cginc, world_render.md §13).
Shader "NFSMW/WorldDecal"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _VertexScale ("Vertex color scale", Float) = 2
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 10
        [Enum(UnityEngine.Rendering.BlendOp)] _BlendOp ("Blend op", Float) = 0
        _MWLit ("world.efx lighting (world scenery)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "AlphaTest+20" "IgnoreProjector" = "True" }
        Blend [_SrcBlend] [_DstBlend]
        BlendOp [_BlendOp]
        ZWrite Off
        Offset -1, -2
        Cull Off
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "MWLighting.cginc"
            #include "MWShadow.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _VertexScale, _MWLit;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_FOG_COORDS(1)
                half3 sun : TEXCOORD2; half3 inscatter : TEXCOORD3; float4 shadow : TEXCOORD4;
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                o.sun = 0;
                o.inscatter = 0;
                o.shadow = 0;
                UNITY_TRANSFER_FOG(o, o.pos);
                if (_MWLit > 0.5 && _MW_Enabled > 0.5)
                {
                    float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                    MWLight l = MWLightVertex(wpos, UnityObjectToWorldNormal(v.normal), v.color);
                    o.shadow = float4(wpos, MWShadowLevel(unity_ObjectToWorld._m03_m13_m23));
                    o.color = l.prelit;
                    o.sun = l.sun;
                    o.inscatter = l.inscatter;
                }
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                if (_MWLit > 0.5 && _MW_Enabled > 0.5)   // world.efx technique 0, as World.shader
                    return fixed4(saturate(2 * (tex.rgb * i.sun * MWSunShadow(i.shadow.xyz, i.shadow.w) + tex.rgb * i.color.rgb) + i.inscatter), tex.a * i.color.a);
                fixed4 c = fixed4(tex.rgb * i.color.rgb * _VertexScale, tex.a * i.color.a);   // ps: tex * vertex colour, alpha included
                UNITY_APPLY_FOG_COLOR(i.fogCoord, c, fixed4(0, 0, 0, 0));
                return c;
            }
            ENDCG
        }
    }
}
