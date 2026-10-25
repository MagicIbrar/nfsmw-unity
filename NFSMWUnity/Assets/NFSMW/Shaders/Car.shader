// speed.exe fx\car.efx (resource IDI_CAR_FX, D3DX fx_2_0), disassembled with D3DDisassemble: technique `car`, vs_1_1
// (shader 1 / 11) + ps_2_0 (shader 8, the single-tap shadow form). Per vertex: three shaper lights
// Σ saturate(N·Lᵢ + 0.1)·Cᵢ (LocalDirectionMatrix rows / LocalColourMatrix columns), ×saturate(2·vertex red) (baked
// occlusion), × the light material's DiffuseMin + N·V·DiffuseRange (alpha too); specular colour SpecularMin +
// SpecularRange·(N·V)^SpecularPower; envmap factor EnvmapMin.x + EnvmapRange.x·(N·V)^EnvmapPower; N·V clamps at 0.01;
// vs_1_1 colour outputs saturate. Per pixel: highlight 2·sat(reflect(L0,N)·V)^16 · diffuse.a · specular colour,
// colour = (diffuse·tex + highlight·s)·s + envcube(R)·envfactor·diffuse.a·0.5 with s = lerp(0.2 + InvShadowStrength, 1,
// lit); alpha = tex.a·diffuse.a. The car effect has no fog (no FOGON/oFog in car.efx).
// InvShadowStrength and the specular scale come from CarEnvironment (time of day, car_render.md §12).
// [ours] The cube map is CarEnvironment's cube (world space; MW renders one eEnvMap around the player car).
// Glass / decals / damage overlays use the same shader with alpha or additive blending (_SrcBlend/_DstBlend/_ZWrite).
Shader "NFSMW/Car"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff (<0 off)", Float) = -1
        _DiffuseMin ("DiffuseMin", Vector) = (1, 1, 1, 1)
        _DiffuseRange ("DiffuseRange", Vector) = (0, 0, 0, 0)
        _SpecularMin ("SpecularMin", Vector) = (0, 0, 0, 0)
        _SpecularRange ("SpecularRange", Vector) = (0, 0, 0, 0)
        _EnvmapMin ("EnvmapMin", Vector) = (0, 0, 0, 0)
        _EnvmapRange ("EnvmapRange", Vector) = (0, 0, 0, 0)
        _SpecularPower ("SpecularPower", Float) = 1
        _EnvmapPower ("EnvmapPower", Float) = 1
        _InvShadowStrength ("InvShadowStrength", Float) = 0.3
        // legacy knobs (traffic tint through the old _Paint/_Color path) [ours]
        _Color ("Paint", Color) = (1, 1, 1, 1)
        _Paint ("Paint amount", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 0
        [Toggle] _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "AlphaTest" }
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff, _SpecularPower, _EnvmapPower, _InvShadowStrength, _Paint;
            float4 _DiffuseMin, _DiffuseRange, _SpecularMin, _SpecularRange, _EnvmapMin, _EnvmapRange;
            fixed4 _Color;

            // CarEnvironment globals: the three shaper lights (world space, toward the light) and the env cube
            float4 _MW_CarLightDir0, _MW_CarLightDir1, _MW_CarLightDir2;
            float4 _MW_CarLightCol0, _MW_CarLightCol1, _MW_CarLightCol2;
            float _MW_CarLightsSet;
            samplerCUBE _MW_CarEnvMap;
            float _MW_CarEnvSet;
            // the front end's static garage cube (eInitFEEnvMapPlat 0x6BF690, CarEnvironment.SetGarage): the exe looks the
            // cube up with the view-space reflection vector (D3D view: x right, y up, z forward), so a fixed cube stays
            // camera-locked; in game the rendered cube follows the camera and the world-space lookup is equivalent
            float _MW_CarEnvView;
            // CarEnvironment time-of-day constants: INVSHADOWSTRENGTH (tunnel fade + 1 - shadow strength) and the TOD
            // CarSpecScale that sub_6C76E0 folds into SpecularMin / SpecularRange; _MW_CarTodSet = 0 keeps the material values
            float _MW_CarInvShadow, _MW_CarSpecScale, _MW_CarTodSet;
            // render flag 1 (CarRenderInfo::Render 0x74E160: EnableEnvMap off or a car under DisableEnvMapSize 18 px): the
            // body and tyres draw without the env map; per renderer through a MaterialPropertyBlock (CarRenderInfo)
            float _MW_CarEnvOff;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 n : TEXCOORD1;      // t4: the interpolated (unnormalised) normal
                float3 v : TEXCOORD2;      // t5: per-vertex view vector
                float3 r : TEXCOORD3;      // t0: reflection vector for the cube
                float4 d0 : TEXCOORD4;     // oD0: diffuse lighting, a = diffuse alpha
                float4 d1 : TEXCOORD5;     // oD1: specular colour, a = envmap factor
                SHADOW_COORDS(6)
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float3 n = UnityObjectToWorldNormal(v.normal);
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 V = normalize(_WorldSpaceCameraPos - wp);
                float ndv = max(dot(V, n), 0.01);
                float ao = saturate(2 * v.color.r);
                float3 L0 = _MW_CarLightDir0.xyz, L1 = _MW_CarLightDir1.xyz, L2 = _MW_CarLightDir2.xyz;
                float3 C0 = _MW_CarLightCol0.rgb, C1 = _MW_CarLightCol1.rgb, C2 = _MW_CarLightCol2.rgb;
                if (_MW_CarLightsSet < 0.5)
                {
                    // before CarEnvironment runs (editor previews): the front end's QRace / garage rig (ShaperLightsQRace
                    // 0x8F2D30, WORLD_SPACE: game (0, -0.669, 0.743), (-0.692, 0.717, -0.087), (0.888, 0.452, 0.087) in Unity axes)
                    L0 = float3(0, 0.743, -0.669); L1 = float3(-0.692, -0.087, 0.717); L2 = float3(0.888, 0.087, 0.452);
                    C0 = float3(0.98, 0.98, 0.931); C1 = float3(0.415, 0.415, 0.5); C2 = float3(0.2905, 0.2905, 0.35);
                }
                float3 lights = saturate(dot(n, L0) + 0.1) * C0 + saturate(dot(n, L1) + 0.1) * C1 + saturate(dot(n, L2) + 0.1) * C2;
                float4 diffuse = _DiffuseMin + ndv * _DiffuseRange;
                o.d0 = saturate(float4(lights * ao * diffuse.rgb, diffuse.a));
                float specScale = _MW_CarTodSet > 0.5 ? _MW_CarSpecScale : 1.0;
                float3 spec = (_SpecularMin.rgb + _SpecularRange.rgb * pow(ndv, _SpecularPower)) * specScale;
                float env = _EnvmapMin.x + _EnvmapRange.x * pow(ndv, _EnvmapPower);
                o.d1 = saturate(float4(spec * ao, ao * env));
                o.r = 2 * ndv * n - V;
                if (_MW_CarEnvView > 0.5)
                {
                    float3 rv = mul((float3x3)UNITY_MATRIX_V, o.r);   // Unity view space looks down -z
                    o.r = float3(rv.x, rv.y, -rv.z);
                }
                o.n = n;
                o.v = V;
                TRANSFER_SHADOW(o);
                return o;
            }

            fixed4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                if (_Cutoff >= 0) clip(tex.a - _Cutoff);
                tex.rgb = lerp(tex.rgb, tex.rgb * _Color.rgb, _Paint);
                float3 n = i.n * (facing > 0 ? 1 : -1);
                float3 L0 = _MW_CarLightsSet < 0.5 ? float3(0, 0.743, -0.669) : _MW_CarLightDir0.xyz;
                float3 R0 = 2 * dot(n, L0) * n - L0;
                float x = saturate(dot(R0, i.v));
                float x2 = x * x, x4 = x2 * x2, x8 = x4 * x4;
                float hl = 2 * x8 * x8;
                float3 highlight = hl * i.d0.a * i.d1.rgb;
                float lit = SHADOW_ATTENUATION(i);
                float invShadow = _MW_CarTodSet > 0.5 ? _MW_CarInvShadow : _InvShadowStrength;
                float s = lerp(0.2 + invShadow, 1, lit);
                float3 env = _MW_CarEnvSet > 0.5 ? texCUBE(_MW_CarEnvMap, i.r).rgb
                                                 : DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, i.r, 0), unity_SpecCube0_HDR);
                env *= 1 - saturate(_MW_CarEnvOff);
                float3 c = (i.d0.rgb * tex.rgb + highlight * s) * s + env * i.d1.a * i.d0.a * 0.5;
                return fixed4(c, tex.a * i.d0.a);
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
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff;
            struct v2f { V2F_SHADOW_CASTER; float2 uv : TEXCOORD1; };
            v2f vert (appdata_base v)
            {
                v2f o;
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }
            float4 frag (v2f i) : SV_Target
            {
                if (_Cutoff >= 0) clip(tex2D(_MainTex, i.uv).a - _Cutoff);
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }
}
