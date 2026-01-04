Shader "Hidden/Revive/PurificationWorldVisual"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalRenderPipeline" }

        Pass
        {
            Name "PurificationWorldVisual"

            Cull Off
            ZWrite Off
            ZTest Always

            Stencil
            {
                Ref 1
                Comp NotEqual
                Pass Keep
                Fail Keep
                ZFail Keep
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_PurificationFieldTex);
            SAMPLER(sampler_PurificationFieldTex);

            TEXTURE2D(_LockedMaskTex);

            float4 _PurificationFieldParams; // (originX, originZ, sizeX, sizeZ)
            float _PurificationFieldRes;

            CBUFFER_START(UnityPerMaterial)
                float _RuinStrength;
                float _Desaturate;
                float _Darken;
            CBUFFER_END

            static const float kRawFarThreshold = 1e-5;

            float SamplePurify01(float2 worldXZ)
            {
                float2 originXZ = _PurificationFieldParams.xy;
                float2 sizeXZ = _PurificationFieldParams.zw;

                if (sizeXZ.x <= 1e-6 || sizeXZ.y <= 1e-6)
                    return 0.0;

                float2 uv = (worldXZ - originXZ) / sizeXZ;
                if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0)
                    return 0.0;

                return SAMPLE_TEXTURE2D(_PurificationFieldTex, sampler_PurificationFieldTex, uv).r;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;

                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                float depth = SampleSceneDepth(uv);

            #if !UNITY_REVERSED_Z
                depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
            #endif

                bool isBackground = abs(depth - UNITY_RAW_FAR_CLIP_VALUE) < kRawFarThreshold;
                if (isBackground)
                    return col;

                float3 positionWS = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);

                float purify01 = SamplePurify01(positionWS.xz);
                float lockedMask = SAMPLE_TEXTURE2D(_LockedMaskTex, sampler_PointClamp, uv).r;
                purify01 = lerp(purify01, 0.0, saturate(lockedMask));
                float ruin01 = saturate((1.0 - purify01) * _RuinStrength);

                float lum = Luminance(col.rgb);
                col.rgb = lerp(col.rgb, lum.xxx, saturate(_Desaturate * ruin01));
                col.rgb *= lerp(1.0, 1.0 - _Darken, ruin01);

                return col;
            }
            ENDHLSL
        }

        Pass
        {
            Name "PurificationLockedMask"

            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite Off
            ZTest LEqual
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float positionWSY : TEXCOORD0;
            };

            float4 _LockedFadeParams;
            float _LockedFade01;

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWSY = TransformObjectToWorld(input.positionOS.xyz).y;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float bottomY = _LockedFadeParams.x;
                float invHeight = _LockedFadeParams.y;
                float feather = max(_LockedFadeParams.z, 1e-4);

                float y01 = saturate((input.positionWSY - bottomY) * invHeight);
                float frontier = lerp(-feather, 1.0, saturate(_LockedFade01));
                float lockedMask = smoothstep(frontier, frontier + feather, y01);
                return half4(lockedMask, 0, 0, 0);
            }

            ENDHLSL
        }
    }
}
