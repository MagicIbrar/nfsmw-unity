// speed.exe fx\skybox.efx (resource IDI_SKYBOX_FX, technique "world", disassembled with D3DDisassemble), drawn on the
// SKYDOME_XENON model by Runtime/Sky/SkyRender.cs. CPU constants from Runtime/Sky/TimeOfDayDriver.cs:
//   CloudIntensity (SKY_DIFFUSESCALE) = _MW_CloudIntensity, CloudScroll = _MW_CloudScroll, fog = _MW_Fog*, LocalLightVec
//   = _MW_LightDir. Samplers: WRAP, MISC_1..3 with MIPMAPLODBIAS -1.
// p0  vs: oT0 = (uv0.x - 0.4 CloudScroll, uv0.y), oT2 = (uv0.x - (0.2 CloudScroll + 0.3), uv0.y), oT1 = uv1,
//         oT3 = in-scatter at a fixed 15000 m (expp of -21640.4258 = -15000 log2 e); no culling, no blending.
//     ps: a = 2 * diffuse(T0).a * diffuse(T2).a * CloudIntensity.x
//         rgb = (MISC_1(T1) + diffuse(T0)) / 2 * a + oT3 * (1 - a), alpha 1
// p1  oT0 = (uv0.x - CloudScroll, uv0.y); out = MISC_2(T0).rgb, MISC_2.a * CloudIntensity.y; SRCALPHA/INVSRCALPHA,
//     ZWRITE off, ZFUNC EQUAL
// p2  out = MISC_3(T1).rgb, MISC_3.a * CloudIntensity.z; same states
Shader "NFSMW/SkyBox"
{
    Properties
    {
        _MainTex ("diffuse_sampler (dome texture)", 2D) = "white" {}
        _Misc1 ("SKY_TEXTURE_MISC_1", 2D) = "black" {}
        _Misc2 ("SKY_TEXTURE_MISC_2", 2D) = "black" {}
        _Misc3 ("SKY_TEXTURE_MISC_3", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off

        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex, _Misc1, _Misc2, _Misc3;
        float4 _MW_LightDir, _MW_CloudIntensity;
        float4 _MW_FogBrBm, _MW_FogOneOverBrBm, _MW_FogConst1, _MW_FogConst2, _MW_FogConst3;
        float _MW_CloudScroll;

        struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float2 uv1 : TEXCOORD1; };

        float4 SkyPos(float4 v)
        {
            float4 p = UnityObjectToClipPos(v);
        #if UNITY_REVERSED_Z
            p.z = p.w * 1e-6;          // [ours] at the far end of the depth range: the dome is shrunk inside the far plane
        #else
            p.z = p.w * (1 - 1e-6);
        #endif
            return p;
        }

        // ps_1_1 sampler with D3DSAMP_MIPMAPLODBIAS -1
        float4 TexBias(sampler2D s, float2 uv) { return tex2Dbias(s, float4(uv, 0, -1)); }
        ENDCG

        Pass
        {
            ZWrite On
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct v2f { float4 pos : SV_POSITION; float2 t0 : TEXCOORD0; float2 t1 : TEXCOORD1; float2 t2 : TEXCOORD2; float3 t3 : TEXCOORD3; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = SkyPos(v.vertex);
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 toEye = normalize(_WorldSpaceCameraPos - wpos);         // LocalEyePos - v
                float3 L = normalize(_MW_LightDir.xyz);                          // rsq of LocalLightVec
                float c = dot(L, toEye);
                float hgBase = _MW_FogConst3.z * c + _MW_FogConst3.y;
                float hg = pow(rsqrt(hgBase), 3) * _MW_FogConst3.x;              // (.)^-1.5 * (1-g)^2
                float3 ext = exp2(-21640.4258 * _MW_FogBrBm.xyz);
                float3 s = (_MW_FogConst1.xyz * (c * c + 1) + _MW_FogConst2.xyz * hg) * _MW_FogOneOverBrBm.xyz * (1 - ext);
                o.t3 = min(max(s * _MW_FogConst3.w, 0), 1);
                o.t0 = float2(v.uv.x - _MW_CloudScroll * 0.4, v.uv.y);
                o.t2 = float2(v.uv.x - (_MW_CloudScroll * 0.2 + 0.3), v.uv.y);
                o.t1 = v.uv1;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float4 t0 = tex2D(_MainTex, i.t0);
                float4 t1 = TexBias(_Misc1, i.t1);
                float4 t2 = tex2D(_MainTex, i.t2);
                // [?] saturate: ps_1_1 register range; a only exceeds 1 when CloudIntensity.x is 1 (cloudy areas)
                float a = saturate(2 * (t0.a * t2.a) * _MW_CloudIntensity.x);
                float3 rgb = (t1.rgb + t0.rgb) * 0.5 * a + i.t3 * (1 - a);
                return fixed4(rgb, 1);
            }
            ENDCG
        }

        Pass
        {
            ZWrite Off
            ZTest LEqual        // [ours] exe ZFUNC EQUAL against pass 0's depth: same pixels here
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct v2f { float4 pos : SV_POSITION; float2 t0 : TEXCOORD0; };
            v2f vert (appdata v)
            {
                v2f o;
                o.pos = SkyPos(v.vertex);
                o.t0 = float2(v.uv.x - _MW_CloudScroll, v.uv.y);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                float4 t = TexBias(_Misc2, i.t0);
                return fixed4(t.rgb, t.a * _MW_CloudIntensity.y);
            }
            ENDCG
        }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct v2f { float4 pos : SV_POSITION; float2 t1 : TEXCOORD1; };
            v2f vert (appdata v)
            {
                v2f o;
                o.pos = SkyPos(v.vertex);
                o.t1 = v.uv1;
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                float4 t = TexBias(_Misc3, i.t1);
                return fixed4(t.rgb, t.a * _MW_CloudIntensity.z);
            }
            ENDCG
        }
    }
}
