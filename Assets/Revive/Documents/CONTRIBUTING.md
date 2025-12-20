# Project Revive - 开发贡献指南

欢迎来到 **Project Revive** 项目！本文档将帮助你了解项目的开发规范和协作流程。

---

## 📂 文件夹结构说明

所有团队成员的工作内容**应主要提交到 `Assets/Revive/` 目录下**，按以下结构组织：

### 🎨 美术资产 - `Assets/Revive/Arts/`

```
Assets/Revive/Arts/
├── Font/              # 字体文件
├── Materials/         # 材质球
├── Textures/          # 贴图纹理
├── Skybox/            # 天空盒
└── UI/                # UI相关美术资源
```

**使用说明**:
- 将 DCC 软件（Blender/Maya/3ds Max）导出的资产放在此处
- 模型文件推荐使用 `.fbx` 格式
- 贴图使用 `.png` 或 `.tga` 格式
- 材质球命名规范: `Mat[资产名称]`，如 `MatSlime`, `MatTree`

---

### 🎮 预制件 - `Assets/Revive/Prefabs/`

```
Assets/Revive/Prefabs/
├── Characters/        # 角色预制件（史莱姆、NPC等）
├── Nature/            # 自然物件（树、草、石头等）
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

**使用说明**:
- 所有场景中使用的对象**必须先做成预制件**
- 预制件命名使用帕斯卡命名法: `SlimeWater`, `TreeDry`, `RockCracked`
- 变体预制件放在对应的子文件夹中

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
├── CONTRIBUTING.md    # 本文件
├── TechnicalDesign.md # 技术设计文档（待创建）
└── API_Reference.md   # API参考文档（待创建）
```

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

