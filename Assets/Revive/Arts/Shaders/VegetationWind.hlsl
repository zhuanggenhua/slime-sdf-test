#ifndef VEGETATION_WIND_EXACT_ORIGINAL_INCLUDED
#define VEGETATION_WIND_EXACT_ORIGINAL_INCLUDED

// 完全复制你原来手写 Shader 中的噪声和风函数
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

void ApplyVegetationWind_float(
    float3 PositionOS,                 // 输入：物体空间顶点位置（必须）
    out float3 OutPositionWS           // 输出：最终世界空间位置
)
{
    // ===== 读取材质属性和实例数据 =====
    float WindStrength     = _WindStrength;
    float WindSpeed        = _WindSpeed;
    float WindFrequency    = _WindFrequency;
    float MaxHeight        = _MaxHeight;
    float MinScale         = _MinScale;

    float GrowthPhase = _InstanceGrowthPhase;
    float2 CustomOffset = _InstanceWindOffset.xy;

    #ifndef UNITY_INSTANCING_ENABLED
        GrowthPhase = _GlobalGrowthPhase;
        CustomOffset = float2(0, 0);
    #endif

    // ===== 生长缩放（先缩放再转世界）=====
    float growthScale = lerp(MinScale, 1.0, GrowthPhase);
    float3 scaledPositionOS = PositionOS * growthScale;

    // 转到世界空间（用于风噪声计算）
    float3 positionWS = TransformObjectToWorld(scaledPositionOS);

    // ===== 完全复制原版风计算 =====
    float heightFactor = saturate(PositionOS.y / MaxHeight);
    heightFactor = heightFactor * heightFactor; // Square for smoother falloff

    float2 windUV = (positionWS.xz + CustomOffset) * WindFrequency;

    float noise1 = SmoothNoise(windUV * 0.5);
    float noise2 = SmoothNoise(windUV * 1.3 + float2(3.7, 2.1));

    float windNoise = noise1 * 0.7 + noise2 * 0.3;

    float windPhase = _Time.y * WindSpeed + windNoise * 6.28318;
    float windWave = sin(windPhase);

    float windPhase2 = _Time.y * WindSpeed * 0.7 + windNoise * 3.14159;
    float windWave2 = sin(windPhase2) * 0.5;

    float bendAmount = (windWave + windWave2) * WindStrength * heightFactor;

    float3 windOffset = float3(bendAmount, 0, bendAmount * 0.6);

    // ===== 输出最终位置 =====
    OutPositionWS = positionWS + windOffset;
}

#endif