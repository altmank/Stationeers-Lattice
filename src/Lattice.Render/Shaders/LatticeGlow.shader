// Material key `glow`: an additive shell a little outside the surface, brightest at the silhouette. _Color's alpha is
// the glow's strength.
Shader "Lattice/Glow"
{
    Properties
    {
        _Color ("Colour", Color) = (0.3, 1, 0.4, 0.8)
        _LatticeBeyond ("Beyond the clip: mode, dim", Vector) = (0, 1, 0, 0)
        _LatticeOffsetFactor ("Depth offset factor", Float) = 0
        _LatticeOffsetUnits ("Depth offset units", Float) = 0
        _GlowOffset ("Shell offset (m)", Range(0, 0.1)) = 0.015
        _RimPower ("Rim power", Range(0.5, 8)) = 2.5
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Offset [_LatticeOffsetFactor], [_LatticeOffsetUnits]
            Cull Back
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ LATTICE_PROCEDURAL
            #pragma target 4.5
            #include "LatticeCommon.cginc"

            float _GlowOffset;
            float _RimPower;

            LatticeVaryings vert(LatticeAttributes v)
            {
                LatticeVaryings o;
                float4x4 objectToWorld = LatticeObjectToWorld(v);
                float3 normal = LatticeWorldNormal(objectToWorld, v.normal);
                float3 world = mul(objectToWorld, v.vertex).xyz + normal * _GlowOffset;
                o.pos = UnityWorldToClipPos(world);
                o.worldPos = world;
                o.worldNormal = normal;
                return o;
            }

            fixed4 frag(LatticeVaryings i) : SV_Target
            {
                float rim = LatticeRim(i.worldPos, i.worldNormal, _RimPower);
                fixed4 colour = LatticeApplyBeyond(fixed4(_Color.rgb, _Color.a), i.worldPos);
                return fixed4(colour.rgb * colour.a * rim, 0);
            }
            ENDCG
        }
    }
}
