Shader "GTT/AALineShader"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)      
        _LineWidth ("Line Width", Float) = 1.0         // line width in pixels
        _AAScale ("AA Scale", Range(1.0, 10.0)) = 2.7  // anti-aliasing
        _ZTest ("ZTest", int) = 0
		_ZWrite ("ZWrite", int) = 0
		_CullMode ("Cull mode", int) = 2
        _OffsetFactor ("Offset Factor", Float) = -3.0  // slope offset
        _OffsetUnits ("Offset Units", Float) = -1.0    // fixed depth offset
    }
    SubShader
    {
        Tags {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
			ZTest [_ZTest]
			Cull [_CullMode]
			Offset [_OffsetFactor], [_OffsetUnits]

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma vertex vert
            #pragma geometry geo
            #pragma fragment frag

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _LineWidth;
                float _AAScale;
            CBUFFER_END

            struct vInput
            {
                float4 vertex_pos : POSITION;
            };

            struct vOutput
            {
                float4 clip_pos : SV_POSITION;
                float3 world_pos : TEXCOORD0;
            };

            struct geoOutput
            {
                float4 clip_pos : SV_POSITION;
                float3 world_pos : TEXCOORD0;
                noperspective float2 uv : TEXCOORD1;
            };

            vOutput vert(vInput input)
            {
                vOutput o;
                o.clip_pos = TransformObjectToHClip(input.vertex_pos.xyz);
                o.world_pos = TransformObjectToWorld(input.vertex_pos.xyz);
                return o;
            }

            [maxvertexcount(4)]
            void geo(line vOutput input[2], inout TriangleStream<geoOutput> triangle_stream)
            {
                vOutput p0 = input[0];
                vOutput p1 = input[1];

                // make sure P0 is the near point
                if (p0.clip_pos.w > p1.clip_pos.w)
                {
                    vOutput temp = p0;
                    p0 = p1;
                    p1 = temp;
                }

                // screen space
                float2 a = p0.clip_pos.xy / p0.clip_pos.w;
                float2 b = p1.clip_pos.xy / p1.clip_pos.w;

                // calculate the perpendicular vector, and scale it by the line width
                float2 dir = normalize(b - a);
                float2 perp = float2(-dir.y, dir.x) * (_LineWidth / _ScreenParams.xy) * 0.5;

                float half_uv = _LineWidth * 1.125;

                // generate quad vertices
                geoOutput o[4];
                o[0].clip_pos = float4(p0.clip_pos.xy + perp * p0.clip_pos.w, p0.clip_pos.zw);
                o[0].world_pos = p0.world_pos;
                o[0].uv = float2(half_uv, 0);

                o[1].clip_pos = float4(p0.clip_pos.xy - perp * p0.clip_pos.w, p0.clip_pos.zw);
                o[1].world_pos = p0.world_pos;
                o[1].uv = float2(-half_uv, 0);

                o[2].clip_pos = float4(p1.clip_pos.xy - perp * p1.clip_pos.w, p1.clip_pos.zw);
                o[2].world_pos = p1.world_pos;
                o[2].uv = float2(-half_uv, 0);

                o[3].clip_pos = float4(p1.clip_pos.xy + perp * p1.clip_pos.w, p1.clip_pos.zw);
                o[3].world_pos = p1.world_pos;
                o[3].uv = float2(half_uv, 0);

                triangle_stream.Append(o[1]);
                triangle_stream.Append(o[2]);
                triangle_stream.Append(o[0]);
                triangle_stream.Append(o[3]);
                triangle_stream.RestartStrip();
            }

            float4 frag(geoOutput o) : SV_TARGET
            {
                float4 col = _Color;

                float normalizedUv = o.uv.x / (_LineWidth * 1.125); // normalize to [0, 1]
                float aa = exp2(-_AAScale * normalizedUv * normalizedUv);

                return float4(col.rgb, col.a * aa);
            }
            ENDHLSL
        }
    }
}