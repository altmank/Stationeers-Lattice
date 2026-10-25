// Material key `wire`: mesh edges, from barycentric coordinates a geometry shader gives each triangle. Shader model
// 4.0 (Direct3D 11). Also draws a thing whose style is Wire beyond the clip.
Shader "Lattice/Wire"
{
    Properties
    {
        _Color ("Colour", Color) = (0.25, 0.45, 1, 0.8)
        _LatticeOffsetFactor ("Depth offset factor", Float) = 0
        _LatticeOffsetUnits ("Depth offset units", Float) = 0
        _WireWidth ("Width (px)", Range(0.5, 4)) = 1.2
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Offset [_LatticeOffsetFactor], [_LatticeOffsetUnits]
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex LatticeVert
            #pragma geometry geom
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ LATTICE_PROCEDURAL
            #pragma target 4.5
            #include "LatticeCommon.cginc"

            float _WireWidth;

            struct WireVaryings
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 bary : TEXCOORD1;
            };

            [maxvertexcount(3)]
            void geom(triangle LatticeVaryings input[3], inout TriangleStream<WireVaryings> stream)
            {
                WireVaryings o;
                [unroll]
                for (int k = 0; k < 3; k++)
                {
                    o.pos = input[k].pos;
                    o.worldPos = input[k].worldPos;
                    o.bary = float3(k == 0, k == 1, k == 2);
                    stream.Append(o);
                }
            }

            fixed4 frag(WireVaryings i) : SV_Target
            {
                float3 d = fwidth(i.bary);
                float3 near = smoothstep(float3(0, 0, 0), d * _WireWidth, i.bary);
                float edge = 1 - min(near.x, min(near.y, near.z));
                clip(edge - 0.01);
                fixed4 colour = _Color;
                colour.a *= edge;
                // Beyond the clip, a wire is the look itself: shown at full strength unless the style hides it.
                float beyond = LatticeBeyondAmount(i.worldPos);
                clip(0.5 - beyond * step(0.5, _LatticeBeyond.x) * step(_LatticeBeyond.x, 1.5));
                return colour;
            }
            ENDCG
        }
    }
}
