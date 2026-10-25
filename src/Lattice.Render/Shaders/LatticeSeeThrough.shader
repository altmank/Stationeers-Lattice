// Material key `see-through`: the part of a thing hidden behind walls and terrain, drawn faintly. Draws only where
// something nearer is already in the depth buffer (ZTest Greater), fading out over the style's range.
Shader "Lattice/SeeThrough"
{
    Properties
    {
        _Color ("Colour", Color) = (0.25, 0.45, 1, 0.3)
        _LatticeBeyond ("Beyond the clip: mode, dim", Vector) = (0, 1, 0, 0)
        _LatticeSeeThrough ("See-through: strength, range m", Vector) = (0.6, 80, 0, 0)
        _LatticeOffsetFactor ("Depth offset factor", Float) = 0
        _LatticeOffsetUnits ("Depth offset units", Float) = 0
        _RimPower ("Rim power", Range(0.5, 8)) = 2
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite Off
            ZTest Greater
            Offset [_LatticeOffsetFactor], [_LatticeOffsetUnits]
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex LatticeVert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ LATTICE_PROCEDURAL
            #pragma target 4.5
            #include "LatticeCommon.cginc"

            float _RimPower;

            fixed4 frag(LatticeVaryings i) : SV_Target
            {
                float strength = _LatticeSeeThrough.x;
                float range = max(_LatticeSeeThrough.y, 0.01);
                float distance = length(_WorldSpaceCameraPos - i.worldPos);
                float rim = LatticeRim(i.worldPos, i.worldNormal, _RimPower);
                fixed4 colour = _Color;
                colour.a *= strength * saturate(1 - distance / range) * (0.35 + 0.65 * rim);
                clip(colour.a - 0.002);
                return LatticeApplyBeyond(colour, i.worldPos);
            }
            ENDCG
        }
    }
}
