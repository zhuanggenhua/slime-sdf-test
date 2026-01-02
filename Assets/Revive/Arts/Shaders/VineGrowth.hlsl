// VineGrowth.hlsl
// 藤蔓生长Shader自定义函数
// 用于ShaderGraph的Custom Function节点

void VineGrowth_float(
    float3 Position,        // 输入：顶点位置（Object Space）
    float3 Normal,          // 输入：顶点法线（用于边缘效果）
    float PathProgress,     // 输入：顶点色.x (路径位置0-1)
    float Thickness,        // 输入：顶点色.y (该点的粗细0.1-1.0)
    float GrowthValue,      // 输入：生长值参数 (0-1)
    out float3 OutPosition, // 输出：修改后的顶点位置
    out float OutAlpha,     // 输出：Alpha透明度
    out float OutGlow       // 输出：边缘发光强度
)
{
    // 1. 判断是否在生长范围内
    float isVisible = step(PathProgress, GrowthValue);
    
    // 2. 计算到生长边缘的距离
    float distToEdge = GrowthValue - PathProgress;
    
    // 3. 边缘淡入区域
    float fadeRange = 0.08f;
    float edgeFade = smoothstep(0.0, fadeRange, distToEdge);
    
    // 4. 末端30%由细变粗的过程
    float tipGrowthRange = 0.3; 
    float tipThicknessScale = 1.0;
    
    if (distToEdge < tipGrowthRange)
    {
        // 在末端30%范围内，从20%粗细渐变到100%粗细
        float tipProgress = distToEdge / tipGrowthRange; // 0(边缘) -> 1(30%之外)
        tipThicknessScale = lerp(0.2, 1.0, smoothstep(0.0, 1.0, tipProgress));
    }
    
    // 5. 生长边缘的轻微脉冲效果
    float growthPulse = (1.0 - edgeFade) * 0.05;
    
    // 6. 组合位置调整
    // 基于Normal方向调整径向粗细（不影响路径方向）
    float radiusAdjustment = Thickness * tipThicknessScale - Thickness; // 粗细变化量
    float3 thicknessOffset = Normal * radiusAdjustment;
    float3 pulseOffset = Normal * growthPulse * Thickness * 0.08;
    
    OutPosition = Position + (thicknessOffset + pulseOffset) * isVisible;
    
    // 7. Alpha透明度
    float edgeAlpha = lerp(0.2, 1.0, edgeFade);
    // 末端细的部分稍微透明（嫩芽效果）
    float thicknessAlpha = lerp(0.7, 1.0, tipThicknessScale * Thickness);
    OutAlpha = isVisible * edgeAlpha * thicknessAlpha;
    
    // 8. 边缘发光效果
    // 末端越细，发光越强
    float glowIntensity = (1.0 - edgeFade) * (1.0 - edgeFade);
    float tipGlow = 1.0 + (1.0 - tipThicknessScale) * 2.0; // 细的部分发光更强
    OutGlow = isVisible * glowIntensity * tipGlow;
}

