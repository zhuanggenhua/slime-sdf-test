# 美术资产导入完整指南

**版本**: 1.0  
**更新时间**: 2025-12-30  
**适用对象**: 美术团队、关卡设计师

---

## 📋 目录

1. [导入流程概览](#导入流程概览)
2. [FBX模型导入设置](#fbx模型导入设置)
3. [创建Prefab规范](#创建prefab规范)
4. [碰撞体设置](#碰撞体设置)
5. [外部资产包处理](#外部资产包处理)
6. [常见问题排查](#常见问题排查)
7. [检查清单](#检查清单)

---

## 导入流程概览

### 完整流程图

```
[从DCC导出FBX] 
    ↓
[放入 Arts/Model/对应文件夹] 
    ↓
[配置Import Settings] 
    ↓
[创建Prefab] → [放入 Prefabs/Models/对应文件夹]
    ↓
[添加Collider（可选）]
    ↓
[设置材质]
    ↓
[测试并提交]
```

### 文件夹对应关系

| FBX原始模型位置 | Prefab位置 |
|----------------|-----------|
| `Assets/Revive/Arts/Model/tree/` | `Assets/Revive/Prefabs/Models/tree/` |
| `Assets/Revive/Arts/Model/stone/` | `Assets/Revive/Prefabs/Models/stone/` |
| `Assets/Revive/Arts/Model/bush/` | `Assets/Revive/Prefabs/Models/bush/` |
| `Assets/Revive/Arts/Model/flower/` | `Assets/Revive/Prefabs/Models/flower/` |
| `Assets/Revive/Arts/Model/mushroom/` | `Assets/Revive/Prefabs/Models/mushroom/` |
| `Assets/Revive/Arts/Model/stump/` | `Assets/Revive/Prefabs/Models/stump/` |
| `Assets/Revive/Arts/Model/bridge/` | `Assets/Revive/Prefabs/Models/bridge/` |

---

## FBX模型导入设置

### 第一步：放置FBX文件

1. 将从DCC软件（Blender/Maya/3ds Max）导出的FBX文件放入对应文件夹：
   ```
   Assets/Revive/Arts/Model/[类型]/[模型名称].fbx
   ```

2. **命名规范**：
   - 使用小写字母和数字
   - 用下划线分隔单词
   - 添加变体编号
   
   ✅ **正确示例**：
   ```
   tree001.fbx
   stone_large_002.fbx
   bush_autumn_003.fbx
   ```
   
   ❌ **错误示例**：
   ```
   Tree 1.fbx          （有空格）
   石头002.fbx         （有中文）
   STONE_FINAL_V3.fbx  （全大写）
   ```

### 第二步：配置Import Settings

在Unity中选中FBX文件，在Inspector中配置以下设置：

#### 📐 **Model选项卡**

##### 1. Scale Factor（缩放因子）

**目的**：确保模型在Unity中的尺寸正确

**配置方式**：

- **方法A：使用Convert Units（推荐）**
  ```
  ✅ Scene > Unit Conversion
  勾选 [Convert Units]
  
  Unity会自动根据FBX的单位信息转换
  ```

- **方法B：手动设置Scale Factor**
  ```
  如果DCC中使用的单位是：
  - 厘米 (cm): Scale Factor = 0.01
  - 米 (m):   Scale Factor = 1.0
  - 英寸 (in): Scale Factor = 0.0254
  
  取消勾选 [Convert Units]
  手动输入 Scale Factor
  ```

**验证**：
- 一个"标准树"在Unity中的高度应该在 3-8 单位
- 一个"小石头"在Unity中的大小应该在 0.5-2 单位
- 一个"史莱姆"在Unity中的高度应该在 1-2 单位

##### 2. 旋转设置（Bake Axis Conversion）

**目的**：确保模型在Unity中的朝向正确

Unity使用 **Y-Up, Left-Handed** 坐标系

**配置方式**：

- **情况1：DCC中已按Unity标准设置（Y-Up）**
  ```
  ✅ Scene > Axis Conversion
  勾选 [Bake Axis Conversion]
  
  Unity会自动烘焙轴向转换到网格数据中
  ```

- **情况2：DCC中使用不同坐标系（如Z-Up）**
  ```
  ❌ 不勾选 [Bake Axis Conversion]
  
  导入后在Prefab中手动调整旋转：
  - 如果模型躺平：Rotation X = -90°
  - 如果模型倒置：Rotation X = 180°
  ```

**各DCC软件的坐标系**：
| DCC软件 | 默认坐标系 | 建议设置 |
|---------|-----------|---------|
| Blender | Z-Up | 导出时选择"Forward: -Z, Up: Y" |
| Maya | Y-Up | 默认即可 |
| 3ds Max | Z-Up | 导出时选择"Y-Up" |

##### 3. 其他推荐设置

```
Model选项卡推荐设置：

Scene:
  ✅ Scale Factor: 根据单位设置（见上文）
  ✅ Convert Units: 勾选（推荐）
  ✅ Bake Axis Conversion: 根据情况（见上文）
  ✅ Import Cameras: 取消勾选（不需要导入摄像机）
  ✅ Import Lights: 取消勾选（不需要导入灯光）

Meshes:
  ✅ Read/Write: 勾选（如果需要运行时修改网格）
  ✅ Optimize Mesh: 勾选（优化网格数据）
  ✅ Generate Colliders: 取消勾选（手动添加更精确）

Geometry:
  ✅ Keep Quads: 取消勾选（自动转为三角面）
  ✅ Weld Vertices: 勾选（合并重复顶点）
  ✅ Index Format: Auto（自动选择）
  ✅ Normals: Import（导入法线）
  ✅ Tangents: Calculate Mikk T Space（计算切线）
```

#### 🎨 **Materials选项卡**

```
推荐设置：

Material Creation Mode:
  选择 [None] 或 [Standard (Legacy)]
  
  理由：我们会在Prefab中手动设置材质
  
Location:
  选择 [Use External Materials (Legacy)]
  
  材质会保存在同一文件夹的Materials子文件夹
```

#### 🎬 **Animation选项卡**

```
如果模型包含动画：
  ✅ Import Animation: 勾选

如果是静态模型（树、石头等）：
  ❌ Import Animation: 取消勾选（节省资源）
```

### 第三步：应用设置

配置完成后，点击右下角的 `Apply` 按钮

---

## 创建Prefab规范

### 为什么需要Prefab？

**⚠️ 关键规范**：关卡搭建**必须使用Prefab**，**不要直接使用FBX模型**

**原因**：
1. ✅ 统一修改：修改Prefab会同步更新所有实例
2. ✅ 材质管理：在Prefab中统一设置材质
3. ✅ 碰撞体：在Prefab中添加碰撞体
4. ✅ 脚本挂载：某些模型需要挂载脚本
5. ✅ 版本控制：Prefab的修改记录更清晰

### 方法1：创建Prefab Variant（推荐）

**适用场景**：模型导入设置正确，不需要调整旋转

**步骤**：

1. 在Project窗口中，**右键点击FBX文件**
2. 选择 `Create` → `Prefab Variant`
3. 重命名Prefab（去掉"Variant"后缀）
4. 将Prefab移动到对应的 `Assets/Revive/Prefabs/Models/[类型]/` 文件夹

**示例**：
```
原始FBX:
  Assets/Revive/Arts/Model/tree/tree001.fbx

创建Prefab Variant并重命名为:
  Assets/Revive/Prefabs/Models/tree/tree001.prefab
```

**优点**：
- ✅ 保持与原始FBX的链接
- ✅ FBX更新后Prefab会自动更新
- ✅ 简单快速

### 方法2：创建自定义Prefab

**适用场景**：
- 需要调整模型旋转
- 需要添加多个子对象
- 需要复杂的材质设置

**步骤**：

1. 在Hierarchy窗口中创建空GameObject
2. 重命名为模型名称（如 `tree001`）
3. 双击进入Prefab编辑模式，或拖入场景
4. 从Project窗口拖入FBX模型作为子对象
5. 调整子对象的 Transform：
   ```
   Position: (0, 0, 0)
   Rotation: 根据需要调整（如 X: -90）
   Scale: (1, 1, 1)
   ```
6. 在根对象上添加Collider和脚本
7. 将GameObject拖入 `Assets/Revive/Prefabs/Models/[类型]/` 创建Prefab

**示例结构**：
```
tree001 (Prefab Root)
├── Transform (0, 0, 0)
├── Capsule Collider
└── tree001_model (FBX子对象)
    ├── Transform (0, 0, 0) | Rotation (-90, 0, 0)
    └── Mesh Renderer
```

**优点**：
- ✅ 完全控制Prefab结构
- ✅ 可以在根对象调整Pivot点
- ✅ 适合复杂模型

---

## 材质设置规范

### 为什么需要统一材质规范？

- 统一的视觉风格
- 便于后期批量调整和优化
- 确保正确的渲染效果
- 方便团队协作

### 当前阶段：临时方案（GameJam期间）

**⚠️ 重要说明**：目前使用Unity标准Shader作为临时方案，GameJam结束后会创建项目自定义Shader并**通过脚本自动批量替换**，美术同学无需担心后期修改工作量！

---

### 标准材质创建流程

#### 1. 通用固体材质（树干、石头、泥土、建筑）

**适用对象**：大部分静态模型

**创建步骤**：

1. 在Project窗口中右键 → `Create` → `Material`
2. 命名为 `Mat_[模型名称]`（例如：`Mat_tree001`, `Mat_stone_large`）
3. 将材质拖入对应文件夹：`Assets/Revive/Arts/Materials/[类型]/`

**Shader配置**：
```
Shader: Universal Render Pipeline/Lit

Surface Options:
  ✅ Workflow Mode: Metallic
  ✅ Surface Type: Opaque
  ✅ Render Face: Front
  ✅ Alpha Clipping: 关闭

Surface Inputs:
  Base Map: 主纹理贴图
  Metallic: 0.0 (非金属物体)
  Smoothness: 0.3-0.5 (根据材质调整)
  Normal Map: 法线贴图（如果有）
  Occlusion: 遮蔽贴图（如果有）
```

**快速设置参考**：
| 材质类型 | Smoothness | Metallic | 说明 |
|---------|-----------|----------|------|
| 树干/木材 | 0.3 | 0.0 | 粗糙表面 |
| 石头 | 0.4 | 0.0 | 中等粗糙 |
| 泥土 | 0.2 | 0.0 | 非常粗糙 |
| 叶子 | 0.4 | 0.0 | 略有光泽 |

---

#### 2. 透明/镂空材质（树叶、花草）

**⚠️ 特殊情况 - 尾迹系统用植被请跳过此节，参见下方"植被材质例外"**

**适用对象**：需要透明效果的模型（双面叶子、镂空花瓣等）

**Shader配置**：
```
Shader: Universal Render Pipeline/Lit

Surface Options:
  ✅ Workflow Mode: Metallic
  ✅ Surface Type: Opaque (使用Alpha Clipping而非Transparent)
  ✅ Render Face: Both (双面渲染)
  ✅ Alpha Clipping: 开启
  ✅ Threshold: 0.5

Surface Inputs:
  Base Map: 带Alpha通道的贴图
  Smoothness: 0.4
  Metallic: 0.0
```

**为什么使用Alpha Clipping而非Transparent？**
- ✅ 性能更好（不需要排序）
- ✅ 适合硬边缘的镂空效果（树叶、花瓣）
- ✅ 避免透明度排序问题

---

#### 3. 植被材质（⚠️ 例外情况）

**适用对象**：用于`VegetationGrowthTrail`系统的小草、小花模型

**⚠️ 重要**：这类植被**不使用**URP/Lit，而是使用项目自定义的`Revive/VegetationWind` Shader

**Shader配置**：
```
Shader: Revive/VegetationWind

原因：
  ✅ 支持GPU Instancing（必需！）
  ✅ 内置风摆动效果
  ✅ 支持生长动画

配置参考：
  Base Map: 植被贴图（带Alpha）
  Base Color: (1, 1, 1, 1)
  Alpha Cutoff: 0.5
  Cull Mode: Off (双面)
  
  ⚠️ 必须开启：GPU Instancing (在Inspector底部)
```

**详细配置指南**：参见 [TrailSystem_ShaderGuide.md](TrailSystem_ShaderGuide.md)

---

### 材质命名规范

**格式**：`Mat_[模型名称]_[变体]`

**示例**：
```
✅ 正确命名：
  Mat_tree001
  Mat_stone_large
  Mat_bush_autumn
  Mat_flower001_petal
  Mat_tree002_bark
  Mat_tree002_leaves

❌ 错误命名：
  tree material (有空格)
  材质_树001 (有中文)
  TreeMat (不符合规范)
  MAT_TREE (全大写)
```

---

### 材质文件夹组织

```
Assets/Revive/Arts/Materials/
├── tree/              # 树木材质
│   ├── Mat_tree001.mat
│   └── Mat_tree001_bark.mat
├── stone/             # 石头材质
│   ├── Mat_stone001.mat
│   └── Mat_stone_large.mat
├── vegetation/        # 植被材质（用于尾迹系统）
│   ├── Mat_grass001.mat
│   └── Mat_flower001.mat
├── bush/              # 灌木材质
└── shared/            # 共享材质（如通用地面等）
```

---

### 材质创建检查清单

在创建材质时，确认以下内容：

**基础设置**：
- [ ] Shader选择正确（大部分用URP/Lit）
- [ ] 命名符合规范（Mat_开头）
- [ ] 放在正确的Materials文件夹
- [ ] Base Map贴图已分配
- [ ] Surface Type设置正确（Opaque/Transparent）

**渲染设置**：
- [ ] Metallic值合理（非金属物体通常为0）
- [ ] Smoothness值合理（0.2-0.5）
- [ ] Alpha Clipping按需开启
- [ ] Render Face按需设置（Front/Both）

**特殊情况**：
- [ ] 植被材质使用Revive/VegetationWind
- [ ] 植被材质开启了GPU Instancing
- [ ] 双面材质设置Render Face: Both

---

### 后期优化计划

**GameJam结束后的工作（程序负责，美术无需操心）**：

1. **创建项目自定义Shader** `Revive/Lit`
   - 基于URP Lit改造
   - 添加区域苏醒的颜色渐变功能
   - 添加项目特定的视觉效果

2. **批量替换材质Shader**
   - 使用编辑器工具自动扫描所有材质
   - 一键将URP/Lit替换为Revive/Lit
   - 5分钟内完成，无需手动操作

3. **保留设置**
   - 所有贴图引用保持不变
   - Metallic/Smoothness等参数保持不变
   - 只替换Shader本身

**美术同学需要知道的**：
- ✅ 现在放心使用URP/Lit创建材质
- ✅ 不要自己去改Shader
- ✅ 后期会自动替换，不会增加工作量
- ✅ 专注于贴图和参数调整即可

---

### 快速参考：材质创建速查表

```
📦 通用材质（树、石头、建筑）
   Shader: URP/Lit
   Surface Type: Opaque
   Metallic: 0.0
   Smoothness: 0.3-0.5

🌸 透明材质（树叶、花瓣）
   Shader: URP/Lit
   Surface Type: Opaque
   Alpha Clipping: 开启 (0.5)
   Render Face: Both

🌱 植被材质（尾迹系统用）⚠️
   Shader: Revive/VegetationWind
   ⚠️ GPU Instancing: 必须开启
   Cull Mode: Off
   详见：TrailSystem_ShaderGuide.md

📋 命名规范
   Mat_[模型名]_[变体]
   例如：Mat_tree001, Mat_stone_large
```

---

## 碰撞体设置

### 为什么需要Collider？

- 玩家角色需要与环境碰撞
- 物理交互（推动、堆叠）
- 触发器检测（进入区域）

### 添加Collider的时机

**在Prefab编辑模式中添加**（不要在FBX上直接添加）

### Collider选择指南

| 模型类型 | 推荐Collider | 参数设置 |
|---------|-------------|---------|
| **树木** | Capsule Collider | Radius: 0.3-0.8<br>Height: 3-8<br>Center: (0, height/2, 0) |
| **石头（大）** | Box Collider | 手动调整Size和Center |
| **石头（小）** | Sphere Collider | Radius: 0.5-1.5 |
| **灌木** | Sphere Collider | Radius: 0.8-1.5 |
| **小花** | 不需要 | 纯装饰，不需要碰撞 |
| **蘑菇** | Capsule Collider (小) | Radius: 0.2-0.5 |
| **桥** | Box Collider | 精确匹配桥面 |
| **树桩** | Cylinder → Box Collider | 手动调整 |

### 添加Collider步骤

1. 打开Prefab编辑模式（双击Prefab）
2. 选中根对象或FBX子对象
3. `Add Component` → 搜索并添加对应的Collider
4. 在Scene视图中调整Collider的绿色线框，确保：
   - ✅ 包裹模型的主要部分
   - ✅ 不要过大（影响游戏体验）
   - ✅ 不要过小（玩家可能穿模）
5. 保存Prefab

### 高级技巧：Mesh Collider

**⚠️ 谨慎使用**，会影响性能

**适用场景**：
- 复杂地形
- 需要精确碰撞的特殊模型

**设置**：
```
Mesh Collider:
  ❌ Convex: 取消勾选（静态物体）
  ✅ Cooking Options: 默认即可
```

---

## 外部资产包处理

### 适用场景

从Unity Asset Store或其他来源获取的模型资产

### 导入流程

#### 1. 提取原始模型

将外部资产包中的模型文件提取到：
```
Assets/Revive/Arts/Model/[类型]/
```

**不要**将整个资产包直接放入项目！

#### 2. 提取材质和Shader

如果模型使用特殊材质或Shader：

**材质**：
```
Assets/Revive/Arts/Materials/[类型]/
```

**Shader**：
```
Assets/Revive/Arts/Shaders/
```

**示例结构**：
```
外部资产: "Fantasy Nature Pack"

提取后:
Assets/Revive/Arts/Model/tree/
  ├── fantasy_tree_01.fbx
  └── fantasy_tree_02.fbx

Assets/Revive/Arts/Materials/tree/
  ├── Mat_FantasyTreeBark.mat
  └── Mat_FantasyTreeLeaves.mat

Assets/Revive/Arts/Shaders/
  └── FantasyTreeShader.shader
```

#### 3. 创建Prefab

按照[创建Prefab规范](#创建prefab规范)创建Prefab：
```
Assets/Revive/Prefabs/Models/tree/
  ├── fantasy_tree_01.prefab
  └── fantasy_tree_02.prefab
```

#### 4. 材质引用处理

**选项A：直接使用外部材质**
- 将材质复制到 `Arts/Materials/`
- 在Prefab中引用

**选项B：转换为项目标准材质**
- 创建新的URP材质
- 复制贴图和设置
- 统一项目材质风格（推荐）

### 外部资产命名规范

给外部资产添加前缀以区分来源：

```
资产包: "Nature Mega Pack"
前缀: nmp_

导入后:
  nmp_tree001.fbx
  nmp_rock_large.fbx
  Mat_nmp_bark.mat
```

---

## 常见问题排查

### 问题1：模型尺寸错误

**症状**：模型太大或太小

**解决方案**：
1. 检查Import Settings中的Scale Factor
2. 确认DCC中的单位设置
3. 勾选Convert Units让Unity自动处理
4. 如果还不对，手动调整Scale Factor

**参考尺寸**：
- 史莱姆高度：1-2 单位
- 树高度：3-8 单位
- 大石头：1-3 单位
- 小草：0.1-0.5 单位

### 问题2：模型旋转错误

**症状**：模型躺平、倒置或朝向错误

**解决方案A：在Import Settings中修复**
1. 选中FBX
2. Model选项卡
3. 勾选 Bake Axis Conversion
4. 点击Apply

**解决方案B：在Prefab中修复**
1. 创建自定义Prefab（方法2）
2. 将FBX作为子对象
3. 在子对象上调整Rotation
4. 保存Prefab

### 问题3：材质丢失或错误

**症状**：模型显示为粉红色

**解决方案**：
1. 进入Prefab编辑模式
2. 选中Mesh Renderer组件
3. 在Materials列表中重新分配材质
4. 如果材质不存在，创建新材质：
   ```
   右键 → Create → Material
   命名: Mat_[模型名称]
   Shader: Universal Render Pipeline/Lit
   ```

### 问题4：法线错误

**症状**：模型表面有奇怪的光照效果

**解决方案**：
1. 选中FBX
2. Model选项卡 → Normals
3. 改为 Calculate（重新计算法线）
4. 或改为 Import（使用DCC中的法线）
5. 点击Apply

### 问题5：Collider不准确

**症状**：玩家穿模或卡住

**解决方案**：
1. 进入Prefab编辑模式
2. 在Scene视图中调整Collider参数
3. 确保绿色线框与模型大致匹配
4. 使用简单Collider（Box/Sphere/Capsule）而非Mesh Collider

### 问题6：Prefab变更不同步

**症状**：修改Prefab后场景中的实例没更新

**解决方案**：
1. 确认使用的是Prefab实例而非Prefab Variant
2. 右键Prefab实例 → Prefab → Unpack Completely
3. 重新拖入更新后的Prefab
4. 或在Hierarchy中选中实例 → Overrides → Revert All

### 问题7：不知道该用哪个Shader

**症状**：创建材质时不确定选择哪个Shader

**解决方案**：

**大部分情况（90%）**：
```
Shader: Universal Render Pipeline/Lit
```

**特殊情况1 - 植被（用于尾迹系统）**：
```
Shader: Revive/VegetationWind
条件：模型会被VegetationGrowthTrail系统动态生成
```

**特殊情况2 - 需要透明/镂空**：
```
Shader: Universal Render Pipeline/Lit
Surface Type: Opaque
Alpha Clipping: 开启
Render Face: Both（双面）
```

**何时用什么Shader速记**：
- 树干、石头、建筑 → URP/Lit
- 树叶、花朵（静态） → URP/Lit + Alpha Clipping
- 小草、小花（动态生成） → Revive/VegetationWind

### 问题8：担心后期需要改Shader

**症状**：不确定现在用URP/Lit是否合适，担心后期返工

**解答**：

✅ **完全不用担心！**

1. 现在使用URP/Lit是**正确的临时方案**
2. GameJam结束后会创建项目自定义Shader（Revive/Lit）
3. 届时使用**编辑器工具自动批量替换**，5分钟完成
4. 你的所有设置（贴图、参数）都会保留
5. 无需手动修改任何材质

**程序会负责**：
- 创建Revive/Lit Shader
- 写批量替换工具
- 一键替换所有材质

**美术只需要**：
- 现在放心使用URP/Lit
- 专注于贴图和参数调整
- 无需担心后续工作量

---

## 检查清单

### 导入FBX时的检查项

- [ ] FBX文件放在正确的 `Arts/Model/[类型]/` 文件夹
- [ ] 文件命名符合规范（小写、下划线、编号）
- [ ] Import Settings中设置了正确的Scale Factor或勾选Convert Units
- [ ] 根据DCC坐标系设置了Bake Axis Conversion
- [ ] 取消勾选Import Cameras和Import Lights
- [ ] 静态模型取消勾选Import Animation
- [ ] 点击Apply应用设置

### 创建Prefab时的检查项

- [ ] Prefab保存在对应的 `Prefabs/Models/[类型]/` 文件夹
- [ ] Prefab命名与FBX一致（去掉扩展名）
- [ ] 如果使用自定义Prefab，FBX作为子对象
- [ ] 模型在Unity中的朝向正确（Y轴朝上）
- [ ] 模型在Unity中的尺寸合理
- [ ] 已添加合适的Collider（如果需要）
- [ ] 已设置材质（不是粉红色）
- [ ] 材质使用正确的Shader（通常是URP/Lit）
- [ ] 材质命名符合规范（Mat_开头）
- [ ] 植被材质开启了GPU Instancing（如果适用）

### 提交前的检查项

- [ ] 在场景中测试Prefab（拖入场景）
- [ ] 玩家能正常碰撞（如果有Collider）
- [ ] 材质显示正确
- [ ] 没有Console错误或警告
- [ ] Prefab文件和.meta文件都提交
- [ ] 提交信息清晰（如："art: 添加树木模型001-005"）

---

## 快速参考卡片

### 导入设置速查表

```
📐 Model选项卡:
  Scale Factor: 根据单位（cm=0.01, m=1.0）
  ✅ Convert Units
  ✅ Bake Axis Conversion (如果DCC是Y-Up)
  ❌ Import Cameras
  ❌ Import Lights
  ✅ Optimize Mesh

🎨 Materials选项卡:
  Material Creation Mode: None
  Location: Use External Materials (Legacy)

🎬 Animation选项卡:
  静态模型: ❌ Import Animation
```

### Collider速查表

```
🌳 树: Capsule (R: 0.5, H: 5)
🪨 石头（大）: Box
⚫ 石头（小）: Sphere (R: 1.0)
🌿 灌木: Sphere (R: 1.0)
🌸 小花: 无
🍄 蘑菇: Capsule (R: 0.3, H: 0.5)
🌉 桥: Box
```

### 材质速查表

```
📦 通用材质：URP/Lit
   - Surface Type: Opaque
   - Metallic: 0.0
   - Smoothness: 0.3-0.5

🌸 透明材质：URP/Lit
   - Alpha Clipping: 开启
   - Render Face: Both

🌱 植被材质：Revive/VegetationWind ⚠️
   - GPU Instancing: 必须开启
```

### 文件夹速查表

```
FBX原始模型:    Assets/Revive/Arts/Model/[类型]/
Prefab:         Assets/Revive/Prefabs/Models/[类型]/
材质:           Assets/Revive/Arts/Materials/[类型]/
Shader:         Assets/Revive/Arts/Shaders/
贴图:           Assets/Revive/Arts/Textures/[类型]/
```

---

## 工作流程示例

### 示例：导入一棵新树

**DCC阶段（Blender）**：
```
1. 在Blender中创建树模型
2. 确认坐标系：Y-Up
3. 确认单位：米(m)
4. 应用所有变换（Apply All Transforms）
5. 导出为FBX：
   - Forward: -Z
   - Up: Y
   - 勾选 Apply Transform
```

**Unity导入阶段**：
```
1. 将tree010.fbx放入:
   Assets/Revive/Arts/Model/tree/

2. 选中tree010.fbx，在Inspector中:
   Model选项卡:
     ✅ Convert Units (勾选)
     ✅ Bake Axis Conversion (勾选)
     ✅ Optimize Mesh (勾选)
   点击 Apply

3. 在Scene中测试尺寸和朝向
```

**创建Prefab阶段**：
```
1. 右键tree010.fbx → Create → Prefab Variant

2. 重命名为 tree010.prefab

3. 移动到:
   Assets/Revive/Prefabs/Models/tree/

4. 双击打开Prefab编辑模式

5. 添加 Capsule Collider:
   - Radius: 0.4
   - Height: 6
   - Center: (0, 3, 0)

6. 设置材质:
   - 创建新材质：右键 → Create → Material
   - 命名：Mat_tree010
   - Shader: Universal Render Pipeline/Lit
   - Surface Type: Opaque
   - Metallic: 0.0, Smoothness: 0.4
   - 分配Base Map贴图（如果有）

7. 保存并关闭Prefab
```

**测试阶段**：
```
1. 将tree010.prefab拖入场景

2. 测试：
   - 玩家能碰撞到树
   - 材质显示正确
   - 尺寸合理

3. 调整Collider或材质（如果需要）
```

**提交阶段**：
```
1. Git提交:
   git add Assets/Revive/Arts/Model/tree/tree010.fbx
   git add Assets/Revive/Arts/Model/tree/tree010.fbx.meta
   git add Assets/Revive/Prefabs/Models/tree/tree010.prefab
   git add Assets/Revive/Prefabs/Models/tree/tree010.prefab.meta

2. 提交信息:
   art: 添加树木模型tree010
   
   - 新增高大的松树模型
   - 已配置Capsule Collider
   - 材质：Mat_tree010（URP/Lit）
```

---

## 联系与支持

**遇到问题？**
- 查看本文档的[常见问题排查](#常见问题排查)章节
- 在团队Discord/微信群询问技术负责人
- 查看Unity官方文档：[Model Import Settings](https://docs.unity3d.com/Manual/FBXImporter-Model.html)

**文档更新**：
- 如果发现新的问题或最佳实践，请更新本文档
- 或通知技术负责人添加

---

**祝导入顺利！让我们一起创造美丽的游戏世界！** 🌳🪨🌸

---

*文档版本: 1.0*  
*最后更新: 2025-12-30*

