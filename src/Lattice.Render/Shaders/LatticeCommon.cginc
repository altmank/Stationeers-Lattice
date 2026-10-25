// The material contract's shader side (Lattice.Api MaterialKeys): every name Lattice sets, and the clip, pattern and
// see-through helpers Lattice's shaders share. A consumer's own shader may include this file to read the same names.
#ifndef LATTICE_COMMON_INCLUDED
#define LATTICE_COMMON_INCLUDED

#include "UnityCG.cginc"

// Set per draw call through a MaterialPropertyBlock: one style per batch, never per instance.
fixed4 _Color;               // final colour and opacity, eases, effects and dim applied on the CPU
float _LatticePattern;       // 0 none, 1 hatch, 2 cross-hatch, 3 dots
fixed4 _LatticePatternColor;
float4 _LatticeEffect;       // kind (0 steady, 1 pulse, 2 blink), period s, low or duty, phase s
float _LatticeTime;          // seconds, unscaled
float4 _LatticeSeeThrough;   // strength 0..1, range m
float4 _LatticeEdge;         // line style (0 solid, 1 dashed, 2 dotted), width px
fixed4 _LatticeEdgeColor;    // the edge colour when one material draws fill and edge (keyword LATTICE_EDGE_OWN_COLOUR)

// Lattice's own procedural draws (keyword LATTICE_PROCEDURAL, Graphics.DrawMeshInstancedProcedural): a draw group's
// instance matrices in a buffer written only when the group is rebuilt, and the part's matrix in its piece's space.
// Without the keyword the shader draws instanced (DrawMeshInstanced), as a consumer's override or Material(key) does.
#if defined(LATTICE_PROCEDURAL)
StructuredBuffer<float4x4> _LatticeInstances;
float4x4 _LatticeLocal;
#define LATTICE_INSTANCE_INPUT uint latticeInstance : SV_InstanceID;
#else
#define LATTICE_INSTANCE_INPUT UNITY_VERTEX_INPUT_INSTANCE_ID
#endif

// Globals, set once a frame before any camera renders and never reset. A per-draw _LatticeBeyond overrides the
// global one for Lattice's own batches (a style's BeyondClip); the global is the viewer's default for other shaders.
float _LatticeClipMode;      // 0 none, 1 plane, 2 box
float4 _LatticeClipPlane;    // xyz unit normal, w = dot(normal, a point on the plane); beyond = dot(normal, p) > w
float4 _LatticeClipMin;      // box: beyond = outside [min, max]
float4 _LatticeClipMax;
float4 _LatticeBeyond;       // mode (0 drawn, 1 hidden, 2 dim, 3 wire), dim fraction 0..1

struct LatticeAttributes
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    LATTICE_INSTANCE_INPUT
};

struct LatticeVaryings
{
    float4 pos : SV_POSITION;
    float3 worldPos : TEXCOORD0;
    float3 worldNormal : TEXCOORD1;
};

// The instance's object-to-world matrix: from the group's buffer times the part's matrix, or Unity's instancing.
float4x4 LatticeObjectToWorld(LatticeAttributes v)
{
#if defined(LATTICE_PROCEDURAL)
    return mul(_LatticeInstances[v.latticeInstance], _LatticeLocal);
#else
    UNITY_SETUP_INSTANCE_ID(v);
    return unity_ObjectToWorld;
#endif
}

// A normal into world space. Pieces are turned and placed, never scaled unevenly, so the matrix itself serves.
float3 LatticeWorldNormal(float4x4 objectToWorld, float3 normal)
{
    return normalize(mul((float3x3)objectToWorld, normal));
}

LatticeVaryings LatticeVert(LatticeAttributes v)
{
    LatticeVaryings o;
    float4x4 objectToWorld = LatticeObjectToWorld(v);
    float3 world = mul(objectToWorld, v.vertex).xyz;
    o.worldPos = world;
    o.pos = UnityWorldToClipPos(world);
    o.worldNormal = LatticeWorldNormal(objectToWorld, v.normal);
    return o;
}

// 1 where a point is beyond the viewer's clip, 0 inside it or when no clip is in force.
float LatticeBeyondAmount(float3 worldPos)
{
    if (_LatticeClipMode < 0.5)
        return 0;
    if (_LatticeClipMode < 1.5)
        return step(_LatticeClipPlane.w, dot(_LatticeClipPlane.xyz, worldPos));
    float3 outside = step(worldPos, _LatticeClipMin.xyz) + step(_LatticeClipMax.xyz, worldPos);
    return saturate(outside.x + outside.y + outside.z);
}

// The style's beyond-clip look on a fill: hidden discards, dim and wire scale opacity by the dim fraction (the wire
// itself is the wire material's pass).
fixed4 LatticeApplyBeyond(fixed4 colour, float3 worldPos)
{
    float beyond = LatticeBeyondAmount(worldPos);
    float mode = _LatticeBeyond.x;
    clip(0.5 - beyond * step(0.5, mode) * step(mode, 1.5));
    colour.a *= lerp(1, _LatticeBeyond.y, beyond * step(1.5, mode));
    return colour;
}

// Hatch, cross-hatch or dots in world space (8 per metre), so a pattern stays put on a ghost as the camera moves.
float LatticePatternMask(float3 worldPos)
{
    const float density = 8;
    if (_LatticePattern < 0.5)
        return 0;
    if (_LatticePattern > 2.5)
    {
        float3 cell = frac(worldPos * density) - 0.5;
        float d = length(cell);
        float aa = fwidth(d);
        return 1 - smoothstep(0.22 - aa, 0.22 + aa, d);
    }
    float a = frac((worldPos.x + worldPos.y + worldPos.z) * density);
    float lineA = 1 - smoothstep(0.2, 0.2 + fwidth(a) * 1.5, abs(a - 0.5));
    if (_LatticePattern < 1.5)
        return lineA;
    float b = frac((worldPos.x - worldPos.y + worldPos.z) * density);
    float lineB = 1 - smoothstep(0.2, 0.2 + fwidth(b) * 1.5, abs(b - 0.5));
    return max(lineA, lineB);
}

fixed4 LatticeApplyPattern(fixed4 colour, float3 worldPos)
{
    float mask = LatticePatternMask(worldPos) * _LatticePatternColor.a;
    colour.rgb = lerp(colour.rgb, _LatticePatternColor.rgb, mask);
    colour.a = max(colour.a, mask * _LatticePatternColor.a);
    return colour;
}

// How strongly a surface faces away from the viewer: 0 facing, 1 at the silhouette.
float LatticeRim(float3 worldPos, float3 worldNormal, float power)
{
    float3 toCamera = normalize(_WorldSpaceCameraPos - worldPos);
    return pow(1 - saturate(abs(dot(normalize(worldNormal), toCamera))), power);
}

#endif
