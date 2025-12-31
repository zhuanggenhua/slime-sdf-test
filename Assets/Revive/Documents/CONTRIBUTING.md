# Project Revive - 开发贡献指南

欢迎来到 **Project Revive** 项目！本文档将帮助你了解项目的开发规范和协作流程。

---

## 📂 文件夹结构说明

所有团队成员的工作内容**应主要提交到 `Assets/Revive/` 目录下**，按以下结构组织：

### 🎨 美术资产 - `Assets/Revive/Arts/`

```
Assets/Revive/Arts/
├── Model/             # 3D模型源文件（FBX）
│   ├── tree/          # 树木模型
│   ├── stone/         # 石头模型
│   ├── bush/          # 灌木模型
│   ├── flower/        # 花朵模型
│   ├── mushroom/      # 蘑菇模型
│   ├── stump/         # 树桩模型
│   └── bridge/        # 桥梁模型
├── Materials/         # 材质球
├── Textures/          # 贴图纹理
├── Shaders/           # 自定义Shader
├── Font/              # 字体文件
├── Skybox/            # 天空盒
└── UI/                # UI相关美术资源
```

**📘 详细导入指南**：
> **[美术资产导入完整指南](ArtAssetImportGuide.md)** ⭐ 必读！

本文档包含：
- ✅ FBX导入设置详解（Scale Factor、Bake Axis Conversion）
- ✅ Prefab创建规范（Prefab Variant vs 自定义Prefab）
- ✅ 碰撞体添加指南
- ✅ 外部资产包处理流程
- ✅ 常见问题排查

**快速使用说明**:
- 将 DCC 软件（Blender/Maya/3ds Max）导出的 **FBX模型** 放在 `Model/[类型]/` 对应文件夹
- 配置Import Settings（参见详细指南）
- 为模型创建Prefab并放入 `Prefabs/Models/[类型]/` 文件夹（**必须！**）
- 贴图使用 `.png` 或 `.tga` 格式，放入 `Textures/` 文件夹
- 材质球命名规范: `Mat_[资产名称]`，如 `Mat_Slime`, `Mat_Tree`

---

### 🎮 预制件 - `Assets/Revive/Prefabs/`

```
Assets/Revive/Prefabs/
├── Models/            # 3D模型Prefab（对应Arts/Model结构）⭐
│   ├── tree/          # 树木Prefab
│   ├── stone/         # 石头Prefab
│   ├── bush/          # 灌木Prefab
│   ├── flower/        # 花朵Prefab
│   ├── mushroom/      # 蘑菇Prefab
│   ├── stump/         # 树桩Prefab
│   └── bridge/        # 桥梁Prefab
├── Characters/        # 角色预制件（史莱姆、NPC等）
├── Nature/            # 自然物件（组合预制件）
├── Items/             # 道具物品
├── Pickables/         # 可拾取物品（继承PickableItem）
├── Gameplay/          # 游戏玩法相关预制件
├── Effects/           # 特效预制件
├── UI/                # UI预制件
│   └── Shared/        # 共享UI组件
├── Audio/             # 音频相关预制件
├── Lights/            # 灯光预制件
└── Prototyping/       # 原型测试用预制件
```

**⚠️ 重要规范**:
- **关卡搭建必须使用Prefab，不要直接使用FBX模型！**
- 所有场景中使用的对象**必须先做成预制件**
- 3D模型的Prefab必须放在 `Models/[类型]/` 对应文件夹
- 预制件命名使用帕斯卡命名法或小写下划线: `tree001`, `stone_large_02`
- 变体预制件放在对应的子文件夹中

**📘 如何创建Prefab**：参见 **[美术资产导入完整指南](ArtAssetImportGuide.md)** 的 "创建Prefab规范" 章节

---

### 💻 脚本 - `Assets/Revive/Scripts/`

```
Assets/Revive/Scripts/
├── Characters/        # 角色相关脚本
├── AI/                # AI行为脚本
├── Items/             # 物品与交互脚本
├── Environment/       # 环境交互脚本
├── Managers/          # 游戏管理器
├── UI/                # UI逻辑脚本
└── Utils/             # 工具类脚本
```

**命名规范**:
- 类名使用帕斯卡命名法: `SlimeController`, `EcoSystemManager`
- 接口使用 `I` 前缀: `ITransformable`, `IHealable`
- 私有字段使用下划线前缀: `_currentState`, `_healthPoints`

---

### 🎬 场景 - `Assets/Revive/Scenes/`

```
Assets/Revive/Scenes/
├── MainLevel.unity    # 主关卡场景
├── TestScenes/        # 测试场景（不提交到主分支）
└── Prototypes/        # 原型场景
```

**使用说明**:
- 主场景由**技术负责人**负责维护
- 个人测试场景放在 `TestScenes/` 下，命名格式: `Test_[功能]_[姓名]`
- 完成功能后合并到主场景

---

### 📄 文档 - `Assets/Revive/Documents/`

```
Assets/Revive/Documents/
├── CONTRIBUTING.md              # 本文件（开发贡献指南）
├── ArtAssetImportGuide.md       # 美术资产导入完整指南 ⭐
├── TrailSystem_ShaderGuide.md   # 尾迹系统Shader配置指南
└── README.md                    # 项目说明（待创建）
```

**文档说明**:
- **ArtAssetImportGuide.md** - 美术团队必读，包含FBX导入、Prefab创建、Collider设置等完整流程
- **TrailSystem_ShaderGuide.md** - VegetationWind Shader的使用说明，美术配置植被材质时参考
- **CONTRIBUTING.md** - 本文件，团队协作规范总览

---

## 🎨 美术资产快速导入流程

> **详细流程请参阅**: **[美术资产导入完整指南](ArtAssetImportGuide.md)**

### 最小步骤（5分钟）

#### 1️⃣ 放置FBX
```
将模型放入: Assets/Revive/Arts/Model/[类型]/模型名.fbx
例如: Assets/Revive/Arts/Model/tree/tree010.fbx
```

#### 2️⃣ 配置Import Settings
```
选中FBX → Inspector → Model选项卡:
  ✅ Convert Units (勾选)
  ✅ Bake Axis Conversion (勾选，如果DCC是Y-Up)
  点击 Apply
```

#### 3️⃣ 创建Prefab
```
右键FBX → Create → Prefab Variant
重命名并移动到: Assets/Revive/Prefabs/Models/[类型]/模型名.prefab
```

#### 4️⃣ 添加Collider（可选）
```
双击Prefab → Add Component → 选择Collider类型:
  - 树: Capsule Collider
  - 石头（大）: Box Collider
  - 石头（小）: Sphere Collider
  - 灌木: Sphere Collider
```

#### 5️⃣ 测试并提交
```
拖入场景测试 → 确认尺寸、旋转、碰撞正常 → Git提交
```

### 常见问题速查

| 问题 | 解决方案 |
|------|---------|
| 模型太大/太小 | 勾选 Convert Units 或调整 Scale Factor |
| 模型躺平/倒置 | 勾选 Bake Axis Conversion 或在Prefab中调整旋转 |
| 显示粉红色 | 在Prefab中重新分配材质 |
| 玩家穿模 | 添加或调整Collider |

### 文件夹对应关系表

| 资源类型 | FBX位置 | Prefab位置 |
|---------|---------|-----------|
| 树木 | `Arts/Model/tree/` | `Prefabs/Models/tree/` |
| 石头 | `Arts/Model/stone/` | `Prefabs/Models/stone/` |
| 灌木 | `Arts/Model/bush/` | `Prefabs/Models/bush/` |
| 花朵 | `Arts/Model/flower/` | `Prefabs/Models/flower/` |
| 蘑菇 | `Arts/Model/mushroom/` | `Prefabs/Models/mushroom/` |
| 树桩 | `Arts/Model/stump/` | `Prefabs/Models/stump/` |
| 桥梁 | `Arts/Model/bridge/` | `Prefabs/Models/bridge/` |

---

## 🛠️ TopDownEngine 使用规范

### 角色扩展

使用 TopDownEngine 的 `Character` 类作为基础：

```csharp
using MoreMountains.TopDownEngine;

public class SlimeCharacter : Character
{
    // 通过添加新的 MMStateMachine 来扩展逻辑
    public MMStateMachine<SlimeStates> SlimeStateMachine;
    
    protected override void Awake()
    {
        base.Awake();
        // 初始化自定义状态机
        SlimeStateMachine = new MMStateMachine<SlimeStates>(gameObject, true);
    }
}
```

**核心要点**:
- ✅ 继承 `Character` 类，不要修改原始文件
- ✅ 使用 `MMStateMachine` 来管理状态
- ✅ 通过 `CharacterAbility` 添加新能力

---

### AI 系统

使用 TopDownEngine 的 `AIBrain` 来实现敌人/NPC AI：

```csharp
using MoreMountains.TopDownEngine;

public class NPCAnimalBrain : AIBrain
{
    // 使用 AIAction 和 AIDecision 组合实现行为
    // 在 Inspector 中配置状态和转换条件
}
```

**核心要点**:
- ✅ 使用 `AIBrain` + `AIAction` + `AIDecision` 组合
- ✅ 在 Inspector 中可视化配置 AI 行为树
- ❌ 不需要从零编写 AI 系统

---

### 物品交互

实现可交互物品时，继承 `PickableItem`：

```csharp
using MoreMountains.TopDownEngine;
using MoreMountains.InventoryEngine;

public class WaterDroplet : PickableItem
{
    protected override void Pick(GameObject picker)
    {
        base.Pick(picker);
        // 实现吸收水份的逻辑
        SlimeCharacter slime = picker.GetComponent<SlimeCharacter>();
        if (slime != null)
        {
            slime.AbsorbMaterial(MaterialType.Water);
        }
    }
}
```

**核心要点**:
- ✅ 继承 `PickableItem` 实现拾取逻辑
- ✅ 使用 `InventoryEngine` 管理库存
- ✅ 通过 `ButtonActivated` 实现按键交互

---

### 保存加载系统

**使用 MMSaveLoadManager**

```csharp
using MoreMountains.Tools;

// 保存数据
MMSaveLoadManager.Save(gameData, "GameSave.json");

// 加载数据
GameData data = (GameData)MMSaveLoadManager.Load(typeof(GameData), "GameSave.json");
```


---

## 🎵 音频集成 (Wwise)

音效师负责：
- Wwise 工程文件单独管理（不提交到 Unity 项目）
- 通过 Wwise Unity Integration 生成 SoundBank
- SoundBank 文件提交到 `Assets/StreamingAssets/Audio/`

**音频事件调用示例**:
```csharp
AkSoundEngine.PostEvent("Play_Slime_Transform", gameObject);
```

---

## 🔄 Git 工作流程

### 分支管理

```
main                  # 主分支（稳定版本）
├── dev               # 开发分支（日常开发）
│   ├── feature/slime-transform    # 功能分支
│   ├── feature/eco-system         # 功能分支
│   └── fix/collision-bug          # 修复分支
```

### 提交规范

使用清晰的提交信息格式：

```
[类型] 简短描述

详细说明（可选）
```

**类型标签**:
- `feature` - 新功能
- `fix` - Bug修复
- `art` - 美术资源
- `audio` - 音效相关
- `refactor` - 代码重构
- `doc` - 文档更新
- `test` - 测试相关

**示例**:
```
feature: 实现史莱姆水形态变换

- 添加 SlimeWaterState 状态
- 实现吸收水份逻辑
- 添加水滴特效
```

---

## 🐛 调试与测试

### 日志规范
todo

---

## 🎓 学习资源

### TopDownEngine 文档
- 官方文档: https://topdown-engine-docs.moremountains.com/
- API 文档: https://topdown-engine-docs.moremountains.com/API/

### Unity 最佳实践
- Unity 官方文档: https://docs.unity3d.com/

---

---

**祝开发顺利！Let's Revive the World! 🌱**

---

