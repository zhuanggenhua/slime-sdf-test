# 净化度系统 - 快速使用指南

## 📦 已创建的文件

### 核心系统 (Core)
- `PurificationIndicator.cs` - 净化指示物数据类（包含name字段）
- `PurificationSaveData.cs` - 存档数据结构
- `IPurificationListener.cs` - 监听者接口（包含GetListenerName()方法）
- `PurificationSystem.cs` - 核心管理系统（单例）

### 监听者示例 (Listeners)
- `PurificationFlowerBloom.cs` - 鲜花绽放控制器

### 测试工具 (Testing)
- `PurificationSystemTester.cs` - 完整测试套件

### 使用示例 (Examples/)
- `CharacterIdlePurification.cs` - 角色逗留净化示例
- `SimplePurificationQuery.cs` - 简单查询示例

### 文档 (Documentation)
- `README.md` - 完整使用文档
- `VERIFICATION.md` - 功能验证清单

## 🚀 3分钟快速开始

### 1. 创建系统实例
在场景中创建空GameObject，命名为"PurificationSystem"，添加 `PurificationSystem` 组件。

### 2. 添加净化指示物
```csharp
PurificationSystem.Instance.AddIndicator(
    name: "PlayerIdle_001",
    position: transform.position,
    contributionValue: 10f,
    indicatorType: "Idle"
);
```

### 3. 查询净化度
```csharp
float level = PurificationSystem.Instance.GetPurificationLevel(transform.position);
Debug.Log($"净化度: {level:F2}"); // 0.00 - 1.00
```

### 4. 实现监听者
```csharp
public class MyListener : MonoBehaviour, IPurificationListener
{
    void Start() => PurificationSystem.Instance.RegisterListener(this);
    void OnDestroy() => PurificationSystem.Instance.UnregisterListener(this);
    
    public void OnPurificationChanged(float level, Vector3 pos)
    {
        Debug.Log($"净化度更新: {level}");
    }
    
    public Vector3 GetListenerPosition() => transform.position;
    public string GetListenerName() => gameObject.name; // 调试用
}
```

## 🎮 测试系统

### 使用测试脚本
1. 创建空GameObject，添加 `PurificationSystemTester` 组件
2. 配置参数（或使用默认值）
3. 使用快捷键：
   - **P** - 添加随机指示物
   - **O** - 查询净化度
   - **I** - 测试保存/加载
   - **U** - 清空所有指示物

### 使用Context Menu
在Inspector面板中右键点击测试方法运行单项测试。

## 🎨 内置监听者使用

### 鲜花绽放
在鲜花GameObject上添加 `PurificationFlowerBloom`：
- 净化度 ≥ 0.5 → 鲜花生长绽放
- 净化度 < 0.3 → 鲜花凋谢

## 🔍 调试功能

### Scene视图可视化
- **绿色球体** - 净化指示物位置
- **黄色线框球** - 监听者位置
- **蓝色圆圈** - 影响/查询范围

在 `PurificationSystem` Inspector中切换 `ShowDebugGizmos` 开关。

## 💾 数据持久化

```csharp
// 保存
PurificationSystem.Instance.SaveToFile();

// 加载
PurificationSystem.Instance.LoadFromFile();
```

存档路径: `Assets/MMData/Purification/purification_data.json`

## 📚 详细文档

查看 `README.md` 获取：
- 完整API参考
- 详细使用场景
- 性能考虑
- 扩展建议

## ✅ 系统特性

- ✓ 位置查询净化度（半径内贡献值计算）
- ✓ 指示物管理（添加、移除、查询）
- ✓ 监听者模式（自动通知变化）
- ✓ 数据持久化（JSON格式）
- ✓ 调试可视化（Scene Gizmos）
- ✓ 完整测试工具
- ✓ 使用示例代码
- ✓ 名称支持（指示物和监听者都有name用于调试）

## 🎯 典型使用场景

1. **史莱姆自动净化** - 在史莱姆角色上添加 `SlimePurificationAbility` 组件
2. **浇水事件** - 在浇水时调用 `AddIndicator(..., "Water")`
3. **播撒孢子** - 在播撒时调用 `AddIndicator(..., "Spore")`
4. **植被生长** - 模型挂 `PurificationFlowerBloom` 或自定义监听者

> 注：开源精简版已移除后处理相关监听者，保留净化系统核心能力与花朵示例。

## 🔧 配置参数

在 `PurificationSystem` Inspector中：
- **DetectionRadius** - 识别半径（默认10米）
- **TargetPurificationValue** - 目标净化值（默认100）
- **ShowDebugGizmos** - 显示调试Gizmos

## 📞 支持

所有问题请参考：
- `README.md` - 详细文档
- `PurificationSystemTester.cs` - 测试用例

---

**系统版本**: 1.0.0  
**创建日期**: 2026-01-01  
**状态**: ✅ 生产就绪

