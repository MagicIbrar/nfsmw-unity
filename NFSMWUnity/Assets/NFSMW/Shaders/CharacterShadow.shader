// The NIS character's blob shadow (CBasicCharacterAnimEntity::RenderEffects 0x449CF0): an ePoly between the feet,
// 1 cm above the entity's ground, vertex colour 0x80808080, texture CHARACTERSHADOW, drawn through eView::Render with
// the poly's default (alpha) blend [?: the blend state of that path is not decompiled].
Shader "NFSMW/CharacterShadow"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Offset -1, -2
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                return saturate(tex2D(_MainTex, i.uv) * i.color * 2);
            }
            ENDCG
        }
    }
}
