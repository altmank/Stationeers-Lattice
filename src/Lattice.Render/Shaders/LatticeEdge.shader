// Material key `edge`: the outline, as an inverted hull pushed out along the normal by a width in pixels. It reads
// nothing from the mesh on the CPU, so it works on meshes the game keeps unreadable. Dashed and dotted lines are cut
// by world-distance stripes. The hull reads _Color, or _LatticeEdgeColor under LATTICE_EDGE_OWN_COLOUR (Lattice/GhostEdged).
// Ghost fills write no depth, so the hull's back faces would show through the fill across the whole ghost. Three
// passes on the same draw mask the inside by stencil instead: the un-extruded mesh marks one stencil bit, the hull
// draws where that bit is clear, and the mesh clears the bit again, leaving the buffer as it was found.
// The bit (_LatticeStencilBit, 32 = bit 5) keeps clear of bit 7 (128), which the built-in deferred path marks on
// every non-background pixel (_StencilNonBackground, Unity 2022.3 player and unity_builtin_extra), and of bits 0-3,
// left to the deferred path's other uses. Read and write masks are that one bit only.
Shader "Lattice/Edge"
{
    Properties
    {
        _Color ("Colour", Color) = (0.25, 0.45, 1, 1)
        _LatticeOffsetFactor ("Depth offset factor", Float) = 0
        _LatticeOffsetUnits ("Depth offset units", Float) = 0
        [IntRange] _LatticeStencilBit ("Stencil bit value", Range(1, 255)) = 32
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        CGINCLUDE
        #include "LatticeCommon.cginc"

        LatticeVaryings maskVert(LatticeAttributes v)
        {
            return LatticeVert(v);
        }

        fixed4 maskFrag(LatticeVaryings i) : SV_Target
        {
            return 0;
        }
        ENDCG

        Pass
        {
            Name "MASK"
            ZWrite Off
            ZTest Always
            Cull Back
            ColorMask 0
            Stencil
            {
                Ref [_LatticeStencilBit]
                WriteMask [_LatticeStencilBit]
                Comp Always
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex maskVert
            #pragma fragment maskFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ LATTICE_PROCEDURAL
            #pragma target 4.5
            ENDCG
        }

        Pass
        {
            Name "HULL"
            ZWrite Off
            ZTest LEqual
            Offset [_LatticeOffsetFactor], [_LatticeOffsetUnits]
            Cull Front
            Blend SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref [_LatticeStencilBit]
                ReadMask [_LatticeStencilBit]
                Comp NotEqual
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ LATTICE_PROCEDURAL
            #pragma multi_compile_local __ LATTICE_EDGE_OWN_COLOUR
            #pragma target 4.5

            #if defined(LATTICE_EDGE_OWN_COLOUR)
            #define LATTICE_EDGE_COLOUR _LatticeEdgeColor
            #else
            #define LATTICE_EDGE_COLOUR _Color
            #endif

            LatticeVaryings vert(LatticeAttributes v)
            {
                LatticeVaryings o;
                float4x4 objectToWorld = LatticeObjectToWorld(v);
                float3 world = mul(objectToWorld, v.vertex).xyz;
                float3 normal = LatticeWorldNormal(objectToWorld, v.normal);
                float4 clipPos = UnityWorldToClipPos(world);
                float2 clipNormal = mul((float3x3)UNITY_MATRIX_VP, normal).xy;
                float len = max(length(clipNormal), 1e-5);
                float width = max(_LatticeEdge.y, 1);
                clipPos.xy += clipNormal / len * width * 2 / _ScreenParams.xy * clipPos.w;
                o.pos = clipPos;
                o.worldPos = world;
                o.worldNormal = normal;
                return o;
            }

            fixed4 frag(LatticeVaryings i) : SV_Target
            {
                float style = _LatticeEdge.x;
                float along = frac((i.worldPos.x + i.worldPos.y + i.worldPos.z) * 4);
                float keep = style < 0.5 ? 1 : (style < 1.5 ? step(along, 0.6) : step(along, 0.25));
                clip(keep - 0.5);
                return LatticeApplyBeyond(LATTICE_EDGE_COLOUR, i.worldPos);
            }
            ENDCG
        }

        Pass
        {
            Name "UNMASK"
            ZWrite Off
            ZTest Always
            Cull Back
            ColorMask 0
            Stencil
            {
                Ref 0
                WriteMask [_LatticeStencilBit]
                Comp Always
                Pass Zero
            }

            CGPROGRAM
            #pragma vertex maskVert
            #pragma fragment maskFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ LATTICE_PROCEDURAL
            #pragma target 4.5
            ENDCG
        }
    }
}
