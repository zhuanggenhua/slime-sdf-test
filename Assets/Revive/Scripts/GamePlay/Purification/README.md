# 净化度系统 (Purification System)

## 概述

净化度系统是一个灵活的位置相关数据管理系统，用于记录和查询场景中特定位置的"净化程度"。系统支持：
- 添加净化指示物（记录净化事件）
- 位置查询（获取某位置的净化度）
- 监听者模式（自动通知变化）
- 数据持久化（保存/加载）
- 场景调试可视化

## 核心概念

### 1. 净化指示物 (PurificationIndicator)
净化指示物代表一个净化事件，包含：
- **名称**: 用于调试标识
- **位置**: 世界坐标
- **贡献值**: 对净化度的贡献
- **类型**: 事件类型（如：Idle逗留、Water浇水、Spore孢子等）

### 2. 净化度计算
净化度 = min(1.0, 半径内贡献值总和 / 目标净化值)
- 默认识别半径: 10米
- 默认目标净化值: 100

### 3. 监听者 (IPurificationListener)
实现此接口的对象可以监听净化度变化并自动响应。

## 快速开始

### 1. 创建系统实例

在场景中创建空GameObject，添加 `PurificationSystem` 组件。系统会自动作为单例存在。

```csharp
// 系统是单例，可通过Instance访问
PurificationSystem.Instance.DetectionRadius = 10f;
PurificationSystem.Instance.TargetPurificationValue = 100f;
```

### 2. 添加净化指示物

```csharp
// 角色逗留时添加指示物
PurificationSystem.Instance.AddIndicator(
    name: "Player_Idle_001",
    position: playerPosition,
    contributionValue: 10f,
    indicatorType: "Idle"
);

// 浇水事件
PurificationSystem.Instance.AddIndicator(
    name: "Water_Spot_001",
    position: waterPosition,
    contributionValue: 20f,
    indicatorType: "Water"
);

// 播撒孢子
PurificationSystem.Instance.AddIndicator(
    name: "Spore_Cloud_001",
    position: sporePosition,
    contributionValue: 15f,
    indicatorType: "Spore"
);
```

### 3. 查询净化度

```csharp
// 简单查询
float level = PurificationSystem.Instance.GetPurificationLevel(queryPosition);
Debug.Log($"净化度: {level:F2}"); // 输出: 0.00 - 1.00

// 详细查询
float totalContribution;
int indicatorCount;
float level = PurificationSystem.Instance.GetPurificationLevelDetailed(
    queryPosition,
    radius: 10f,
    out totalContribution,
    out indicatorCount
);
```

### 4. 实现监听者

创建脚本实现 `IPurificationListener` 接口：

```csharp
using Revive.GamePlay.Purification;

public class MyListener : MonoBehaviour, IPurificationListener
{
    void Start()
    {
        // 注册监听
        PurificationSystem.Instance.RegisterListener(this);
    }
    
    void OnDestroy()
    {
        // 注销监听
        PurificationSystem.Instance.UnregisterListener(this);
    }
    
    // 实现接口方法
    public void OnPurificationChanged(float purificationLevel, Vector3 position)
    {
        // 净化度变化时自动调用
        Debug.Log($"净化度更新: {purificationLevel}");
    }
    
    public Vector3 GetListenerPosition()
    {
        return transform.position;
    }
    
    public string GetListenerName()
    {
        return gameObject.name;
    }
}
```

### 5. 监听者移动时更新

```csharp
void OnCharacterMove()
{
    // 监听者移动后，主动请求更新
    PurificationSystem.Instance.RequestUpdate(this);
}
```

## 内置监听者示例

### 1. 氛围效果控制器 (PurificationAtmosphereController)

根据净化度调整场景氛围（如灰暗程度）。

**使用方法：**
1. 在场景相机或主对象上添加 `PurificationAtmosphereController` 组件
2. 配置参数：
   - Volume: 后处理Volume组件
   - MaxDarknessIntensity: 最大灰暗强度（净化度=0时）
   - MinDarknessIntensity: 最小灰暗强度（净化度=1时）
   - TransitionSpeed: 过渡速度（默认2.0）
3. 系统会自动根据净化度调整场景氛围

### 2. 鲜花绽放控制器 (PurificationFlowerBloom)

根据净化度控制鲜花的生长和绽放。

**使用方法：**
1. 在鲜花GameObject上添加 `PurificationFlowerBloom` 组件
2. 配置参数：
   - FlowerRoot: 鲜花根对象
   - BloomThreshold: 绽放阈值（默认0.5）
   - WitherThreshold: 凋谢阈值（默认0.3）
   - BloomedScale: 完全绽放时的缩放
   - WitheredScale: 完全凋谢时的缩放
3. 净化度达到阈值时，鲜花会自动生长/凋谢

## 数据持久化

### 保存数据

```csharp
// 保存到默认文件
PurificationSystem.Instance.SaveToFile();

// 保存到指定文件
PurificationSystem.Instance.SaveToFile("my_save.json");
```

### 加载数据

```csharp
// 从默认文件加载
PurificationSystem.Instance.LoadFromFile();

// 从指定文件加载
PurificationSystem.Instance.LoadFromFile("my_save.json");
```

存档位置: `/MMData/Purification/purification_data.json`

## 调试功能

### Scene视图可视化

在 `PurificationSystem` Inspector面板中：
- **ShowDebugGizmos**: 启用/禁用Gizmos显示
- 绿色球体: 净化指示物
- 黄色线框球: 监听者
- 蓝色半透明圈: 影响/查询范围

### 测试脚本

使用 `PurificationSystemTester` 进行功能测试：

1. 创建空GameObject，添加 `PurificationSystemTester` 组件
2. 配置测试参数
3. 快捷键：
   - **P**: 添加随机指示物
   - **O**: 查询净化度
   - **I**: 测试保存/加载
   - **U**: 清空所有指示物
4. 或在Inspector中点击Context Menu测试各项功能

## API参考

### PurificationSystem

#### 指示物管理
- `AddIndicator(name, position, contributionValue, indicatorType)` - 添加指示物
- `RemoveIndicator(indicator)` - 移除指示物
- `RemoveIndicatorsByName(name)` - 按名称移除
- `RemoveIndicatorsByType(type)` - 按类型移除
- `ClearAllIndicators()` - 清空所有
- `GetAllIndicators()` - 获取所有指示物
- `GetIndicatorsInRange(position, radius)` - 获取范围内指示物

#### 净化度查询
- `GetPurificationLevel(position, radius)` - 获取净化度
- `GetPurificationLevelDetailed(position, radius, out totalContribution, out indicatorCount)` - 获取详细信息

#### 监听者管理
- `RegisterListener(listener)` - 注册监听者
- `UnregisterListener(listener)` - 注销监听者
- `RequestUpdate(listener)` - 监听者请求更新
- `NotifyAllListeners()` - 通知所有监听者

#### 数据持久化
- `SaveToFile(filename)` - 保存数据
- `LoadFromFile(filename)` - 加载数据
- `DeleteSaveFile(filename)` - 删除存档

### IPurificationListener 接口

```csharp
public interface IPurificationListener
{
    void OnPurificationChanged(float purificationLevel, Vector3 position);
    Vector3 GetListenerPosition();
    string GetListenerName();
}
```

## 使用场景示例

### 场景1: 史莱姆自动净化（SlimeAbility）
使用 `SlimePurificationAbility` 作为史莱姆的能力组件：

```csharp
// 在史莱姆角色上添加 SlimePurificationAbility 组件
// 它会像buff一样自动定时添加净化指示物，无需判断速度或状态

// 配置参数：
// - PurificationInterval: 3秒（每3秒自动净化一次）
// - ContributionValue: 10（每次贡献10点净化值）
// - IndicatorType: "SlimeIdle"

// 可以通过代码控制：
var ability = GetComponent<SlimePurificationAbility>();
ability.EnablePurificationAbility();  // 启用
ability.DisablePurificationAbility(); // 禁用
ability.TriggerPurificationNow();     // 立即触发一次
```

### 场景2: 浇水净化
```csharp
// 在浇水系统中
void OnWaterSplash(Vector3 position)
{
    PurificationSystem.Instance.AddIndicator(
        $"Water_{Time.time}",
        position,
        20f,
        "Water"
    );
}
```

### 场景3: 区域净化检测
```csharp
// 检测某区域是否净化完成
bool IsAreaPurified(Vector3 center, float radius, float threshold = 0.8f)
{
    float level = PurificationSystem.Instance.GetPurificationLevel(center, radius);
    return level >= threshold;
}
```

## 性能考虑

- 当前版本使用简单的距离检测（O(n)复杂度）
- 适用于中小规模指示物数量（< 1000个）
- 如需优化，可扩展为空间分区结构（Grid/Quadtree）

## 扩展建议

1. **时间衰减**: 在Update中逐渐降低指示物的贡献值
2. **不同半径**: 为不同类型指示物设置不同影响半径
3. **权重计算**: 根据距离计算权重（近的贡献更大）
4. **粒子效果**: 在净化度变化时播放粒子效果
5. **音效反馈**: 添加净化度变化的音效
6. **UI显示**: 创建UI显示当前净化度进度条

## 许可证

此系统为Revive项目的一部分。

