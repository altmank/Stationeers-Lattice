// Material key `ghost`: the translucent ghost fill, unlit, with a rim that brightens toward the silhouette. It writes
// no depth, so everything drawn after ghosts (overlays, see-through, labels, a consumer's own overlays) tests depth
// against the opaque world only, never against another ghost. Back faces are culled, so a ghost shows its front faces.
Shader "Lattice/Ghost"
{
    Properties
    {
        _Color ("Colour", Color) = (0.25, 0.45, 1, 0.3)
        _RimPower ("Rim power", Range(0.5, 8)) = 3
        _RimStrength ("Rim strength", Range(0, 2)) = 0.8
        _LatticeOffsetFactor ("Depth offset factor", Float) = 0
        _LatticeOffsetUnits ("Depth offset units", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "FILL"
            ZWrite Off
            ZTest LEqual
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
            float _RimStrength;

            fixed4 frag(LatticeVaryings i) : SV_Target
            {
                float rim = LatticeRim(i.worldPos, i.worldNormal, _RimPower) * _RimStrength;
                fixed4 colour = _Color;
                colour.rgb = saturate(colour.rgb * (1 + rim));
                colour.a = saturate(colour.a * (1 + rim));
                colour = LatticeApplyPattern(colour, i.worldPos);
                return LatticeApplyBeyond(colour, i.worldPos);
            }
            ENDCG
        }
    }
}
