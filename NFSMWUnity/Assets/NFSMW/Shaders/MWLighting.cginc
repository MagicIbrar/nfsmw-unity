// Shared vertex lighting of speed.exe's world effect (fx\world.efx technique 0 "world", vs_1_1, disassembled):
//   c4 DiffuseColour, c5 AmbientColour, c6 LocalLightVec, c8 Fog_Br_Plus_Bm, c9 Fog_One_Over_BrBm, c10 Fog_Const_1,
//   c11 Fog_Const_2, c12 Fog_Const_3, c13 FogValue, c14 LocalEyePos; def c15 (1, 0, 2500, log2 e)
//   oD1 = sat(L.N) * DiffuseColour                                          (sun, not extinguished)
//   d   = clamp(|v - eye| * FogValue, 0, 2500)
//   ext = 2^(-d * (Br + Bm) * log2 e)
//   oT2 = clamp((Const_1 * (1 + cos^2) + Const_2 * Const_3.x * (Const_3.z cos + Const_3.y)^-1.5) * One_Over_BrBm
//               * (1 - ext) * Const_3.w, 0, 1),  cos = L . normalize(eye - v)
//   oD0 = (min(ext, 1) * AmbientColour.rgb * AmbientColour.w * vertex colour.rgb, vertex colour.a)
// The CPU side (TimeOfDay, fog constants) is Runtime/Sky/TimeOfDayDriver.cs; see NFSMW_RE/specs/sky_lighting.md.
#ifndef MW_LIGHTING_INCLUDED
#define MW_LIGHTING_INCLUDED
#include "UnityCG.cginc"

float4 _MW_LightDir;        // xyz: normalised direction toward the sun (Unity world space); TimeOfDay +0x180
float4 _MW_Diffuse;         // DiffuseColour (0x6C6300)
float4 _MW_Ambient;         // AmbientColour, w = 4 (0x6C6450)
float4 _MW_Specular;        // SpecularColour (0x6D6AA0)
float4 _MW_FogBrBm;         // Fog_Br_Plus_Bm
float4 _MW_FogOneOverBrBm;  // Fog_One_Over_BrBm
float4 _MW_FogConst1;       // Fog_Const_1 = Br * 3/(16 pi)
float4 _MW_FogConst2;       // Fog_Const_2 = Bm / (4 pi)
float4 _MW_FogConst3;       // Fog_Const_3 = ((1-g)^2, 1+g^2, -2g, in-scatter)
float _MW_FogValue;         // FogValue = FogDistanceScale
float _MW_Enabled;

struct MWLight { float3 sun; float4 prelit; float3 inscatter; };

// wpos/n in world space; vcol = the vertex colour (D3DCOLOR, 0..1)
MWLight MWLightVertex(float3 wpos, float3 n, float4 vcol)
{
    MWLight o;
    float3 L = _MW_LightDir.xyz;
    o.sun = saturate(saturate(dot(L, n)) * _MW_Diffuse.rgb);                 // oD1 (colour outputs clamp)
    float3 toEye = _WorldSpaceCameraPos - wpos;
    float dist = length(toEye);
    float3 V = toEye * rsqrt(max(dot(toEye, toEye), 1e-12));
    float c = dot(L, V);
    float d = clamp(dist * _MW_FogValue, 0, 2500);
    float hg = pow(rsqrt(_MW_FogConst3.z * c + _MW_FogConst3.y), 3) * _MW_FogConst3.x;
    float3 ext = exp2(-d * _MW_FogBrBm.xyz * 1.44269502);
    float3 s = (_MW_FogConst1.xyz * (c * c + 1) + _MW_FogConst2.xyz * hg) * _MW_FogOneOverBrBm.xyz * (1 - ext);
    o.inscatter = min(max(s * _MW_FogConst3.w, 0), 1);                        // oT2
    o.prelit = saturate(float4(min(ext, 1) * _MW_Ambient.rgb * _MW_Ambient.w * vcol.rgb, vcol.a));   // oD0
    return o;
}

// The same fog block on its own (worldreflect / worldnormalmap / tree / GlossyWindow vertex shaders, world_render.md §2):
// ext = min(extinction, 1) (oT6 / oT5 / folded into oD0), inscatter = the clamped in-scatter (oT5 / oT4 / oT2 / oT3)
void MWFogTerms(float3 wpos, out float3 ext, out float3 inscatter)
{
    float3 L = _MW_LightDir.xyz;
    float3 toEye = _WorldSpaceCameraPos - wpos;
    float dist = length(toEye);
    float3 V = toEye * rsqrt(max(dot(toEye, toEye), 1e-12));
    float c = dot(L, V);
    float d = clamp(dist * _MW_FogValue, 0, 2500);
    float hg = pow(rsqrt(_MW_FogConst3.z * c + _MW_FogConst3.y), 3) * _MW_FogConst3.x;
    float3 e = exp2(-d * _MW_FogBrBm.xyz * 1.44269502);
    float3 s = (_MW_FogConst1.xyz * (c * c + 1) + _MW_FogConst2.xyz * hg) * _MW_FogOneOverBrBm.xyz * (1 - e);
    inscatter = min(max(s * _MW_FogConst3.w, 0), 1);
    ext = min(e, 1);
}

// Normal / specular maps hold raw data: in a Linear-colour-space project the sRGB-flagged runtime textures come back
// linearised, so undo that for data maps (no-op in Gamma colour space) [ours]
float3 MWRawData(float3 t)
{
#if defined(UNITY_COLORSPACE_GAMMA)
    return t;
#else
    return LinearToGammaSpace(t);
#endif
}
#endif
