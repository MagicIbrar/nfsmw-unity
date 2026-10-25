// The sun's visibility query (sub_6D0350, NFSMW_RE/specs/world_render.md §5): the exe draws a 32 x 32 (640x480 units)
// WHITE16X16_NOALPHA quad at the sun with fe.efx, colour writes off, Z test on at z 0.999 (sub_6C0160), inside a D3D
// occlusion query, and reads pixels / 1024 (clamped to 1) four frames later. Port (Runtime/Sky/SunRender.cs, issued
// from VisualTreatment's AfterForwardAlpha buffer): pass 0 clears the frame's alpha, pass 1 draws the quad into alpha
// with the same depth test, pass 2 counts the quad's lit pixels into a 1x1 float target that is read back
// asynchronously. The frame's alpha is rebuilt afterwards by the overbright mask (VisualTreatment passes 9-11).
Shader "NFSMW/SunQuery"
{
    Properties { _MainTex ("Frame", 2D) = "white" {} }
    SubShader
    {
        Cull Off
        ZWrite Off
        Pass // 0: alpha clear (full-screen quad given in clip space)
        {
            ZTest Always
            ColorMask A
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert (float4 v : POSITION) : SV_POSITION { return float4(v.xy, 0.5, 1); }
            fixed4 frag () : SV_Target { return 0; }
            ENDCG
        }
        Pass // 1: the query quad at z 0.999 (D3D depth), depth tested, alpha 1 where it passes
        {
            ZTest LEqual
            ColorMask A
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert (float4 v : POSITION) : SV_POSITION
            {
            #if defined(UNITY_REVERSED_Z)
                float4 p = float4(v.xy, 1 - 0.999, 1);
            #else
                float4 p = float4(v.xy, 0.999 * 2 - 1, 1);
            #endif
                p.y *= _ProjectionParams.x;
                return p;
            }
            fixed4 frag () : SV_Target { return 1; }
            ENDCG
        }
        Pass // 2: count: pixels of the quad's rectangle with alpha set, / 1024
        {
            ZTest Always
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MW_SunQueryRect;   // first / last pixel centres (x0, y0, x1, y1), v up
            float4 _MW_SunQuerySize;   // width, height, 1 / width, 1 / height
            float4 frag (v2f_img i) : SV_Target
            {
                int x0 = (int)_MW_SunQueryRect.x, y0 = (int)_MW_SunQueryRect.y;
                int x1 = min((int)_MW_SunQueryRect.z, x0 + 511), y1 = min((int)_MW_SunQueryRect.w, y0 + 511);
                float n = 0;
                [loop] for (int y = y0; y <= y1; y++)
                    [loop] for (int x = x0; x <= x1; x++)
                    {
                        float2 uv = (float2(x, y) + 0.5) * _MW_SunQuerySize.zw;
                        n += tex2Dlod(_MainTex, float4(uv, 0, 0)).a > 0.5 ? 1 : 0;
                    }
                return float4(n / 1024.0, 0, 0, 1);
            }
            ENDCG
        }
    }
}
