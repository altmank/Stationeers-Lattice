// Material key `ants`: moving dashes along a consumer's own line mesh (an outline round built pieces), cut and moved
// by LatticeDash, the rule a ghost's dashed edge uses, so the two match. The mesh is triangles, two per segment from A
// to B, four vertices: POSITION this vertex's end (object space), TEXCOORD0 xyz the segment's other end (object
// space) and w the side (+1 or -1), TEXCOORD1.x the length along the line at this vertex's end in metres, which the
// dashes are cut by. The vertex is pushed sideways in screen space by half of _LatticeEdge.y pixels, so the line is
// that wide at any distance. Unity's line topology is one pixel wide on D3D11 and Vulkan, hence quads. An under-line
// is a second draw of the same mesh, first, as a wider solid line in its own colour.
Shader "Lattice/Ants"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 1, 1, 1)
        _LatticeBeyond ("Beyond the clip: mode, dim", Vector) = (0, 1, 0, 0)
        _LatticeEdge ("Line: style, width px, dash m, speed m/s", Vector) = (1, 3, 0, 0)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ANTS"
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 4.5
            #include "LatticeCommon.cginc"

            struct AntsAttributes
            {
                float4 vertex : POSITION;
                float4 other : TEXCOORD0;
                float2 along : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct AntsVaryings
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float along : TEXCOORD1;
            };

            AntsVaryings vert(AntsAttributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                AntsVaryings o;
                float3 here = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)).xyz;
                float3 there = mul(unity_ObjectToWorld, float4(v.other.xyz, 1)).xyz;
                float4 clipHere = UnityWorldToClipPos(here);
                float4 clipThere = UnityWorldToClipPos(there);
                float2 screenHere = clipHere.xy / max(abs(clipHere.w), 1e-5) * _ScreenParams.xy;
                float2 screenThere = clipThere.xy / max(abs(clipThere.w), 1e-5) * _ScreenParams.xy;
                float2 dir = screenThere - screenHere;
                dir = dot(dir, dir) > 1e-8 ? normalize(dir) : float2(1, 0);
                float2 side = float2(-dir.y, dir.x) * v.other.w;
                clipHere.xy += side * max(_LatticeEdge.y, 1) / _ScreenParams.xy * clipHere.w;
                o.pos = clipHere;
                o.worldPos = here;
                o.along = v.along.x;
                return o;
            }

            fixed4 frag(AntsVaryings i) : SV_Target
            {
                clip(LatticeDash(i.along, _LatticeEdge) - 0.5);
                return LatticeApplyBeyond(_Color, i.worldPos);
            }
            ENDCG
        }
    }
}
