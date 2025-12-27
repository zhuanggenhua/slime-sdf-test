# 尾迹系统 - Shader与材质配置指南

本文档说明尾迹系统所需的Shader和材质配置要求。

## 一、Decal系统材质要求

### 1.1 Decal Shader Graph 创建

**位置**: `Assets/Revive/Arts/Shaders/DecalWetGround.shadergraph`

**类型**: URP Decal Shader Graph

**必需属性**:

```
Properties:
- _BaseColor (Color) = (1,1,1,1)
  用途: 控制decal的基础颜色和透明度
  
- _Alpha (Float, Range(0,1)) = 1.0
  用途: 额外的透明度控制，用于淡入淡出动画
  
- _BaseMap (Texture2D) = "white" {}
  用途: 湿润效果的基础纹理贴图
  
- _NormalMap (Texture2D) = "bump" {}
  用途: 法线贴图，增强立体感
  
- _NormalStrength (Float, Range(0,2)) = 1.0
  用途: 法线强度控制
```

**Shader逻辑**:
1. 从BaseMap采样颜色
2. 乘以BaseColor
3. Alpha通道 = BaseMap.a * BaseColor.a * _Alpha
4. 法线贴图UnpackNormal后乘以NormalStrength
5. 输出到Decal的Albedo和Normal通道

**创建步骤**:
1. 在Shader Graph中选择 `Decal Shader Graph`
2. 在Graph Inspector中设置:
   - Surface Type: Transparent
   - Affect Albedo: 开启
   - Affect Normal: 开启
3. 添加上述属性
4. 连接节点到Fragment输出

### 1.2 Decal材质配置

创建3-5个材质变体:

**Mat_WetGround_Water** (水迹效果)
- BaseColor: 浅蓝色 (0.8, 0.9, 1.0, 0.6)
- BaseMap: 水迹纹理
- NormalStrength: 0.5

**Mat_WetGround_Slime** (史莱姆黏液)
- BaseColor: 淡绿色 (0.7, 1.0, 0.8, 0.7)
- BaseMap: 黏液纹理
- NormalStrength: 0.8

**Mat_WetGround_Nutrient** (营养液)
- BaseColor: 棕绿色 (0.6, 0.8, 0.5, 0.5)
- BaseMap: 营养液纹理
- NormalStrength: 0.6

### 1.3 贴图资产需求

**尺寸**: 1024x1024 或 512x512  
**格式**: PNG (Alpha通道)  
**类型**: 
- Base Map: RGB颜色 + Alpha透明度
- Normal Map: 切线空间法线贴图

**建议内容**:
- 水迹: 不规则形状，边缘渐变透明
- 黏液: 粘稠液体形状，有光泽
- 营养液: 有机质纹理，边缘柔和

---

## 二、植被系统材质要求

### 2.1 Vegetation Shader Graph 创建

**位置**: `Assets/Revive/Arts/Shaders/VegetationWind.shadergraph`

**类型**: URP Lit Shader Graph (支持GPU Instancing)

**必需属性**:

```
Properties:
- _BaseColor (Color) = (1,1,1,1)
  用途: 植被的基础颜色
  
- _BaseMap (Texture2D) = "white" {}
  用途: 植被贴图（支持透明）
  
- _Cutoff (Float, Range(0,1)) = 0.5
  用途: Alpha Clip阈值（用于叶片镂空）
  
- _GrowthPhase (Float, Range(0,1)) = 1.0
  用途: 生长阶段（0=未生长，1=完全生长）
  由代码自动设置
  
- _WindStrength (Float) = 0.3
  用途: 风力强度，由代码从WindZone同步
  
- _WindSpeed (Float) = 1.0
  用途: 风速，由代码从WindZone同步
  
- _MaxHeight (Float) = 1.0
  用途: 植被最大高度，用于计算风场影响
```

**Per-Instance数据** (通过MaterialPropertyBlock):
```
_CustomData (Vector4数组):
  x: 生长阶段 (0-1)
  y: 世界X坐标（用于风场噪声）
  z: 世界Z坐标（用于风场噪声）
  w: 生成时间（用于动画偏移）
```

**Shader逻辑**:

**顶点着色器**:
```
1. 读取_CustomData (使用UNITY_SETUP_INSTANCE_ID)
2. 计算生长缩放：
   vertexScale = lerp(0.01, 1.0, _CustomData.x)
   positionOS *= vertexScale

3. 计算风场效果：
   - 使用positionWS.xz + _CustomData.yz生成噪声
   - windPhase = _Time.y * _WindSpeed + noise
   - windOffset = sin(windPhase) * _WindStrength
   
4. 只影响顶部顶点：
   heightFactor = positionOS.y / _MaxHeight
   bendAmount = windOffset * heightFactor * heightFactor
   
5. 应用弯曲：
   positionWS.x += bendAmount
   positionWS.z += bendAmount * 0.5
```

**片元着色器**:
```
1. 采样BaseMap
2. Alpha Clip测试
3. 输出颜色 = BaseMap * BaseColor
```

**创建步骤**:
1. 创建Lit Shader Graph
2. 在Graph Inspector中设置:
   - Surface Type: Opaque (或Transparent if needed)
   - Alpha Clipping: 开启
   - Two Sided: 开启（用于叶片双面显示）
   - GPU Instancing: 开启 ⚠️ **必须**
3. 在Vertex阶段添加风场和生长动画逻辑
4. 使用Custom Function节点实现复杂逻辑

### 2.2 Custom Function节点示例

**VegetationWindBend.hlsl**:
```hlsl
void VegetationWindBend_float(
    float3 PositionOS,
    float3 PositionWS,
    float GrowthPhase,
    float WindStrength,
    float WindSpeed,
    float MaxHeight,
    float CustomX,
    float CustomZ,
    out float3 BentPositionWS)
{
    // 生长缩放
    float growthScale = lerp(0.01, 1.0, GrowthPhase);
    float3 scaledPosOS = PositionOS * growthScale;
    
    // 风场噪声
    float noise = frac(sin(dot(float2(CustomX, CustomZ), float2(12.9898, 78.233))) * 43758.5453);
    float windPhase = _Time.y * WindSpeed + noise * 6.28318;
    float windOffset = sin(windPhase) * WindStrength;
    
    // 高度衰减
    float heightFactor = saturate(PositionOS.y / MaxHeight);
    float bendAmount = windOffset * heightFactor * heightFactor;
    
    // 应用弯曲
    BentPositionWS = PositionWS;
    BentPositionWS.x += bendAmount;
    BentPositionWS.z += bendAmount * 0.5;
}
```

### 2.3 植被材质配置

**Mat_Vegetation_Grass** (小草)
- BaseColor: 绿色 (0.4, 0.8, 0.3, 1.0)
- Cutoff: 0.3
- MaxHeight: 0.5

**Mat_Vegetation_Flower** (小花)
- BaseColor: 彩色 (根据花的颜色)
- Cutoff: 0.5
- MaxHeight: 0.3

**Mat_Vegetation_Moss** (苔藓)
- BaseColor: 深绿色 (0.2, 0.5, 0.3, 1.0)
- Cutoff: 0.2
- MaxHeight: 0.1

### 2.4 植被网格要求

**面数**: 50-200三角面  
**UV**: 必须有UV0  
**顶点色**: 可选，可用于额外变化  
**轴心**: 模型底部中心（Y=0）  

**建议结构**:
- 小草: 2-4个交叉面片
- 小花: 花瓣 + 茎
- 苔藓: 低面数平面 + 噪声

---

## 三、材质路径结构

```
Assets/Revive/Arts/
├── Shaders/
│   ├── DecalWetGround.shadergraph
│   ├── VegetationWind.shadergraph
│   └── VegetationWindBend.hlsl
│
├── Materials/
│   ├── Decals/
│   │   ├── Mat_WetGround_Water.mat
│   │   ├── Mat_WetGround_Slime.mat
│   │   └── Mat_WetGround_Nutrient.mat
│   │
│   └── Vegetation/
│       ├── Mat_Vegetation_Grass.mat
│       ├── Mat_Vegetation_Flower.mat
│       └── Mat_Vegetation_Moss.mat
│
└── Textures/
    ├── Decals/
    │   ├── T_WetGround_Water.png
    │   ├── T_WetGround_Slime.png
    │   └── T_WetGround_Nutrient.png
    │
    └── Vegetation/
        ├── T_Grass_01.png
        ├── T_Flower_01.png
        └── T_Moss_01.png
```

---

## 四、性能优化建议

### Decal系统
1. 限制最大数量（默认100个）
2. 使用对象池避免频繁创建销毁
3. 使用低分辨率贴图（512x512足够）
4. Decal投影深度不要太大（0.5米内）

### 植被系统
1. **必须启用GPU Instancing**
2. 使用低面数模型（<200三角面）
3. 批次大小控制在1023以内
4. 使用简单的风场算法
5. 考虑距离LOD（远处使用更简单的模型）

---

## 五、测试检查清单

### Decal测试
- [ ] Decal正常投影到地面
- [ ] Alpha淡入淡出动画流畅
- [ ] 多个材质配置正常切换
- [ ] 性能：100个Decal时FPS稳定

### 植被测试
- [ ] 植被正常渲染
- [ ] 生长动画从小到大平滑
- [ ] 风场效果自然摆动
- [ ] GPU Instancing正常工作
- [ ] 存档加载后植被正确恢复
- [ ] 性能：1000个实例时FPS稳定

---

## 六、常见问题

**Q: Decal不显示？**
A: 检查材质是否正确设置为Decal Shader，Decal Projector是否添加

**Q: 植被不摆动？**
A: 检查WindZone是否存在，EnableWindInteraction是否开启

**Q: 植被渲染性能差？**
A: 确保材质启用了GPU Instancing，检查批次大小

**Q: 存档加载后植被位置不对？**
A: 检查地面LayerMask设置，确保射线检测正确

---

美术同学请根据此文档创建对应的Shader和材质。  
如有问题请联系程序协助调试。

