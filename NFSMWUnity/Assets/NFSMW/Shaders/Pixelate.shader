// NIS face pixelation (FacePixelation + its PC pass FUN_006d49a0 in eDisplayFrame): a width x height rectangle in
// 640x480 screen units, centred on the perp's head, drawn from a copy of the frame. [?] The block size of the PC pass
// is not decoded; _Block (screen pixels per block, in 640x480 units) defaults to 8.
Shader "NFSMW/Pixelate"
{
    Properties
    {
        _Block ("Block size (640x480 px)", Float) = 8
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Overlay" "IgnoreProjector" = "True" }
        ZWrite Off
        ZTest Always
        Cull Off
        GrabPass { "_NisPixelGrab" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _NisPixelGrab;
            float4 _NisPixelGrab_TexelSize;
            float _Block;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float4 grab : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.grab = ComputeGrabScreenPos(o.pos);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float2 uv = i.grab.xy / i.grab.w;
                float2 block = _Block * float2(1.0 / 640.0, 1.0 / 480.0);
                uv = (floor(uv / block) + 0.5) * block;
                return tex2D(_NisPixelGrab, uv);
            }
            ENDCG
        }
    }
}
