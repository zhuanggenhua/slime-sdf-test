基于https://github.com/lamp-cap/Unity_Slime

# slime-sdf-test

一个基于 Unity 6 的史莱姆玩法实验仓库，当前公开内容集中在三个模块：

- `Slime`：基于 PBF 的史莱姆主体、分离、发射、召回与场景水珠系统
- `World SDF`：静态场景 SDF 烘焙、加载与运行时碰撞采样链路
- `Purification`：位置驱动的净化度系统，以及场景中的可视化和交互示例

当前仓库保留一个主演示场景：

- `Assets/Revive/Scenes/SlimeTest.unity`

## 环境

- Unity `6000.3.10f1`
- URP `17.3.0`
- Input System `1.18.0`
- Cinemachine `3.1.4`
- TopDownEngine（仓库内已包含裁剪后的依赖内容）

## 功能概览

### 1. 史莱姆模拟

- 主体史莱姆使用 PBF 粒子模拟
- 支持分离体、喷射粒子、场景水珠三类粒子状态
- 支持 CCA 连通组件分析，用于识别分离块并分配控制器
- 支持主体/分离体回收、接触融合、淡出回收
- 支持 Terrain 高度场、静态碰撞体和 World SDF 共同参与地面/障碍碰撞

主要代码入口：

- `Assets/Revive/Scripts/Characters/Slime/Slime_PBF.cs`
- `Assets/Revive/Scripts/Characters/Slime/Jobs/Jobs_Simulation_PBF.cs`

### 2. World SDF

- 提供场景静态碰撞体的 SDF 烘焙工具
- 运行时从 `WorldSdf.bytes` 加载体积数据
- 用于史莱姆和水珠的静态碰撞、法线与摩擦计算
- 与普通 Collider 回退路径共存，可按项目需求切换

主要代码入口：

- `Assets/Revive/Scripts/Characters/Slime/Editor/WorldSdfBakerWindow.cs`
- `Assets/Revive/Scripts/Characters/Slime/Physics/WorldSdfAsset.cs`

编辑器入口：

- `Slime/World/Bake SDF`

默认输出资源位置：

- `Assets/MMData/SDFData/WorldSdf.bytes`

### 3. Purification 净化系统

- 基于位置和半径累计净化贡献
- 支持查询、监听、存档和可视化
- 场景内可用作花朵绽放、区域恢复、世界材质反馈等逻辑驱动

主要代码入口：

- `Assets/Revive/Scripts/GamePlay/Purification/PurificationSystem.cs`
- `Assets/Revive/Scripts/GamePlay/Purification/README.md`

## 快速开始

### 1. 打开项目

1. 使用 Unity Hub 打开仓库根目录
2. 确认编辑器版本为 `6000.3.10f1`
3. 等待包与资源导入完成

### 2. 打开演示场景

打开：

- `Assets/Revive/Scenes/SlimeTest.unity`

### 3. 运行场景

直接进入 Play Mode 即可查看当前公开功能联动：

- 史莱姆主体模拟
- 分离/回收表现
- SDF 或普通静态碰撞参与下的地形交互
- 净化系统相关演示内容

## World SDF 使用方法

### 烘焙

1. 在 Unity 顶部菜单打开 `Slime/World/Bake SDF`
2. 设置烘焙范围、体素大小、静态层掩码
3. 需要时可使用“从场景碰撞体自动计算范围”
4. 点击“烘焙 SDF 资源”
5. 将生成的 `WorldSdf.bytes` 保存到 `Assets/MMData/SDFData/`

### 挂接

在史莱姆对象的 `Slime_PBF` 组件上确认以下配置：

- `Use World Static Sdf` 已启用
- `World Static Sdf Bytes` 指向生成的 `WorldSdf.bytes`
- `World Static Collider Layers` 包含你希望参与静态碰撞/SDF 烘焙的层

### 运行时行为

- 若 SDF 可用，则优先用于静态场景碰撞
- 若关闭 SDF，或当前路径允许回退，则仍可使用普通静态 Collider
- Terrain 会在运行时自动读取场景中的 `Terrain` 高度图，不需要手动拖引用

## Purification 使用方法

如果你只想看基础用法，先读：

- `Assets/Revive/Scripts/GamePlay/Purification/README.md`

最常见的使用方式：

- 在场景中放置 `PurificationSystem`
- 用 `AddIndicator(...)` 或相关组件向系统写入净化贡献
- 用 `GetPurificationLevel(...)` 查询某个位置的净化程度
- 用 `IPurificationListener` 响应净化变化

## 目录结构

```text
project-revive-copy/
|- Assets/
|  |- Revive/
|  |  |- Scenes/
|  |  |  |- SlimeTest.unity
|  |  |- Scripts/
|  |  |  |- Characters/Slime/
|  |  |  |- GamePlay/Purification/
|  |- MMData/SDFData/
|- Packages/
|- ProjectSettings/
|- README.md
```

## 当前仓库定位

这个仓库不是完整游戏工程归档，而是围绕以下技术点整理出的可运行样例：

- 史莱姆粒子体模拟
- 连通组件分离与回收控制
- 场景静态 SDF 碰撞
- 净化度系统与世界反馈

如果你想继续扩展，建议优先从以下文件读起：

- `Assets/Revive/Scripts/Characters/Slime/Slime_PBF.cs`
- `Assets/Revive/Scripts/Characters/Slime/Editor/WorldSdfBakerWindow.cs`
- `Assets/Revive/Scripts/GamePlay/Purification/PurificationSystem.cs`
