# 🦋 蝴蝶特效配置系统使用指南

## 概述

蝴蝶特效配置系统允许你为多朵花共享蝴蝶特效配置，避免重复配置，同时也支持单朵花的自定义配置。

## 核心组件

### 1. `ButterflyEffectConfig.cs` - ScriptableObject 配置资源
可复用的蝴蝶特效配置，可在多个花朵之间共享。

### 2. `PurificationFlowerBloom.cs` - 花朵组件
支持两种配置模式：
- **配置资源模式**（推荐）：引用 `ButterflyEffectConfig` 资源
- **本地覆盖模式**：直接在组件上配置

---

## 快速开始

### 方式一：使用配置资源（推荐）

#### 1️⃣ 创建配置资源

**方法 A：通过菜单创建**
```
右键点击 Project 窗口
→ Create
→ Revive
→ Purification
→ Butterfly Effect Config
```

**方法 B：从已有花朵创建**
1. 选中已配置好本地设置的花朵
2. 在 Inspector 底部点击 **"从本地配置创建 ScriptableObject 资源"**
3. 选择保存位置（推荐：`Assets/Revive/Arts/Configs/Purification/`）

#### 2️⃣ 配置资源设置

在创建的配置资源 Inspector 中：

**快速预设**（点击按钮一键应用）：
- **浪漫场景**：80% 概率，30秒生命周期，不随花凋谢
- **标准场景**：30% 概率，永久存在，随花凋谢
- **稀有场景**：10% 概率，15秒生命周期，随花凋谢

**手动配置**：
- `Butterfly Prefabs`：添加蝴蝶预制体（可多个，随机选择）
- `Spawn Chance`：生成概率（0-1）
- `Spawn Offset`：相对花朵的偏移位置
- `Spawn Random Radius`：生成位置随机半径
- `Lifetime`：自动销毁时间（0 = 不销毁）
- `Remove On Wither`：花凋谢时是否移除蝴蝶

#### 3️⃣ 分配给花朵

选中花朵 GameObject：
1. 在 `PurificationFlowerBloom` 组件中
2. 将创建的配置资源拖拽到 **`Butterfly Config`** 字段
3. 确保 **`Use Local Override`** 未勾选

✅ 完成！现在这朵花会使用配置资源的设置。

---

### 方式二：使用本地覆盖配置

适合需要特殊配置的单朵花。

1. 选中花朵 GameObject
2. 在 `PurificationFlowerBloom` 组件中勾选 **`Use Local Override`**
3. 在 **"本地覆盖配置"** 区域配置蝴蝶设置
4. 配置参数与配置资源相同

---

## 配置模式对比

| 特性 | 配置资源模式 | 本地覆盖模式 |
|------|-------------|-------------|
| 多花共享 | ✅ 是 | ❌ 否 |
| 修改便捷性 | ✅ 改一处全部生效 | ❌ 需逐个修改 |
| 特殊定制 | ⚠️ 需创建新资源 | ✅ 直接修改 |
| 推荐场景 | 大量相同花朵 | 少量特殊花朵 |

---

## 配置优先级

系统按以下优先级选择配置：

```
1. Use Local Override = ✓
   → 使用本地配置（忽略 Butterfly Config）

2. Use Local Override = ✗ & Butterfly Config 已设置
   → 使用配置资源

3. Use Local Override = ✗ & Butterfly Config 未设置
   → 使用本地配置（向后兼容）
```

---

## 实用场景配置

### 场景 1：森林中的普通花朵（共享配置）

**创建配置**：`ForestFlower_ButterflyConfig.asset`
```
Spawn Chance: 0.3 (30%)
Lifetime: 0 (永久)
Remove On Wither: ✓
```

**应用到花朵**：将此配置分配给所有森林花朵

### 场景 2：魔法花园特效花（个性化）

**花 A**（浪漫主题）：
```
Use Local Override: ✓
Spawn Chance: 0.8 (80%)
Lifetime: 30
Prefabs: [蓝色蝴蝶, 紫色蝴蝶]
```

**花 B**（稀有珍贵）：
```
Use Local Override: ✓
Spawn Chance: 0.1 (10%)
Lifetime: 15
Prefabs: [金色蝴蝶]
```

### 场景 3：混合使用

**普通花朵**（95%）：使用 `StandardButterfly_Config.asset`
**特殊花朵**（5%）：勾选 `Use Local Override`，自定义配置

---

## Inspector 增强功能

### ButterflyEffectConfig Inspector

**快速预设按钮**：
- 一键应用常用配置
- 避免手动输入错误

**配置状态提示**：
- ✅ 有效配置显示绿色提示
- ⚠️ 无效配置显示警告

### PurificationFlowerBloom Inspector

**配置源提示**：
- 清楚显示当前使用的配置来源
- 实时显示配置参数摘要

**一键创建资源**：
- 从本地配置快速生成 ScriptableObject

**运行时测试**（Play Mode）：
- "强制绽放" 按钮：立即绽放并尝试生成蝴蝶
- "强制凋谢" 按钮：立即凋谢并移除蝴蝶

---

## 最佳实践

### ✅ 推荐做法

1. **预先规划**：根据场景需求创建2-3个配置资源（标准、稀有、浪漫）
2. **统一管理**：将配置资源统一存放在 `Assets/Revive/Arts/Configs/Purification/`
3. **描述性命名**：配置资源命名清晰，如 `Forest_StandardButterfly`
4. **少用覆盖**：只对真正特殊的花使用本地覆盖

### ❌ 避免做法

1. ❌ 每朵花都创建一个配置资源
2. ❌ 所有花都勾选 `Use Local Override`
3. ❌ 配置资源中引用 null 预制体
4. ❌ 忘记分配配置资源导致使用空的本地配置

---

## 调试技巧

### Console 日志

系统会输出详细日志，包含配置来源：

```
✓ [Flower_01] 成功生成蝴蝶特效: BlueButterflyPrefab_From_Flower_01 
   在位置 (10.5, 2.0, 5.3) (来源: 配置资源: ForestButterfly)

✓ [Flower_02] 蝴蝶生成概率判定失败: 0.75 > 0.30

✓ [Flower_01] 移除蝴蝶: BlueButterflyPrefab_From_Flower_01
```

### 场景调试

1. **Inspector 状态**：选中花朵查看 "蝴蝶配置状态" 区域
2. **运行时测试**：Play Mode 中使用 "强制绽放/凋谢" 按钮
3. **配置验证**：检查配置资源 Inspector 中的状态提示

---

## 常见问题

### Q: 花朵开花了但没有蝴蝶？

**A:** 检查以下项：
1. 配置资源或本地配置是否设置了预制体？
2. 生成概率是否太低？（30%意味着平均3朵花才有1只蝴蝶）
3. Console 是否有 "概率判定失败" 日志？
4. 是否已经有蝴蝶存在？（每朵花只会有一只）

### Q: 修改配置资源后没有生效？

**A:** 确认：
1. 修改的是正确的配置资源（查看花朵 Inspector 中引用的是哪个）
2. 花朵未勾选 `Use Local Override`
3. Play Mode 中重新触发开花（或使用 "强制绽放" 按钮）

### Q: 如何让所有花都使用新配置？

**A:** 
1. 修改配置资源即可，所有引用它的花都会自动使用新配置
2. 或者批量选中花朵，一次性分配新的配置资源

### Q: 蝴蝶会跟随花朵移动吗？

**A:** 
不会。蝴蝶在生成时确定位置，之后独立存在。
如果需要跟随，请在蝴蝶预制体中添加跟随脚本。

---

## 技术细节

### 配置加载顺序

```csharp
bool useConfig = !UseLocalOverride && ButterflyConfig != null && ButterflyConfig.IsValid();

if (useConfig)
    使用配置资源的参数
else
    使用本地配置参数
```

### 生成流程

```
1. 花朵完全绽放 (OnFullyBloomed)
   ↓
2. 调用 TrySpawnButterfly()
   ↓
3. 检查是否已有蝴蝶（避免重复）
   ↓
4. 获取配置源（资源 or 本地）
   ↓
5. 概率判定 (Random.Range(0, 1) <= SpawnChance)
   ↓
6. 随机选择预制体
   ↓
7. 计算生成位置（花朵位置 + 偏移 + 随机）
   ↓
8. 实例化蝴蝶
   ↓
9. 如果设置了 Lifetime，注册自动销毁
```

---

## 版本历史

**v1.0** - 2026-01-03
- ✨ 支持 ScriptableObject 配置资源
- ✨ 支持本地覆盖配置
- ✨ 自定义 Inspector 增强
- ✨ 快速预设功能
- ✨ 从本地配置一键创建资源

---

## 相关文件

- `ButterflyEffectConfig.cs` - 配置资源类
- `PurificationFlowerBloom.cs` - 花朵组件
- `ButterflyEffectConfigEditor.cs` - 配置资源编辑器
- `PurificationFlowerBloomEditor.cs` - 花朵组件编辑器

---

💡 **提示**：如有疑问或需要新功能，请联系开发团队！

