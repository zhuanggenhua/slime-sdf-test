# 史莱姆尾迹系统 - 使用指南

## 概述

史莱姆尾迹系统为史莱姆角色提供两种视觉效果：
1. **湿润地面Decal** - 临时的湿漉漉效果，会随时间消失
2. **植被生长** - 永久的小花小草，支持存档

## 快速开始

### 1. 在角色上添加组件

在史莱姆角色GameObject上添加以下结构：

```
SlimeCharacter (GameObject)
├── TrailEffectManager (Component)
│   ├── WetGroundDecalTrail (Component)
│   └── VegetationGrowthTrail (Component)
```

### 2. 配置WetGroundDecalTrail

**基础设置**:
- Spawn Distance Threshold: 1.0 (每移动1米生成)
- Ground Layer Mask: Default, Terrain (选择地面层)
- Raycast Distance: 10.0

**Decal设置**:
- Decal Size: (1, 1, 0.5)
- Fade In Duration: 0.3秒
- Lifetime: 10秒
- Fade Out Duration: 1秒

**材质配置**:
1. 点击 "Decal Materials" 列表
2. 添加材质配置（Size: 3）
3. 为每个配置设置：
   - Decal Material: 选择Decal材质
   - Base Color: 设置颜色
   - Spawn Probability: 设置生成概率（0-1）

**随机化**:
- Size Randomness: 0.2 (20%尺寸变化)
- Rotation Randomness: 180 (±180度旋转)
- Position Offset Radius: 0.2 (0.2米位置偏移)

### 3. 配置VegetationGrowthTrail

**基础设置**:
- Spawn Distance Threshold: 1.0
- Ground Layer Mask: Default, Terrain
- Raycast Distance: 10.0

**植被设置**:
- Density: 5 (每米5个植被)
- Spread Radius: 1.5 (1.5米扩散范围)

**植被类型配置**:
1. 点击 "Vegetation Types" 列表
2. 添加植被类型（Size: 3-5）
3. 为每个类型设置：
   - Prefab: 植被预制件（用于自动提取Mesh和Material）
   - 或手动设置：
     - Mesh: 植被网格
     - Material: 植被材质（必须支持GPU Instancing）
   - Spawn Probability: 生成概率
   - Scale Range: 缩放范围 (如 0.8 到 1.2)

**生长动画**:
- Growth Duration: 2秒
- Growth Curve: 使用默认曲线或自定义

**风场**:
- Enable Wind Interaction: ✓
- Wind Zone: 留空自动查找，或手动指定

**渲染**:
- Max Instances Per Batch: 1023
- Cast Shadows: ✓
- Receive Shadows: ✓

**存档**:
- Save File Name: "VegetationTrail" (可自定义)

### 4. 配置TrailEffectManager

- Enable Trails: ✓ 开启
- Global Distance Multiplier: 1.0 (全局距离倍数)
- Trail Effects: 自动收集子对象中的尾迹组件

## 调试功能

### 在Inspector中调试

1. **启用调试信息**:
   - 在WetGroundDecalTrail或VegetationGrowthTrail组件上
   - 勾选 "Show Debug Info"

2. **Scene视图可视化**:
   - WetGroundDecalTrail: 显示Decal边界框和剩余时间
   - VegetationGrowthTrail: 显示路径点和路径线

3. **Inspector调试面板**（运行时）:
   - 显示激活效果数量
   - 显示上次生成时间
   - 植被系统额外显示：
     - 总植被数量
     - 路径点数量
     - 存档操作按钮

### 植被存档操作

在运行时的Inspector中（需要启用Show Debug Info）：

- **保存植被**: 保存当前所有路径点
- **加载植被**: 从存档恢复植被（会清除当前植被）
- **清除所有植被**: 删除所有植被
- **删除存档**: 删除存档文件

### 代码中使用

```csharp
// 获取角色
SlimeCharacter slime = GetComponent<SlimeCharacter>();

// 启用/禁用尾迹
slime.EnableTrailEffects();
slime.DisableTrailEffects();
slime.ToggleTrailEffects();

// 直接操作植被系统
VegetationGrowthTrail vegTrail = slime.TrailManager.GetTrailEffect<VegetationGrowthTrail>();
vegTrail.SaveToFile("MyCustomSave");
vegTrail.LoadFromFile("MyCustomSave");
vegTrail.ClearAll();
```

## 性能调优

### Decal系统

**参数影响**:
- Max Decal Count: 限制最大数量，默认100
- Lifetime: 减少生存时间可降低同时存在的数量
- Spawn Distance Threshold: 增大值减少生成频率

**优化建议**:
- 使用512x512贴图足够
- Decal Size的深度不要超过0.5米
- 在低端设备上降低Max Decal Count到50

### 植被系统

**参数影响**:
- Density: 每米生成数量，直接影响性能
- Spread Radius: 扩散范围，间接影响密度
- Max Instances Per Batch: 批次大小，不要超过1023

**优化建议**:
- 使用低面数模型（50-200三角面）
- 确保材质启用GPU Instancing
- 在低端设备上降低Density到2-3
- 考虑实现视锥剔除（TODO）

## 常见问题

### Decal不显示

1. 检查Decal Materials列表是否为空
2. 检查材质是否正确配置
3. 检查Ground Layer Mask是否包含地面层
4. 检查角色是否处于Grounded状态

### 植被不显示

1. 检查Vegetation Types列表是否为空
2. 检查Mesh和Material是否正确设置
3. 检查Material是否启用GPU Instancing
4. 检查Ground Layer Mask设置

### 植被不摆动

1. 检查Enable Wind Interaction是否开启
2. 检查场景中是否有WindZone
3. 检查Shader是否正确实现风场逻辑

### 性能问题

1. 降低Density
2. 减少Max Decal Count
3. 使用更简单的Mesh
4. 检查是否启用GPU Instancing

### 存档问题

1. 检查Save File Name是否设置
2. 检查MMSaveLoadManager是否正确配置
3. 检查存档路径权限

## 材质和Shader要求

详细的Shader创建指南请参考：
👉 [TrailSystem_ShaderGuide.md](TrailSystem_ShaderGuide.md)

### Decal材质清单

需要创建的材质（位于 `Assets/Revive/Arts/Materials/Decals/`）：
- [ ] Mat_WetGround_Water (水迹)
- [ ] Mat_WetGround_Slime (黏液)
- [ ] Mat_WetGround_Nutrient (营养液)

### 植被材质清单

需要创建的材质（位于 `Assets/Revive/Arts/Materials/Vegetation/`）：
- [ ] Mat_Vegetation_Grass (小草)
- [ ] Mat_Vegetation_Flower (小花)
- [ ] Mat_Vegetation_Moss (苔藓)

## 扩展开发

### 添加新的尾迹类型

1. 继承 `TrailEffectBase` 类
2. 实现抽象方法：
   - `SpawnEffect(Vector3 position, Vector3 normal)`
   - `UpdateEffects()`
   - `DrawDebugGizmos()`
3. 实现 `ActiveEffectCount` 属性
4. 添加到TrailEffectManager的TrailEffects列表

示例：

```csharp
public class MyCustomTrail : TrailEffectBase
{
    public override int ActiveEffectCount => _myEffects.Count;
    
    protected override void SpawnEffect(Vector3 position, Vector3 normal)
    {
        // 你的生成逻辑
    }
    
    protected override void UpdateEffects()
    {
        // 你的更新逻辑
    }
    
    protected override void DrawDebugGizmos()
    {
        // 你的调试绘制
    }
}
```

### 自定义存档格式

参考 `VegetationSaveSystem.cs` 实现自定义存档逻辑。

## 技术支持

如有问题请联系：
- 程序负责人：[待补充]
- 文档位置：`Assets/Revive/Documents/`

---

**最后更新**: 2025-12-27  
**系统版本**: 1.0

