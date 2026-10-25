// World shadows (NFSMW_RE/specs/world_render.md §13). With g_ShadowDetail [0x901830] > 0 (1 at the top performance
// preset 0x6C1990) and a card with depth textures ([0x982C08]: D24S8 on NVIDIA, DF16 on ATI), the render list
// (sub_6E0AB0, player view only) gives every draw of world / worldreflect / worldnormalmap / GlossyWindow / car a
// shadow technique by the view depth of its bounding-box centre while [0x8FAE68] (0.1 + 0.8 sun strength) > 0.1:
//   depth <= 100 m: shadowLevel L = [0x982C3C] (2: 4 taps), 100..400 m: max(L - 1, 1) / max(L - 2, 1) (1 tap),
//   beyond 400 m: technique 0 (no shadow).
// The shadow techniques multiply only the SUN terms (diffuse N.L and the specular) by
//   f = saturate(shadow + saturate((0.2 - v) * 5) + InvShadowStrength)
// (world.efx ps 0x9F84 1 tap, 0x8DC0 4 taps at +-0.5 texel; v = the shadow map's v, a fade at the map's far edge),
// InvShadowStrength = [0x982BA8] tunnel fade + 1 - [0x8FAE68] (as car.efx).
// Port: the shadow map is Unity's cascaded sun shadow map, copied by a light command buffer (Runtime/World/
// WorldShadows.cs) and sampled here with the cascade selection of Unity's split spheres [ours: the exe renders one
// 1024 x 1024 light-space perspective map, sub_6E44C0]; the map-edge fade becomes Unity's shadow distance fade [ours].
#ifndef MW_SHADOW_INCLUDED
#define MW_SHADOW_INCLUDED
#include "UnityCG.cginc"

UNITY_DECLARE_SHADOWMAP(_MW_SunShadowMap);
float _MW_SunShadowTexel;       // one texel of the shadow map atlas (1 / resolution)
float _MW_WorldShadows;         // 1: shadow techniques this frame (g_ShadowDetail > 0 and [0x8FAE68] > 0.1)
float _MW_SunShadowValid;       // 1 once this camera's sun shadow map exists (set by the light command buffer)
float _MW_InvShadowStrength;    // [0x982BA8] + 1 - [0x8FAE68]

// shadowLevel of a draw (sub_6E0AB0 0x6E0CC5..0x6E0DBA, L = 2): 2 near, 1 to 400 m, 0 beyond. centre = the object origin.
float MWShadowLevel(float3 centre)
{
    if (_MW_WorldShadows * _MW_SunShadowValid < 0.5) return 0;
    float depth = -mul(UNITY_MATRIX_V, float4(centre, 1)).z;
    return depth > 400 ? 0 : (depth > 100 ? 1 : 2);
}

float4 MWCascadeWeights(float3 wpos)
{
    float3 c0 = wpos - unity_ShadowSplitSpheres[0].xyz;
    float3 c1 = wpos - unity_ShadowSplitSpheres[1].xyz;
    float3 c2 = wpos - unity_ShadowSplitSpheres[2].xyz;
    float3 c3 = wpos - unity_ShadowSplitSpheres[3].xyz;
    float4 d2 = float4(dot(c0, c0), dot(c1, c1), dot(c2, c2), dot(c3, c3));
    float4 w = float4(d2 < unity_ShadowSplitSqRadii);
    w.yzw = saturate(w.yzw - w.xyz);
    return w;
}

// the raw shadow-map tap(s) of a level (1 = lit), without the edge fade and InvShadowStrength: particles.efx's form
float MWSunShadowTap(float3 wpos, float level)
{
    if (level < 0.5) return 1;
    float4 w = MWCascadeWeights(wpos);
    if (dot(w, float4(1, 1, 1, 1)) < 0.5) return 1;
    float4 p = float4(wpos, 1);
    float3 c = mul(unity_WorldToShadow[0], p).xyz * w.x + mul(unity_WorldToShadow[1], p).xyz * w.y
             + mul(unity_WorldToShadow[2], p).xyz * w.z + mul(unity_WorldToShadow[3], p).xyz * w.w;
    if (level > 1.5)
    {
        float d = 0.5 * _MW_SunShadowTexel;
        return 0.25 * (UNITY_SAMPLE_SHADOW(_MW_SunShadowMap, float3(c.xy + float2(-d, -d), c.z))
                     + UNITY_SAMPLE_SHADOW(_MW_SunShadowMap, float3(c.xy + float2( d, -d), c.z))
                     + UNITY_SAMPLE_SHADOW(_MW_SunShadowMap, float3(c.xy + float2(-d,  d), c.z))
                     + UNITY_SAMPLE_SHADOW(_MW_SunShadowMap, float3(c.xy + float2( d,  d), c.z)));
    }
    return UNITY_SAMPLE_SHADOW(_MW_SunShadowMap, c);
}

// f for the sun terms; 1 = fully lit (level 0 or outside the shadow map)
float MWSunShadow(float3 wpos, float level)
{
    if (level < 0.5) return 1;
    float4 w = MWCascadeWeights(wpos);
    if (dot(w, float4(1, 1, 1, 1)) < 0.5) return 1;
    float4 p = float4(wpos, 1);
    float3 c = mul(unity_WorldToShadow[0], p).xyz * w.x + mul(unity_WorldToShadow[1], p).xyz * w.y
             + mul(unity_WorldToShadow[2], p).xyz * w.z + mul(unity_WorldToShadow[3], p).xyz * w.w;
    float s;
    if (level > 1.5)
    {
        float d = 0.5 * _MW_SunShadowTexel;
        s = 0.25 * (UNITY_SAMPLE_SHADOW(_MW_SunShadowMap, float3(c.xy + float2(-d, -d), c.z))
                  + UNITY_SAMPLE_SHADOW(_MW_SunShadowMap, float3(c.xy + float2( d, -d), c.z))
                  + UNITY_SAMPLE_SHADOW(_MW_SunShadowMap, float3(c.xy + float2(-d,  d), c.z))
                  + UNITY_SAMPLE_SHADOW(_MW_SunShadowMap, float3(c.xy + float2( d,  d), c.z)));
    }
    else s = UNITY_SAMPLE_SHADOW(_MW_SunShadowMap, c);
    float fade = saturate(distance(wpos, unity_ShadowFadeCenterAndType.xyz) * _LightShadowData.z + _LightShadowData.w);
    return saturate(s + fade + _MW_InvShadowStrength);
}
#endif
