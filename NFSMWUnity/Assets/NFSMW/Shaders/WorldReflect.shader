// Roads and pavements (solid effect id 1): speed.exe fx\worldreflect.efx (IDI_WORLDREFLECT_FX) technique 0
// "world_reflect_noshadow_2_0" (vs_1_1 + ps_2_0, disassembled; NFSMW_RE/specs/world_render.md §2).
// CPU constants (0x6D71F0): SpecularColour = (1 - cloud) x TOD specular, w = road wetness (_MW_SpecularRoad),
// SpecularPower 11, ReflectedTex = the road reflection view (Runtime/World/RoadReflection.cs, _MW_RoadReflectionTex).
// Material textures (material +0x18..+0x1C): slot 0 diffuse, slot 1 normal map, slot 3 specular map.
//   vs: oD0 = (Ambient.rgb * Ambient.w, 1) * vertex colour         (prelit, NOT extinguished here)
//       oT1 = projective screen position of the vertex (ReflectedTex lookup)
//       oT3 = normalize(tangent-space light), oT4 = tangent-space (eye - v)/|eye - v|, binormal = cross(N, T) * T.w
//       oT5 = in-scatter, oT6 = min(extinction, 1)                (the world.efx fog block)
//   ps: n = normalize(2 (normal - 0.5)); R = 2 (n.L) n - L; spec = pow(sat(R.V), SpecularPower) * SpecularColour.rgb
//       s = 2 specmap; lit = tex * v0 + tex * sat(n.L) * DiffuseColour + spec * s
//       gloss = s.x * tex.a * v0.a; c = (ReflectedTex(t1) * gloss * SpecularColour.w + lit) * oT6
//       out = 2 c + oT5, alpha 1
// Shadow techniques (world_reflect_shadowhw_2_0, shadowLevel 1 ps 0xAAF0 / 2 ps 0x95AC): the sun terms
//   (tex * sat(n.L) * DiffuseColour + spec * s) are multiplied by f (MWShadow.cginc, world_render.md §13).
Shader "NFSMW/WorldReflect"
{
    Properties
    {
        _MainTex ("Diffuse", 2D) = "white" {}
        _NormalMap ("Normal map (slot 1)", 2D) = "bump" {}
        _SpecMap ("Specular map (slot 3)", 2D) = "black" {}
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "AlphaTest" }
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

            sampler2D _MainTex, _NormalMap, _SpecMap, _MW_RoadReflectionTex;
            float4 _MainTex_ST;
            float4 _MW_SpecularRoad;
            float _MW_SpecularPower;

            struct appdata
            {
                float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT; float2 uv : TEXCOORD0;
                fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 screen : TEXCOORD1;   // oT1
                float3 lightTs : TEXCOORD2;  // oT3
                float3 viewTs : TEXCOORD3;   // oT4
                half3 inscatter : TEXCOORD4; // oT5
                half3 ext : TEXCOORD5;       // oT6
                float4 shadow : TEXCOORD6;   // world position, w = shadow level (oT7 in the shadow techniques)
                half4 prelit : COLOR0;       // oD0
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.screen = ComputeScreenPos(o.pos);
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.shadow = float4(wpos, MWShadowLevel(unity_ObjectToWorld._m03_m13_m23));
                float3 n = UnityObjectToWorldNormal(v.normal);
                float3 t = UnityObjectToWorldDir(v.tangent.xyz);
                float3 b = cross(n, t) * v.tangent.w;
                float3 L = _MW_LightDir.xyz;
                float3 lt = float3(dot(t, L), dot(b, L), dot(n, L));
                o.lightTs = lt * rsqrt(max(dot(lt, lt), 1e-12));
                float3 V = normalize(_WorldSpaceCameraPos - wpos);
                o.viewTs = float3(dot(t, V), dot(b, V), dot(n, V));
                float3 ext, ins;
                MWFogTerms(wpos, ext, ins);
                o.inscatter = ins;
                o.ext = ext;
                o.prelit = saturate(float4(_MW_Ambient.rgb * _MW_Ambient.w, 1) * v.color);
                if (_MW_Enabled < 0.5) { o.prelit = half4(v.color.rgb * 2, v.color.a); o.ext = 1; o.inscatter = 0; }
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float4 tex = tex2D(_MainTex, i.uv);
                if (_MW_Enabled < 0.5) return fixed4(tex.rgb * i.prelit.rgb, 1);
                float3 n = normalize(2 * (MWRawData(tex2D(_NormalMap, i.uv).rgb) - 0.5));
                float3 L = normalize(i.lightTs);
                float ndl = dot(n, L);
                float3 R = 2 * ndl * n - L;
                float3 V = normalize(i.viewTs);
                float3 spec = pow(saturate(dot(R, V)), _MW_SpecularPower) * _MW_SpecularRoad.rgb;
                float3 s = 2 * tex2D(_SpecMap, i.uv).rgb;
                float3 lit = (tex.rgb * saturate(ndl) * _MW_Diffuse.rgb + spec * s) * MWSunShadow(i.shadow.xyz, i.shadow.w);
                lit = tex.rgb * i.prelit.rgb + lit;
                float gloss = s.x * tex.a * i.prelit.a;
                float3 refl = tex2Dproj(_MW_RoadReflectionTex, UNITY_PROJ_COORD(i.screen)).rgb * gloss;
                float3 c = (refl * _MW_SpecularRoad.w + lit) * i.ext;
                return fixed4(saturate(2 * c + i.inscatter), 1);   // 8-bit target: clamps at 1
            }
            ENDCG
        }
    }
    Fallback "NFSMW/World"
}
