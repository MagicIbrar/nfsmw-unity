// Alpha-blended world and car glass: texture x vertex color, no depth write.
// _MWLit = 1 (world scenery materials, Runtime/World/WorldMaterials.cs): world.efx technique 0 lighting as World.shader
// (NFSMW_RE/specs/world_render.md §3, with the shadow techniques' sun factor of §13); car glass and race props keep
// _MWLit = 0 (unlit, Unity fog).
Shader "NFSMW/WorldTransparent"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _VertexScale ("Vertex color scale", Float) = 2
        _MWLit ("world.efx lighting (world scenery)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "MWLighting.cginc"
            #include "MWShadow.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _VertexScale, _MWLit;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f
            {
                float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_FOG_COORDS(1)
                half3 sun : TEXCOORD2; half3 inscatter : TEXCOORD3; float4 shadow : TEXCOORD4;
            };

            v2f vert (appdata v)
            {
                v2f o;
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
                if (_MWLit > 0.5 && _MW_Enabled > 0.5)   // world.efx technique 0, as World.shader
                {
                    fixed4 t = tex2D(_MainTex, i.uv);
                    return fixed4(saturate(2 * (t.rgb * i.sun * MWSunShadow(i.shadow.xyz, i.shadow.w) + t.rgb * i.color.rgb) + i.inscatter), t.a * i.color.a);
                }
                fixed4 tex = tex2D(_MainTex, i.uv) * _Color;
                fixed4 c = fixed4(tex.rgb * i.color.rgb * _VertexScale, tex.a * i.color.a);
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
}
