// FEng multi-image (object type 12; blend state as UIBlend.shader): the base texture times the alpha of a mask texture. The mask has its own UV
// rectangle and turns about a pivot (in mask UV space) — how the HUD's arc meters fill (a half-circle arc under a
// rotated half-circle mask). _Rect = the base image's uvRect (x, y, w, h) so each fragment knows where it sits on
// the quad; _MaskRect = (u0, v0, u1, v1) as stored (v0 = top); _MaskRot = (pivot u, pivot v, angle in radians).
Shader "NFSMW/UIMultiImage"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Mask ("Mask", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Rect ("Base uvRect", Vector) = (0, 0, 1, 1)
        _MaskRect ("Mask UVs", Vector) = (0, 0, 1, 1)
        _MaskRot ("Mask pivot / angle", Vector) = (0.5, 0.5, 0, 0)
        _SrcBlend ("Src blend", Float) = 5
        _DstBlend ("Dst blend", Float) = 10
        _Cutoff ("Alpha test (0 = off)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend [_SrcBlend] [_DstBlend]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex, _Mask;
            fixed4 _Color;
            float4 _Rect, _MaskRect, _MaskRot;
            float _Cutoff;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                // where the fragment sits on the image, in stored-UV terms: s across, tv down from the top
                // (FEView's uvRect is (u0, v1, u1 - u0, v0 - v1), and stored rows are top first like Unity's v)
                float s = (i.uv.x - _Rect.x) / (abs(_Rect.z) > 1e-5 ? _Rect.z : 1e-5);
                float tv = (i.uv.y - (_Rect.y + _Rect.w)) / (abs(_Rect.w) > 1e-5 ? -_Rect.w : 1e-5);
                float2 m = float2(lerp(_MaskRect.x, _MaskRect.z, s), lerp(_MaskRect.y, _MaskRect.w, tv));
                // turn the mask about its pivot
                float sn = sin(_MaskRot.z), cs = cos(_MaskRot.z);
                float2 d = m - _MaskRot.xy;
                m = _MaskRot.xy + float2(d.x * cs - d.y * sn, d.x * sn + d.y * cs);
                fixed mask = (any(m < 0) || any(m > 1)) ? 0 : tex2D(_Mask, m).a;
                c.a *= mask;   // fe_mask.efx (IDI_FE_MASK_FX) ps_1_1: r0.w = t1.w * (t0 * v0).w
                if (_Cutoff > 0) clip(c.a - _Cutoff);
                return c;
            }
            ENDCG
        }
    }
}
