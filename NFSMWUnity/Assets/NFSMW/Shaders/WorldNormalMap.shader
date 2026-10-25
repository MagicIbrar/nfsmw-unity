// Normal-mapped world geometry (solid effect id 3: walls, cliffs, barriers): speed.exe fx\worldnormalmap.efx
// (IDI_WORLDNORMALMAP_FX) technique 0 "worldnormalmap_2_0" (vs_1_1 + ps_2_0; NFSMW_RE/specs/world_render.md §2).
// CPU constants: the world-type setup 0x6D6AA0 (SpecularColour = TOD specular, SpecularPower 11).
//   vs: oD0 = (Ambient.rgb * Ambient.w, 1) * vertex colour; oT2 = normalize(tangent-space light);
//       oT3 = tangent-space (eye - v)/|eye - v|; oT4 = in-scatter; oT5 = min(extinction, 1)
//   ps: n = normalize(2 (normal - 0.5)); R = 2 (n.L) n - L; spec = pow(sat(R.V), SpecularPower) * SpecularColour.rgb
//       c = (tex * v0 + tex * sat(n.L) * DiffuseColour + spec * specmap) * oT5     (specmap NOT doubled here)
//       out = 2 c + oT4, alpha = tex.a * v0.a
// Blend / alpha test come from the texture's TextureInfo (BlendState), set by Runtime/World/WorldMaterials.cs.
// Shadow techniques (shadowLevel 1 ps 0xA604 / 2 ps 0x91EC): the sun terms (tex * sat(n.L) * DiffuseColour + spec *
// specmap) are multiplied by f (MWShadow.cginc, world_render.md §13).
Shader "NFSMW/WorldNormalMap"
{
    Properties
    {
        _MainTex ("Diffuse", 2D) = "white" {}
        _NormalMap ("Normal map (slot 1)", 2D) = "bump" {}
        _SpecMap ("Specular map (slot 3)", 2D) = "black" {}
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

            sampler2D _MainTex, _NormalMap, _SpecMap;
            float4 _MainTex_ST;
            float _Cutoff, _MW_SpecularPower;

            struct appdata
            {
                float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT; float2 uv : TEXCOORD0;
                fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 lightTs : TEXCOORD1;  // oT2
                float3 viewTs : TEXCOORD2;   // oT3
                half3 inscatter : TEXCOORD3; // oT4
                half3 ext : TEXCOORD4;       // oT5
                float4 shadow : TEXCOORD5;   // world position, w = shadow level (oT7 in the shadow techniques)
                half4 prelit : COLOR0;       // oD0
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
                float a = tex.a * i.prelit.a;
                clip(a - _Cutoff);
                if (_MW_Enabled < 0.5) return fixed4(tex.rgb * i.prelit.rgb, a);
                float3 n = normalize(2 * (MWRawData(tex2D(_NormalMap, i.uv).rgb) - 0.5));
                float3 L = normalize(i.lightTs);
                float ndl = dot(n, L);
                float3 R = 2 * ndl * n - L;
                float3 V = normalize(i.viewTs);
                float3 spec = pow(saturate(dot(R, V)), _MW_SpecularPower) * _MW_Specular.rgb;
                float3 lit = (tex.rgb * saturate(ndl) * _MW_Diffuse.rgb + spec * tex2D(_SpecMap, i.uv).rgb) * MWSunShadow(i.shadow.xyz, i.shadow.w);
                float3 c = (tex.rgb * i.prelit.rgb + lit) * i.ext;
                return fixed4(saturate(2 * c + i.inscatter), a);
            }
            ENDCG
        }
    }
    Fallback "NFSMW/World"
}
