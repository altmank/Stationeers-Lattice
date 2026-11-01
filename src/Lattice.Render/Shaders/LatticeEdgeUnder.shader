// Lattice's internal edge-with-under-line material (not a contract key): Lattice/Edge's passes with one more between
// the mask and the line: an unbroken hull _LatticeEdgeUnder.x pixels wide in _LatticeEdgeUnderColor, under the
// line's dashes. Lattice draws with it only for a look with an under-line while `edge` is not overridden, so an edge
// without one keeps Lattice/Edge's three passes.
Shader "Lattice/EdgeUnder"
{
    Properties
    {
        _Color ("Colour", Color) = (0.25, 0.45, 1, 1)
        _LatticeBeyond ("Beyond the clip: mode, dim", Vector) = (0, 1, 0, 0)
        _LatticeEdge ("Edge: line style, width px, dash m, speed m/s", Vector) = (0, 1, 0, 0)
        _LatticeEdgeColor ("Edge colour", Color) = (0.25, 0.45, 1, 1)
        _LatticeEdgeUnder ("Under-line: width px", Vector) = (0, 0, 0, 0)
        _LatticeEdgeUnderColor ("Under-line colour", Color) = (0, 0, 0, 1)
        _LatticeOffsetFactor ("Depth offset factor", Float) = 0
        _LatticeOffsetUnits ("Depth offset units", Float) = 0
        [IntRange] _LatticeStencilBit ("Stencil bit value", Range(1, 255)) = 32
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        UsePass "Lattice/Edge/MASK"

        Pass
        {
            Name "UNDER"
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
            #pragma target 4.5
            #include "LatticeCommon.cginc"

            LatticeVaryings vert(LatticeAttributes v)
            {
                return LatticeHullVert(v, _LatticeEdgeUnder.x);
            }

            fixed4 frag(LatticeVaryings i) : SV_Target
            {
                return LatticeApplyBeyond(_LatticeEdgeUnderColor, i.worldPos);
            }
            ENDCG
        }

        UsePass "Lattice/Edge/HULL"
        UsePass "Lattice/Edge/UNMASK"
    }
}
