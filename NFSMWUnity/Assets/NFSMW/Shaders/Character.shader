// NIS characters, after speed.exe's fx\worldbone.efx (embedded fx_2_0 at file offset 0x5310C0; vs_1_1 + ps_1_1,
// disassembled):
//   vertex: facing = max(sat(dot(normalize(LocalEyePos - v0), N)), 0.01)        (unskinned position and normal)
//           diffuse = DiffuseMin + DiffuseRange * facing                          (rgba)
//           light   = sat(dot(N, L0) + 0.3) * C0 + sat(dot(N, L1) + 0.3) * C1     (LocalDirectionMatrix rows, LocalColourMatrix columns)
//           oD0 = diffuse.rgb * light, oD0.a = diffuse.a ; oD1 = specular/envmap terms scaled by vertex colour red (0 on the characters)
//   pixel:  out = tex * oD0, alpha x2 (c0 = 1,1,1,2)
// Skinning is done on the CPU (NisCharacter); the mesh normals stay in the bind pose like the shader's v1.
// CPU constants: DiffuseMin/Range per material from the solid's light material (table 0x134013, material byte +0x1D;
// sub_6C76E0: DiffuseMin = MinScale*MinRGB, a = MinA; DiffuseRange = MaxScale*MaxRGB - DiffuseMin, a = (MaxA-MinA)/4),
// set by NisCharacter. L0/C0, L1/C1 = lights 0 and 1 of ShaperLightsCharacters (0x8F2F10): WorldModel::RenderModel
// 0x73B9C0 gives the NIS character entities elSetupLightContext(ShaperLightsCharacters, ...), a CAMERA_SPACE rig
// (mode 1) rebuilt from the rendering camera each frame: L0 = -0.815 right + 0.242 up - 0.527 forward, colour
// 0.5 x 0.53; L1 = 0.470 right - 0.883 forward, colour 1 x 0.59 (both toward the light, from behind the camera).
// CarEnvironment publishes them as _MW_CharLightDir0/1 / _MW_CharLightCol0/1; the rig's third slot (the NIS cop quick
// light, AddQuickDynamicLight) is never read by worldbone.efx (LocalDirectionMatrix c71-c72 = two rows). Before the
// rig is published: the car rig, then the sun and a sky fill [ours, editor only]. vs_1_1 saturates oD0.
Shader "NFSMW/Character"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        _DiffuseMin ("DiffuseMin", Vector) = (0.5, 0.5, 0.5, 0.5)
        _DiffuseRange ("DiffuseRange", Vector) = (0.5, 0.5, 0.5, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
        Cull Back
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "MWLighting.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff;
            float4 _DiffuseMin, _DiffuseRange;
            float4 _MW_CarLightDir0, _MW_CarLightDir1, _MW_CarLightCol0, _MW_CarLightCol1;
            float _MW_CarLightsSet;
            float4 _MW_CharLightDir0, _MW_CharLightDir1, _MW_CharLightCol0, _MW_CharLightCol1;
            float _MW_CharLightsSet;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half4 diffuse : COLOR0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 n = UnityObjectToWorldNormal(v.normal);
                float3 V = normalize(_WorldSpaceCameraPos - wpos);
                float facing = max(saturate(dot(V, n)), 0.01);
                float4 diffuse = _DiffuseMin + _DiffuseRange * facing;
                float3 L0 = _MW_Enabled > 0.5 ? normalize(_MW_LightDir.xyz) : normalize(float3(0.3, 0.8, 0.5));
                float3 C0 = _MW_Enabled > 0.5 ? _MW_Diffuse.rgb : float3(0.8, 0.8, 0.8);
                float3 L1 = float3(0, 1, 0);
                float3 C1 = _MW_Enabled > 0.5 ? _MW_Ambient.rgb * _MW_Ambient.w : float3(0.4, 0.4, 0.45);
                if (_MW_CarLightsSet > 0.5)
                {
                    L0 = normalize(_MW_CarLightDir0.xyz); C0 = _MW_CarLightCol0.rgb;
                    L1 = normalize(_MW_CarLightDir1.xyz); C1 = _MW_CarLightCol1.rgb;
                }
                if (_MW_CharLightsSet > 0.5)   // ShaperLightsCharacters (camera space), the exe's rig for the NIS entities
                {
                    L0 = normalize(_MW_CharLightDir0.xyz); C0 = _MW_CharLightCol0.rgb;
                    L1 = normalize(_MW_CharLightDir1.xyz); C1 = _MW_CharLightCol1.rgb;
                }
                float3 light = saturate(dot(n, L0) + 0.3) * C0 + saturate(dot(n, L1) + 0.3) * C1;
                o.diffuse = saturate(half4(diffuse.rgb * light, diffuse.a));
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                float a = tex.a * i.diffuse.a * 2;
                clip(a - _Cutoff);
                return fixed4(tex.rgb * i.diffuse.rgb, 1);
            }
            ENDCG
        }
    }
}
