// Lattice's internal fill-and-edge material (not a contract key): one draw call draws a ghost's fill, then its edge's
// three stencil passes, so a look with both costs one call per part, not two. The passes are the ones of
// Lattice/Ghost and Lattice/Edge; the material enables LATTICE_EDGE_OWN_COLOUR, so the fill reads _Color and the edge
// _LatticeEdgeColor. Lattice uses it only while neither `ghost` nor `edge` is overridden.
Shader "Lattice/GhostEdged"
{
    Properties
    {
        _Color ("Colour", Color) = (0.25, 0.45, 1, 0.3)
        _LatticeEdgeColor ("Edge colour", Color) = (0.25, 0.45, 1, 1)
        _RimPower ("Rim power", Range(0.5, 8)) = 3
        _RimStrength ("Rim strength", Range(0, 2)) = 0.8
        _LatticeOffsetFactor ("Depth offset factor", Float) = 0
        _LatticeOffsetUnits ("Depth offset units", Float) = 0
        [IntRange] _LatticeStencilBit ("Stencil bit value", Range(1, 255)) = 32
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        UsePass "Lattice/Ghost/FILL"
        UsePass "Lattice/Edge/MASK"
        UsePass "Lattice/Edge/HULL"
        UsePass "Lattice/Edge/UNMASK"
    }
}
