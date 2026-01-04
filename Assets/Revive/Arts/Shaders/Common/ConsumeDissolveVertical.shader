Shader "Revive/ConsumeDissolveVertical"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        _Dissolve ("Dissolve", Range(0, 1)) = 0
        _DissolveInvert ("Dissolve Invert", Range(0, 1)) = 0
        _DissolveMinY ("Dissolve Min Y", Float) = 0
        _DissolveMaxY ("Dissolve Max Y", Float) = 1

        _NoiseScale ("Noise Scale", Range(0.1, 50)) = 12
        _NoiseStrength ("Noise Strength", Range(0, 0.5)) = 0.08
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
            float4 _BaseMap_ST;

            float _Dissolve;
            float _DissolveInvert;
            float _DissolveMinY;
            float _DissolveMaxY;

            float _NoiseScale;
            float _NoiseStrength;
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
            float3 positionWS : TEXCOORD1;
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
            o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
            return o;
        }

        half4 frag(Varyings input) : SV_Target
        {
            float d = saturate(_Dissolve);
            float inv = step(0.5, _DissolveInvert);

            float denom = max(1e-4, _DissolveMaxY - _DissolveMinY);
            float height01 = saturate((input.positionWS.y - _DissolveMinY) / denom);

            float noise = Hash21(input.positionWS.xz * max(0.1, _NoiseScale));
            float value = saturate(height01 + (noise - 0.5) * _NoiseStrength);

            float clipValue = lerp(value - d, (1.0 - d) - value, inv);
            clip(clipValue + 1e-4);

            half4 baseCol = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
            clip(baseCol.a - _Cutoff);

            float edgeWidth = max(1e-4, _EdgeWidth);
            float th = lerp(d, 1.0 - d, inv);

            float edgeMaskA = smoothstep(th, th + edgeWidth, value);
            float edgeMaskB = 1.0 - smoothstep(th - edgeWidth, th, value);
            float edgeMask = lerp(edgeMaskA, edgeMaskB, inv);

            baseCol.rgb = lerp(_EdgeColor.rgb, baseCol.rgb, edgeMask);
            return baseCol;
        }
        ENDHLSL

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 GetShadowPositionHClip(ShadowAttributes input)
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                positionCS = ApplyShadowClamping(positionCS);
                return positionCS;
            }

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.positionCS = GetShadowPositionHClip(input);
                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 baseCol = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                clip(baseCol.a - _Cutoff);

                float inv = step(0.5, _DissolveInvert);
                float d = saturate(_Dissolve);
                float th = lerp(d, 1.0 - d, inv);

                float denom = max(1e-4, _DissolveMaxY - _DissolveMinY);
                float height01 = saturate((input.positionWS.y - _DissolveMinY) / denom);
                float noise = Hash21(input.positionWS.xz * max(0.1, _NoiseScale));
                float value = saturate(height01 + (noise - 0.5) * _NoiseStrength);

                float clipValue = lerp(value - th, th - value, inv);
                clip(clipValue + 1e-4);

                return 0;
            }
            ENDHLSL
        }

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
