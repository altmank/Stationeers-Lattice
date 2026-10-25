// Material key `faded`: for a consumer's faded built pieces, drawn at the faded-world draw order. Translucent and
// instanced, reads _Color and the clip globals, and writes no depth in either mode, so a ghost behind a faded wall
// draws over it and see-through does not fire behind it. Lattice ships it and fades nothing itself.
// Modes are material state (keywords), not property-block values: a consumer makes its own copy of the material per
// mode and per texture.
// - Default: _MainTex times _Color.
// - LATTICE_TEXTURE_ARRAY: the game's built pieces take their texture from a texture array (the material's main
//   texture is a Texture2DArray, CustomColorMapping.cs:35) indexed per instance by _DiffuseIndex (Thing.cs:676, set by
//   RenderBatch.cs:182 with SetFloatArray). Put the game material's main texture in _MainTexArray on the copy; the
//   game batch's property block then works unchanged.
// - LATTICE_DITHER: screen-door dithered by _Color's alpha instead of blended. It still writes no depth.
// - _LatticeFade: a per-instance opacity multiplier above 0 (0 or unset reads as 1), set with SetFloatArray for a
//   per-piece fade.
Shader "Lattice/Faded"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 1, 1, 0.3)
        _LatticeBeyond ("Beyond the clip: mode, dim", Vector) = (0, 1, 0, 0)
        _MainTex ("Texture", 2D) = "white" {}
        _MainTexArray ("Texture array", 2DArray) = "" {}
        [Toggle(LATTICE_DITHER)] _Dither ("Dithered", Float) = 0
        [Toggle(LATTICE_TEXTURE_ARRAY)] _UseArray ("Texture array", Float) = 0
        _LatticeOffsetFactor ("Depth offset factor", Float) = 0
        _LatticeOffsetUnits ("Depth offset units", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent-10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Offset [_LatticeOffsetFactor], [_LatticeOffsetUnits]
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ LATTICE_DITHER
            #pragma multi_compile_local __ LATTICE_TEXTURE_ARRAY
            #pragma target 3.5
            #pragma require 2darray
            #include "LatticeCommon.cginc"

        #if defined(LATTICE_TEXTURE_ARRAY)
            UNITY_DECLARE_TEX2DARRAY(_MainTexArray);
        #else
            sampler2D _MainTex;
        #endif
            float4 _MainTex_ST;

            UNITY_INSTANCING_BUFFER_START(LatticeFadedProps)
                UNITY_DEFINE_INSTANCED_PROP(float, _DiffuseIndex)
                UNITY_DEFINE_INSTANCED_PROP(float, _LatticeFade)
            UNITY_INSTANCING_BUFFER_END(LatticeFadedProps)

            struct FadedAttributes
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct FadedVaryings
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float4 screen : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            FadedVaryings vert(FadedAttributes v)
            {
                FadedVaryings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityWorldToClipPos(world);
                o.worldPos = world;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            fixed4 frag(FadedVaryings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
            #if defined(LATTICE_TEXTURE_ARRAY)
                fixed4 texel = UNITY_SAMPLE_TEX2DARRAY(_MainTexArray, float3(i.uv, UNITY_ACCESS_INSTANCED_PROP(LatticeFadedProps, _DiffuseIndex)));
            #else
                fixed4 texel = tex2D(_MainTex, i.uv);
            #endif
                float3 light = normalize(float3(0.3, 0.85, 0.45));
                float shade = 0.6 + 0.4 * saturate(dot(normalize(i.worldNormal), light));
                fixed4 colour = texel * _Color;
                colour.rgb *= shade;
                // An unset per-instance multiplier reads 0: treat it as 1 (fully the material's colour).
                float fade = UNITY_ACCESS_INSTANCED_PROP(LatticeFadedProps, _LatticeFade);
                colour.a *= fade > 0 ? fade : 1;
                colour = LatticeApplyBeyond(colour, i.worldPos);
            #if defined(LATTICE_DITHER)
                const float4x4 bayer = float4x4(
                    0.0 / 16, 8.0 / 16, 2.0 / 16, 10.0 / 16,
                    12.0 / 16, 4.0 / 16, 14.0 / 16, 6.0 / 16,
                    3.0 / 16, 11.0 / 16, 1.0 / 16, 9.0 / 16,
                    15.0 / 16, 7.0 / 16, 13.0 / 16, 5.0 / 16);
                uint2 pixel = (uint2)(i.screen.xy / i.screen.w * _ScreenParams.xy) % 4;
                clip(colour.a - bayer[pixel.x][pixel.y] - 0.001);
                colour.a = 1;
            #endif
                return colour;
            }
            ENDCG
        }
    }
}
