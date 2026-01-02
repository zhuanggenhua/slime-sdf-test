Shader "Revive/ConsumeDissolve"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        _Dissolve ("Dissolve", Range(0, 1)) = 0
        _NoiseScale ("Noise Scale", Range(0.1, 50)) = 12
        _EdgeWidth ("Edge Width", Range(0, 0.3)) = 0.06
        _EdgeColor ("Edge Color", Color) = (1, 0.6, 0.2, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float _Cutoff;

            float _Dissolve;
            float _NoiseScale;
            float _EdgeWidth;
            float4 _EdgeColor;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionHCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        float Hash21(float2 p)
        {
            p = frac(p * float2(123.34, 345.45));
            p += dot(p, p + 34.345);
            return frac(p.x * p.y);
        }

        Varyings vert(Attributes input)
        {
            Varyings o;
            o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
            o.uv = input.uv;
            return o;
        }

        half4 frag(Varyings input) : SV_Target
        {
            float2 uv = input.uv;
            float noise = Hash21(uv * max(0.1, _NoiseScale));

            float d = saturate(_Dissolve);
            clip(noise - d + 1e-4);

            half4 baseCol = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv) * _BaseColor;
            clip(baseCol.a - _Cutoff);

            float edgeWidth = max(1e-4, _EdgeWidth);
            float edgeMask = smoothstep(d, d + edgeWidth, noise);
            baseCol.rgb = lerp(_EdgeColor.rgb, baseCol.rgb, edgeMask);

            return baseCol;
        }
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
