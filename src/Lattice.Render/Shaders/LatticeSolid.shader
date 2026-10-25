// Material key `solid`: an opaque fill, shaded by a fixed light from above so a solid ghost still reads its shape.
Shader "Lattice/Solid"
{
    Properties
    {
        _Color ("Colour", Color) = (0.25, 0.45, 1, 1)
        _LatticeOffsetFactor ("Depth offset factor", Float) = 0
        _LatticeOffsetUnits ("Depth offset units", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Opaque" "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite On
            ZTest LEqual
            Offset [_LatticeOffsetFactor], [_LatticeOffsetUnits]
            Cull Back

            CGPROGRAM
            #pragma vertex LatticeVert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ LATTICE_PROCEDURAL
            #pragma target 4.5
            #include "LatticeCommon.cginc"

            fixed4 frag(LatticeVaryings i) : SV_Target
            {
                float3 light = normalize(float3(0.3, 0.85, 0.45));
                float shade = 0.55 + 0.45 * saturate(dot(normalize(i.worldNormal), light));
                fixed4 colour = fixed4(_Color.rgb * shade, 1);
                colour = LatticeApplyPattern(colour, i.worldPos);
                colour = LatticeApplyBeyond(colour, i.worldPos);
                clip(colour.a - 0.5);
                return fixed4(colour.rgb, 1);
            }
            ENDCG
        }
    }
}
