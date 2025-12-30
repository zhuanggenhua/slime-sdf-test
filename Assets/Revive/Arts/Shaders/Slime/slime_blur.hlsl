float3 SlimeBlur(float2 UV, float BlurRadius, float DistortionStrength, float3 WorldNormal)
{
    float3 color = 0;
    float sum = 0;

    // 高斯权重（13 tap 近似）
    float weights[13] = {
        0.009888, 0.018487, 0.031739, 0.050028, 0.072403,
        0.096225, 0.117587,
        0.096225, 0.072403, 0.050028, 0.031739, 0.018487, 0.009888
    };

    float2 texelSize = _ScreenSize.zw; // 1 / (Width, Height)

    // 将世界法线转换为视图空间
    float3 viewNormal = mul((float3x3)UNITY_MATRIX_V, WorldNormal);
    float2 screenNormal = -viewNormal.xy;

    // 应用法向视平面长度到模糊距离
    BlurRadius *= length(screenNormal);

    // 额外基于法线的偏移
    float2 normalOffset = screenNormal * DistortionStrength * texelSize;

    [unroll]
    for (int i = -6; i <= 6; i++)
    {
        [unroll]
        for (int j = -6; j <= 6; j++)
        {
            float offsetX = i * BlurRadius * texelSize.x;
            float offsetY = j * BlurRadius * texelSize.y;
            float weight = weights[i + 6];

            // 基础高斯偏移（水平 + 垂直）
            float2 gaussianOffset = float2(offsetX, offsetY);

            // 总偏移
            float2 sampleUV = UV + gaussianOffset + normalOffset;

            // 采样场景颜色
            color += SHADERGRAPH_SAMPLE_SCENE_COLOR(sampleUV).rgb * weight;

            sum += weight;
        }
    }

    color /= sum;
    return color;
}

void SlimeBlur_float(float2 UV, float BlurRadius, float DistortionStrength, float3 WorldNormal, out float3 Out)
{
    Out = SlimeBlur(UV, BlurRadius, DistortionStrength, WorldNormal);
}