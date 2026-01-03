Shader "Revive/Slime/FaceStencil"
{
    Properties
    {
        _Texture2D("Texture2D", 2D) = "white" {}
        _Color("Color", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalRenderPipeline" "Queue" = "Transparent+2" "RenderType" = "Transparent" }

        Pass
        {
            Name "Forward"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            Stencil
            {
                Ref 1
                Comp Equal
                Pass Keep
                Fail Keep
                ZFail Keep
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_Texture2D);
            SAMPLER(sampler_Texture2D);

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _Texture2D_ST;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _Texture2D);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_Texture2D, sampler_Texture2D, input.uv);
                return tex * _Color;
            }

            ENDHLSL
        }
    }
}
