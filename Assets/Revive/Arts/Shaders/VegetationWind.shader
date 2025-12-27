Shader "Revive/VegetationWind"
{
    Properties
    {
        [Header(Base)]
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        
        [Header(Alpha)]
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        
        [Header(Surface)]
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        
        [Header(Wind)]
        _WindStrength("Wind Strength", Float) = 0.3
        _WindSpeed("Wind Speed", Float) = 1.0
        _WindFrequency("Wind Frequency", Float) = 1.0
        _MaxHeight("Max Height", Float) = 1.0
        
        [Header(Growth)]
        _GrowthPhase("Growth Phase", Range(0.0, 1.0)) = 1.0
        _MinScale("Min Scale", Float) = 0.01
        
        [Header(Advanced)]
        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clipping", Float) = 1.0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull Mode", Float) = 0  // 0 = Off (Two Sided)
    }

    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        
        LOD 300
        Cull [_Cull]

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            
            // Unity defined keywords
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            
            // Shader keywords
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Textures and Samplers
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            // Per-Material Properties
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _Smoothness;
                half _Metallic;
                float _WindStrength;
                float _WindSpeed;
                float _WindFrequency;
                float _MaxHeight;
                float _GrowthPhase;
                float _MinScale;
            CBUFFER_END

            // Per-Instance Custom Data (Set by MaterialPropertyBlock)
            #ifdef UNITY_INSTANCING_ENABLED
                UNITY_INSTANCING_BUFFER_START(PerInstanceData)
                    UNITY_DEFINE_INSTANCED_PROP(float4, _CustomData)
                UNITY_INSTANCING_BUFFER_END(PerInstanceData)
            #endif

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float4 tangentOS    : TANGENT;
                float2 texcoord     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float3 positionWS   : TEXCOORD1;
                float3 normalWS     : TEXCOORD2;
                float fogFactor     : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Noise function for wind variation
            float SimpleNoise(float2 uv)
            {
                return frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453);
            }

            // Smooth noise
            float SmoothNoise(float2 uv)
            {
                float2 i = floor(uv);
                float2 f = frac(uv);
                f = f * f * (3.0 - 2.0 * f);
                
                float a = SimpleNoise(i);
                float b = SimpleNoise(i + float2(1.0, 0.0));
                float c = SimpleNoise(i + float2(0.0, 1.0));
                float d = SimpleNoise(i + float2(1.0, 1.0));
                
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Wind calculation
            float3 ApplyWind(float3 positionWS, float3 positionOS, float windStrength, float windSpeed, float windFrequency, float maxHeight, float2 customOffset)
            {
                // Calculate height factor (only top vertices are affected)
                float heightFactor = saturate(positionOS.y / maxHeight);
                heightFactor = heightFactor * heightFactor; // Square for smoother falloff
                
                // Generate wind noise based on world position and custom offset
                float2 windUV = (positionWS.xz + customOffset) * windFrequency;
                float noise1 = SmoothNoise(windUV * 0.5);
                float noise2 = SmoothNoise(windUV * 1.3 + float2(3.7, 2.1));
                
                // Combine noises for more natural movement
                float windNoise = noise1 * 0.7 + noise2 * 0.3;
                
                // Time-based animation
                float windPhase = _Time.y * windSpeed + windNoise * 6.28318;
                float windWave = sin(windPhase);
                
                // Secondary wave for more complex motion
                float windPhase2 = _Time.y * windSpeed * 0.7 + windNoise * 3.14159;
                float windWave2 = sin(windPhase2) * 0.5;
                
                // Calculate bend amount
                float bendAmount = (windWave + windWave2) * windStrength * heightFactor;
                
                // Apply wind offset
                float3 windOffset = float3(bendAmount, 0, bendAmount * 0.6);
                
                return positionWS + windOffset;
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Get custom data from instancing
                float growthPhase = _GrowthPhase;
                float2 customOffset = float2(0, 0);
                
                #ifdef UNITY_INSTANCING_ENABLED
                    float4 customData = UNITY_ACCESS_INSTANCED_PROP(PerInstanceData, _CustomData);
                    growthPhase = customData.x; // x: Growth phase (0-1)
                    customOffset = customData.yz; // yz: World position offset for wind noise
                #endif

                // Apply growth scaling
                float growthScale = lerp(_MinScale, 1.0, growthPhase);
                float3 scaledPositionOS = input.positionOS.xyz * growthScale;
                
                // Transform to world space
                float3 positionWS = TransformObjectToWorld(scaledPositionOS);
                
                // Apply wind effect
                positionWS = ApplyWind(positionWS, input.positionOS.xyz, _WindStrength, _WindSpeed, _WindFrequency, _MaxHeight, customOffset);
                
                // Output
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Sample textures
                half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 baseColor = albedoAlpha * _BaseColor;

                // Alpha clipping
                #ifdef _ALPHATEST_ON
                    clip(baseColor.a - _Cutoff);
                #endif

                // Prepare lighting data
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalize(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                
                // Prepare surface data
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = baseColor.rgb;
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = float3(0, 0, 1);
                surfaceData.emission = 0;
                surfaceData.occlusion = 1.0;
                surfaceData.alpha = baseColor.a;
                surfaceData.clearCoatMask = 0;
                surfaceData.clearCoatSmoothness = 0;

                // Calculate lighting
                half4 color = UniversalFragmentPBR(inputData, surfaceData);

                // Apply fog
                color.rgb = MixFog(color.rgb, inputData.fogCoord);

                return color;
            }
            ENDHLSL
        }

        // Shadow Caster Pass
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _Smoothness;
                half _Metallic;
                float _WindStrength;
                float _WindSpeed;
                float _WindFrequency;
                float _MaxHeight;
                float _GrowthPhase;
                float _MinScale;
            CBUFFER_END

            #ifdef UNITY_INSTANCING_ENABLED
                UNITY_INSTANCING_BUFFER_START(PerInstanceData)
                    UNITY_DEFINE_INSTANCED_PROP(float4, _CustomData)
                UNITY_INSTANCING_BUFFER_END(PerInstanceData)
            #endif

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float2 texcoord     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv           : TEXCOORD0;
                float4 positionCS   : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float SimpleNoise(float2 uv)
            {
                return frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453);
            }

            float SmoothNoise(float2 uv)
            {
                float2 i = floor(uv);
                float2 f = frac(uv);
                f = f * f * (3.0 - 2.0 * f);
                
                float a = SimpleNoise(i);
                float b = SimpleNoise(i + float2(1.0, 0.0));
                float c = SimpleNoise(i + float2(0.0, 1.0));
                float d = SimpleNoise(i + float2(1.0, 1.0));
                
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float3 ApplyWind(float3 positionWS, float3 positionOS, float windStrength, float windSpeed, float windFrequency, float maxHeight, float2 customOffset)
            {
                float heightFactor = saturate(positionOS.y / maxHeight);
                heightFactor = heightFactor * heightFactor;
                
                float2 windUV = (positionWS.xz + customOffset) * windFrequency;
                float noise1 = SmoothNoise(windUV * 0.5);
                float noise2 = SmoothNoise(windUV * 1.3 + float2(3.7, 2.1));
                float windNoise = noise1 * 0.7 + noise2 * 0.3;
                
                float windPhase = _Time.y * windSpeed + windNoise * 6.28318;
                float windWave = sin(windPhase);
                float windPhase2 = _Time.y * windSpeed * 0.7 + windNoise * 3.14159;
                float windWave2 = sin(windPhase2) * 0.5;
                
                float bendAmount = (windWave + windWave2) * windStrength * heightFactor;
                float3 windOffset = float3(bendAmount, 0, bendAmount * 0.6);
                
                return positionWS + windOffset;
            }

            float4 GetShadowPositionHClip(Attributes input)
            {
                float growthPhase = _GrowthPhase;
                float2 customOffset = float2(0, 0);
                
                #ifdef UNITY_INSTANCING_ENABLED
                    float4 customData = UNITY_ACCESS_INSTANCED_PROP(PerInstanceData, _CustomData);
                    growthPhase = customData.x;
                    customOffset = customData.yz;
                #endif

                float growthScale = lerp(_MinScale, 1.0, growthPhase);
                float3 scaledPositionOS = input.positionOS.xyz * growthScale;
                float3 positionWS = TransformObjectToWorld(scaledPositionOS);
                positionWS = ApplyWind(positionWS, input.positionOS.xyz, _WindStrength, _WindSpeed, _WindFrequency, _MaxHeight, customOffset);
                
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _MainLightPosition.xyz));

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return positionCS;
            }

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
                output.positionCS = GetShadowPositionHClip(input);
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);

                #ifdef _ALPHATEST_ON
                    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(alpha - _Cutoff);
                #endif

                return 0;
            }
            ENDHLSL
        }

        // DepthOnly Pass (for depth prepass)
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _Smoothness;
                half _Metallic;
                float _WindStrength;
                float _WindSpeed;
                float _WindFrequency;
                float _MaxHeight;
                float _GrowthPhase;
                float _MinScale;
            CBUFFER_END

            #ifdef UNITY_INSTANCING_ENABLED
                UNITY_INSTANCING_BUFFER_START(PerInstanceData)
                    UNITY_DEFINE_INSTANCED_PROP(float4, _CustomData)
                UNITY_INSTANCING_BUFFER_END(PerInstanceData)
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float SimpleNoise(float2 uv)
            {
                return frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453);
            }

            float SmoothNoise(float2 uv)
            {
                float2 i = floor(uv);
                float2 f = frac(uv);
                f = f * f * (3.0 - 2.0 * f);
                
                float a = SimpleNoise(i);
                float b = SimpleNoise(i + float2(1.0, 0.0));
                float c = SimpleNoise(i + float2(0.0, 1.0));
                float d = SimpleNoise(i + float2(1.0, 1.0));
                
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float3 ApplyWind(float3 positionWS, float3 positionOS, float windStrength, float windSpeed, float windFrequency, float maxHeight, float2 customOffset)
            {
                float heightFactor = saturate(positionOS.y / maxHeight);
                heightFactor = heightFactor * heightFactor;
                
                float2 windUV = (positionWS.xz + customOffset) * windFrequency;
                float noise1 = SmoothNoise(windUV * 0.5);
                float noise2 = SmoothNoise(windUV * 1.3 + float2(3.7, 2.1));
                float windNoise = noise1 * 0.7 + noise2 * 0.3;
                
                float windPhase = _Time.y * windSpeed + windNoise * 6.28318;
                float windWave = sin(windPhase);
                float windPhase2 = _Time.y * windSpeed * 0.7 + windNoise * 3.14159;
                float windWave2 = sin(windPhase2) * 0.5;
                
                float bendAmount = (windWave + windWave2) * windStrength * heightFactor;
                float3 windOffset = float3(bendAmount, 0, bendAmount * 0.6);
                
                return positionWS + windOffset;
            }

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float growthPhase = _GrowthPhase;
                float2 customOffset = float2(0, 0);
                
                #ifdef UNITY_INSTANCING_ENABLED
                    float4 customData = UNITY_ACCESS_INSTANCED_PROP(PerInstanceData, _CustomData);
                    growthPhase = customData.x;
                    customOffset = customData.yz;
                #endif

                float growthScale = lerp(_MinScale, 1.0, growthPhase);
                float3 scaledPositionOS = input.positionOS.xyz * growthScale;
                float3 positionWS = TransformObjectToWorld(scaledPositionOS);
                positionWS = ApplyWind(positionWS, input.positionOS.xyz, _WindStrength, _WindSpeed, _WindFrequency, _MaxHeight, customOffset);

                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);

                #ifdef _ALPHATEST_ON
                    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(alpha - _Cutoff);
                #endif

                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}

