// speed.exe's end-of-frame post chain (___eDisplayFrame 0x6DF57B..0x6DF8D1; NFSMW_RE/specs/world_render.md, "End-of-frame
// post chain"). All targets are D3DFMT_A8R8G8B8 (0x8F9AD8 = 0x15); the quarter targets are W/4 x H/4 (0x6C2E60).
//   sub_6DBEC0: StretchRect(back buffer -> full copy), then filter.efx (0x6D2CD0) with 4 taps of 0.25 at (+-0.5, +-0.5)
//     source texels (0x8F9560), i.e. a 4x4 box: pass 4 -> VT bloom source A (rgb, a = dot(rgb, LUMINANCE_VECTOR)),
//     pass 5 -> overbright source C (rgb * a, a): the back buffer's alpha is the overbright mask.
//   sub_6DBFE0: [0x8F9474] = 2 x (A -> B horizontal, B -> A vertical), filter pass 3: 1/3 at +-1.5 and 1/6 at +-3 texels.
//   sub_6D53D0 / sub_6D4EF0: visualtreatment.efx technique 0 (ps_2_0), below; LUMINANCE_VECTOR = (0.6125, 0.5154, 0.0721)
//     (the effect default, never set by the CPU); GAINMAP = the UVESVIGNETTE texture (GLOBALB, 256x256).
//       lum   = dot(scene, LUMINANCE_VECTOR); desat = Desaturation.x * scene + Desaturation.y * lum
//       curve = spline(bloom.a, bloom.r) (point sampled 256x1: r = black bloom curve, g = colour bloom curve)
//       out   = (scene * curve.y * ColourBloomIntensity * ColourBloomTint + desat * (curve.x * BBI.x + BBI.y)
//                + gain.y * VisualEffectVignette) * VisualEffectBrightness * gain.z
//   sub_6DC040 (overbright.efx, option [0x9017FC]): A = pContrast(C): t = sat(2 (C.rgb - OverBrightOffset)),
//     rgb = t * dot(t, OverBrightGreyScale (0.5)), a = 0; 2 x blur as above; then A is added over the frame with
//     BLENDFACTOR / ONE (sub_6D3B80), the factor 0.6 (0.8 without the visual treatment) or, under tunnel glare,
//     intensity x [0x8FAE64] x 200 / 255.
// The PC shader never reads VisualEffectRadialBlur. The speed blur is eDisplayFrame's motion blur (sub_6DBB20), done
// with passes 4 and 5. Passes: 0 VT bloom source, 1/2 filter blur, 3 visual treatment, 4 motion-blur filter, 5 motion-blur
// blend, 6 overbright source, 7 pContrast, 8 overbright add, 9-11 the back-buffer alpha mask (command buffer).
Shader "NFSMW/VisualTreatment"
{
    Properties { _MainTex ("Scene", 2D) = "white" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex, _BloomTex, _SplineTex, _BlurTex, _GainTex;
    float4 _MainTex_TexelSize, _BloomTex_TexelSize;
    float4 _BlackBloomIntensity, _Desaturation, _ColourBloomTint, _BlurRect;
    float _ColourBloomIntensity, _Brightness, _Vignette, _MotionAlpha, _HasGain, _OverBrightOffset, _OverBrightBlend;
    static const float3 LUM = float3(0.6125, 0.5154, 0.0721);   // LUMINANCE_VECTOR (filter.efx / visualtreatment.efx)

    // filter.efx's 4-tap box: in Unity's pixel-centre convention +-1 source texel samples the 2x2 blocks of a 4x4 box
    float4 Box4(float2 uv)
    {
        float2 d = _MainTex_TexelSize.xy;
        return (tex2D(_MainTex, uv + float2(-d.x, -d.y)) + tex2D(_MainTex, uv + float2(d.x, -d.y)) +
                tex2D(_MainTex, uv + float2(-d.x, d.y)) + tex2D(_MainTex, uv + float2(d.x, d.y))) * 0.25;
    }
    // filter.efx pass 3 with the weights of 0x8F9530 (horizontal) / 0x8F9500 (vertical)
    float4 Blur4(float2 uv, float2 d)
    {
        return (tex2D(_MainTex, uv + d * 1.5) + tex2D(_MainTex, uv - d * 1.5)) * (1.0 / 3.0)
             + (tex2D(_MainTex, uv + d * 3.0) + tex2D(_MainTex, uv - d * 3.0)) * (1.0 / 6.0);
    }
    ENDCG
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass // 0: VT bloom source A, filter.efx pass 4: rgb = 4x4 box, a = dot(rgb, LUMINANCE_VECTOR)
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            fixed4 frag (v2f_img i) : SV_Target
            {
                float3 c = Box4(i.uv).rgb;
                return fixed4(c, dot(c, LUM));
            }
            ENDCG
        }
        Pass // 1: horizontal blur (filter pass 3, 4 taps)
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            fixed4 frag (v2f_img i) : SV_Target { return Blur4(i.uv, float2(_MainTex_TexelSize.x, 0)); }
            ENDCG
        }
        Pass // 2: vertical blur
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            fixed4 frag (v2f_img i) : SV_Target { return Blur4(i.uv, float2(0, _MainTex_TexelSize.y)); }
            ENDCG
        }
        Pass // 3: the treatment (visualtreatment.efx technique 0)
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            fixed4 frag (v2f_img i) : SV_Target
            {
                float3 scene = tex2D(_MainTex, i.uv).rgb;
                float4 bloom = tex2D(_BloomTex, i.uv);
                float4 curve = tex2D(_SplineTex, float2(bloom.a, bloom.r));
                float lum = dot(scene, LUM);
                float3 desat = _Desaturation.x * scene + _Desaturation.y * lum;
                float3 colourBloom = curve.y * _ColourBloomIntensity * _ColourBloomTint.rgb;
                float black = curve.x * _BlackBloomIntensity.x + _BlackBloomIntensity.y;
                float3 gain;
                if (_HasGain > 0.5)
                {
                    gain = tex2D(_GainTex, i.uv).rgb;
                #if !defined(UNITY_COLORSPACE_GAMMA)
                    gain = LinearToGammaSpace(gain);   // [ours] a data texture: undo the sRGB decode of the runtime texture
                #endif
                }
                else
                {
                    float2 v = i.uv * 2 - 1;           // [ours] fallback without UVESVIGNETTE
                    gain = float3(0, saturate(dot(v, v) * 0.5), 1);
                }
                float3 o = scene * colourBloom + desat * black + gain.y * _Vignette;
                return fixed4(saturate(o * _Brightness * gain.z), 1);
            }
            ENDCG
        }
        Pass // 4: one motion-blur filter pass (FUN_006d2840): a full-screen quad with 4 texcoord sets of weight 0.25.
        {    // Tap k spans u in [-k*rect.x, 1 - k*rect.z] and v in [-k*rect.y, 1 - k*rect.w] (D3D v runs down).
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            fixed4 frag (v2f_img i) : SV_Target
            {
                float2 uv = float2(i.uv.x, 1 - i.uv.y);
                float4 c = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float2 t = lerp(-k * _BlurRect.xy, 1 - k * _BlurRect.zw, uv);
                    c += tex2D(_MainTex, float2(t.x, 1 - t.y)) * 0.25;
                }
                return c;
            }
            ENDCG
        }
        Pass // 5: the filtered copy over the frame with D3DRS_BLENDFACTOR (BLENDFACTOR / INVBLENDFACTOR)
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            fixed4 frag (v2f_img i) : SV_Target
            {
                // the exe blends RGB only (ColorWriteMode 7 for the frame): the back-buffer alpha (the overbright mask,
                // drawn before the motion blur in eDisplayFrame) is not blurred
                fixed4 c = tex2D(_MainTex, i.uv);
                return fixed4(lerp(c.rgb, tex2D(_BlurTex, i.uv).rgb, _MotionAlpha), c.a);
            }
            ENDCG
        }
        Pass // 6: overbright source C, filter.efx pass 5: 4x4 box, rgb *= a (the back-buffer alpha mask)
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            fixed4 frag (v2f_img i) : SV_Target
            {
                float4 c = Box4(i.uv);
                return fixed4(c.rgb * c.a, c.a);
            }
            ENDCG
        }
        Pass // 7: overbright.efx pContrast (ps_1_1): t = sat(2 (C - offset)); rgb = t * dot(t, 0.5); a = 0
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            fixed4 frag (v2f_img i) : SV_Target
            {
                float3 t = saturate(2 * (tex2D(_MainTex, i.uv).rgb - _OverBrightOffset));
                return fixed4(t * dot(t, float3(0.5, 0.5, 0.5)), 0);
            }
            ENDCG
        }
        Pass // 8: the overbright add (sub_6D3B80: D3DBLEND_BLENDFACTOR / D3DBLEND_ONE, factor _OverBrightBlend)
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            fixed4 frag (v2f_img i) : SV_Target { return fixed4(tex2D(_MainTex, i.uv).rgb * _OverBrightBlend, 0); }
            ENDCG
        }
        // 9-11: the back-buffer alpha the overbright source reads (command buffer at BeforeImageEffects). The exe clears
        // the frame to 0x00000000 after Present (eDisplayFrame 0x6E7628), draws the world with ColorWriteMode 7 (RGB), the
        // tunnel glare quad white into alpha only (0x6DEFCE), then the sky dome's pass 0 again with ColorWriteMode 8 and
        // ZFUNC LESSEQUAL (0x6DF409, byte_982BAD limits skybox.efx to pass 0), so only visible sky (and glare) bloom.
        Pass // 9: clear the alpha (full-screen quad given in clip space)
        {
            ColorMask A
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert (float4 v : POSITION) : SV_POSITION { return float4(v.xy, 0.5, 1); }
            fixed4 frag () : SV_Target { return 0; }
            ENDCG
        }
        Pass // 10: the sky dome's alpha (skybox.efx pass 0 writes alpha 1); the same far-plane z as SkyBox.shader's SkyPos
        {
            ColorMask A
            ZTest LEqual
            Offset -1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert (float4 v : POSITION) : SV_POSITION
            {
                float4 p = UnityObjectToClipPos(v);
            #if UNITY_REVERSED_Z
                p.z = p.w * 1e-6;
            #else
                p.z = p.w * (1 - 1e-6);
            #endif
                return p;
            }
            fixed4 frag () : SV_Target { return 1; }
            ENDCG
        }
        Pass // 11: the tunnel glare quad (worldmin.efx, vertex colour 0xFFFFFFFF) into alpha; world-space mesh
        {
            ColorMask A
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert (float4 v : POSITION) : SV_POSITION { return mul(UNITY_MATRIX_VP, float4(v.xyz, 1)); }
            fixed4 frag () : SV_Target { return 1; }
            ENDCG
        }
    }
}
