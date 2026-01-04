using System;
using System.Collections.Generic;
using System.ComponentModel;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Runtime.CompilerServices;
using MoreMountains.TopDownEngine;
using MoreMountains.Tools;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling; 
using Revive.Environment;
using Revive.Environment.Watering;

 
namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆PBF模拟器 - 基于Position Based Fluids的史莱姆物理模拟
    /// </summary>
    public class Slime_PBF : MonoBehaviour
    {
        // ...
        [System.Serializable]
        public enum RenderMode
        {
            Particles,  // 粒子模式 - 显示原始粒子
            Surface,    // 表面模式 - 使用Marching Cubes生成表面
        }
        
        private struct SlimeInstance
        {
            public bool Active;
            public float3 Center;
            public Vector3 Pos;
            public Vector3 Dir;
            public float Radius;
            public int ControllerSlot;
        }
        
        private const int DefaultInitialMainCount = 800;
        private const int MinParticlePoolCapacity = DropletSubsystem.DROPLET_START + DropletSubsystem.DROPLET_CAPACITY;
        private const int MaxParticlePoolCapacity = 32768;
        private const int batchCount = 64;
        private const int MainBodyMaxParticles = DropletSubsystem.DROPLET_START;
        private const float MainInitSpacingSim = 0.5f;
        private const float MainRespawnRandomOffsetScale = 0.5f;
        private const float RandomCenterOffset = 0.5f;

        private const float DefaultSimToWorldScale = 0.1f;
        private const float DefaultWorldToSimScale = 1f / DefaultSimToWorldScale;

        [ChineseHeader("坐标缩放")]
        [ChineseLabel("Sim→World缩放"), Tooltip("模拟坐标到世界坐标的缩放比例（world = sim * scale）。")]
        [SerializeField, Range(0.01f, 1f), DefaultValue(0.1f)]
        private float simToWorldScale = DefaultSimToWorldScale;

        private float SimToWorldScale
        {
            get
            {
                if (float.IsNaN(simToWorldScale) || float.IsInfinity(simToWorldScale) || simToWorldScale <= 0f)
                    return DefaultSimToWorldScale;
                return simToWorldScale;
            }
        }

        private float WorldToSimScale => 1f / SimToWorldScale;
        private float SlimeWorldScaleFactor => SimToWorldScale / DefaultSimToWorldScale;

        private float ParticleRadiusWorldScaled => particleRadiusWorld * SlimeWorldScaleFactor;
        
        #region 【核心物理参数】
        
        [ChineseHeader("核心物理参数")]
        [ChineseLabel("粘性强度"), Tooltip("控制粒子之间的粘性力，值越大史莱姆越粘稠")]
        [SerializeField, Range(0, 100), DefaultValue(10f)] 
        private float viscosityStrength = 10f;
        
        [ChineseLabel("聚集强度"), Tooltip("控制粒子向中心聚集的力度，值越大史莱姆越紧密")]
        [SerializeField, Range(0.1f, 100), DefaultValue(50f)] 
        private float concentration = 50f;
        
        [ChineseLabel("基准速度(世界)"), Tooltip("当前参数对应的基准速度，高于此速度时动态增强聚集力")]
        [SerializeField, Range(0.5f, 5f), DefaultValue(1.5f)]
        private float baseSpeed = 1.5f;
        
        [ChineseLabel("水平形变上限(世界)"), Tooltip("XZ方向粒子距离中心的最大距离，0=禁用")]
        [SerializeField, Range(0f, 5f), DefaultValue(1f)]
        private float maxDeformDistXZ = 1f;
        
        [ChineseLabel("垂直形变上限(世界)"), Tooltip("Y方向粒子距离中心的最大距离，通常比水平更小以防止落地散开")]
        [SerializeField, Range(0f, 5f), DefaultValue(2.5f)]
        private float maxDeformDistY = 2.5f;
        
        [ChineseLabel("碰撞形变限制"), Tooltip("碰撞后是否应用形变上限，防止落地时过度扩展")]
        [SerializeField, DefaultValue(true)]
        private bool enableCollisionDeformLimit = true;
        
        [ChineseLabel("速度一致化"), Tooltip("启用后，钳制位移不会注入速度，减少炒开但可能导致滞后")]
        [SerializeField, DefaultValue(true)]
        private bool enableVelocityConsistency = true;
        
        [ChineseLabel("重力"), Tooltip("负值向下，正值向上")]
        [SerializeField, Range(-10, 10), DefaultValue(-5f)] 
        private float gravity = -5f;
        
        [ChineseLabel("表面阈值"), Tooltip("用于Marching Cubes生成表面的密度阈值")]
        [SerializeField, Range(0, 5), DefaultValue(2f)] 
        private float threshold = 2f;
        
        [ChineseLabel("各向异性渲染"), Tooltip("启用后表面更平滑但性能耗费更高")]
        [SerializeField, DefaultValue(true)] 
        private bool useAnisotropic = false;
        
        #endregion
        
        #region 【模拟参数】
        
        [ChineseHeader("模拟参数")]
        private float deltaTime => Time.fixedDeltaTime;
        
        [ChineseLabel("预测步长"), Tooltip("位置预测的时间步长")]
        [SerializeField, Range(0.01f, 0.05f), DefaultValue(0.02f)]
        private float predictStep = 0.02f;
        
        [ChineseLabel("目标密度"), Tooltip("PBF目标密度")]
        [SerializeField, Range(0.5f, 5f), DefaultValue(1.5f)]
        private float targetDensity = 1.5f;
        
        [ChineseLabel("速度衰减"), Tooltip("每帧速度衰减系数")]
        [SerializeField, Range(0.9f, 1f), DefaultValue(0.99f)]
        private float velocityDamping = 0.99f;

        [ChineseLabel("最大速度"), Tooltip("粒子最大速度限制")]
        [SerializeField, Range(10f, 100f), DefaultValue(30f)]
        private float maxVelocity = 30f;
        
        [ChineseLabel("脸部高度系数"), Tooltip("脸/眼睛在史莱姆表面的垂直位置，相对于半径的比例")]
        [SerializeField, Range(0f, 0.9f), DefaultValue(0.2f)]
        private float faceHeightFactor = 0.2f;
        
        [ChineseLabel("垂直偏移"), Tooltip("控制中心的垂直偏移系数，让史莱姆更立体")]
        [SerializeField, Range(0f, 0.5f), DefaultValue(0.2f)]
        private float verticalOffset = 0.2f;
        
        [ChineseLabel("水珠垂直偏移"), Tooltip("控制水珠凝聚中心的垂直偏移系数，让水珠更立体（仅影响 SceneDroplet 的凝聚方向）")]
        [SerializeField, Range(0f, 0.5f), DefaultValue(0f)]
        private float dropletVerticalOffset = 0f;
        
        #endregion
        
        #region 【发射参数】
        
        [ChineseHeader("发射参数")]
        [ChineseLabel("发射速度"), Tooltip("粒子被发射时的初始速度")]
        [SerializeField, Range(0.1f, 10f), DefaultValue(1f)]
        private float emitSpeed = 1f;
        
        [ChineseLabel("发射角度阈值"), Tooltip("值越高发射范围越窄（0=全方向, 1=正前方）")]
        [SerializeField, Range(0.5f, 1.0f), DefaultValue(0.7f)]
        private float emitAngleThreshold = 0.7f;
        
        [ChineseLabel("发射冷却"), Tooltip("两次发射之间的最小间隔（秒）")]
        [SerializeField, Range(0.05f, 1f), DefaultValue(0.2f)]
        private float emitCooldown = 0.2f;
        
        [ChineseLabel("发射批量"), Tooltip("单次发射的粒子数量")]
        [SerializeField, Range(1, 200), DefaultValue(50)]
        private int emitBatchSize = 50;

        [ChineseLabel("发射中心前移(半径倍数)"), Tooltip("用于选择发射粒子区域的中心点：center + dir * (mainRadius * factor)")]
        [SerializeField, Range(0f, 2f), DefaultValue(0.5f)]
        private float emitSpawnForwardRadiusFactor = 0.5f;

        [ChineseLabel("发射中心上移(半径倍数)"), Tooltip("用于选择发射粒子区域的中心点：y = center.y + mainRadius * factor")]
        [SerializeField, Range(0f, 2f), DefaultValue(0.5f)]
        private float emitSpawnUpRadiusFactor = 0.5f;

        [ChineseLabel("发射仰角(度)"), Tooltip("控制发射速度方向的仰角（只影响最终速度方向，不影响水平瞄准）")]
        [SerializeField, Range(0f, 80f), DefaultValue(35f)]
        private float emitPitchDegrees = 35f;
        
        /// <summary>发射冷却时间（秒）</summary>
        public float EmitCooldown => emitCooldown;
        
        /// <summary>单次发射粒子数量</summary>
        public int EmitBatchSize => emitBatchSize;
        
        #endregion

        #region 【发射音效】

        [ChineseHeader("发射音效")]
        [ChineseLabel("首次发射音效")]
        [SerializeField]
        private AudioClip emitFirstSfx;

        [ChineseLabel("连续发射音效")]
        [SerializeField]
        private AudioClip emitRepeatSfx;

        #endregion

        #region 【融合音效】

        [ChineseHeader("融合音效")]
        [ChineseLabel("融合音效"), Tooltip("分离粒子/水珠融合回主体时播放")]
        [SerializeField]
        private AudioClip mergeSfx;

        [ChineseLabel("融合音效音量")]
        [SerializeField, Range(0f, 1f), DefaultValue(1f)]
        private float mergeSfxVolume = 1f;

        [ChineseLabel("融合音效冷却(秒)")]
        [SerializeField, Range(0f, 1f), DefaultValue(0.15f)]
        private float mergeSfxCooldownSeconds = 0.15f;

        private float _lastMergeSfxUnscaledTime = -999f;
        private int _lastMergeSfxFixedSerial = -999999;

        #endregion
        
        #region 【控制器参数】
        
        [ChineseHeader("控制器参数")]
        [ChineseLabel("核心范围"), Tooltip("此范围内的粒子被认为是主体")]
        [SerializeField, Range(5f, 30f), DefaultValue(20f)]
        private float coreRadius = 20f;
        
        #endregion
        
        #region 【粒子池扩展】
        
        [ChineseHeader("粒子池扩展（B2架构）")]
        [ChineseLabel("最大粒子数"), Tooltip("预分配的最大粒子池容量，最小16384以支持水珠固定分区")]
        [SerializeField, Range(MinParticlePoolCapacity, MaxParticlePoolCapacity), DefaultValue(MinParticlePoolCapacity)]  // 最小值改为16384
        private int maxParticles = MinParticlePoolCapacity;  // 必须≥16384，水珠使用固定分区[8192-16383]
        
        [ChineseLabel("活跃粒子数"), Tooltip("当前参与模拟的粒子数（只读）")]
        [SerializeField]
        private int activeParticles = DefaultInitialMainCount;
        
        #endregion
        
        #region 【召回参数】
        
        [ChineseHeader("召回避障")]
        [ChineseLabel("台阶最大高度"), Tooltip("可自动跨越的台阶高度（米，世界坐标）")]
        [SerializeField, Range(0f, 10f), DefaultValue(3f)]
        private float recallStepMaxHeight = 3f;

        [ChineseLabel("台阶提前触发距离"), Tooltip("距离障碍物此距离内判定为台阶可起跳（米，世界坐标）")]
        [SerializeField, Range(0f, 1f), DefaultValue(0.1f)]
        private float recallStepTriggerAdvance = 0.1f;

        [ChineseLabel("台阶高度补偿百分比"), Tooltip("在实际台阶高度基础上额外上抬的百分比")]
        [SerializeField, Range(0f, 1f), DefaultValue(0.2f)]
        private float recallHeightCompPercent = 0.2f;

        [ChineseLabel("台阶起跳速度倍率"), Tooltip("基于物理高度计算的起跳速度再乘以该倍率")]
        [SerializeField, Range(0.2f, 3f), DefaultValue(1f)]
        private float recallStepJumpSpeedScale = 1f;

        [ChineseLabel("台阶最大上抬速度"), Tooltip("台阶跳跃时向上速度上限（米/秒，世界坐标）")]
        [SerializeField, Range(0.1f, 20f), DefaultValue(3f)]
        private float recallStepMaxUpSpeed = 3f;
        
        [ChineseLabel("贴墙滑动权重"), Tooltip("沿障碍物表面滑动的权重")]
        [SerializeField, Range(0f, 1f), DefaultValue(0.8f)]
        private float recallSlideWeight = 0.8f;
        
        [ChineseLabel("障碍检测距离"), Tooltip("前方检测障碍物的距离（米，世界坐标）")]
        [SerializeField, Range(0.1f, 5f), DefaultValue(0.3f)]
        private float recallObstacleCheckDist = 0.3f;

        [ChineseLabel("投影保持距离边距"), Tooltip("召回被墙阻挡时，目标点在墙前额外保持的距离（米，世界坐标）")]
        [SerializeField, Range(0f, 1f), DefaultValue(0.1f)]
        private float recallObstacleKeepDistanceMargin = 0.1f;

        [ChineseLabel("召回避障使用静态SDF"), Tooltip("仅影响召回避障的静态SDF采样；关闭可快速判断是否为SDF烘焙/加载问题。")]
        [SerializeField, DefaultValue(true)]
        private bool recallAvoidUseStaticSdf = true;

        [ChineseHeader("召回全局流场")]
        [ChineseLabel("启用全局流场"), Tooltip("以玩家为中心构建2D距离场，为召回提供可绕墙的方向引导（仅静态SDF）。")]
        [SerializeField, DefaultValue(false)]
        private bool recallUseGlobalFlowField = false;

        [ChineseLabel("流场范围"), Tooltip("流场覆盖玩家周围的半径（米，世界坐标）。")]
        [SerializeField, Range(2f, 30f), DefaultValue(10f)]
        private float recallGlobalFlowRange = 10f;

        [ChineseLabel("流场格子尺寸"), Tooltip("2D网格的格子尺寸（米，世界坐标）。")]
        [SerializeField, Range(0.25f, 1.0f), DefaultValue(0.5f)]
        private float recallGlobalFlowCellSize = 0.5f;

        [ChineseLabel("流场更新间隔帧"), Tooltip("每隔N帧更新一次距离场，降低CPU开销。")]
        [SerializeField, Range(1, 30), DefaultValue(6)]
        private int recallGlobalFlowUpdateIntervalFrames = 6;

        [ChineseLabel("流场障碍距离"), Tooltip("SDF距离小于该值视为障碍（米，世界坐标）。")]
        [SerializeField, Range(0.02f, 1.0f), DefaultValue(0.15f)]
        private float recallGlobalFlowBlockedDistance = 0.15f;

        [ChineseLabel("流场采样半高"), Tooltip("SDF采样的上下偏移半高（米，世界坐标），用于更稳定地检测墙体。")]
        [SerializeField, Range(0.05f, 2.0f), DefaultValue(0.6f)]
        private float recallGlobalFlowSampleHalfHeight = 0.6f;

        #endregion

        #region 【CCA控制器参数】
        
        [ChineseHeader("CCA控制器参数")]
        [ChineseLabel("主体半径扩展系数"), Tooltip("主体实际半径 = 最大粒子距离 × 此系数")]
        [SerializeField, Range(1.0f, 2.0f), DefaultValue(1.1f)]
        private float mainRadiusScale = 1.1f;
        
        [ChineseLabel("主体重叠判断系数"), Tooltip("距离 < 主体半径 × 此系数 时认为是主体的一部分")]
        [SerializeField, Range(0.5f, 1.0f), DefaultValue(0.6f)]
        private float mainOverlapThreshold = 0.6f;
        
        [ChineseLabel("主体半径收缩速率"), Tooltip("每帧最多收缩的比例，防止 mainRadius 突然变小导致大规模分离")]
        [SerializeField, Range(0.95f, 1.0f), DefaultValue(0.99f)]
        private float mainRadiusShrinkRate = 0.99f;
        
        [ChineseLabel("启用自动分离"), Tooltip("禁用后只有主动发射才会分离，冲击不会导致粒子散开")]
        [SerializeField, DefaultValue(true)]
        private bool enableAutoSeparate = true;
        
        [ChineseLabel("最小切割粒子数"), Tooltip("只有连通组件>=此数量才会分离，防止小碎片分离")]
        [SerializeField, Range(10, 200), DefaultValue(30)]
        private int minSeparateClusterSize = 30;

        [ChineseLabel("淡出粒子阈值"), Tooltip("分离控制器粒子数低于此值才会进入淡出判定")]
        [SerializeField, Range(5, 200), DefaultValue(30)]
        private int separatedFadeOutParticleThreshold = 30;

        [ChineseLabel("淡出确认帧数"), Tooltip("连续低于阈值多少帧后才开始淡出")]
        [SerializeField, Range(1, 60), DefaultValue(10)]
        private int separatedFadeOutConfirmFrames = 10;

        [ChineseLabel("淡出持续帧数"), Tooltip("开始淡出后，逐步休眠的总帧数")]
        [SerializeField, Range(1, 120), DefaultValue(20)]
        private int separatedFadeOutFrames = 20;
        
        [ChineseLabel("分离延迟帧数"), Tooltip("粒子需连续离开主体多少帧才判定为分离，防止高速移动时瞬间分离")]
        [SerializeField, Range(1, 30), DefaultValue(10)]
        private int separateDelayFrames = 10;
        
        [ChineseLabel("发射自由帧数"), Tooltip("发射后粒子不受主体影响的帧数")]
        [SerializeField, Range(30, 300), DefaultValue(120)]
        private int emitFreeFrames = 120;
        
        #endregion
        
        #region 【粒子距离保护】
        
        [ChineseHeader("粒子距离保护")]
        [ChineseLabel("启用距离保护"), Tooltip("超过最大距离的粒子会被休眠")]
        [SerializeField, DefaultValue(true)]
        private bool enableDistanceCulling = true;
        
        [ChineseLabel("最大允许距离"), Tooltip("粒子离主体超过此距离会被休眠（模拟坐标）")]
        [SerializeField, Range(50f, 500f), DefaultValue(200f)]
        private float maxParticleDistance = 200f;
        
        [ChineseLabel("检查间隔秒数"), Tooltip("每N秒检查一次，因为很难出现所以不用太频繁")]
        [SerializeField, Range(1f, 30f), DefaultValue(10f)]
        private float distanceCullingInterval = 10f;
        
        private float _lastCullingTime = float.MinValue;
        
        #endregion
        
        #region 【渲染资源】
        
        [ChineseHeader("渲染资源")]
        [ChineseLabel("脸部网格"), Tooltip("史莱姆脸部表情使用的网格")]
        [SerializeField] private Mesh faceMesh;
        
        [ChineseLabel("脸部材质"), Tooltip("史莱姆脸部表情使用的材质")]
        [SerializeField] private Material faceMat;
        
        [ChineseLabel("表面材质"), Tooltip("Surface模式下史莱姆主体的渲染材质")]
        [SerializeField] private Material mat;
        
        [ChineseLabel("粒子网格"), Tooltip("Particles模式下单个粒子使用的网格")]
        [SerializeField] private Mesh particleMesh;

        private static readonly int _surfaceColorId = Shader.PropertyToID("_Color");
        private static readonly int _slimeBlurStrengthId = Shader.PropertyToID("_BlurStrength");
        private static readonly int _slimeDistortionStrengthId = Shader.PropertyToID("_DistortionStrength");
        private MaterialPropertyBlock _surfaceTintBlock;
        private bool _surfaceTintEnabled;
        private Color _surfaceTintColor = Color.white;

        private SlimeCarrySlot _carrySlot;

        [ChineseHeader("携带物显示")]
        [ChineseLabel("携带物时降低主体模糊/扭曲")]
        [SerializeField, DefaultValue(false)]
        private bool reduceDistortionWhenCarrying;

        [ChineseLabel("携带物时模糊强度")]
        [SerializeField, Range(0f, 5f), DefaultValue(0f)]
        private float carryBlurStrength;

        [ChineseLabel("携带物时扭曲强度")]
        [SerializeField, Range(0f, 50f), DefaultValue(0f)]
        private float carryDistortionStrength;

        [ChineseHeader("水珠渲染")]
        [ChineseLabel("水珠表面材质(可选)"), Tooltip("Surface模式下水珠网格使用的材质；为空则复用主体材质")]
        [SerializeField] private Material dropletSurfaceMat;
        
        [ChineseLabel("粒子材质"), Tooltip("Particles模式下粒子的渲染材质")]
        [SerializeField] private Material particleMat;
        
        [ChineseLabel("气泡材质"), Tooltip("史莱姆内部气泡效果的渲染材质")]
        [SerializeField] private Material bubblesMat;

        private static readonly int _bubblesSizeId = Shader.PropertyToID("_Size");
        private static readonly int _bubblesSimToWorldScaleId = Shader.PropertyToID("_SimToWorldScale");
        private static readonly int _bubblesBufferId = Shader.PropertyToID("_BubblesBuffer");
        private static readonly int _bubblesPredictOffsetWorldId = Shader.PropertyToID("_PredictOffsetWorld");
        private static readonly int _faceTextureId = Shader.PropertyToID("_Texture2D");
        private static readonly int _faceColorId = Shader.PropertyToID("_Color");
        private MaterialPropertyBlock _bubblesBlock;
        private float _bubblesBaseSize = 0.035f;
        private bool _bubblesBaseSizeCached;

        private Material _slimeStencilMaskMat;
        private Material _faceStencilMat;
        private Material _bubblesStencilMat;

        [ChineseHeader("气泡效果")]
        [ChineseLabel("启用气泡")]
        [SerializeField, DefaultValue(true)]
        private bool enableBubbles = true;

        [ChineseLabel("气泡速度"), Tooltip("控制内部气泡的生成概率/活跃度")]
        [SerializeField, Range(0f, 100f), DefaultValue(10f)]
        private float bubbleSpeed = 10f;

        private float _bubbleBoostEndTime = -1f;
        private float _bubbleBoostMultiplier = 1f;
        private float _bubbleBoostSizeMultiplier = 1f;

        private bool _consumeWindFieldImmune;
        
        #endregion

        public void SetSurfaceTint(Color tint, bool enabled)
        {
            _surfaceTintEnabled = enabled;
            _surfaceTintColor = tint;
            if (!_surfaceTintEnabled)
            {
                return;
            }

            if (_surfaceTintBlock == null)
            {
                _surfaceTintBlock = new MaterialPropertyBlock();
            }

            _surfaceTintBlock.SetColor(_surfaceColorId, tint);
        }

        private bool ShouldReduceDistortionForCarry()
        {
            if (!reduceDistortionWhenCarrying)
                return false;

            if (_carrySlot == null)
                _carrySlot = GetComponentInChildren<SlimeCarrySlot>(true);

            return _carrySlot != null && _carrySlot.HasHeldObject;
        }

        public bool TryGetSurfaceBaseColor(out Color color)
        {
            if (mat != null && mat.HasProperty(_surfaceColorId))
            {
                color = mat.GetColor(_surfaceColorId);
                return true;
            }

            color = Color.white;
            return false;
        }

        public void SetConsumeWindFieldImmune(bool immune)
        {
            _consumeWindFieldImmune = immune;
        }

        public void TriggerConsumeBubbleBurst(int count, float lifetimeSeconds, float radiusMultiplier, float upSpeedWorld)
        {
            if (!_runtimeInitialized)
                return;
            if (!_bubblesBuffer.IsCreated || !_bubblesPoolBuffer.IsCreated)
                return;

            int spawnCount = Mathf.Min(Mathf.Max(0, count), _bubblesPoolBuffer.Length);
            if (spawnCount <= 0)
                return;

            uint seed = (uint)(Time.frameCount * 9781 + spawnCount * 6271);
            if (seed == 0)
                seed = 1;
            var rnd = new global::Unity.Mathematics.Random(seed);

            Vector3 centerWorld = MainBodyCentroidWorld;
            if (centerWorld == Vector3.zero && trans != null)
                centerWorld = trans.position;
            float3 centerSim = (float3)(centerWorld * WorldToSimScale);

            float radiusSim = MainBodyRadiusWorld * WorldToSimScale;
            radiusSim = math.max(radiusSim, 0.5f);

            float life = Mathf.Max(0f, lifetimeSeconds);
            float upSpeedSim = Mathf.Max(0f, upSpeedWorld) * WorldToSimScale;
            float rMul = Mathf.Max(0f, radiusMultiplier);

            for (int i = 0; i < spawnCount; i++)
            {
                int last = _bubblesPoolBuffer.Length - 1;
                int id = _bubblesPoolBuffer[last];
                _bubblesPoolBuffer.RemoveAtSwapBack(last);

                float3 dir = rnd.NextFloat3Direction();
                float dist = rnd.NextFloat() * radiusSim;
                float3 pos = centerSim + dir * dist;

                float radius = (rnd.NextFloat() * 0.7f + 0.3f) * PBF_Utils.CellSize * rMul;
                float3 vel = new float3(
                    rnd.NextFloat(-0.15f, 0.15f) * WorldToSimScale,
                    upSpeedSim,
                    rnd.NextFloat(-0.15f, 0.15f) * WorldToSimScale
                );

                _bubblesBuffer[id] = new Effects.Bubble
                {
                    Pos = pos,
                    Radius = radius,
                    Vel = vel,
                    LifeTime = life,
                };
            }
        }

        public void TriggerConsumeBubbleBoost(float multiplier, float durationSeconds, float sizeMultiplier)
        {
            float dur = Mathf.Max(0f, durationSeconds);
            if (dur <= 0f)
            {
                _bubbleBoostEndTime = -1f;
                _bubbleBoostMultiplier = 1f;
                _bubbleBoostSizeMultiplier = 1f;
                return;
            }

            _bubbleBoostEndTime = Time.time + dur;
            _bubbleBoostMultiplier = Mathf.Max(0f, multiplier);
            _bubbleBoostSizeMultiplier = Mathf.Max(0f, sizeMultiplier);
        }

        private void UpdateBubblesEffects()
        {
            if (!enableBubbles)
                return;
            if (!_runtimeInitialized)
                return;
            if (!_bubblesBuffer.IsCreated || !_bubblesPoolBuffer.IsCreated)
                return;
            if (!_gridBuffer.IsCreated)
                return;
            if (!_gridLut.IsCreated)
                return;

            float dt = deltaTime;

            float boost = 1f;
            float sizeBoost = 1f;
            if (_bubbleBoostEndTime > 0f)
            {
                if (Time.time <= _bubbleBoostEndTime)
                {
                    boost = Mathf.Max(0f, _bubbleBoostMultiplier);
                    sizeBoost = Mathf.Max(0f, _bubbleBoostSizeMultiplier);
                }
                else
                {
                    _bubbleBoostEndTime = -1f;
                    _bubbleBoostMultiplier = 1f;
                    _bubbleBoostSizeMultiplier = 1f;
                }
            }

            float thresholdScaled = threshold * 1.2f;

            int gridLen = Mathf.Max(1, Mathf.Min(_gridBuffer.Length, Mathf.Max(1, blockNum) * PBF_Utils.GridSize));
            var grid = _gridBuffer.GetSubArray(0, gridLen);

            JobHandle handle = default;

            if (blockNum > 0)
            {
                float spawnProb = math.clamp(0.01f * bubbleSpeed * boost, 0f, 1f);
                handle = new Effects.GenerateBubblesJobs()
                {
                    GridLut = _gridLut,
                    Keys = _blockBuffer,
                    Grid = grid,
                    BubblesStack = _bubblesPoolBuffer,
                    BubblesBuffer = _bubblesBuffer,
                    Speed = spawnProb,
                    Threshold = thresholdScaled,
                    BlockCount = blockNum,
                    MinPos = minPos,
                    Seed = (uint)Time.frameCount,
                }.Schedule();
            }

            handle = new Effects.BubblesViscosityJob()
            {
                Lut = _pbfSystem.Lut,
                Particles = _particlesTemp,
                VelocityR = _velocityTempBuffer,
                Controllers = _controllerBuffer,
                BubblesBuffer = _bubblesBuffer,
                ViscosityStrength = viscosityStrength / 50f,
            }.Schedule(_bubblesBuffer.Length, batchCount, handle);

            handle = new Effects.UpdateBubblesJob()
            {
                GridLut = _gridLut,
                Grid = grid,
                BubblesBuffer = _bubblesBuffer,
                BubblesStack = _bubblesPoolBuffer,
                Threshold = thresholdScaled,
                MinPos = minPos,
                DeltaTime = dt,
            }.Schedule(handle);

            handle.Complete();

            ApplyBubbleSizeBoost(sizeBoost);
        }

        private void ApplyBubbleSizeBoost(float sizeBoost)
        {
            if (bubblesMat == null)
                return;
            if (!bubblesMat.HasProperty(_bubblesSizeId))
                return;

            if (!_bubblesBaseSizeCached)
            {
                _bubblesBaseSize = bubblesMat.GetFloat(_bubblesSizeId);
                _bubblesBaseSizeCached = true;
            }

            if (_bubblesBlock == null)
            {
                _bubblesBlock = new MaterialPropertyBlock();
            }

            _bubblesBlock.SetFloat(_bubblesSizeId, _bubblesBaseSize * SlimeWorldScaleFactor * Mathf.Max(0f, sizeBoost));
            if (bubblesMat.HasProperty(_bubblesSimToWorldScaleId))
                _bubblesBlock.SetFloat(_bubblesSimToWorldScaleId, SimToWorldScale);
        }
        
        #region 【渲染设置】
        
        [ChineseHeader("渲染设置")]
        [Tooltip("控制目标 - 史莱姆跟随的Transform")]
        public Transform trans;

        private SlimePipeTravelAbility _pipeTravelAbility;
        private TopDownController3D _topDownController3D;
        
        
        [Tooltip("体积组件 - 管理史莱姆资源状态")]
        [SerializeField] private SlimeVolume _slimeVolume;

       
        [ChineseHeader("出生凝聚")]
        [ChineseLabel("出生凝聚特效")]
        [SerializeField, DefaultValue(false)]
        private bool enableSpawnCoalesce;

        [ChineseLabel("出生初始主体粒子数")]
        [SerializeField, Range(1, 512), DefaultValue(32)]
        private int spawnCoalesceInitialMainParticles = 32;

        [ChineseLabel("出生分批粒子数")]
        [SerializeField, Range(10, 2000), DefaultValue(200)]
        private int spawnCoalesceParticlesPerBatch = 200;

        [ChineseLabel("出生批次间隔(秒)")]
        [SerializeField, Range(0f, 1f), DefaultValue(0.05f)]
        private float spawnCoalesceBatchInterval = 0.05f;

        [ChineseLabel("出生开始延迟(秒)")]
        [SerializeField, Min(0f), DefaultValue(1f)]
        private float spawnCoalesceStartDelaySeconds = 1f;

        [ChineseLabel("出生悬空高度(米)")]
        [SerializeField, Min(0f), DefaultValue(1.5f)]
        private float spawnCoalesceHoverHeightWorld = 1.5f;

        [ChineseLabel("出生垂直分布倍率")]
        [SerializeField, Range(0.1f, 2f), DefaultValue(1f)]
        private float spawnCoalesceVerticalScale = 1f;

        [ChineseLabel("出生向心速度倍率")]
        [SerializeField, Range(0f, 2f), DefaultValue(0.5f)]
        private float spawnCoalesceInwardVelocityScale = 0.5f;

        [ChineseLabel("出生生成范围倍率")]
        [SerializeField, Range(1f, 20f), DefaultValue(3.5f)]
        private float spawnCoalesceSpawnRadius = 3.5f;
        
        [Tooltip("渲染模式 - Particles显示粒子，Surface显示表面")]
        public RenderMode renderMode = RenderMode.Surface;

        [ChineseLabel("分离体显示眼睛"), Tooltip("是否为分离体（controllerId>0）绘制脸部/眼睛")]
        [SerializeField] private bool showEyesOnSeparated = true;

        [ChineseHeader("更新频率（性能）")]
        [ChineseLabel("重排实例间隔(帧)"), Tooltip("重排可控实例/控制器映射的间隔帧数，数值越大越省CPU但响应可能更慢")]
        [SerializeField, Range(1, 10), DefaultValue(4)]
        private int rearrangeInstancesIntervalFrames = 4;

        [ChineseLabel("非控制实例射线间隔(帧)"), Tooltip("非受控实例的射线检测间隔帧数，用于降低非关键射线开销")]
        [SerializeField, Range(1, 20), DefaultValue(6)]
        private int nonControlledRaycastIntervalFrames = 6;

        [ChineseLabel("水珠MarchingCubes间隔(帧)"), Tooltip("Surface模式下水珠表面重建的间隔帧数，数值越大越省CPU但更新更不及时")]
        [SerializeField, Range(1, 10), DefaultValue(3)]
        private int dropletMarchingCubesIntervalFrames = 3;

        [ChineseLabel("主体MarchingCubes间隔(帧)"), Tooltip("Surface模式下主体表面重建的间隔帧数，数值越大越省CPU但更新更不及时")]
        [SerializeField, Range(1, 10), DefaultValue(1)]
        private int mainMarchingCubesIntervalFrames = 1;

        [ChineseLabel("追帧时主体MarchingCubes间隔(帧)"), Tooltip("发生追帧（同一渲染帧内多次FixedUpdate）时主体表面重建的间隔帧数")]
        [SerializeField, Range(1, 20), DefaultValue(4)]
        private int mainMarchingCubesIntervalFramesWhenBacklog = 4;

        [ChineseLabel("追帧时主体表面降频"), Tooltip("发生追帧时降低主体MarchingCubes更新频率以避免LateUpdate卡顿")]
        [SerializeField, DefaultValue(true)]
        private bool adaptiveMainSurfaceUpdateWhenBacklog = true;

        [ChineseHeader("脸部缩放")]
        [ChineseLabel("脸部缩放系数"), Tooltip("脸部/眼睛的基础缩放系数")]
        [SerializeField, Range(0.01f, 1f), DefaultValue(0.2f)]
        private float faceScale = 0.2f;

        [ChineseLabel("脸部缩放基准粒子数"), Tooltip("当主体粒子数等于该值时，脸部大小为基准大小（默认按2048粒子标定）")]
        [SerializeField, Range(1, 4096), DefaultValue(2048)]
        private int faceScaleBaseParticles = 2048;

        [ChineseLabel("脸部缩放下限"), Tooltip("按粒子数计算的缩放倍率下限")]
        [SerializeField, Range(0.05f, 2f), DefaultValue(0.5f)]
        private float faceScaleCountFactorMin = 0.5f;

        [ChineseLabel("脸部缩放上限"), Tooltip("按粒子数计算的缩放倍率上限")]
        [SerializeField, Range(0.05f, 4f), DefaultValue(1.5f)]
        private float faceScaleCountFactorMax = 1.5f;

        #endregion
        
        #region 【运行时状态】（只读）
        
        		[ChineseHeader("运行时状态（只读）")]
		[Tooltip("当前网格块数量")]
		public int blockNum;
		
		[Tooltip("当前气泡数量")]
		public int bubblesNum;
		
		[Tooltip("边界最小点")]
		public float3 minPos;
		
		[Tooltip("边界最大点")]
		public float3 maxPos;

		public Bounds MainBodyBoundsWorld
		{
			get
			{
				Bounds b = default;
				b.SetMinMax(minPos * SimToWorldScale, maxPos * SimToWorldScale);
				return b;
			}
		}

		public Bounds DropletBoundsWorld => _dropletBounds;

		public Bounds CombinedBoundsWorld
		{
			get
			{
				Bounds b = MainBodyBoundsWorld;
				if (_dropletSubsystem.ActiveCount > 0)
				{
					b.Encapsulate(_dropletBounds.min);
					b.Encapsulate(_dropletBounds.max);
				}
				return b;
			}
		}

		public Vector3 MainBodyCentroidWorld => _mainBodyCentroidWorld;
		public Vector3 RenderPredictOffsetWorld => GetRenderPredictOffsetWorld();
		public Vector3 MainBodyCentroidWorldFixed
		{
			get
			{
				if (_fixedMainBodyCentroidWorldValid)
					return _lastFixedMainBodyCentroidWorld;
				return _mainBodyCentroidWorld;
			}
		}
		public Vector3 MainBodyCentroidWorldForRender
		{
			get
			{
				if (!_fixedMainBodyCentroidWorldValid)
					return _mainBodyCentroidWorld;

				float fixedDt = Time.fixedDeltaTime;
				float alpha = fixedDt > 1e-6f ? Mathf.Clamp01((Time.time - Time.fixedTime) / fixedDt) : 1f;
				return Vector3.Lerp(_prevFixedMainBodyCentroidWorld, _lastFixedMainBodyCentroidWorld, alpha);
			}
		}

		public float MainBodyRadiusWorld
		{
			get
			{
				if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
					return _controllerBuffer[0].Radius * SimToWorldScale;

				float3 sizeWorld = (maxPos - minPos) * SimToWorldScale;
				return math.max(sizeWorld.x, sizeWorld.z) * 0.5f;
			}
		}

		public int MainActiveParticles => activeParticles;

		public void GetVolumeParticleCounts(out int mainBodyCount, out int separatedCount, out int emittedCount)
		{
			mainBodyCount = 0;
			separatedCount = 0;
			emittedCount = 0;
			if (!_particles.IsCreated || activeParticles <= 0)
				return;

			for (int i = 0; i < activeParticles; i++)
			{
				var p = _particles[i];
				switch (p.Type)
				{
					case ParticleType.MainBody:
						mainBodyCount++;
						break;
					case ParticleType.Separated:
						separatedCount++;
						break;
					case ParticleType.Emitted:
						emittedCount++;
						break;
				}
			}
		}

		public int DropletActiveParticles => _dropletSubsystem.ActiveCount;

        public NativeArray<Particle> Particles => _particles;

        public NativeHashMap<int, int2> SpatialLut => _pbfSystem.Lut;

        public NativeArray<int2> SpatialHashes => _pbfSystem.Hashes;

        public event Action<Slime_PBF> BeforeControl;

        [SerializeField]
        private bool enableWatering = true;

        private readonly List<PbfWaterReceiver> _wateringReceivers = new List<PbfWaterReceiver>(64);
        private readonly List<int> _wateringConsumeMain = new List<int>(256);
        private readonly List<int> _wateringConsumeDroplets = new List<int>(256);
        private readonly HashSet<int> _wateringReservedMain = new HashSet<int>();
        private readonly HashSet<int> _wateringReservedDroplets = new HashSet<int>();

        public bool TryConsumeMainParticle(int index)
        {
            if (!_runtimeInitialized)
                return false;

            if (!_particles.IsCreated || !_velocityBuffer.IsCreated)
                return false;

            if (index < 0 || index >= activeParticles)
                return false;

            var p = _particles[index];
            if (p.Type == ParticleType.MainBody || p.Type == ParticleType.Dormant || p.Type == ParticleType.FadingOut)
                return false;

            if (p.SourceId >= 0)
                return false;

            ParticleStateManager.SetDormant(ref p);
            _particles[index] = p;

            int last = activeParticles - 1;
            if (index < last)
            {
                ParticleStateManager.SwapParticles(ref _particles, index, last);
                var tempVel = _velocityBuffer[index];
                _velocityBuffer[index] = _velocityBuffer[last];
                _velocityBuffer[last] = tempVel;
            }

            activeParticles--;
            return true;
        }

        public bool TryConsumeDropletParticle(int globalIndex)
        {
            return _dropletSubsystem.MigrateToMainBody(globalIndex, out _, out _);
        }

        /// <summary>
        /// 恢复主体粒子（生成 Separated 类型粒子，会自动被融合逻辑吸收），用于水井等补给点
        /// </summary>
        /// <param name="count">要恢复的粒子数量</param>
        /// <param name="spawnRadius">生成范围半径倍数（相对于主体半径，默认2.5倍）</param>
        /// <returns>实际恢复的粒子数量</returns>
        public int RestoreMainBodyParticles(int count, float spawnRadius = 2.5f, float verticalScale = 0.6f, float inwardVelocityScale = 0.5f)
        {
            if (!_runtimeInitialized || !_particles.IsCreated || !_velocityBuffer.IsCreated)
                return 0;

            if (count <= 0)
                return 0;

            float3 mainCenterSim = (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
                ? _controllerBuffer[0].Center
                : (trans != null ? (float3)(trans.position * WorldToSimScale) : float3.zero);

            return RestoreMainBodyParticlesInternal(count, mainCenterSim, spawnRadius, verticalScale, inwardVelocityScale);
        }

        public int RestoreMainBodyParticlesAtWorldCenter(int count, Vector3 worldCenter, float spawnRadius = 2.5f, float verticalScale = 1f, float inwardVelocityScale = 0.5f)
        {
            if (!_runtimeInitialized || !_particles.IsCreated || !_velocityBuffer.IsCreated)
                return 0;

            if (count <= 0)
                return 0;

            float3 centerSim = (float3)(worldCenter * WorldToSimScale);
            return RestoreMainBodyParticlesInternal(count, centerSim, spawnRadius, verticalScale, inwardVelocityScale);
        }

        public bool BeginExternalCoalesceLockAtWorld(Vector3 anchorWorld, float hoverHeightWorld)
        {
            if (_externalCoalesceLockActive)
                return true;
            if (_spawnCoalesceLockActive)
                return false;

            _externalCoalesceLockActive = true;

            Vector3 pos = anchorWorld + Vector3.up * Mathf.Max(0f, hoverHeightWorld);
            Transform root = _topDownController3D != null ? _topDownController3D.transform : trans;
            if (root == null)
                root = transform;
            root.position = pos;

            _externalCoalesceOriginalSlimeGravity = gravity;
            gravity = 0f;

            if (_topDownController3D != null)
            {
                _externalCoalesceOriginalControllerGravityActive = _topDownController3D.GravityActive;
                _externalCoalesceOriginalFreeMovement = _topDownController3D.FreeMovement;
                _topDownController3D.GravityActive = false;
                _topDownController3D.FreeMovement = false;
            }

            _externalCoalesceCharacter = GetComponentInParent<Character>();
            _externalCoalesceCharacter?.Freeze();
            return true;
        }

        public void EndExternalCoalesceLock()
        {
            if (!_externalCoalesceLockActive)
                return;
            _externalCoalesceLockActive = false;

            gravity = _externalCoalesceOriginalSlimeGravity;
            _externalCoalesceCharacter?.UnFreeze();
            if (_topDownController3D != null)
            {
                _topDownController3D.GravityActive = _externalCoalesceOriginalControllerGravityActive;
                _topDownController3D.FreeMovement = _externalCoalesceOriginalFreeMovement;
            }
            _externalCoalesceCharacter = null;
        }

        private int RestoreMainBodyParticlesInternal(int count, float3 centerSim, float spawnRadius, float verticalScale, float inwardVelocityScale)
        {
            int maxRestorable = MainBodyMaxParticles - activeParticles;
            if (maxRestorable <= 0)
                return 0;

            int toRestore = math.min(count, maxRestorable);

            float mainRadius = (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
                ? _controllerBuffer[0].Radius
                : coreRadius;

            float innerRadius = mainRadius * 1.2f;
            float outerRadius = mainRadius * spawnRadius;

            int restored = 0;
            for (int i = 0; i < toRestore; i++)
            {
                int newIdx = activeParticles;
                if (newIdx >= MainBodyMaxParticles || newIdx >= _particles.Length)
                    break;

                float radius = UnityEngine.Random.Range(innerRadius, outerRadius);
                float theta = UnityEngine.Random.value * 2f * math.PI;
                float phi = math.acos(2f * UnityEngine.Random.value - 1f);

                float3 randomOffset = new float3(
                    radius * math.sin(phi) * math.cos(theta),
                    radius * math.cos(phi) * verticalScale,
                    radius * math.sin(phi) * math.sin(theta)
                );

                float3 toCenter = -math.normalize(randomOffset);
                float3 initialVelocity = toCenter * mainRadius * inwardVelocityScale;

                var newParticle = new Particle
                {
                    Position = centerSim + randomOffset,
                    Type = ParticleType.Separated,
                    ControllerSlot = 0,
                    BlobId = 0,
                    FreeFrames = 0,
                    SourceId = -1,
                    ClusterId = 0,
                    FramesOutsideMain = 0
                };

                _particles[newIdx] = newParticle;
                _velocityBuffer[newIdx] = initialVelocity;
                activeParticles++;
                restored++;
            }

            return restored;
        }

        private void ApplyWateringInteraction()
        {
            if (!enableWatering)
                return;

            double invFreqMs = 1000.0 / Stopwatch.Frequency;

            var instances = PbfWaterReceiver.Instances;
            if (instances == null || instances.Count == 0)
                return;

            PerformanceProfiler.Begin("Watering_BuildReceiverList");
            _wateringReceivers.Clear();
            for (int i = 0; i < instances.Count; i++)
            {
                var r = instances[i];
                if (r == null)
                    continue;

                _wateringReceivers.Add(r);
            }

            PerformanceProfiler.End("Watering_BuildReceiverList");

            if (_wateringReceivers.Count == 0)
                return;

            PerformanceProfiler.Begin("Watering_SortReceivers");
            _wateringReceivers.Sort(CompareWaterReceiver);
            PerformanceProfiler.End("Watering_SortReceivers");

            int mainCount = MainActiveParticles;
            int dropletCount = DropletActiveParticles;

            if (mainCount <= 0 && dropletCount <= 0)
                return;

            bool hasCandidatesMain = false;
            float3 candidatesMinSim = default;
            float3 candidatesMaxSim = default;
            if (mainCount > 0)
            {
                for (int i = 0; i < mainCount; i++)
                {
                    Particle p = Particles[i];
                    if (p.SourceId >= 0)
                        continue;

                    if (p.Type != ParticleType.Emitted && p.Type != ParticleType.Separated)
                        continue;

                    float3 pos = p.Position;
                    if (!hasCandidatesMain)
                    {
                        hasCandidatesMain = true;
                        candidatesMinSim = pos;
                        candidatesMaxSim = pos;
                    }
                    else
                    {
                        candidatesMinSim = math.min(candidatesMinSim, pos);
                        candidatesMaxSim = math.max(candidatesMaxSim, pos);
                    }
                }
            }

            if (!hasCandidatesMain && dropletCount <= 0)
                return;

            Bounds queryBoundsWorld = default;
            bool hasQueryBoundsWorld = false;
            if (hasCandidatesMain)
            {
                queryBoundsWorld.SetMinMax((Vector3)(candidatesMinSim * SimToWorldScale), (Vector3)(candidatesMaxSim * SimToWorldScale));
                float marginW = math.max(particleRadiusWorld, PBF_Utils.CellSize * SimToWorldScale);
                queryBoundsWorld.Expand(marginW * 2f);
                hasQueryBoundsWorld = true;
            }

            if (dropletCount > 0)
            {
                if (!hasQueryBoundsWorld)
                {
                    queryBoundsWorld = _dropletBounds;
                    hasQueryBoundsWorld = true;
                }
                else
                {
                    queryBoundsWorld.Encapsulate(_dropletBounds.min);
                    queryBoundsWorld.Encapsulate(_dropletBounds.max);
                }
            }

            var particles = Particles;
            var lut = SpatialLut;
            var hashes = SpatialHashes;

            _wateringConsumeMain.Clear();
            _wateringConsumeDroplets.Clear();
            _wateringReservedMain.Clear();
            _wateringReservedDroplets.Clear();

            int receiversProcessed = 0;
            int receiversWithBounds = 0;
            int receiversWithTarget = 0;
            int candidatesMain = 0;
            int candidatesDroplets = 0;
            int containsCalls = 0;
            long containsTicks = 0;
            long queryMainTicks = 0;
            long queryDropletsTicks = 0;
            long consumeSortApplyTicks = 0;

            for (int r = 0; r < _wateringReceivers.Count; r++)
            {
                var receiver = _wateringReceivers[r];
                if (receiver == null)
                    continue;

                if (!receiver.WantsWater)
                    continue;

                receiversProcessed++;

                bool hasBounds = receiver.TryGetVolumeBoundsWorld(out Bounds boundsWorld);
                if (!hasBounds)
                {
                    continue;
                }

                receiversWithBounds++;

                if (hasQueryBoundsWorld && !boundsWorld.Intersects(queryBoundsWorld))
                {
                    continue;
                }

                var target = receiver.Target;
                if (target == null)
                {
                    continue;
                }

                receiversWithTarget++;

                int maxConsume = receiver.MaxConsumePerUpdate;
                float3 minSim = (float3)boundsWorld.min * WorldToSimScale;
                float3 maxSim = (float3)boundsWorld.max * WorldToSimScale;

                int consumedByThis = 0;

                if (mainCount > 0)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    WaterQueryMainPartition(
                        receiver,
                        boundsWorld,
                        particles,
                        mainCount,
                        lut,
                        hashes,
                        minSim,
                        maxSim,
                        maxConsume,
                        ref consumedByThis,
                        ref candidatesMain,
                        ref containsCalls,
                        ref containsTicks);
                    queryMainTicks += (Stopwatch.GetTimestamp() - t0);
                }

                if (receiver.ConsumeDroplets && dropletCount > 0 && (maxConsume <= 0 || consumedByThis < maxConsume))
                {
                    long t0 = Stopwatch.GetTimestamp();
                    WaterQueryDropletPartition(
                        receiver,
                        boundsWorld,
                        particles,
                        dropletCount,
                        minSim,
                        maxSim,
                        maxConsume,
                        ref consumedByThis,
                        ref candidatesDroplets,
                        ref containsCalls,
                        ref containsTicks);
                    queryDropletsTicks += (Stopwatch.GetTimestamp() - t0);
                }

                if (consumedByThis > 0)
                {
                    target.ReceiveWater(new WaterInput
                    {
                        Amount = consumedByThis * receiver.WaterPerParticle,
                        ParticleCount = consumedByThis,
                        PositionWorld = boundsWorld.center
                    });
                }
            }

            PerformanceProfiler.Add("Watering_QueryMain", queryMainTicks * invFreqMs);
            PerformanceProfiler.Add("Watering_QueryDroplets", queryDropletsTicks * invFreqMs);
            PerformanceProfiler.Add("Watering_ContainsPointWorld", containsTicks * invFreqMs);

            if (_wateringConsumeDroplets.Count > 0)
            {
                long t0 = Stopwatch.GetTimestamp();
                _wateringConsumeDroplets.Sort();
                for (int i = _wateringConsumeDroplets.Count - 1; i >= 0; i--)
                {
                    TryConsumeDropletParticle(_wateringConsumeDroplets[i]);
                }
                consumeSortApplyTicks += (Stopwatch.GetTimestamp() - t0);
            }

            if (_wateringConsumeMain.Count > 0)
            {
                long t0 = Stopwatch.GetTimestamp();
                _wateringConsumeMain.Sort();
                for (int i = _wateringConsumeMain.Count - 1; i >= 0; i--)
                {
                    TryConsumeMainParticle(_wateringConsumeMain[i]);
                }
                consumeSortApplyTicks += (Stopwatch.GetTimestamp() - t0);
            }

            PerformanceProfiler.Add("Watering_ConsumeSortAndApply", consumeSortApplyTicks * invFreqMs);

            PerformanceProfiler.CounterSet("Watering_Receivers", _wateringReceivers.Count);
            PerformanceProfiler.CounterSet("Watering_ReceiversProcessed", receiversProcessed);
            PerformanceProfiler.CounterSet("Watering_ReceiversWithBounds", receiversWithBounds);
            PerformanceProfiler.CounterSet("Watering_ReceiversWithTarget", receiversWithTarget);
            PerformanceProfiler.CounterSet("Watering_CandidatesMain", candidatesMain);
            PerformanceProfiler.CounterSet("Watering_CandidatesDroplets", candidatesDroplets);
            PerformanceProfiler.CounterSet("Watering_ContainsCalls", containsCalls);
            PerformanceProfiler.CounterSet("Watering_ConsumeMain", _wateringConsumeMain.Count);
            PerformanceProfiler.CounterSet("Watering_ConsumeDroplets", _wateringConsumeDroplets.Count);
            PerformanceProfiler.CounterSet("Watering_ConsumeTotal", _wateringConsumeMain.Count + _wateringConsumeDroplets.Count);
        }

        private static int CompareWaterReceiver(PbfWaterReceiver a, PbfWaterReceiver b)
        {
            if (ReferenceEquals(a, b))
                return 0;

            if (a == null)
                return 1;

            if (b == null)
                return -1;

            return a.GetInstanceID().CompareTo(b.GetInstanceID());
        }

        private void WaterQueryMainPartition(
            PbfWaterReceiver receiver,
            Bounds boundsWorld,
            NativeArray<Particle> particles,
            int mainCount,
            NativeHashMap<int, int2> lut,
            NativeArray<int2> hashes,
            float3 minSim,
            float3 maxSim,
            int maxConsume,
            ref int consumedByThis,
            ref int candidatesMain,
            ref int containsCalls,
            ref long containsTicks)
        {
            int3 minCoord = PBF_Utils.GetCoord(minSim);
            int3 maxCoord = PBF_Utils.GetCoord(maxSim);

            bool wantEmitted = receiver.ConsumeEmitted;
            bool wantSeparated = receiver.ConsumeSeparated;

            for (int z = minCoord.z; z <= maxCoord.z; z++)
            for (int y = minCoord.y; y <= maxCoord.y; y++)
            for (int x = minCoord.x; x <= maxCoord.x; x++)
            {
                int key = PBF_Utils.GetKey(new int3(x, y, z));
                if (!lut.TryGetValue(key, out int2 range))
                    continue;

                for (int i = range.x; i < range.y; i++)
                {
                    int originalIndex = hashes[i].y;
                    if (originalIndex < 0 || originalIndex >= mainCount)
                    {
                        continue;
                    }

                    if (_wateringReservedMain.Contains(originalIndex))
                    {
                        continue;
                    }

                    Particle p = particles[originalIndex];
                    if (p.Type == ParticleType.Dormant || p.Type == ParticleType.FadingOut || p.Type == ParticleType.MainBody)
                    {
                        continue;
                    }

                    if (p.SourceId >= 0)
                    {
                        continue;
                    }

                    if (p.Type == ParticleType.Emitted)
                    {
                        if (!wantEmitted)
                        {
                            continue;
                        }
                    }
                    else if (p.Type == ParticleType.Separated)
                    {
                        if (!wantSeparated)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }

                    float3 pos = p.Position;
                    if (pos.x < minSim.x || pos.x > maxSim.x ||
                        pos.y < minSim.y || pos.y > maxSim.y ||
                        pos.z < minSim.z || pos.z > maxSim.z)
                    {
                        continue;
                    }

                    Vector3 posWorld = (Vector3)(pos * SimToWorldScale);

                    candidatesMain++;
                    containsCalls++;
                    long t0 = Stopwatch.GetTimestamp();
                    bool inside = receiver.ContainsPointWorld(posWorld);
                    containsTicks += (Stopwatch.GetTimestamp() - t0);
                    if (!inside)
                    {
                        continue;
                    }

                    _wateringReservedMain.Add(originalIndex);
                    _wateringConsumeMain.Add(originalIndex);
                    consumedByThis++;

                    if (maxConsume > 0 && consumedByThis >= maxConsume)
                        return;
                }
            }
        }

        private void WaterQueryDropletPartition(
            PbfWaterReceiver receiver,
            Bounds boundsWorld,
            NativeArray<Particle> particles,
            int dropletCount,
            float3 minSim,
            float3 maxSim,
            int maxConsume,
            ref int consumedByThis,
            ref int candidatesDroplets,
            ref int containsCalls,
            ref long containsTicks)
        {
            for (int localIndex = 0; localIndex < dropletCount; localIndex++)
            {
                int globalIndex = DropletSubsystem.DROPLET_START + localIndex;
                if (_wateringReservedDroplets.Contains(globalIndex))
                    continue;

                Particle p = particles[globalIndex];
                if (p.Type != ParticleType.SceneDroplet)
                    continue;

                float3 pos = p.Position;
                if (pos.x < minSim.x || pos.x > maxSim.x ||
                    pos.y < minSim.y || pos.y > maxSim.y ||
                    pos.z < minSim.z || pos.z > maxSim.z)
                {
                    continue;
                }

                Vector3 posWorld = (Vector3)(pos * SimToWorldScale);
                if (!boundsWorld.Contains(posWorld))
                    continue;

                candidatesDroplets++;
                containsCalls++;
                long t0 = Stopwatch.GetTimestamp();
                bool inside = receiver.ContainsPointWorld(posWorld);
                containsTicks += (Stopwatch.GetTimestamp() - t0);
                if (!inside)
                    continue;

                _wateringReservedDroplets.Add(globalIndex);
                _wateringConsumeDroplets.Add(globalIndex);
                consumedByThis++;

                if (maxConsume > 0 && consumedByThis >= maxConsume)
                    return;
            }
        }

        private Vector3 _mainBodyCentroidWorld;
        private Vector3 _prevFixedMainBodyCentroidWorld;
        private Vector3 _lastFixedMainBodyCentroidWorld;
        private bool _fixedMainBodyCentroidWorldValid;
        
        #endregion
        
        #region 【碰撞体查询设置】
        
        [ChineseHeader("碰撞体查询设置（桶索引缓存）")]
        [ChineseLabel("主体桶索引缓存容量"), Tooltip("主体PBF使用的碰撞体缓存容量（由桶索引查询填充），越大开销越高")]
        [SerializeField, Range(16, 128), DefaultValue(64)]
        private int mainColliderIndexQueryCacheCapacity = 64;
        
        [ChineseLabel("主体桶索引查询半径(世界)"), Tooltip("以史莱姆为中心进行桶索引查询的半径（米，世界坐标）")]
        [SerializeField, Range(5f, 50f), DefaultValue(20f)]
        private float mainColliderIndexQueryRadiusWorld = 20f;

        [ChineseLabel("启用地面兜底射线"), Tooltip("开启后：每帧用向下射线更新控制器 GroundY，用于主体粒子的地面钳制。关闭后：不再做兜底射线，GroundY 将保持为很低值以避免粒子被错误抬高。")]
        [SerializeField, DefaultValue(true)]
        private bool enableGroundFallbackRaycast = true;
        
        [ChineseLabel("静态碰撞层"), Tooltip("用于查询静态场景碰撞体的层掩码。0 表示全部层（~0），可能导致 Default 等层也参与普通碰撞。")]
        [SerializeField]
        private LayerMask worldStaticColliderLayers;
        
        [ChineseLabel("动态碰撞层"), Tooltip("用于查询动态物体碰撞体的层掩码（通常包含可移动/可交互物体）。")]
        [SerializeField]
        private LayerMask worldDynamicColliderLayers;

        private int ColliderQueryMask => (worldStaticColliderLayers.value != 0 ? worldStaticColliderLayers.value : ~0) | worldDynamicColliderLayers.value;

        private int GroundQueryMask => ((worldStaticColliderLayers.value != 0 ? worldStaticColliderLayers.value : ~0) & ~worldDynamicColliderLayers.value);

        [Header("World Static SDF Collision")]
        [ChineseLabel("启用世界静态SDF"), Tooltip("启用后，静态碰撞可由预烘焙的 SDF 提供。")]
        [SerializeField]
        private bool useWorldStaticSdf = true;

        [ChineseLabel("世界静态SDF资源"), Tooltip("WorldSdfBaker 烘焙输出的 WorldSdf.bytes 资源。")]
        [SerializeField]
        private TextAsset worldStaticSdfBytes;

        [ChineseLabel("使用SDF时禁用静态碰撞回退"), Tooltip("开启后：当 SDF 可用时，不再使用普通静态碰撞体回退（SDF-only）。")]
        [SerializeField]
        private bool disableStaticColliderFallbackWhenUsingSdf;

        [ChineseLabel("世界静态SDF摩擦"), Tooltip("SDF 碰撞的摩擦系数（0=无摩擦，1=强摩擦）。")]
        [SerializeField, Range(0f, 1f)]
        private float worldStaticSdfFriction = 0.2f;

        [ChineseLabel("粒子半径(世界)"), Tooltip("用于与静态碰撞/SDF交互的粒子半径（米，世界坐标）。")]
        [SerializeField, Range(0.001f, 0.2f)]
        private float particleRadiusWorld = 0.05f;
        
        [ChineseHeader("水珠碰撞桶索引查询（独立缓存）")]
        [ChineseLabel("水珠桶索引缓存容量"), Tooltip("场景水珠使用的碰撞体缓存容量（由桶索引查询填充），越大开销越高")]
        [SerializeField, Range(16, 512), DefaultValue(128)]
        private int dropletColliderIndexQueryCacheCapacity = 128;
        
        [ChineseLabel("水珠桶索引查询半径(世界)"), Tooltip("为场景水珠进行桶索引查询的半径（米，世界坐标）")]
        [SerializeField, Range(5f, 80f), DefaultValue(20f)]
        private float dropletColliderIndexQueryRadiusWorld = 20f;

        [ChineseHeader("水珠液体模式")]
        [ChineseLabel("动态Drag强度"), Tooltip("速度匹配强度，越大越容易被物体带动")]
        [SerializeField, Range(0f, 100f)]
        private float dropletDynamicDragStrength = 35f;

        [ChineseLabel("动态Drag半径(世界)"), Tooltip("动态物体周围的影响半径（米，世界坐标）")]
        [SerializeField, Range(0f, 5f)]
        private float dropletDynamicDragRadiusWorld = 1.5f;

        [ChineseLabel("动态碰撞转液体态"), Tooltip("开启后：水珠碰到动态碰撞体会切换为液体态（锁定）。默认关闭：碰撞不会改变水珠形态。")]
        [SerializeField, DefaultValue(false)]
        private bool dropletEnableLiquidModeOnDynamicCollision;

        [ChineseLabel("液体态PBF迭代数"), Tooltip("水珠进入液体态后的PBF迭代数（更省性能但体积约束更弱）")]
        [SerializeField, Range(1, 4)]
        private int dropletLiquidSolverIterations = 3;

        [ChineseLabel("液体水重力Y"), Tooltip("内部坐标的重力Y（负值向下）。仅液体态水珠使用")]
        [SerializeField, Range(-30f, 0f), DefaultValue(-5f)]
        private float dropletLiquidGravityY = -10f;

        #endregion
        
        #region 【调试设置】
        
        [ChineseHeader("调试设置")]
        [Tooltip("性能分析器日志总开关（PerformanceProfiler）")]
        public bool performanceProfilerEnabled = true;

        public bool adaptiveMainSimSubsteps = true;

        [Min(1), DefaultValue(2)]
        public int mainSimSubstepsPerFixedUpdate = 2;

        [Min(1), DefaultValue(1)]
        public int mainSimSubstepsWhenBacklog = 1;

        [Tooltip("显示网格调试信息")]
        public bool gridDebug;
        
        [Tooltip("显示近场碰撞体线框")]
        public bool colliderCollectDebug;
        
        [Tooltip("显示CCA组件边界盒")]
        public bool ccaDebug;

        [Tooltip("显示召回避障 SphereCast 调试线框")]
        public bool recallAvoidanceGizmos;
        
        #endregion
        
        #region 【场景水珠管理】
        
        private readonly List<DropWater> allSources = new List<DropWater>();
        private int[] _absorbedFromSourceCounts;
        private readonly List<int> _absorbedSourceIds = new List<int>(16);
        private bool[] _sourceMayContact;
        
        #endregion

        #region Buffers
        
        private NativeArray<Particle> _particles;
        private NativeArray<Particle> _particlesTemp;
        private NativeArray<Particle> _particlesRenderBuffer; // 渲染用世界坐标缓冲
        private NativeArray<float3> _posOld;
        private NativeArray<float3> _clampDelta; // 【P4】钳制位移量，用于速度回算修正
        private NativeArray<float3> _velocityBuffer;
        private NativeArray<float3> _velocityTempBuffer;
        private NativeArray<int2> _renderHashesBuffer;

        private NativeArray<float3> _freeParticleCentroidTemp;
        private NativeArray<int> _freeParticleCountTemp;
        
        // PBF物理系统（封装了_lut, _hashes, _posPredict, _lambdaBuffer等缓冲区）
        private PBFSystem _pbfSystem;
        
        // 渲染专用缓冲区（独立于物理）
        private NativeHashMap<int, int2> _renderLut; // 渲染专用邻域表
        private NativeArray<float4x4> _covBuffer;
        private NativeArray<MyBoxCollider> _colliderBuffer;
        private int _currentColliderCount; // 当前有效碰撞体数量
        private RaycastHit[] _raycastHits;

        private int _dbgRecallSphereCastFrame;
        private Vector3 _dbgRecallSphereOriginW;
        private Vector3 _dbgRecallSphereDirW;
        private float _dbgRecallSphereRadiusW;
        private float _dbgRecallSphereCastDistW;
        private bool _dbgRecallSphereHitRaw;
        private bool _dbgRecallSphereHitFinal;
        private bool _dbgRecallSphereHitFilteredByNormal;
        private bool _dbgRecallSphereHitFromSdf;
        private Vector3 _dbgRecallSphereHitPointW;
        private Vector3 _dbgRecallSphereHitNormalW;
        private bool _dbgEyeJitterCallFromRearrange;

        private readonly HashSet<int> _colliderInstanceIds = new HashSet<int>(1024);

        private NativeArray<MyBoxCollider> _dropletColliderBuffer;
        private int _currentDropletColliderCount;
        private readonly HashSet<int> _dropletColliderInstanceIds = new HashSet<int>(256);

        private NativeArray<float3> _boundsBuffer;
        private NativeArray<float> _gridBuffer;
        private NativeArray<float> _gridTempBuffer;
        private NativeHashMap<int3, int> _gridLut;
        private NativeList<int3> _gridKeys;
        private NativeArray<float> _dropletGridBuffer;
        private NativeArray<float> _dropletGridTempBuffer;
        private NativeHashMap<int3, int> _dropletGridLut;
        private NativeList<int3> _dropletGridKeys;
        private NativeArray<int> _dropletGridIDBuffer;
        private NativeArray<int4> _blockBuffer;
        private NativeArray<int> _blockColorBuffer;

        private NativeArray<int4> _dropletBlockBuffer;
        private NativeArray<int> _dropletBlockColorBuffer;
        
        private NativeArray<Effects.Bubble> _bubblesBuffer;
        private NativeList<int> _bubblesPoolBuffer;
        
        private NativeList<Effects.Component> _componentsBuffer;
        private NativeArray<int> _gridIDBuffer;
        private NativeArray<int> _prevGridBlobId;  // 上一帧网格稳定ID（用于投票）
        private int _nextBlobId = 1;  // 下一个可用的稳定ID（0=主体，1+=分离组）
        private NativeList<ParticleController> _controllerBuffer;
        private NativeArray<ParticleController> _sourceControllers;
        private int[] _componentToGroup;
        private Stack<int> _freeSeparatedControllerIds;
        private int[] _controllerFreeFramesCounts;
        private int[] _controllerSmallSeparatedFrames;
        private int[] _controllerFadeRemainingFrames;
        private int[] _controllerFadePerFrame;
        private int[] _controllerFadeBudget;
        private float[] _controllerStepJumpTargetCenterY;
        private int[] _controllerStepJumpTargetExpireFrame;
        private bool _connect;
        private float _connectStartTime;
        private NativeArray<int> _blobIdToControllerSlot;
        private NativeArray<byte> _recallEligibleBlobIds;
        private int[] _recallEligibleBlobAbsentFrames;
        private NativeList<SlimeInstance> _slimeInstances;
        private int _controlledInstance;
        private Stack<int> _instancePool;
        private NativeArray<float3> _controllerMainBodyCentroid;
        private NativeArray<int> _controllerMainBodyCount;
        private NativeArray<float3> _controllerCentroidAll;
        private NativeArray<int> _controllerCountAll;
        private int _lastRearrangeFrame = -999999;
        private int _lastRearrangeControllerCount = -1;
        
            private ComputeBuffer _bubblesDataBuffer;
        
            // 粒子渲染器（封装 GPU Buffer 管理）
            private SlimeParticleRenderer _particleRenderer;
        
            // 场景水珠独立管理器（固定分区[8192-16383]）
            private DropletSubsystem _dropletSubsystem;

            private int _lastParticleRenderUploadFrame = -999999;
            private bool _renderInitErrorLogged;

            private NativeParallelMultiHashMap<int, int> _mergeMainBodyLut;
            private NativeParallelMultiHashMap<int, int> _mergeAbsorbLut;
            private NativeArray<int> _autoSeparateClusterCounts;
            private NativeArray<float3> _autoSeparateClusterCenters;

            private NativeList<WindFieldZoneData> _windZones;

            #endregion

            #region 【World Static SDF Collision】
            private bool _mouseDown;
            private float3 _velocityY = float3.zero;
        		private Bounds _bounds;
		private Vector3 _velocity = Vector3.zero;
		private Vector3 _prevTransPosition; // 上一帧 trans.position，用于计算速度
		private Vector3 _lastFixedTransPosWorld;
		private bool _lastFixedTransPosWorldInitialized;

		private Vector3 _renderVelocity = Vector3.zero;
		private Vector3 _prevRenderTransPosition;
		private bool _deferredPostSimPending;
		private int _fixedUpdateSerial;
		private int _fixedUpdateCountFrame = -999999;
		private int _fixedUpdateCountThisFrame;
		private int _lastPerformanceProfilerFrame = -999999;
		private bool _runtimeInitialized;
		private int _spawnCoalesceRemaining;
		private float _spawnCoalesceNextBatchTime;
		private bool _spawnCoalesceLockActive;
		private float _spawnCoalesceOriginalSlimeGravity;
		private bool _spawnCoalesceOriginalControllerGravityActive;
		private bool _spawnCoalesceOriginalFreeMovement;
		private Character _spawnCoalesceCharacter;
		private bool _externalCoalesceLockActive;
		private float _externalCoalesceOriginalSlimeGravity;
		private bool _externalCoalesceOriginalControllerGravityActive;
		private bool _externalCoalesceOriginalFreeMovement;
		private Character _externalCoalesceCharacter;
		private float _lastMainRadius; // 用于 mainRadius 防抖
		private float3 _prevMainControllerCenter; // 上一帧主控制器中心，用于正确计算PosOld
		private int _lastMainMarchingCubesFrame = -999999;
		private int _lastDropletMarchingCubesFrame = -999999;
		private int _lastSurfaceSpikeLogFrame = -999999;
		private int _lastGroundFallbackHitLogFrame = -999999;
		private int _lastMergeScanMainRangeLogFrame = -999999;
		private int _lastMergeScanDropletPartitionLogFrame = -999999;
		private Camera _cachedMainCamera;
		private UnityEngine.Plane[] _cachedFrustumPlanes;

		private WorldSdfRuntime _worldStaticSdf;
		private NativeArray<float> _dummyWorldSdfDistances;
		private NativeArray<int> _dummyWorldSdfBrickOffsets;
		private NativeArray<float> _dummyWorldSdfBrickDistances;
		private WorldSdfRuntime.Volume _dummyWorldSdfVolume;
		private NativeArray<float> _dummyTerrainHeights01;
		private NativeArray<float> _terrainHeights01;
		private int _terrainResX;
		private int _terrainResZ;
		private float3 _terrainOriginSim;
		private float3 _terrainSizeSim;
		private bool _terrainHeightfieldLogged;
		private bool _worldIndexMaskWarnLogged;
		private Terrain _terrainHeightfieldSource;

		private LMarchingCubes _marchingCubes;
		private Mesh _mesh;
		private Vector3 _meshOriginWorld;

		private LMarchingCubes _marchingCubesDroplet;
		private Mesh _dropletMesh;
		private Bounds _dropletBounds;
		private Vector3 _dropletMeshOriginWorld;

        // ...

        void Awake()
        {
            if (trans == null)
            {
                trans = transform;
            }

            _pipeTravelAbility = GetComponentInParent<SlimePipeTravelAbility>();
            _topDownController3D = GetComponentInParent<TopDownController3D>();

            // ...

            if (!_dummyWorldSdfDistances.IsCreated)
            {
                _dummyWorldSdfDistances = new NativeArray<float>(1, Allocator.Persistent);
                _dummyWorldSdfDistances[0] = 1000000f;
            }

			if (!_dummyWorldSdfBrickOffsets.IsCreated)
			{
				_dummyWorldSdfBrickOffsets = new NativeArray<int>(1, Allocator.Persistent);
				_dummyWorldSdfBrickOffsets[0] = -1;
			}
			if (!_dummyWorldSdfBrickDistances.IsCreated)
			{
				_dummyWorldSdfBrickDistances = new NativeArray<float>(1, Allocator.Persistent);
				_dummyWorldSdfBrickDistances[0] = 1000000f;
			}

			if (!_dummyTerrainHeights01.IsCreated)
			{
				_dummyTerrainHeights01 = new NativeArray<float>(1, Allocator.Persistent);
				_dummyTerrainHeights01[0] = 0f;
			}
            if (!_recallEligibleBlobIds.IsCreated)
            {
                _recallEligibleBlobIds = new NativeArray<byte>(1, Allocator.Persistent);
                _recallEligibleBlobIds[0] = 0;
            }
            _dummyWorldSdfVolume = new WorldSdfRuntime.Volume
            {
                Storage = WorldSdfRuntime.Volume.StorageKind.Dense,
                OriginSim = float3.zero,
                Dims = new int3(1, 1, 1),
                VoxelSizeSim = 1f,
                MaxDistanceSim = 1000000f,
                DenseDistancesSim = _dummyWorldSdfDistances,
				BrickSize = 1,
				BrickDims = new int3(1, 1, 1),
				BrickStrideY = 1,
				BrickStrideX = 1,
				BrickOffsets = _dummyWorldSdfBrickOffsets,
				BrickDistances = _dummyWorldSdfBrickDistances,
            };
        }
        
        public void StartRecall()
        {
            if (!PrepareRecallEligibleControllers())
            {
                return;
            }

            _connect = true;
            _connectStartTime = Time.time;

            // 召回是一次性快照：记录 eligible blobId 的“缺席帧数”。
            // 规则：eligible blobId 一旦在召回期间缺席过（>0帧），后续不允许被新组件继承，避免“发射后才出现的团”复用旧 eligible id 被预埋召回。
            int required = _recallEligibleBlobIds.IsCreated ? _recallEligibleBlobIds.Length : 0;

            if (_recallEligibleBlobAbsentFrames == null || _recallEligibleBlobAbsentFrames.Length < required)
                _recallEligibleBlobAbsentFrames = new int[required];
            for (int i = 0; i < required; i++)
                _recallEligibleBlobAbsentFrames[i] = -1;
            for (int i = 1; i < required; i++)
            {
                if (_recallEligibleBlobIds[i] != 0)
                    _recallEligibleBlobAbsentFrames[i] = 0;
            }
        }

        private bool PrepareRecallEligibleControllers()
        {
            if (!_controllerBuffer.IsCreated || _controllerBuffer.Length <= 0)
                return false;
            if (!_particles.IsCreated || activeParticles <= 0)
                return false;

            RefreshBlobIdToControllerSlotMapping();

            if (!_recallEligibleBlobIds.IsCreated || _recallEligibleBlobIds.Length < _blobIdToControllerSlot.Length)
            {
                if (_recallEligibleBlobIds.IsCreated)
                    _recallEligibleBlobIds.Dispose();
                _recallEligibleBlobIds = new NativeArray<byte>(_blobIdToControllerSlot.Length, Allocator.Persistent);
            }

            for (int i = 0; i < _recallEligibleBlobIds.Length; i++)
                _recallEligibleBlobIds[i] = 0;
            if (_recallEligibleBlobIds.Length > 0)
                _recallEligibleBlobIds[0] = 0;

            bool hasAny = false;
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                if (p.SourceId >= 0)
                    continue;
                if (p.Type != ParticleType.Separated && p.Type != ParticleType.Emitted)
                    continue;
                int blobId = p.BlobId;
                if (blobId <= 0)
                    continue;
                if (blobId < 0 || blobId >= _recallEligibleBlobIds.Length)
                    continue;
                if (_recallEligibleBlobIds[blobId] == 0)
                {
                    GetOrCreateControllerSlotForBlobId(blobId);
                    _recallEligibleBlobIds[blobId] = 1;
                }
                hasAny = true;
            }

            return hasAny;
        }

        public void SwitchInstance()
        {
            if (!_slimeInstances.IsCreated || _slimeInstances.Length <= 0)
                return;

            for (int i = 0; i < _slimeInstances.Length; i++)
            {
                if (!_slimeInstances[i].Active)
                    continue;
                _controlledInstance = i;
                if (trans != null)
                    trans.position = _slimeInstances[i].Center * SimToWorldScale;
                break;
            }
        }

        private void FadeOutSmallSeparatedControllers()
        {
            if (!_controllerBuffer.IsCreated || _controllerBuffer.Length <= 1)
                return;
            if (separatedFadeOutParticleThreshold <= 0)
                return;
            if (separatedFadeOutConfirmFrames <= 0)
                return;
            if (separatedFadeOutFrames <= 0)
                return;

            float3 mainCenter = float3.zero;
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
                mainCenter = _controllerBuffer[0].Center;
            else if (trans != null)
                mainCenter = (float3)(trans.position * WorldToSimScale);

            int controllerCount = _controllerBuffer.Length;

            if (_controllerSmallSeparatedFrames == null || _controllerSmallSeparatedFrames.Length < controllerCount)
                _controllerSmallSeparatedFrames = new int[controllerCount];
            if (_controllerFadeRemainingFrames == null || _controllerFadeRemainingFrames.Length < controllerCount)
                _controllerFadeRemainingFrames = new int[controllerCount];
            if (_controllerFadePerFrame == null || _controllerFadePerFrame.Length < controllerCount)
                _controllerFadePerFrame = new int[controllerCount];
            if (_controllerFadeBudget == null || _controllerFadeBudget.Length < controllerCount)
                _controllerFadeBudget = new int[controllerCount];

            for (int c = 1; c < controllerCount; c++)
            {
                var ctrl = _controllerBuffer[c];
                if (!ctrl.IsValid)
                {
                    _controllerSmallSeparatedFrames[c] = 0;
                    _controllerFadeRemainingFrames[c] = 0;
                    _controllerFadePerFrame[c] = 0;
                    _controllerFadeBudget[c] = 0;
                    continue;
                }

                if (_controllerFadeRemainingFrames[c] <= 0)
                {
                    int count = ctrl.ParticleCount;
                    if (count > 0 && count < separatedFadeOutParticleThreshold)
                    {
                        _controllerSmallSeparatedFrames[c]++;
                        if (_controllerSmallSeparatedFrames[c] >= separatedFadeOutConfirmFrames)
                        {
                            _controllerFadeRemainingFrames[c] = separatedFadeOutFrames;
                            _controllerFadePerFrame[c] = math.max(1, (count + separatedFadeOutFrames - 1) / separatedFadeOutFrames);

                            for (int i = 0; i < activeParticles; i++)
                            {
                                var p = _particles[i];
                                if (p.SourceId >= 0)
                                    continue;
                                if (p.FreeFrames > 0)
                                    continue;
                                if (p.ControllerSlot != c)
                                    continue;

                                if (p.Type == ParticleType.MainBody || p.Type == ParticleType.Dormant || p.Type == ParticleType.SceneDroplet)
                                    continue;

                                p.Type = ParticleType.FadingOut;
                                p.ClusterId = 0;
                                _particles[i] = p;

                                if (_velocityBuffer.IsCreated && i < _velocityBuffer.Length)
                                    _velocityBuffer[i] = float3.zero;
                            }
                        }
                    }
                    else
                    {
                        _controllerSmallSeparatedFrames[c] = 0;
                    }
                }

                if (_controllerFadeRemainingFrames[c] > 0)
                {
                    _controllerFadeBudget[c] = (_controllerFadeRemainingFrames[c] <= 1)
                        ? int.MaxValue
                        : math.max(1, _controllerFadePerFrame[c]);
                }
                else
                {
                    _controllerFadeBudget[c] = 0;
                }
            }

            for (int i = activeParticles - 1; i >= 0; i--)
            {
                var p = _particles[i];
                if (p.Type == ParticleType.MainBody || p.Type == ParticleType.Dormant)
                    continue;
                if (p.SourceId >= 0)
                    continue;
                if (p.FreeFrames > 0)
                    continue;
                int cid = p.ControllerSlot;
                if (cid <= 0 || cid >= controllerCount)
                {
                    if (p.Type != ParticleType.FadingOut)
                        continue;

                    ParticleStateManager.SetDormant(ref p);
                    _particles[i] = p;

                    if (i < activeParticles - 1)
                    {
                        ParticleStateManager.SwapParticles(ref _particles, i, activeParticles - 1);
                        var tempVel = _velocityBuffer[i];
                        _velocityBuffer[i] = _velocityBuffer[activeParticles - 1];
                        _velocityBuffer[activeParticles - 1] = tempVel;
                    }

                    activeParticles--;

                    if (activeParticles < DropletSubsystem.DROPLET_START && activeParticles >= 0 && activeParticles < _particles.Length)
                    {
                        float3 offset = new float3(
                            UnityEngine.Random.value - RandomCenterOffset,
                            UnityEngine.Random.value - RandomCenterOffset,
                            UnityEngine.Random.value - RandomCenterOffset
                        ) * (PBF_Utils.h * MainRespawnRandomOffsetScale);

                        _particles[activeParticles] = new Particle
                        {
                            Position = mainCenter + offset,
                            Type = ParticleType.MainBody,
                            ControllerSlot = 0,
                            BlobId = 0,
                        };
                    }
                }
                else
                {
                    if (p.Type != ParticleType.FadingOut)
                        continue;

                    int budget = _controllerFadeBudget[cid];
                    if (budget <= 0)
                        continue;

                    _controllerFadeBudget[cid]--;
                    ParticleStateManager.SetDormant(ref p);
                    _particles[i] = p;
                    if (i < activeParticles - 1)
                    {
                        ParticleStateManager.SwapParticles(ref _particles, i, activeParticles - 1);
                        var tempVel = _velocityBuffer[i];
                        _velocityBuffer[i] = _velocityBuffer[activeParticles - 1];
                        _velocityBuffer[activeParticles - 1] = tempVel;
                    }
                    activeParticles--;
                    _controllerFadeRemainingFrames[cid]--;
                    if (_controllerFadeRemainingFrames[cid] <= 0)
                    {
                        _controllerSmallSeparatedFrames[cid] = 0;
                        _controllerFadePerFrame[cid] = 0;
                        _controllerFadeBudget[cid] = 0;
                    }
                }
            }
        }

        void Start()
        {
            if (trans == null)
            {
                trans = transform;
            }

            if (float.IsNaN(simToWorldScale) || float.IsInfinity(simToWorldScale) || simToWorldScale <= 0f)
            {
                Debug.LogError($"[Slime_PBF] simToWorldScale 非法：{simToWorldScale}，已回退为 {DefaultSimToWorldScale}");
                simToWorldScale = DefaultSimToWorldScale;
            }

            PerformanceProfiler.Enabled = performanceProfilerEnabled;
            
            // 使用 maxParticles 替代 PBF_Utils.Num
            _particles = new NativeArray<Particle>(maxParticles, Allocator.Persistent);
            _particlesRenderBuffer = new NativeArray<Particle>(maxParticles, Allocator.Persistent);
            int targetInitialMainCount = _slimeVolume != null ? _slimeVolume.initialMainVolume : DefaultInitialMainCount;
            targetInitialMainCount = Mathf.Min(targetInitialMainCount, maxParticles);
            // 主体粒子分区为[0-8191]，最大8192个
            targetInitialMainCount = Mathf.Min(targetInitialMainCount, MainBodyMaxParticles);

            int initialMainCount = targetInitialMainCount;
            if (enableSpawnCoalesce)
            {
                int core = Mathf.Clamp(spawnCoalesceInitialMainParticles, 1, targetInitialMainCount);
                initialMainCount = core;
                _spawnCoalesceRemaining = Mathf.Max(0, targetInitialMainCount - initialMainCount);
                _spawnCoalesceNextBatchTime = Time.time + Mathf.Max(0f, spawnCoalesceStartDelaySeconds);

				if (_spawnCoalesceRemaining > 0)
				{
					Transform root = _topDownController3D != null ? _topDownController3D.transform : trans;
					if (root == null)
						root = transform;
					root.position += Vector3.up * Mathf.Max(0f, spawnCoalesceHoverHeightWorld);
					BeginSpawnCoalesceLock();
				}
            }
            
        // 初始化主体粒子 - 使用简单的线性循环，确保所有粒子都被初始化
            // 获取玩家位置作为粒子生成中心（转换为模拟坐标系）
            float3 spawnCenter = trans != null ? (float3)(trans.position * WorldToSimScale) : float3.zero;
            // 初始化 _prevTransPosition，避免第一帧速度计算错误
            _prevTransPosition = trans != null ? trans.position : Vector3.zero;
            _lastFixedTransPosWorld = _prevTransPosition;
            _lastFixedTransPosWorldInitialized = trans != null;
            
            // 计算立方体边长，确保能容纳 initialMainCount 个粒子
            int cubeSize = Mathf.CeilToInt(Mathf.Pow(initialMainCount, 1f / 3f));
            float cubeHalf = cubeSize / 2.0f;
            
            for (int idx = 0; idx < maxParticles; idx++)
            {
                if (idx < initialMainCount)
                {
                    // 将线性索引转换为 3D 坐标
                    int x = idx % cubeSize;
                    int y = (idx / cubeSize) % cubeSize;
                    int z = idx / (cubeSize * cubeSize);
                    
                    // XZ 居中，Y 从底部向上
                    float3 offset = new float3(x - cubeHalf, y, z - cubeHalf) * MainInitSpacingSim;
                    _particles[idx] = new Particle
                    {
                        Position = spawnCenter + offset,
                        Type = ParticleType.MainBody,
                        SourceId = -1,
                        ClusterId = 0,
                        FreeFrames = 0,
                        ControllerSlot = 0,
                        BlobId = 0,
                        FramesOutsideMain = 0
                    };
                }
                else
                {
                    _particles[idx] = new Particle
                    {
                        Position = new float3(0, -1000, 0),
                        Type = ParticleType.Dormant,
                        SourceId = -1,
                        ClusterId = 0,
                        FreeFrames = 0,
                        ControllerSlot = 0,
                        BlobId = 0,
                        FramesOutsideMain = 0
                    };
                }
            }
            
            // 初始活跃粒子数
            activeParticles = initialMainCount;
            
            // === 【初始化诊断】输出粒子分布信息 ===
            {
            }
            
            // 收集场景中的所有 SceneDropletSource
            allSources.Clear();
            allSources.AddRange(FindObjectsByType<DropWater>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            
            if (allSources.Count > 0)
            {
                // 找到场景水珠源
                foreach (var source in allSources)
                {
                    source.Reset(); // 重置到休眠状态
                }
            }


            var worldIndex = SlimeWorldColliderIndex.GetOrCreate();
            if (useWorldStaticSdf && worldStaticSdfBytes != null)
            {
                _worldStaticSdf.LoadFromBytes(worldStaticSdfBytes);
            }
            LayerMask staticLayers = worldStaticColliderLayers.value != 0 ? worldStaticColliderLayers : (LayerMask)(~0);
            if (!_worldIndexMaskWarnLogged && worldStaticColliderLayers.value == 0)
            {
                _worldIndexMaskWarnLogged = true;
                Debug.LogWarning("[Slime_PBF] worldStaticColliderLayers=0，静态碰撞层将使用 Everything(~0)。这可能把 TerrainCollider/大型体积碰撞体纳入普通碰撞（已在索引侧过滤 TerrainCollider，但仍建议显式配置层）。");
            }
            LayerMask dynamicLayers = worldDynamicColliderLayers;
            worldIndex.Configure(staticLayers, dynamicLayers, includeTriggerColliders: false);
            worldIndex.Rebuild();
            
            // 初始化PBF物理系统（主体配置）
            _pbfSystem = new PBFSystem(PBFSystem.Config.MainBody);
            
            // 原始数据
            _particlesTemp = new NativeArray<Particle>(maxParticles, Allocator.Persistent);
            _posOld = new NativeArray<float3>(maxParticles, Allocator.Persistent);
            _clampDelta = new NativeArray<float3>(maxParticles, Allocator.Persistent); // 【P4】钳制位移量
            _velocityBuffer = new NativeArray<float3>(maxParticles, Allocator.Persistent);
            _velocityTempBuffer = new NativeArray<float3>(maxParticles, Allocator.Persistent);
            _renderLut = new NativeHashMap<int, int2>(maxParticles * 8, Allocator.Persistent);

            _mergeMainBodyLut = new NativeParallelMultiHashMap<int, int>(DropletSubsystem.DROPLET_START, Allocator.Persistent);
            _mergeAbsorbLut = new NativeParallelMultiHashMap<int, int>(DropletSubsystem.DROPLET_START, Allocator.Persistent);
            _autoSeparateClusterCounts = new NativeArray<int>(256, Allocator.Persistent);
            _autoSeparateClusterCenters = new NativeArray<float3>(256, Allocator.Persistent);
            
            // 网格容量需要根据 maxParticles 调整
            // 场景水珠分散时需要更多网格块，使用 maxParticles * 2 确保足够
            int gridNumExpanded = Mathf.Max(PBF_Utils.GridNum * 4, maxParticles * 2);
            _gridBuffer = new NativeArray<float>(PBF_Utils.GridSize * gridNumExpanded, Allocator.Persistent);
            _gridTempBuffer = new NativeArray<float>(PBF_Utils.GridSize * gridNumExpanded, Allocator.Persistent);
            _gridLut = new NativeHashMap<int3, int>(gridNumExpanded, Allocator.Persistent);
            _gridKeys = new NativeList<int3>(gridNumExpanded, Allocator.Persistent);
            _dropletGridBuffer = new NativeArray<float>(PBF_Utils.GridSize * gridNumExpanded, Allocator.Persistent);
            _dropletGridTempBuffer = new NativeArray<float>(PBF_Utils.GridSize * gridNumExpanded, Allocator.Persistent);
            _dropletGridLut = new NativeHashMap<int3, int>(gridNumExpanded, Allocator.Persistent);
            _dropletGridKeys = new NativeList<int3>(gridNumExpanded, Allocator.Persistent);
            _covBuffer = new NativeArray<float4x4>(maxParticles, Allocator.Persistent);
            _blockBuffer = new NativeArray<int4>(gridNumExpanded, Allocator.Persistent);
            _blockColorBuffer = new NativeArray<int>(9, Allocator.Persistent);

            _dropletBlockBuffer = new NativeArray<int4>(gridNumExpanded, Allocator.Persistent);
            _dropletBlockColorBuffer = new NativeArray<int>(9, Allocator.Persistent);
            
            _bubblesBuffer  = new NativeArray<Effects.Bubble>(PBF_Utils.BubblesCount, Allocator.Persistent);
            _bubblesPoolBuffer = new NativeList<int>(PBF_Utils.BubblesCount, Allocator.Persistent);
            for (int i = 0; i < PBF_Utils.BubblesCount; ++i)
            {
                _bubblesBuffer[i] = new Effects.Bubble()
                {
                    LifeTime = -1,
                };
                _bubblesPoolBuffer.Add(i);
            }

            _componentsBuffer = new NativeList<Effects.Component>(16, Allocator.Persistent);
            _gridIDBuffer = new NativeArray<int>(_gridBuffer.Length, Allocator.Persistent);
            _dropletGridIDBuffer = new NativeArray<int>(_dropletGridBuffer.Length, Allocator.Persistent);
            _prevGridBlobId = new NativeArray<int>(_gridBuffer.Length, Allocator.Persistent);
            _controllerBuffer = new NativeList<ParticleController>(16, Allocator.Persistent);
            _windZones = new NativeList<WindFieldZoneData>(16, Allocator.Persistent);
            _boundsBuffer = new NativeArray<float3>(2, Allocator.Persistent);

            _colliderBuffer = new NativeArray<MyBoxCollider>(mainColliderIndexQueryCacheCapacity, Allocator.Persistent);
            _raycastHits = new RaycastHit[16];
            _currentColliderCount = 0;

            _dropletColliderBuffer = new NativeArray<MyBoxCollider>(dropletColliderIndexQueryCacheCapacity, Allocator.Persistent);
            _currentDropletColliderCount = 0;
            
            // 初始控制器中心必须与粒子初始化时的 spawnCenter 一致
            float3 initialCenter = trans != null ? (float3)(trans.position * WorldToSimScale) : float3.zero;
            var initialController = new ParticleController
            {
                Center = initialCenter,
                Radius = DefaultWorldToSimScale,
                Velocity = float3.zero,
                Concentration = concentration,
                IsValid = true,
                GroundY = initialCenter.y - 20f,
                GroundPoint = new float3(initialCenter.x, initialCenter.y - 20f, initialCenter.z),
                GroundNormal = new float3(0, 1, 0),
            };
            _controllerBuffer.Add(initialController);

            _marchingCubes = new LMarchingCubes();
            _marchingCubesDroplet = new LMarchingCubes();


            _particleRenderer = new SlimeParticleRenderer(particleMat, particleMesh, maxParticles);
            _bubblesDataBuffer = new ComputeBuffer(PBF_Utils.BubblesCount, sizeof(float) * 8);
            _particleRenderer.SetSimToWorldScale(SimToWorldScale);

            _slimeInstances = new NativeList<SlimeInstance>(16,  Allocator.Persistent);
            float3 initialInstanceCenter = trans != null ? (float3)(trans.position * WorldToSimScale) : float3.zero;
            float3 initialDir = trans != null ? (float3)trans.forward : new float3(0, 0, 1);
            _slimeInstances.Add(new SlimeInstance()
            {
                Active = true,
                Center = initialInstanceCenter,
                Pos = initialInstanceCenter,
                Dir = initialDir,
                Radius = coreRadius
            });
            _instancePool = new Stack<int>();
            
            int sourceCapacity = math.max(16, allSources.Count);
            _sourceControllers = new NativeArray<ParticleController>(sourceCapacity, Allocator.Persistent);
            
            // 初始化场景水珠子系统（固定分区）
            // 确保至少有1个源容量（即使没找到源）
            _dropletSubsystem = new DropletSubsystem();
            int sourceCapacityForDroplets = math.max(1, allSources.Count);
            _dropletSubsystem.Initialize(_particles, _velocityBuffer, sourceCapacityForDroplets);
            
            
            // 首次刷新近场碰撞体
            RefreshMainNearbyColliders();
            RefreshDropletNearbyColliders();

            if (_slimeVolume == null)
            {
                Debug.LogWarning("[Slime_PBF] 未绑定 SlimeVolume 组件，体积管理功能将不可用");
            }
            else
            {
                _slimeVolume.UpdateFromParticles(_particles, true);
            }

            _runtimeInitialized = true;

            // 初始化 Terrain 高度图缓存
            InitTerrainHeightfield();
        }

        private void InitTerrainHeightfield()
        {
            if (_terrainHeights01.IsCreated)
                return;

            var terrain = Terrain.activeTerrain;
            if (terrain == null)
                terrain = FindFirstObjectByType<Terrain>(FindObjectsInactive.Exclude);
            if (terrain == null)
            {
                if (!_terrainHeightfieldLogged)
                {
                    _terrainHeightfieldLogged = true;
                    Debug.LogWarning("[Slime_PBF] Terrain heightfield 未初始化：场景中未找到 Terrain。Terrain 碰撞将不会启用。 ");
                }
                return;
            }
            if (terrain.terrainData == null)
            {
                if (!_terrainHeightfieldLogged)
                {
                    _terrainHeightfieldLogged = true;
                    Debug.LogWarning($"[Slime_PBF] Terrain heightfield 未初始化：{terrain.name} 的 terrainData 为 null。Terrain 碰撞将不会启用。 ");
                }
                return;
            }

            var td = terrain.terrainData;
            int res = td.heightmapResolution;
            if (res < 2)
            {
                if (!_terrainHeightfieldLogged)
                {
                    _terrainHeightfieldLogged = true;
                    Debug.LogWarning($"[Slime_PBF] Terrain heightfield 未初始化：{terrain.name} heightmapResolution={res} 不合法。Terrain 碰撞将不会启用。 ");
                }
                return;
            }

            float[,] heights = td.GetHeights(0, 0, res, res);
            _terrainResX = res;
            _terrainResZ = res;
            _terrainOriginSim = (float3)(terrain.GetPosition() * WorldToSimScale);
            _terrainSizeSim = (float3)(td.size * WorldToSimScale);
            _terrainHeightfieldSource = terrain;

            _terrainHeights01 = new NativeArray<float>(res * res, Allocator.Persistent);
            int idx = 0;
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    _terrainHeights01[idx++] = heights[z, x];
                }
            }

            if (!_terrainHeightfieldLogged)
            {
                _terrainHeightfieldLogged = true;

                float minH = float.PositiveInfinity;
                float maxH = float.NegativeInfinity;
                double sumH = 0.0;
                for (int i = 0; i < _terrainHeights01.Length; i++)
                {
                    float hi01 = _terrainHeights01[i];
                    if (hi01 < minH) minH = hi01;
                    if (hi01 > maxH) maxH = hi01;
                    sumH += hi01;
                }
                float avgH = _terrainHeights01.Length > 0 ? (float)(sumH / _terrainHeights01.Length) : 0f;

                int cx = res / 2;
                int cz = res / 2;
                int idx00 = 0;
                int idx11 = (res - 1) * res + (res - 1);
                int idxCC = cz * res + cx;
                float h00 = _terrainHeights01[idx00];
                float h10 = _terrainHeights01[idx00 + 1];
                float h01 = _terrainHeights01[idx00 + _terrainResX];
                float h11 = _terrainHeights01[idx00 + _terrainResX + 1];
                float h0 = math.lerp(h00, h10, 0.5f);
                float h1 = math.lerp(h01, h11, 0.5f);
                float h = math.lerp(h0, h1, 0.5f);
                float cacheSimY = _terrainOriginSim.y + h * _terrainSizeSim.y;
                float cacheW = cacheSimY * SimToWorldScale;

                Vector3 originW = terrain.GetPosition();
                Vector3 sizeW = td.size;
                float y00W = originW.y + h00 * sizeW.y;
                float y11W = originW.y + h11 * sizeW.y;
                float yCCW = originW.y + h * sizeW.y;

                Vector3 p0W = activeParticles > 0 ? (Vector3)(_particles[0].Position * SimToWorldScale) : Vector3.zero;
                Debug.Log(
                    $"[Slime_PBF] Terrain heightfield 已加载 terrain={terrain.name} res={res} " +
                    $"originW=({originW.x:F2},{originW.y:F2},{originW.z:F2}) sizeW=({sizeW.x:F2},{sizeW.y:F2},{sizeW.z:F2}) " +
                    $"originSim=({_terrainOriginSim.x:F2},{_terrainOriginSim.y:F2},{_terrainOriginSim.z:F2}) sizeSim=({_terrainSizeSim.x:F2},{_terrainSizeSim.y:F2},{_terrainSizeSim.z:F2}) " +
                    $"h01[min,max,avg]=({minH:F4},{maxH:F4},{avgH:F4}) " +
                    $"samples01[00,cc,11]=({h00:F4},{h:F4},{h11:F4}) samplesYw[00,cc,11]=({y00W:F2},{yCCW:F2},{y11W:F2}) " +
                    $"activeParticles={activeParticles} p0W=({p0W.x:F2},{p0W.y:F2},{p0W.z:F2})" );
            }
        }
        
        private void OnDestroy()
        {
            _runtimeInitialized = false;

            if (_slimeStencilMaskMat != null) Destroy(_slimeStencilMaskMat);
            if (_faceStencilMat != null) Destroy(_faceStencilMat);
            if (_bubblesStencilMat != null) Destroy(_bubblesStencilMat);

            if (_particles.IsCreated) _particles.Dispose();
            if (_particlesRenderBuffer.IsCreated) _particlesRenderBuffer.Dispose();
            if (_particlesTemp.IsCreated) _particlesTemp.Dispose();
            if (_renderHashesBuffer.IsCreated) _renderHashesBuffer.Dispose();
            if (_renderLut.IsCreated) _renderLut.Dispose();
            if (_posOld.IsCreated) _posOld.Dispose();
            if (_clampDelta.IsCreated) _clampDelta.Dispose();
            if (_velocityBuffer.IsCreated) _velocityBuffer.Dispose();
            if (_velocityTempBuffer.IsCreated) _velocityTempBuffer.Dispose();

            if (_mergeMainBodyLut.IsCreated) _mergeMainBodyLut.Dispose();
            if (_mergeAbsorbLut.IsCreated) _mergeAbsorbLut.Dispose();
            if (_autoSeparateClusterCounts.IsCreated) _autoSeparateClusterCounts.Dispose();
            if (_autoSeparateClusterCenters.IsCreated) _autoSeparateClusterCenters.Dispose();
            
            // 释放PBF物理系统
            _pbfSystem?.Dispose();
            if (_boundsBuffer.IsCreated) 
            {
                _boundsBuffer.Dispose();
            }
            if (_gridBuffer.IsCreated) _gridBuffer.Dispose();
            if (_gridTempBuffer.IsCreated) _gridTempBuffer.Dispose();
            if (_dropletGridBuffer.IsCreated) _dropletGridBuffer.Dispose();
            if (_dropletGridTempBuffer.IsCreated) _dropletGridTempBuffer.Dispose();
            if (_covBuffer.IsCreated) _covBuffer.Dispose();
            if (_gridLut.IsCreated) _gridLut.Dispose();
            if (_dropletGridLut.IsCreated) _dropletGridLut.Dispose();
            if (_gridKeys.IsCreated) _gridKeys.Dispose();
            if (_dropletGridKeys.IsCreated) _dropletGridKeys.Dispose();
            if (_blockBuffer.IsCreated) _blockBuffer.Dispose();
            if (_blockColorBuffer.IsCreated) _blockColorBuffer.Dispose();
            if (_dropletBlockBuffer.IsCreated) _dropletBlockBuffer.Dispose();
            if (_dropletBlockColorBuffer.IsCreated) _dropletBlockColorBuffer.Dispose();
            if (_bubblesBuffer.IsCreated) _bubblesBuffer.Dispose();
            if (_bubblesPoolBuffer.IsCreated) _bubblesPoolBuffer.Dispose();
            if (_componentsBuffer.IsCreated) _componentsBuffer.Dispose();
            if (_gridIDBuffer.IsCreated) _gridIDBuffer.Dispose();
            if (_dropletGridIDBuffer.IsCreated) _dropletGridIDBuffer.Dispose();
            if (_prevGridBlobId.IsCreated) _prevGridBlobId.Dispose();
            if (_controllerBuffer.IsCreated) _controllerBuffer.Dispose();
            if (_blobIdToControllerSlot.IsCreated) _blobIdToControllerSlot.Dispose();
            if (_recallEligibleBlobIds.IsCreated) _recallEligibleBlobIds.Dispose();
            if (_slimeInstances.IsCreated) _slimeInstances.Dispose();
            if (_controllerMainBodyCentroid.IsCreated) _controllerMainBodyCentroid.Dispose();
            if (_controllerMainBodyCount.IsCreated) _controllerMainBodyCount.Dispose();
            if (_controllerCentroidAll.IsCreated) _controllerCentroidAll.Dispose();
            if (_controllerCountAll.IsCreated) _controllerCountAll.Dispose();
            if (_colliderBuffer.IsCreated)  _colliderBuffer.Dispose();
            if (_dropletColliderBuffer.IsCreated) _dropletColliderBuffer.Dispose();
            if (_sourceControllers.IsCreated) _sourceControllers.Dispose();

            if (_windZones.IsCreated) _windZones.Dispose();

            _worldStaticSdf.Dispose();
            if (_dummyWorldSdfDistances.IsCreated) _dummyWorldSdfDistances.Dispose();
			if (_dummyWorldSdfBrickOffsets.IsCreated) _dummyWorldSdfBrickOffsets.Dispose();
			if (_dummyWorldSdfBrickDistances.IsCreated) _dummyWorldSdfBrickDistances.Dispose();
			if (_dummyTerrainHeights01.IsCreated) _dummyTerrainHeights01.Dispose();
            
            // 释放场景水珠子系统
            _dropletSubsystem.Dispose();

            if (_marchingCubes != null)
            {
                _marchingCubes.Dispose();
                _marchingCubes = null;
            }

            if (_marchingCubesDroplet != null)
            {
                _marchingCubesDroplet.Dispose();
                _marchingCubesDroplet = null;
            }

            _particleRenderer?.Dispose();
            _particleRenderer = null;
            
            if (_bubblesDataBuffer != null)
            {
                _bubblesDataBuffer.Release();
                _bubblesDataBuffer = null;
            }

            // 释放 Terrain 高度图缓存
            if (_terrainHeights01.IsCreated) _terrainHeights01.Dispose();

            if (_freeParticleCentroidTemp.IsCreated) _freeParticleCentroidTemp.Dispose();
            if (_freeParticleCountTemp.IsCreated) _freeParticleCountTemp.Dispose();
        }

        void Update()
        {
            if (!_runtimeInitialized)
                return;

            ProcessSpawnCoalesce();

            // 渲染预测使用 FixedUpdate 中的稳定速度，避免 transform 仅在 FixedUpdate 跳变时
            // 用 Time.deltaTime 计算速度导致的脉冲/过冲。
            _renderVelocity = _velocity;

            if (trans != null)
            {
                _prevRenderTransPosition = trans.position;
            }
        }

        private void ProcessSpawnCoalesce()
        {
            if (!enableSpawnCoalesce)
                return;

			if (_spawnCoalesceRemaining <= 0)
			{
				EndSpawnCoalesceLock();
				return;
			}
            if (Time.time < _spawnCoalesceNextBatchTime)
                return;

            int batch = Mathf.Min(_spawnCoalesceRemaining, Mathf.Max(1, spawnCoalesceParticlesPerBatch));
            int restored = RestoreMainBodyParticles(batch, spawnCoalesceSpawnRadius, spawnCoalesceVerticalScale, spawnCoalesceInwardVelocityScale);
            _spawnCoalesceRemaining -= restored;
            _spawnCoalesceNextBatchTime = Time.time + Mathf.Max(0f, spawnCoalesceBatchInterval);

            if (_slimeVolume != null && restored > 0)
            {
                _slimeVolume.UpdateFromParticles(_particles, true);
            }
        }

		private void BeginSpawnCoalesceLock()
		{
			if (_spawnCoalesceLockActive)
				return;

			_spawnCoalesceLockActive = true;
			_spawnCoalesceOriginalSlimeGravity = gravity;
			gravity = 0f;

			if (_topDownController3D != null)
			{
				_spawnCoalesceOriginalControllerGravityActive = _topDownController3D.GravityActive;
				_spawnCoalesceOriginalFreeMovement = _topDownController3D.FreeMovement;
				_topDownController3D.GravityActive = false;
				_topDownController3D.FreeMovement = false;
			}

			_spawnCoalesceCharacter = GetComponentInParent<Character>();
			_spawnCoalesceCharacter?.Freeze();
		}

		private void EndSpawnCoalesceLock()
		{
			if (!_spawnCoalesceLockActive)
				return;
			_spawnCoalesceLockActive = false;

			gravity = _spawnCoalesceOriginalSlimeGravity;
			_spawnCoalesceCharacter?.UnFreeze();
			if (_topDownController3D != null)
			{
				_topDownController3D.GravityActive = _spawnCoalesceOriginalControllerGravityActive;
				_topDownController3D.FreeMovement = _spawnCoalesceOriginalFreeMovement;
			}
			_spawnCoalesceCharacter = null;
		}

        private void UpdateCombinedBoundsForParticlesRender()
        {
            float3 minSim = new float3(float.MaxValue);
            float3 maxSim = new float3(float.MinValue);

            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                if (p.Type == ParticleType.Dormant)
                    continue;
                float3 pos = p.Position;
                minSim = math.min(minSim, pos);
                maxSim = math.max(maxSim, pos);
            }

            bool hasMain = minSim.x < float.MaxValue;

            float3 dropletMinSim = float3.zero;
            float3 dropletMaxSim = float3.zero;
            bool hasDroplets = _dropletSubsystem.ActiveCount > 0 && _dropletSubsystem.TryGetActiveBounds(out dropletMinSim, out dropletMaxSim);

            if (!hasMain && !hasDroplets)
            {
                _bounds = new Bounds();
                return;
            }

            float radiusSim = ParticleRadiusWorldScaled * WorldToSimScale;
            float marginSim = math.max(PBF_Utils.h, radiusSim * 2f);

            if (hasMain)
            {
                minSim -= marginSim;
                maxSim += marginSim;
            }

            if (hasDroplets)
            {
                dropletMinSim -= marginSim;
                dropletMaxSim += marginSim;
                _dropletBounds = new Bounds()
                {
                    min = dropletMinSim * SimToWorldScale,
                    max = dropletMaxSim * SimToWorldScale
                };
            }

            if (!hasMain)
            {
                _bounds = _dropletBounds;
                return;
            }

            _bounds = new Bounds()
            {
                min = minSim * SimToWorldScale,
                max = maxSim * SimToWorldScale
            };

            if (hasDroplets)
            {
                _bounds.Encapsulate(_dropletBounds.min);
                _bounds.Encapsulate(_dropletBounds.max);
            }
        }

        private void LateUpdate()
        {
            if (!_runtimeInitialized)
                return;
            if (!isActiveAndEnabled)
                return;

            PerformanceProfiler.CounterAdd("Slime_LateUpdateCalls", 1);

            if (_deferredPostSimPending)
            {
                _deferredPostSimPending = false;
                RunDeferredPostSim();
            }

            if (renderMode == RenderMode.Surface)
            {
                PerformanceProfiler.Begin("RenderSurfaceMeshes");
                RenderSurfaceMeshes();
                PerformanceProfiler.End("RenderSurfaceMeshes");
            }
            else if (renderMode == RenderMode.Particles)
            {
                PerformanceProfiler.Begin("RenderParticles");
                RenderParticles();
                PerformanceProfiler.End("RenderParticles");
            }

            PerformanceProfiler.Begin("RenderBubbles");
            RenderBubbles();
            PerformanceProfiler.End("RenderBubbles");

            PerformanceProfiler.Begin("RenderFaces");
            RenderFaces();
            PerformanceProfiler.End("RenderFaces");

            if (_lastPerformanceProfilerFrame == Time.frameCount)
                PerformanceProfiler.EndFrame();
        }

        private void RunDeferredPostSim()
        {
            PerformanceProfiler.CounterAdd("Slime_RunDeferredPostSimCalls", 1);

            if (renderMode == RenderMode.Surface)
            {
                PerformanceProfiler.CounterAdd("Slime_SurfaceCalls", 1);
                PerformanceProfiler.Begin("Surface");
                Surface();
                PerformanceProfiler.End("Surface");
            }
            
            PerformanceProfiler.Begin("Unshuffle");
            NativeArray<Particle>.Copy(_particles, _particlesTemp, activeParticles);
            NativeArray<float3>.Copy(_velocityBuffer, _velocityTempBuffer, activeParticles);
            var hashesActive = _pbfSystem.Hashes.GetSubArray(0, activeParticles);
            var psUpdatedActive = _particlesTemp.GetSubArray(0, activeParticles);
            var velSortedActive = _velocityTempBuffer.GetSubArray(0, activeParticles);
            new Simulation_PBF.UnshuffleJob
            {
                Hashes = hashesActive,
                PsUpdated = psUpdatedActive,
                VelocitySorted = velSortedActive,
                PsOriginal = _particles,
                VelocityOriginal = _velocityBuffer,
            }.Schedule().Complete();
            PerformanceProfiler.End("Unshuffle");
            
            PerformanceProfiler.Begin("Control");
            PerformanceProfiler.CounterAdd("Slime_ControlCalls", 1);
            PerformanceProfiler.Begin("Control_Watering");
            ApplyWateringInteraction();
            PerformanceProfiler.End("Control_Watering");

            PerformanceProfiler.Begin("Control_BeforeControl");
            BeforeControl?.Invoke(this);
            PerformanceProfiler.End("Control_BeforeControl");

            Control();
            PerformanceProfiler.End("Control");
            
            PerformanceProfiler.Begin("UpdateBubblesEffects");
            UpdateBubblesEffects();
            PerformanceProfiler.End("UpdateBubblesEffects");
            
            bubblesNum = PBF_Utils.BubblesCount - _bubblesPoolBuffer.Length;
            
            if (_bubblesDataBuffer != null)
            {
                PerformanceProfiler.Begin("Bubbles_SetData");
                _bubblesDataBuffer.SetData(_bubblesBuffer);
                PerformanceProfiler.End("Bubbles_SetData");
            }

            if (renderMode == RenderMode.Particles)
            {
                UpdateCombinedBoundsForParticlesRender();
            }
            else
            {
                _bounds = new Bounds()
                {
                    min = minPos * SimToWorldScale,
                    max = maxPos * SimToWorldScale
                };

                if (_dropletSubsystem.ActiveCount > 0)
                {
                    _bounds.Encapsulate(_dropletBounds.min);
                    _bounds.Encapsulate(_dropletBounds.max);
                }
            }

            if (_slimeVolume != null)
            {
                PerformanceProfiler.Begin("Volume_UpdateFromParticles");
                PerformanceProfiler.End("Volume_UpdateFromParticles");
            }
        }

        private Vector3 GetRenderPredictOffsetWorld()
        {
            if (!_runtimeInitialized)
                return Vector3.zero;

            if (trans == null)
                return Vector3.zero;

            if (_lastFixedTransPosWorldInitialized)
            {
                Vector3 offset = trans.position - _lastFixedTransPosWorld;
                if (offset.sqrMagnitude <= 1e-10f)
                {
                    float fixedDt = Time.fixedDeltaTime;
                    float dt = Time.time - Time.fixedTime;
                    if (fixedDt > 1e-6f)
                        dt = Mathf.Clamp(dt, 0f, fixedDt);
                    offset = _renderVelocity * dt;
                }
                if (offset.sqrMagnitude > 2500f)
                    return Vector3.zero;
                return offset;
            }

            if (_fixedMainBodyCentroidWorldValid)
            {
                Vector3 offset = MainBodyCentroidWorldForRender - MainBodyCentroidWorldFixed;
                if (offset.sqrMagnitude > 2500f)
                    return Vector3.zero;
                return offset;
            }

            return Vector3.zero;
        }

        private void EnsureStencilMaterials()
        {
            if (_slimeStencilMaskMat == null)
            {
                var shader = Shader.Find("Hidden/Revive/SlimeStencilMask");
                if (shader != null)
                {
                    _slimeStencilMaskMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    _slimeStencilMaskMat.renderQueue = 3002;
                }
            }

            if (_bubblesStencilMat == null && bubblesMat != null)
            {
                _bubblesStencilMat = new Material(bubblesMat) { hideFlags = HideFlags.HideAndDontSave };
                _bubblesStencilMat.renderQueue = 3003;
            }

            if (_faceStencilMat == null && faceMat != null)
            {
                var shader = Shader.Find("Revive/Slime/FaceStencil");
                if (shader != null)
                {
                    _faceStencilMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    _faceStencilMat.renderQueue = 3003;
                }
            }
        }

        private void RenderBubbles()
        {
            if (!enableBubbles)
                return;
            if (bubblesMat == null)
                return;
            if (particleMesh == null)
                return;
            if (_bubblesDataBuffer == null)
                return;
            if (!_bubblesBuffer.IsCreated)
                return;

            EnsureStencilMaterials();
            Material bubblesMatToUse = _bubblesStencilMat != null ? _bubblesStencilMat : bubblesMat;

            if (_bubblesBlock == null)
                _bubblesBlock = new MaterialPropertyBlock();

            if (!_bubblesBaseSizeCached && bubblesMatToUse.HasProperty(_bubblesSizeId))
            {
                _bubblesBaseSize = bubblesMatToUse.GetFloat(_bubblesSizeId);
                _bubblesBaseSizeCached = true;
            }

            float sizeBoost = 1f;
            if (_bubbleBoostEndTime > 0f && Time.time <= _bubbleBoostEndTime)
                sizeBoost = Mathf.Max(0f, _bubbleBoostSizeMultiplier);

            _bubblesBlock.SetFloat(_bubblesSizeId, _bubblesBaseSize * SlimeWorldScaleFactor * sizeBoost);
            if (bubblesMatToUse.HasProperty(_bubblesSimToWorldScaleId))
                _bubblesBlock.SetFloat(_bubblesSimToWorldScaleId, SimToWorldScale);
            _bubblesBlock.SetBuffer(_bubblesBufferId, _bubblesDataBuffer);

            Vector3 predictOffsetWorld = GetRenderPredictOffsetWorld();
            _bubblesBlock.SetVector(_bubblesPredictOffsetWorldId, predictOffsetWorld);
            Bounds drawBounds = _bounds.size.sqrMagnitude > 1e-6f
                ? _bounds
                : new Bounds(trans != null ? trans.position : Vector3.zero, Vector3.one * 10000f);
            drawBounds.center += predictOffsetWorld;

            Graphics.DrawMeshInstancedProcedural(
                particleMesh,
                0,
                bubblesMatToUse,
                drawBounds,
                PBF_Utils.BubblesCount,
                _bubblesBlock,
                UnityEngine.Rendering.ShadowCastingMode.Off,
                false,
                gameObject.layer);
        }

        private void RenderFaces()
        {
            if (concentration <= 5)
                return;
            if (faceMesh == null || faceMat == null)
                return;
            if (!_slimeInstances.IsCreated)
                return;

            EnsureStencilMaterials();
            Material faceMatToUse = faceMat;
            if (_faceStencilMat != null)
            {
                faceMatToUse = _faceStencilMat;
                if (faceMat.HasProperty(_faceTextureId) && _faceStencilMat.HasProperty(_faceTextureId))
                    _faceStencilMat.SetTexture(_faceTextureId, faceMat.GetTexture(_faceTextureId));
                if (faceMat.HasProperty(_faceColorId) && _faceStencilMat.HasProperty(_faceColorId))
                    _faceStencilMat.SetColor(_faceColorId, faceMat.GetColor(_faceColorId));
            }

            Vector3 predictOffsetWorld = GetRenderPredictOffsetWorld();
            int layer = gameObject.layer;
            for (int i = 0; i < _slimeInstances.Length; i++)
            {
                var slime = _slimeInstances[i];
                if (!slime.Active)
                    continue;

                int controllerId = slime.ControllerSlot;
                if (controllerId > 0 && !showEyesOnSeparated)
                    continue;
                if (controllerId > 0 && _controllerFreeFramesCounts != null && controllerId < _controllerFreeFramesCounts.Length && _controllerFreeFramesCounts[controllerId] > 0)
                    continue;

                Vector3 renderDir = slime.Dir;
                renderDir.y = 0f;
                if (renderDir.sqrMagnitude <= 0.001f)
                    continue;

                int controllerIdForScale = slime.ControllerSlot;
                int countForScale = 0;
                if (controllerIdForScale == 0)
                {
                    if (_controllerMainBodyCount.IsCreated && controllerIdForScale >= 0 && controllerIdForScale < _controllerMainBodyCount.Length)
                        countForScale = _controllerMainBodyCount[controllerIdForScale];
                }
                else
                {
                    if (_controllerCountAll.IsCreated && controllerIdForScale >= 0 && controllerIdForScale < _controllerCountAll.Length)
                        countForScale = _controllerCountAll[controllerIdForScale];
                }

                float countFactor = 1f;
                if (faceScaleBaseParticles > 0 && countForScale > 0)
                {
                    float ratio = (float)countForScale / faceScaleBaseParticles;
                    countFactor = math.pow(math.max(1e-4f, ratio), 1f / 3f);
                    countFactor = math.clamp(countFactor, faceScaleCountFactorMin, faceScaleCountFactorMax);
                }

                float faceWorldScale = faceScale * countFactor * math.sqrt(slime.Radius * SimToWorldScale);
                Graphics.DrawMesh(faceMesh, Matrix4x4.TRS(slime.Pos * SimToWorldScale + predictOffsetWorld,
                    Quaternion.LookRotation(-renderDir),
                    faceWorldScale * Vector3.one), faceMatToUse, layer);
            }
        }

        private void RenderSurfaceMeshes()
        {
            if (mat == null)
                return;

            bool reduceForCarry = ShouldReduceDistortionForCarry();
            MaterialPropertyBlock block = null;
            if (_surfaceTintEnabled || reduceForCarry)
            {
                if (_surfaceTintBlock == null)
                    _surfaceTintBlock = new MaterialPropertyBlock();

                block = _surfaceTintBlock;
                block.Clear();

                if (_surfaceTintEnabled)
                    block.SetColor(_surfaceColorId, _surfaceTintColor);

                if (reduceForCarry)
                {
                    if (mat.HasProperty(_slimeBlurStrengthId))
                        block.SetFloat(_slimeBlurStrengthId, carryBlurStrength);
                    if (mat.HasProperty(_slimeDistortionStrengthId))
                        block.SetFloat(_slimeDistortionStrengthId, carryDistortionStrength);
                }
            }
            int layer = gameObject.layer;
            Vector3 predictOffsetWorld = GetRenderPredictOffsetWorld();

            if (_mesh != null)
            {
                Matrix4x4 matrix = Matrix4x4.TRS(_meshOriginWorld + predictOffsetWorld, Quaternion.identity, Vector3.one);
                Graphics.DrawMesh(_mesh, matrix, mat, layer, null, 0, block);

                EnsureStencilMaterials();
                if (_slimeStencilMaskMat != null)
                    Graphics.DrawMesh(_mesh, matrix, _slimeStencilMaskMat, layer);
            }

            if (_dropletMesh != null)
            {
                Material dropletMatToUse = dropletSurfaceMat != null ? dropletSurfaceMat : mat;
                Graphics.DrawMesh(_dropletMesh, Matrix4x4.TRS(_dropletMeshOriginWorld + predictOffsetWorld, Quaternion.identity, Vector3.one), dropletMatToUse, layer, null, 0, null);
            }
        }

        private void RenderParticles()
        {
            if (_particleRenderer == null)
                return;

            if (particleMat == null || particleMesh == null)
            {
                if (!_renderInitErrorLogged)
                {
                    _renderInitErrorLogged = true;
                    Debug.LogError($"[Slime_PBF] Particles 渲染缺少资源：particleMat={(particleMat != null)} particleMesh={(particleMesh != null)}");
                }
                return;
            }

            _particleRenderer.SetSimToWorldScale(SimToWorldScale);

            PerformanceProfiler.Begin("Particles_ConvertToWorldPositions");
            int totalParticles = ConvertToWorldPositionsForRendering();
            PerformanceProfiler.End("Particles_ConvertToWorldPositions");
            if (totalParticles <= 0)
                return;

            if (!particleMat.enableInstancing && !_renderInitErrorLogged)
            {
                _renderInitErrorLogged = true;
                Debug.LogWarning("[Slime_PBF] particleMat.enableInstancing=false，DrawMeshInstancedProcedural 可能不会生效。");
            }

            if (_bounds.size.sqrMagnitude <= 1e-6f)
                _particleRenderer.SetInfiniteBounds();
            else
                _particleRenderer.SetBounds((float3)_bounds.min, (float3)_bounds.max);

            if (_lastParticleRenderUploadFrame != Time.frameCount)
            {
                _lastParticleRenderUploadFrame = Time.frameCount;
                PerformanceProfiler.Begin("Particles_Upload");
                _particleRenderer.UploadParticles(_particlesRenderBuffer, totalParticles);
                PerformanceProfiler.End("Particles_Upload");
            }

            PerformanceProfiler.Begin("Particles_Draw");
            _particleRenderer.Draw(totalParticles, anisotropic: false);
            PerformanceProfiler.End("Particles_Draw");
        }

        private void FixedUpdate()
        {
            if (!isActiveAndEnabled)
                return;
            if (!_runtimeInitialized)
                return;

            if (_fixedUpdateCountFrame != Time.frameCount)
            {
                _fixedUpdateCountFrame = Time.frameCount;
                _fixedUpdateCountThisFrame = 0;
            }
            _fixedUpdateCountThisFrame++;
			_fixedUpdateSerial++;
            if (_lastPerformanceProfilerFrame != Time.frameCount)
            {
                _lastPerformanceProfilerFrame = Time.frameCount;
                PerformanceProfiler.BeginFrame();
                PerformanceProfiler.CounterAdd("Slime_ProfilerBeginFrameCalls", 1);
            }

            PerformanceProfiler.CounterAdd("Slime_FixedUpdateCalls", 1);

            PerformanceProfiler.CounterSet("Slime_MainActiveParticles", activeParticles);
            PerformanceProfiler.CounterSet("Slime_DropletActiveParticles", _dropletSubsystem.ActiveCount);
            PerformanceProfiler.CounterSet("Slime_DropletColliderCount", _currentDropletColliderCount);

            int totalSources = allSources != null ? allSources.Count : 0;
            int simulatedSources = 0;
            if (totalSources > 0)
            {
                for (int s = 0; s < totalSources; s++)
                {
                    var src = allSources[s];
                    if (src != null && src.State == DropWater.DropletSourceState.Simulated)
                        simulatedSources++;
                }
            }
            PerformanceProfiler.CounterSet("Slime_DropletSourcesTotal", totalSources);
            PerformanceProfiler.CounterSet("Slime_DropletSourcesSimulated", simulatedSources);

            PerformanceProfiler.Begin("UpdateSceneDropletSources");
            UpdateSceneDropletSources();
            PerformanceProfiler.End("UpdateSceneDropletSources");

            PerformanceProfiler.Begin("UpdateSourceControllers");
            UpdateSourceControllers();
            PerformanceProfiler.End("UpdateSourceControllers");
            
            // 不再移除场景水珠，让它们留在主粒子系统中以便渲染和交互
            
            PerformanceProfiler.Begin("RefreshMainColliders");
            RefreshMainNearbyColliders();
            PerformanceProfiler.End("RefreshMainColliders");
            
            PerformanceProfiler.Begin("RefreshDropletColliders");
            RefreshDropletNearbyColliders();
            PerformanceProfiler.End("RefreshDropletColliders");

            // 使用 trans.position 变化量计算速度，保证位置和速度同步
            // 这样高速位置补偿的方向和控制器中心的移动方向一致，不会导致粒子飞散
            if (trans != null && Time.fixedDeltaTime > 0)
            {
                _velocity = (trans.position - _prevTransPosition) / Time.fixedDeltaTime;
                _prevTransPosition = trans.position;

                _renderVelocity = _velocity;
                _prevRenderTransPosition = trans.position;

                _lastFixedTransPosWorld = trans.position;
                _lastFixedTransPosWorldInitialized = true;
            }
            
            // 原版时序：不在 Simulate() 前更新 Center，让 ApplyForceJob 使用上一帧的 Center 和 Radius
            // 这样 len 和 Radius 匹配，粒子更容易满足 len < Radius 条件受向心力
            // Center 和 Radius 统一在 Control() 中更新（Simulate 之后）
            
            int mainSubsteps = math.max(1, mainSimSubstepsPerFixedUpdate);
            if (adaptiveMainSimSubsteps && _fixedUpdateCountThisFrame > 1)
                mainSubsteps = math.max(1, mainSimSubstepsWhenBacklog);

            for (int i = 0; i < mainSubsteps; i++)
            {
                Profiler.BeginSample("Simulate_Main");
                PerformanceProfiler.Begin(i == 0 ? "Simulate_0" : "Simulate_1");
                PerformanceProfiler.CounterAdd(i == 0 ? "Slime_Simulate0Calls" : "Slime_Simulate1Calls", 1);
                Simulate();
                PerformanceProfiler.End(i == 0 ? "Simulate_0" : "Simulate_1");
                Profiler.EndSample();
            }
            
            // 水珠独立模拟（独立分区，使用完整 PBF 物理 + 环境碰撞）
            Profiler.BeginSample("Simulate_Droplets");
            PerformanceProfiler.Begin("Simulate_Droplets");
            PerformanceProfiler.CounterAdd("Slime_SimulateDropletsCalls", 1);
            
            // 实时更新物理参数（从第一个激活的 DropWater 读取，支持 Inspector 调整）
            PerformanceProfiler.Begin("Droplets_UpdatePhysicsParams");
            if (Application.isEditor)
            {
                for (int s = 0; s < allSources.Count; s++)
                {
                    var source = allSources[s];
                    if (source == null || source.State != DropWater.DropletSourceState.Simulated) continue;
                    bool liquidMode = (source.initialMode == DropWater.InitialMode.Liquid);
                    _dropletSubsystem.UpdateSourceLiquidMode(s, liquidMode);
                    _dropletSubsystem.UpdateSourcePhysicsParams(
                        s,
                        source.cohesionStrength,
                        source.cohesionRadius,
                        source.velocityDamping,
                        source.verticalCohesionScale,
                        source.enableViscosity,
                        source.viscosityStrength);
                }
            }
            PerformanceProfiler.End("Droplets_UpdatePhysicsParams");

            _dropletSubsystem.UpdateInteractionSettings(new DropletSubsystem.InteractionSettings
            {
                DynamicDragStrength = dropletDynamicDragStrength,
                DynamicDragRadius = dropletDynamicDragRadiusWorld * WorldToSimScale,
                LiquidSolverIterations = dropletLiquidSolverIterations,
                LiquidGravityY = dropletLiquidGravityY,
                DropletVerticalOffset = dropletVerticalOffset,
                EnableLiquidModeOnDynamicCollision = dropletEnableLiquidModeOnDynamicCollision
            });
            
            // 地面高度：使用场景实际地面（世界Y=0）转为内部坐标
            float dropletGroundY = 0f; // 场景地面世界Y=0 → 内部Y=0
            // 恢复碰撞体，让水珠正常站在地面上
            int useSdf = useWorldStaticSdf && _worldStaticSdf.IsCreated ? 1 : 0;
            _dropletSubsystem.Simulate(deltaTime, _particles, _velocityBuffer, dropletGroundY, targetDensity, 
                _dropletColliderBuffer, _currentDropletColliderCount,
                useSdf,
                useSdf != 0 ? _worldStaticSdf.Data : _dummyWorldSdfVolume,
                ParticleRadiusWorldScaled * WorldToSimScale,
                worldStaticSdfFriction,
                (useSdf != 0 && disableStaticColliderFallbackWhenUsingSdf) ? 1 : 0);
            
            PerformanceProfiler.End("Simulate_Droplets");
            Profiler.EndSample();

            PerformanceProfiler.Begin("Control_MainController_Fixed");
            UpdateMainControllerAfterSimulate();
            PerformanceProfiler.End("Control_MainController_Fixed");

            _deferredPostSimPending = true;
        }

        private void UpdateMainControllerAfterSimulate()
        {
            // 只更新主体控制器（0号），用于追帧时多次 Simulate 之间的 Center/Radius 同步。
            if (_controllerBuffer.Length == 0)
            {
                _controllerBuffer.Add(default);
            }

            float3 mainCenter = trans != null ?
                (float3)(trans.position * WorldToSimScale) : float3.zero;
            
            // 计算主体半径 - 基于 MainBody 粒子的最大距离
            float mainMaxDist = 0f;
            float3 mainCentroidSum = float3.zero;
            int mainCentroidCount = 0;
            int count = math.min(activeParticles, _particles.Length);
            for (int i = 0; i < count; i++)
            {
                var p = _particles[i];
                if (p.Type != ParticleType.MainBody) continue;

                mainCentroidSum += p.Position;
                mainCentroidCount++;
                float dist = math.length(p.Position - mainCenter);
                if (dist < coreRadius)
                {
                    mainMaxDist = math.max(mainMaxDist, dist);
                }
            }

            float3 mainCentroidSim = mainCentroidCount > 0 ? (mainCentroidSum / mainCentroidCount) : mainCenter;
            Vector3 mainCentroidWorld = (Vector3)(mainCentroidSim * SimToWorldScale);
            if (!_fixedMainBodyCentroidWorldValid)
            {
                _prevFixedMainBodyCentroidWorld = mainCentroidWorld;
                _lastFixedMainBodyCentroidWorld = mainCentroidWorld;
                _fixedMainBodyCentroidWorldValid = true;
            }
            else
            {
                _prevFixedMainBodyCentroidWorld = _lastFixedMainBodyCentroidWorld;
                _lastFixedMainBodyCentroidWorld = mainCentroidWorld;
            }
            
            float rawMainRadius = math.max(coreRadius, mainMaxDist * mainRadiusScale);
            float mainRadius;
            if (_lastMainRadius <= 0)
            {
                mainRadius = rawMainRadius;
            }
            else
            {
                float shrinkRate = mainRadiusShrinkRate;
                if (_lastMainRadius > rawMainRadius * 1.5f)
                {
                    shrinkRate = math.min(shrinkRate, 0.90f);
                }
                float minAllowed = _lastMainRadius * shrinkRate;
                mainRadius = math.max(rawMainRadius, minAllowed);
            }
            _lastMainRadius = mainRadius;
            
            float3 mainVelocity = (float3)_velocity * WorldToSimScale;
            float dynamicConcentration = concentration;
            
            float prevGroundY0 = 0f;
            float3 prevGroundPoint0 = float3.zero;
            float3 prevGroundNormal0 = new float3(0, 1, 0);
            if (_controllerBuffer.Length > 0)
            {
                var prev0 = _controllerBuffer[0];
                prevGroundY0 = prev0.GroundY;
                prevGroundPoint0 = prev0.GroundPoint;
                prevGroundNormal0 = prev0.GroundNormal;
            }

            _controllerBuffer[0] = new ParticleController()
            {
                Center = mainCenter,
                Radius = mainRadius,
                Velocity = mainVelocity,
                Concentration = dynamicConcentration,
                ParticleCount = 0,
                FramesWithoutParticles = 0,
                IsValid = true,
                GroundY = prevGroundY0,
                GroundPoint = prevGroundPoint0,
                GroundNormal = prevGroundNormal0,
            };
        }

        private void Surface()
        {
            float t0 = Time.realtimeSinceStartup;

            bool moving = _renderVelocity.sqrMagnitude > 1e-6f;

            int mainInterval = math.max(1, mainMarchingCubesIntervalFrames);
            if (moving)
                mainInterval = 1;
            bool shouldUpdateMainMesh = (_mesh == null) || ((_fixedUpdateSerial - _lastMainMarchingCubesFrame) >= mainInterval);
            if (shouldUpdateMainMesh)
            {
                SurfaceMain();
                _lastMainMarchingCubesFrame = _fixedUpdateSerial;
            }

            SurfaceDroplets();

            float dtMs = (Time.realtimeSinceStartup - t0) * 1000f;
            if (dtMs > 12f)
            {
                if (Time.frameCount - _lastSurfaceSpikeLogFrame > 30)
                {
                    _lastSurfaceSpikeLogFrame = Time.frameCount;

                    int dropletCount = _dropletSubsystem.ActiveCount;
                    Vector3 boundsSizeW = (Vector3)((maxPos - minPos) * SimToWorldScale);
                    int mainClearLen = blockNum * PBF_Utils.GridSize;
                    int dropletBlockNumForLog = 0;
                    if (_dropletGridLut.IsCreated)
                        dropletBlockNumForLog = _dropletGridLut.Count;
                    int dropletClearLen = dropletBlockNumForLog * PBF_Utils.GridSize;
                    Debug.Log($"[SurfaceSpike] frame={Time.frameCount} dt={dtMs:F2}ms mainParticles={activeParticles} dropletParticles={dropletCount} boundsSizeW={boundsSizeW} blockNum={blockNum} clearLen={mainClearLen} dropletBlockNum={dropletBlockNumForLog} dropletClearLen={dropletClearLen}");
                }
            }
        }

        private void SurfaceMain()
        {
            Profiler.BeginSample("Render");

            int totalParticles = ConvertMainToWorldPositionsForRendering();

            PerformanceProfiler.Begin("SurfaceMain_BuildLut");
            BuildRenderLut(totalParticles);
            PerformanceProfiler.End("SurfaceMain_BuildLut");

            PerformanceProfiler.Begin("SurfaceMain_MeanPos");

            var handle = new Reconstruction.ComputeMeanPosJob
            {
                Lut = _renderLut,
                Ps = _particlesRenderBuffer,
                MeanPos = _particlesTemp,
            }.Schedule(totalParticles, batchCount);

            if (useAnisotropic)
            {
                handle = new Reconstruction.ComputeCovarianceJob()
                {
                    Lut = _renderLut,
                    Ps = _particlesRenderBuffer,
                    MeanPos = _particlesTemp,
                    GMatrix = _covBuffer,
                }.Schedule(totalParticles, batchCount, handle);
            }

            new Reconstruction.CalcBoundsJob()
            {
                Ps = _particlesRenderBuffer,
                Controllers = _controllerBuffer,
                SourceControllers = _sourceControllers,
                Bounds = _boundsBuffer,
                ActiveCount = totalParticles,
            }.Schedule(handle).Complete();
            PerformanceProfiler.End("SurfaceMain_MeanPos");

            Profiler.EndSample();

            float blockSize = PBF_Utils.CellSize * 4;
            minPos = math.floor(_boundsBuffer[0] / blockSize) * blockSize;
            maxPos = math.ceil(_boundsBuffer[1] / blockSize) * blockSize;

            Profiler.BeginSample("Allocate");
            if (!PrepareSurfaceGridCore(
                totalParticles,
                minPos,
                _gridLut,
                _gridKeys,
                _gridBuffer,
                _gridTempBuffer,
                _gridIDBuffer,
                _blockBuffer,
                _blockColorBuffer,
                "SurfaceMain_Allocate",
                "SurfaceMain_GetKeyArray",
                "SurfaceMain_ClearGrid",
                "SurfaceMain_ColorBlocks",
                "SurfaceMain_Splat",
                out var keys,
                out blockNum,
                out var grid,
                out var gridTemp,
                out var gridID))
            {
                Profiler.EndSample();
                _mesh = null;
                return;
            }

            Profiler.EndSample();

            JobHandle ccaHandle = default;
            JobHandle particleIdHandle = default;

            _componentsBuffer.Clear();
            ccaHandle = new Effects.ConnectComponentBlockJob()
            {
                Keys = keys,
                Grid = grid,
                GridLut = _gridLut,
                Components = _componentsBuffer,
                GridID = gridID,
                Threshold = PBF_Utils.CCAThreshold,
            }.Schedule();

            particleIdHandle = new Effects.ParticleIDJob()
            {
                GridLut = _gridLut,
                GridID = gridID,
                Controllers = _controllerBuffer,
                SourceControllers = _sourceControllers,
                Particles = _particles,
                MinPos = minPos,
            }.Schedule(activeParticles, batchCount, ccaHandle);

            bool needMainMesh = renderMode == RenderMode.Surface;
            if (needMainMesh)
            {
                Profiler.BeginSample("Blur");
                _mesh = BuildSurfaceMeshFromGridCore(
                    keys,
                    _gridLut,
                    grid,
                    gridTemp,
                    _marchingCubes,
                    minPos,
                    maxPos,
                    "SurfaceMain_Blur",
                    "SurfaceMain_MarchingCubes",
                    out _meshOriginWorld);

                Profiler.EndSample();
            }

            Profiler.BeginSample("CCA");
            PerformanceProfiler.Begin("SurfaceMain_CCA");
            ccaHandle.Complete();
            PerformanceProfiler.End("SurfaceMain_CCA");
            Profiler.EndSample();

            Profiler.BeginSample("StableID");
            PerformanceProfiler.Begin("SurfaceMain_StableID");
            ResolveBlobIds();
            PerformanceProfiler.End("SurfaceMain_StableID");
            Profiler.EndSample();

            Profiler.BeginSample("ParticleID");
            PerformanceProfiler.Begin("SurfaceMain_ParticleID");
            particleIdHandle.Complete();
            PerformanceProfiler.End("SurfaceMain_ParticleID");
            Profiler.EndSample();

            PerformanceProfiler.Begin("SurfaceMain_DisposeKeys");
            PerformanceProfiler.End("SurfaceMain_DisposeKeys");
        }

        private bool PrepareSurfaceGridCore(
            int activeCount,
            float3 minPosSim,
            NativeHashMap<int3, int> gridLut,
            NativeList<int3> gridKeys,
            NativeArray<float> gridBuffer,
            NativeArray<float> gridTempBuffer,
            NativeArray<int> gridIDBuffer,
            NativeArray<int4> blockBuffer,
            NativeArray<int> blockColorBuffer,
            string profAllocate,
            string profGetKeyArray,
            string profClearGrid,
            string profColorBlocks,
            string profSplat,
            out NativeArray<int3> keys,
            out int blockNum,
            out NativeArray<float> grid,
            out NativeArray<float> gridTemp,
            out NativeArray<int> gridID)
        {
            keys = default;
            blockNum = 0;
            grid = default;
            gridTemp = default;
            gridID = default;

            gridLut.Clear();
            gridKeys.Clear();

            PerformanceProfiler.Begin(profAllocate);
            var handle = new Reconstruction.AllocateBlockJob()
            {
                Ps = _particlesTemp,
                GridLut = gridLut,
                Keys = gridKeys,
                MinPos = minPosSim,
                ActiveCount = activeCount,
            }.Schedule();
            handle.Complete();

            PerformanceProfiler.Begin(profGetKeyArray);
            keys = gridKeys.AsArray();
            PerformanceProfiler.End(profGetKeyArray);
            blockNum = keys.Length;

            if (blockNum == 0)
            {
                PerformanceProfiler.End(profAllocate);
                return false;
            }

            int clearLen = blockNum * PBF_Utils.GridSize;
            grid = gridBuffer.GetSubArray(0, clearLen);
            gridTemp = gridTempBuffer.GetSubArray(0, clearLen);
            gridID = gridIDBuffer.GetSubArray(0, clearLen);

            var clearGridHandle = new Reconstruction.ClearGridJob
            {
                Grid = grid,
                GridID = gridID,
            }.Schedule(clearLen, batchCount);

            var colorBlocksHandle = new Reconstruction.ColorBlockJob()
            {
                Keys = keys,
                Blocks = blockBuffer,
                BlockColors = blockColorBuffer,
            }.Schedule();

            PerformanceProfiler.Begin(profClearGrid);
            clearGridHandle.Complete();
            PerformanceProfiler.End(profClearGrid);

            PerformanceProfiler.Begin(profColorBlocks);
            colorBlocksHandle.Complete();
            PerformanceProfiler.End(profColorBlocks);
            PerformanceProfiler.End(profAllocate);

            PerformanceProfiler.Begin(profSplat);

            JobHandle splatHandle;

            #if USE_SPLAT_SINGLE_THREAD
            splatHandle = new Reconstruction.DensityProjectionJob()
            {
                Ps = _particlesTemp,
                GMatrix = _covBuffer,
                Grid = gridBuffer,
                GridLut = gridLut,
                MinPos = minPosSim,
                UseAnisotropic = useAnisotropic,
            }.Schedule();
            #elif USE_SPLAT_COLOR8
            splatHandle = default;
            for (int i = 0; i < 8; i++)
            {
                int2 slice = new int2(blockColorBuffer[i], blockColorBuffer[i + 1]);
                int count = slice.y - slice.x;
                splatHandle = new Reconstruction.DensitySplatColoredJob()
                {
                    ParticleLut = _renderLut,
                    ColorKeys = blockBuffer.Slice(slice.x, count),
                    Ps = _particlesTemp,
                    GMatrix = _covBuffer,
                    Grid = gridBuffer,
                    GridLut = gridLut,
                    MinPos = minPosSim,
                    UseAnisotropic = useAnisotropic,
                }.Schedule(count, count, splatHandle);
            }
            #else
            splatHandle = new Reconstruction.DensityProjectionParallelJob()
            {
                Ps = _particlesTemp,
                GMatrix = _covBuffer,
                GridLut = gridLut,
                Grid = grid,
                ParticleLut = _renderLut,
                Keys = keys,
                UseAnisotropic = useAnisotropic,
                MinPos = minPosSim,
            }.Schedule(keys.Length, batchCount);
            #endif

            splatHandle.Complete();
            PerformanceProfiler.End(profSplat);
            return true;
        }

        private Mesh BuildSurfaceMeshFromGridCore(
            NativeArray<int3> keys,
            NativeHashMap<int3, int> gridLut,
            NativeArray<float> grid,
            NativeArray<float> gridTemp,
            LMarchingCubes marchingCubes,
            float3 minPosSim,
            float3 maxPosSim,
            string profBlur,
            string profMarchingCubes,
            out Vector3 meshOriginWorld)
        {
            meshOriginWorld = (Vector3)(minPosSim * SimToWorldScale);

            PerformanceProfiler.Begin(profBlur);
            new Reconstruction.GridBlurJob()
            {
                Keys = keys,
                GridLut = gridLut,
                GridRead = grid,
                GridWrite = gridTemp,
            }.Schedule(keys.Length, batchCount).Complete();
            PerformanceProfiler.End(profBlur);

            PerformanceProfiler.Begin(profMarchingCubes);
            var mesh = marchingCubes.MarchingCubesParallel(keys, gridLut, gridTemp, threshold, SimToWorldScale * PBF_Utils.CellSize);
            if (mesh != null)
            {
                Vector3 baseSizeW = (Vector3)((maxPosSim - minPosSim) * SimToWorldScale);
                float marginW = PBF_Utils.CellSize * SimToWorldScale * 8f;
                mesh.bounds = new Bounds(baseSizeW * 0.5f, baseSizeW + Vector3.one * marginW);
            }
            PerformanceProfiler.End(profMarchingCubes);
            return mesh;
        }

        private void SurfaceDroplets()
        {
            int dropletCount = _dropletSubsystem.ActiveCount;
            if (dropletCount <= 0)
            {
                _dropletMesh = null;
                return;
            }

            if (renderMode != RenderMode.Surface)
                return;

            if (!_dropletSubsystem.TryGetActiveBounds(out float3 dropletMinSim, out float3 dropletMaxSim))
            {
                _dropletMesh = null;
                return;
            }

            float blockSize = PBF_Utils.CellSize * 4;
            float3 dropletMinPos = math.floor(dropletMinSim / blockSize) * blockSize;
            float3 dropletMaxPos = math.ceil(dropletMaxSim / blockSize) * blockSize;
            _dropletBounds = new Bounds()
            {
                min = dropletMinPos * SimToWorldScale,
                max = dropletMaxPos * SimToWorldScale
            };

            bool moving = _renderVelocity.sqrMagnitude > 1e-6f;
            int dropletInterval = math.max(1, dropletMarchingCubesIntervalFrames);
            if (moving)
                dropletInterval = 1;
            bool shouldUpdateDropletMesh = (_dropletMesh == null) || ((_fixedUpdateSerial - _lastDropletMarchingCubesFrame) >= dropletInterval);
            if (!shouldUpdateDropletMesh)
            {
                return;
            }

            if (_cachedMainCamera == null)
                _cachedMainCamera = Camera.main;
            var cam = _cachedMainCamera;
            if (cam != null)
            {
                Vector3 minW = (Vector3)(dropletMinSim * SimToWorldScale);
                Vector3 maxW = (Vector3)(dropletMaxSim * SimToWorldScale);
                Vector3 centerW = (minW + maxW) * 0.5f;
                Vector3 sizeW = (maxW - minW);
                float marginW = PBF_Utils.h * SimToWorldScale * 4f;
                sizeW += Vector3.one * marginW;
                var testBounds = new Bounds(centerW, sizeW);

                if (_cachedFrustumPlanes == null || _cachedFrustumPlanes.Length != 6)
                    _cachedFrustumPlanes = new UnityEngine.Plane[6];
                GeometryUtility.CalculateFrustumPlanes(cam, _cachedFrustumPlanes);
                if (!GeometryUtility.TestPlanesAABB(_cachedFrustumPlanes, testBounds))
                {
                    _dropletMesh = null;
                    return;
                }
            }

            _dropletSubsystem.CopyToRenderBuffer(_particlesRenderBuffer, 0);

            PerformanceProfiler.Begin("SurfaceDroplet_BuildLut");
            BuildRenderLut(dropletCount);
            PerformanceProfiler.End("SurfaceDroplet_BuildLut");

            PerformanceProfiler.Begin("SurfaceDroplet_MeanPos");
            var handle = new Reconstruction.ComputeMeanPosJob
            {
                Lut = _renderLut,
                Ps = _particlesRenderBuffer,
                MeanPos = _particlesTemp,
            }.Schedule(dropletCount, batchCount);

            if (useAnisotropic)
            {
                handle = new Reconstruction.ComputeCovarianceJob()
                {
                    Lut = _renderLut,
                    Ps = _particlesRenderBuffer,
                    MeanPos = _particlesTemp,
                    GMatrix = _covBuffer,
                }.Schedule(dropletCount, batchCount, handle);
            }

            handle.Complete();
            PerformanceProfiler.End("SurfaceDroplet_MeanPos");

            if (!PrepareSurfaceGridCore(
                dropletCount,
                dropletMinPos,
                _dropletGridLut,
                _dropletGridKeys,
                _dropletGridBuffer,
                _dropletGridTempBuffer,
                _dropletGridIDBuffer,
                _dropletBlockBuffer,
                _dropletBlockColorBuffer,
                "SurfaceDroplet_Allocate",
                "SurfaceDroplet_GetKeyArray",
                "SurfaceDroplet_ClearGrid",
                "SurfaceDroplet_ColorBlocks",
                "SurfaceDroplet_Splat",
                out var keys,
                out var dropletBlockNum,
                out var grid,
                out var gridTemp,
                out var gridID))
            {
                PerformanceProfiler.Begin("SurfaceDroplet_DisposeKeys");
                PerformanceProfiler.End("SurfaceDroplet_DisposeKeys");
                _dropletMesh = null;
                return;
            }

            _dropletMesh = BuildSurfaceMeshFromGridCore(
                keys,
                _dropletGridLut,
                grid,
                gridTemp,
                _marchingCubesDroplet,
                dropletMinPos,
                dropletMaxPos,
                "SurfaceDroplet_Blur",
                "SurfaceDroplet_MarchingCubes",
                out _dropletMeshOriginWorld);
            _lastDropletMarchingCubesFrame = _fixedUpdateSerial;

            PerformanceProfiler.Begin("SurfaceDroplet_DisposeKeys");
            PerformanceProfiler.End("SurfaceDroplet_DisposeKeys");
        }

        private void Simulate()
        {
            _pbfSystem.Lut.Clear();
            // 在 ApplyForceJob 之前更新控制器 Velocity
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
            {
                var mainCtrl = _controllerBuffer[0];
                mainCtrl.Velocity = (float3)_velocity * WorldToSimScale;
                _controllerBuffer[0] = mainCtrl;
            }

            PerformanceProfiler.Begin("StableIdToSlotMapping");
            RefreshBlobIdToControllerSlotMapping();
            PerformanceProfiler.End("StableIdToSlotMapping");
            
            PerformanceProfiler.Begin("ApplyForce");
             
            if (!_controllerBuffer.IsCreated)
                return;

            var controllersForJob = _controllerBuffer.AsArray();
            float3 mainCenterForJob = controllersForJob.Length > 0 ? controllersForJob[0].Center : float3.zero;
            float3 mainVelocityForJob = controllersForJob.Length > 0 ? controllersForJob[0].Velocity : float3.zero;

            NativeArray<WindFieldZoneData> windZonesForJob;
            bool disposeWindZonesForJob = false;
            if (_windZones.IsCreated)
            {
                _windZones.Clear();
                if (!_consumeWindFieldImmune)
                {
                    WindFieldRegistry.FillActiveZonesSimData(_windZones, WorldToSimScale);
                }
                windZonesForJob = _windZones.AsArray();
            }
            else
            {
                windZonesForJob = new NativeArray<WindFieldZoneData>(0, Allocator.Temp);
                disposeWindZonesForJob = true;
            }

            int windTargetLayerBit = 1 << gameObject.layer;
            new Simulation_PBF.ApplyForceJob
            {
                Ps = _particles,
                Velocity = _velocityBuffer,
                PsNew = _particlesTemp,
                Controllers = controllersForJob,
                SourceControllers = _sourceControllers,
                BlobIdToControllerSlot = _blobIdToControllerSlot,
                WindZones = windZonesForJob,
                WindTargetLayerBit = windTargetLayerBit,
                ParticleRadiusSim = ParticleRadiusWorldScaled * WorldToSimScale,
                Gravity = new float3(0, gravity, 0),
                DeltaTime = deltaTime,
                PredictStep = predictStep,
                VelocityDamping = velocityDamping,
                VerticalOffset = verticalOffset,
                DropletVerticalOffset = dropletVerticalOffset,
                EnableRecall = _connect,
                RecallEligibleBlobIds = _recallEligibleBlobIds,
                UseRecallEligibleBlobIds = _connect && _recallEligibleBlobIds.IsCreated,
                MainCenter = mainCenterForJob,
                MainVelocity = mainVelocityForJob,
                MaxDeformDistXZ = maxDeformDistXZ * WorldToSimScale, // 水平形变上限
                MaxDeformDistY = maxDeformDistY * WorldToSimScale,   // 垂直形变上限
            }.Schedule(activeParticles, batchCount).Complete();
            if (disposeWindZonesForJob)
            {
                windZonesForJob.Dispose();
            }
            PerformanceProfiler.End("ApplyForce");

            // 召回调试日志已移除

            PerformanceProfiler.Begin("Hash");
            new Simulation_PBF.HashJob
            {
                Ps = _particlesTemp,
                Hashes = _pbfSystem.Hashes,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("Hash");

            PerformanceProfiler.Begin("Sort");
            // 只排序主体粒子（水珠在固定分区，不参与排序）
            var activeHashes = _pbfSystem.Hashes.GetSubArray(0, activeParticles);
            activeHashes.SortJob(new PBF_Utils.Int2Comparer()).Schedule().Complete();
            PerformanceProfiler.End("Sort");

            PerformanceProfiler.Begin("BuildLut");
            // 构建LUT时使用所有活跃粒子（使用PBFSystem的缓冲区）
            var allActiveHashes = _pbfSystem.Hashes.GetSubArray(0, activeParticles);
            new Simulation_PBF.BuildLutJob
            {
                Hashes = allActiveHashes,
                Lut = _pbfSystem.Lut
            }.Schedule().Complete();
            PerformanceProfiler.End("BuildLut");

            PerformanceProfiler.Begin("Shuffle");
            // 获取上一帧控制器中心
            // 重要：如果还没初始化（第一帧或lengthsq为0但控制器不在原点），使用当前帧中心
            float3 prevCenter = _prevMainControllerCenter;
            float3 curCenter = _controllerBuffer.Length > 0 ? _controllerBuffer[0].Center : float3.zero;
            
            // 检查是否需要初始化：如果prev和cur差距过大（>50单位），说明prev无效
            bool needInit = math.lengthsq(_prevMainControllerCenter) < 1e-6f || 
                            math.length(curCenter - _prevMainControllerCenter) > 50f;
            if (needInit)
                prevCenter = curCenter;
            
            new Simulation_PBF.ShuffleJob
            {
                Hashes = _pbfSystem.Hashes,
                PsRaw = _particles,
                PsNew = _particlesTemp,
                Velocity = _velocityBuffer,
                PosOld = _posOld,
                PosPredict = _pbfSystem.PosPredict,
                VelocityOut = _velocityTempBuffer,
            }.Schedule(activeParticles, batchCount).Complete();
            // 注意：_prevMainControllerCenter 的更新移到 UpdateJob 之后，否则 UpdateJob 拿到的 controllerDelta 会是 0
            PerformanceProfiler.End("Shuffle");

            PerformanceProfiler.Begin("ComputeLambda");
            // 使用带 FreeFrames 检查的版本，排除发射粒子与主体的交互
            new Simulation_PBF.ComputeLambdaJobWithFreeFrames
            {
                Lut = _pbfSystem.Lut,
                PosPredict = _pbfSystem.PosPredict,
                Particles = _particlesTemp, // 用于检查 FreeFrames
                Hashes = _pbfSystem.Hashes, // 用于从排序索引映射回原始粒子索引
                Lambda = _pbfSystem.Lambda,
                TargetDensity = targetDensity,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("ComputeLambda");

            PerformanceProfiler.Begin("ComputeDeltaPos");
            new Simulation_PBF.ComputeDeltaPosJob
            {
                Lut = _pbfSystem.Lut,
                PosPredict = _pbfSystem.PosPredict,
                Lambda = _pbfSystem.Lambda,
                Hashes = _pbfSystem.Hashes, // 用于从排序索引映射回原始粒子索引
                Controllers = controllersForJob,
                BlobIdToControllerSlot = _blobIdToControllerSlot,
                PsOriginal = _particlesTemp, // 排序后的粒子数组
                PsNew = _particles,
                ClampDelta = _clampDelta, // 【P4】输出钳制位移量
                TargetDensity = targetDensity,
                MaxDeformDistXZ = maxDeformDistXZ * WorldToSimScale,
                MaxDeformDistY = maxDeformDistY * WorldToSimScale,
                MainCenter = mainCenterForJob,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("ComputeDeltaPos");

            PerformanceProfiler.Begin("Update_Prepare");
            
            float dynamicMaxVelocity = maxVelocity;
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
            {
                float controllerSpeed = math.length(_controllerBuffer[0].Velocity);
                dynamicMaxVelocity = math.max(dynamicMaxVelocity, controllerSpeed * 1.5f);
            }
            
            PerformanceProfiler.End("Update_Prepare");

            PerformanceProfiler.Begin("Update_GroundHeights");
            bool travelling = _pipeTravelAbility != null && _pipeTravelAbility.IsTravelling;
            bool useStaticSdfThisFrame = useWorldStaticSdf && _worldStaticSdf.IsCreated && !travelling;
            if (enableGroundFallbackRaycast)
            {
                if (!useStaticSdfThisFrame || disableStaticColliderFallbackWhenUsingSdf)
                {
                    RefreshGroundY();
                }
            }
            else
            {
                if (_controllerBuffer.IsCreated)
                {
                    const float veryLowGroundYSim = -10000f;
                    for (int ci = 0; ci < _controllerBuffer.Length; ci++)
                    {
                        var ctrl = _controllerBuffer[ci];
                        ctrl.GroundY = veryLowGroundYSim;
                        ctrl.GroundPoint = new float3(ctrl.Center.x, veryLowGroundYSim, ctrl.Center.z);
                        ctrl.GroundNormal = new float3(0, 1, 0);
                        _controllerBuffer[ci] = ctrl;
                    }
                }
            }
            PerformanceProfiler.End("Update_GroundHeights");

            // 后备地面高度（当粒子的 ControllerSlot 无效时使用）
            float fallbackGroundY = -10f;

            PerformanceProfiler.Begin("Update_Job");
			int useSdf = useStaticSdfThisFrame ? 1 : 0;
			NativeArray<float> terrainHeights01 = _terrainHeights01.IsCreated ? _terrainHeights01 : _dummyTerrainHeights01;
			int terrainResX = _terrainHeights01.IsCreated ? _terrainResX : 0;
			int terrainResZ = _terrainHeights01.IsCreated ? _terrainResZ : 0;
			new Simulation_PBF.UpdateJob
			{
				Ps = _particles,
				PosOld = _posOld,
				ClampDelta = _clampDelta, // 【P4】ComputeDeltaPosJob 输出的钳制位移量
				Colliders = _colliderBuffer,
				Controllers = _controllerBuffer, // 【新增】控制器数组，用于获取每个粒子对应的 GroundY
				BlobIdToControllerSlot = _blobIdToControllerSlot,
				ColliderCount = _currentColliderCount,
				UseStaticSdf = useStaticSdfThisFrame ? 1 : 0,
				StaticSdf = useStaticSdfThisFrame ? _worldStaticSdf.Data : _dummyWorldSdfVolume,
				StaticSdfQueryMul = SlimeWorldScaleFactor,
				DisableStaticColliderFallback = (useStaticSdfThisFrame && disableStaticColliderFallbackWhenUsingSdf) ? 1 : 0,
				ParticleRadiusSim = ParticleRadiusWorldScaled * WorldToSimScale,
				StaticFriction = worldStaticSdfFriction,
				TerrainHeights01 = terrainHeights01,
				TerrainResX = terrainResX,
				TerrainResZ = terrainResZ,
				TerrainOriginSim = _terrainOriginSim,
				TerrainSizeSim = _terrainSizeSim,
				Velocity = _velocityTempBuffer,
				MaxVelocity = dynamicMaxVelocity,
				DeltaTime = deltaTime,
				FallbackGroundY = fallbackGroundY,
                MaxDeformDistXZ = maxDeformDistXZ * WorldToSimScale, // 水平形变上限
                MaxDeformDistY = maxDeformDistY * WorldToSimScale,   // 垂直形变上限
                MainCenter = _controllerBuffer.Length > 0 ? _controllerBuffer[0].Center : float3.zero,
                MainVelocity = _controllerBuffer.Length > 0 ? _controllerBuffer[0].Velocity : float3.zero,
                EnableCollisionDeformLimit = enableCollisionDeformLimit,
                EnableP4VelocityConsistency = enableVelocityConsistency, // 【P4开关】
            			}.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("Update_Job");
            
            PerformanceProfiler.Begin("ApplyViscosity");
            // 使用PBFSystem的缓冲区
            new Simulation_PBF.ApplyViscosityJob
            {
                Lut = _pbfSystem.Lut,
                PosPredict = _pbfSystem.PosPredict,
                Particles = _particlesTemp, // 原始索引的粒子数组
                Hashes = _pbfSystem.Hashes, // 用于获取原始索引
                VelocityR = _velocityTempBuffer,
                VelocityW = _velocityBuffer,
                ViscosityStrength = viscosityStrength,
                TargetDensity = targetDensity,
                DeltaTime = deltaTime,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("ApplyViscosity");
            
        }

        /// <summary>
        /// 激活场景水珠源（使用独立子系统）
        /// </summary>
        private void ActivateSource(DropWater source)
        {
            if (source.State != DropWater.DropletSourceState.Dormant)
                return;

            using (PerformanceProfiler.Scope("Droplet_ActivateSource"))
            {
            
            // 获取该源在 allSources 中的索引作为 SourceId
            int sourceId = allSources.IndexOf(source);
            if (sourceId < 0)
            {
                Debug.LogError($"[Slime_PBF] 源 {source.name} 未在 allSources 中找到");
                return;
            }
            
            // 【新增】射线检测源位置的地面高度
            Vector3 sourceWorldPos = source.transform.position;
            float sourceGroundY = 0f; // 默认地面高度（模拟坐标）
            Vector3 rayOrigin = sourceWorldPos + Vector3.up * 2f; // 抬高起点
            if (UnityEngine.Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 30f, GroundQueryMask))
            {
                sourceGroundY = (hit.point.y ) * WorldToSimScale; // 加上粒子半径，转为模拟坐标
            }
            else
            {
                sourceGroundY = sourceWorldPos.y * WorldToSimScale - 2f; // 后备：源位置下方2单位
            }
            
            // 使用独立子系统激活（传入 DropWater 配置的物理参数 + 地面高度）
            float3 sourcePos = (float3)source.transform.position * WorldToSimScale;
            bool startAsLiquid = (source.initialMode == DropWater.InitialMode.Liquid);
            int allocated = _dropletSubsystem.ActivateSource(sourceId, sourcePos, source.particleCount,
                source.cohesionStrength, source.cohesionRadius, source.velocityDamping, source.verticalCohesionScale,
                source.enableViscosity, source.viscosityStrength,
                startAsLiquid,
                sourceGroundY); // 【新增】传入源位置的地面高度
            
            if (allocated > 0)
            {
                source.SetState(DropWater.DropletSourceState.Simulated);
                source.SetAdaptiveRadius(source.spawnRadius);
            }
            }
        }

        /// <summary>
        /// 休眠场景水珠源（使用独立子系统）
        /// </summary>
        private void DeactivateSource(DropWater source)
        {
            if (source.State != DropWater.DropletSourceState.Simulated)
                return;

            using (PerformanceProfiler.Scope("Droplet_DeactivateSource"))
            {
            
            int sourceId = allSources.IndexOf(source);
            if (sourceId < 0)
            {
                Debug.LogError($"[Slime_PBF] 源 {source.name} 未在 allSources 中找到");
                return;
            }
            
            // 使用独立子系统休眠
            int deactivatedCount = _dropletSubsystem.DeactivateSource(sourceId);
            source.Reset();
            }
        }

        
        /// <summary>
        /// 更新场景水珠源的激活状态
        /// </summary>
        private void UpdateSceneDropletSources()
        {
            if (allSources == null || allSources.Count == 0)
                return;
            
            // 获取主体中心位置
            float3 mainCenter = trans != null ? 
                (float3)(trans.position * WorldToSimScale) : float3.zero;
            
            foreach (var source in allSources)
            {
                if (source == null) continue;
                
                float3 sourcePos = (float3)source.transform.position * WorldToSimScale;
                float distance = math.length(mainCenter - sourcePos);
                float distanceWorld = distance * SimToWorldScale; // 转换回世界坐标
                
                switch (source.State)
                {
                    case DropWater.DropletSourceState.Dormant:
                        if (distanceWorld < source.activationRadius)
                        {
                            ActivateSource(source); // 使用原始方法
                        }
                        break;
                    
                    case DropWater.DropletSourceState.Simulated:
                        // 只检查休眠，吸收改为通过接触融合自动进行
                        if (distanceWorld > source.deactivationRadius)
                        {
                            DeactivateSource(source); // 使用原始方法
                        }
                        // 注意：不再使用 absorbRadius 距离判断吸收
                        // 场景水珠现在通过 MergeContactingParticles 的接触融合自动吸收
                        break;
                }
            }
        }

        /// <summary>
        /// 更新场景水珠的 SourceControllers（在 Simulate 之前调用）
        /// </summary>
        private void UpdateSourceControllers()
        {
            if (allSources == null || allSources.Count == 0)
            {
                return;
            }

            using (PerformanceProfiler.Scope("UpdateSourceControllers"))
            {
            
            // 确保容量足够
            if (!_sourceControllers.IsCreated || _sourceControllers.Length < allSources.Count)
            {
                if (_sourceControllers.IsCreated) _sourceControllers.Dispose();
                _sourceControllers = new NativeArray<ParticleController>(allSources.Count, Allocator.Persistent);
            }
            
            int activeCount = 0;
            for (int s = 0; s < allSources.Count; s++)
            {
                var source = allSources[s];
                if (source == null || source.State != DropWater.DropletSourceState.Simulated)
                {
                    _sourceControllers[s] = new ParticleController { Concentration = 0 }; // 无效
                    continue;
                }
                
                float3 dropletCenter = (float3)source.transform.position * WorldToSimScale;
                float dropletRadius = math.max(2f, source.AdaptiveRadius * WorldToSimScale);
                float dropletConcentration = (source.initialMode == DropWater.InitialMode.Liquid) ? 0f : 5f;
                
                _sourceControllers[s] = new ParticleController()
                {
                    Center = dropletCenter,
                    Radius = dropletRadius,
                    Velocity = float3.zero,
                    Concentration = dropletConcentration,
                };
                activeCount++;
            }
            }
        }


        private void Control()
        {
            RefreshBlobIdToControllerSlotMapping();

            PerformanceProfiler.Begin("Control_MainController");
            // === 稳定控制器：不再每帧清空，而是更新属性 ===
            
            // 确保至少有主体控制器
            if (_controllerBuffer.Length == 0)
            {
                _controllerBuffer.Add(default);
            }

            // === 1. 主体控制器始终基于 trans（用户控制的核） ===
            float3 mainCenter = trans != null ? 
                (float3)(trans.position * WorldToSimScale) : float3.zero;
            
            bool travelling = _pipeTravelAbility != null && _pipeTravelAbility.IsTravelling;
            if (_connect && recallUseGlobalFlowField && !travelling)
            {
                UpdateRecallGlobalFlowField(mainCenter);
            }
            
            // 计算主体半径 - 基于主连通组件的最大距离
            float mainMaxDist = 0;
            int mainBodyCount = 0;
            int count = math.min(activeParticles, _particles.Length);

            // 用 MainBody 粒子的最大距离来算 mainMaxDist
            for (int i = 0; i < count; i++)
            {
                var p = _particles[i];
                // 只计算主体粒子的距离
                if (p.Type != ParticleType.MainBody) continue;
                
                mainBodyCount++;
                float dist = math.length(p.Position - mainCenter);  // 模拟坐标：计算到中心距离
                
                // 只统计 coreRadius 范围内粒子，避免 mainRadius 被远处粒子无限撑大导致不稳定
                if (dist < coreRadius)
                {
                    mainMaxDist = math.max(mainMaxDist, dist);
                }
            }
            
            float rawMainRadius = math.max(coreRadius, mainMaxDist * mainRadiusScale);
            float mainRadius;
            if (_lastMainRadius <= 0)
            {
                mainRadius = rawMainRadius;
            }
            else
            {
                float shrinkRate = mainRadiusShrinkRate;
                if (_lastMainRadius > rawMainRadius * 1.5f)
                {
                    shrinkRate = math.min(shrinkRate, 0.90f);
                }
                float minAllowed = _lastMainRadius * shrinkRate;
                mainRadius = math.max(rawMainRadius, minAllowed);
            }
            _lastMainRadius = mainRadius;
            
            float3 mainVelocity = (float3)_velocity * WorldToSimScale;
            float dynamicConcentration = concentration; // 固定值
            
            float prevGroundY0 = 0f;
            float3 prevGroundPoint0 = float3.zero;
            float3 prevGroundNormal0 = new float3(0, 1, 0);
            if (_controllerBuffer.Length > 0)
            {
                var prev0 = _controllerBuffer[0];
                prevGroundY0 = prev0.GroundY;
                prevGroundPoint0 = prev0.GroundPoint;
                prevGroundNormal0 = prev0.GroundNormal;
            }

            _controllerBuffer[0] = new ParticleController()
            {
                Center = mainCenter,
                Radius = mainRadius,
                Velocity = mainVelocity,
                Concentration = dynamicConcentration,
                ParticleCount = 0,  // 稍后统计
                FramesWithoutParticles = 0,
                IsValid = true,
                GroundY = prevGroundY0,
                GroundPoint = prevGroundPoint0,
                GroundNormal = prevGroundNormal0,
            };
            
            // 重置所有分离控制器的 ParticleCount
            for (int i = 1; i < _controllerBuffer.Length; i++)
            {
                var ctrl = _controllerBuffer[i];
                ctrl.ParticleCount = 0;
                _controllerBuffer[i] = ctrl;
            }

            PerformanceProfiler.End("Control_MainController");

            // === 2. CCA 分组：为每个分离的 CCA 组件创建控制器 ===
            PerformanceProfiler.Begin("Control_CCA_Grouping");
            int compCount = _componentsBuffer.Length;
            if (_componentToGroup == null || _componentToGroup.Length < compCount)
                _componentToGroup = new int[math.max(16, compCount)];
            
            PerformanceProfiler.Begin("Control_CCA_Grouping_Alloc");
            // 统计每个 CCA 组件的分离粒子数
            var detachedParticleCountPerComp = new NativeArray<int>(math.max(1, compCount), Allocator.Temp, NativeArrayOptions.ClearMemory);
            NativeArray<int> recentFromEmitCountPerComp = default;
            if (_connect)
                recentFromEmitCountPerComp = new NativeArray<int>(math.max(1, compCount), Allocator.Temp, NativeArrayOptions.ClearMemory);

            PerformanceProfiler.End("Control_CCA_Grouping_Alloc");

            if (compCount > 0)
                System.Array.Clear(_componentToGroup, 0, compCount);

            int mainCompIdx = -1;
            float bestMainCompDist2 = float.PositiveInfinity;

            PerformanceProfiler.Begin("Control_CCA_Grouping_FindMainComp");
            for (int ci = 0; ci < compCount; ci++)
            {
                float3 cc = minPos + _componentsBuffer[ci].Center * PBF_Utils.CellSize;
                float d2 = math.lengthsq(cc - mainCenter);
                if (d2 < bestMainCompDist2)
                {
                    bestMainCompDist2 = d2;
                    mainCompIdx = ci;
                }
            }

            PerformanceProfiler.End("Control_CCA_Grouping_FindMainComp");

            PerformanceProfiler.Begin("Control_CCA_Grouping_ScanParticles");
            for (int pi = 0; pi < activeParticles; pi++)
            {
                var pp = _particles[pi];
                if (pp.Type != ParticleType.Separated && pp.Type != ParticleType.Emitted)
                    continue;
                if (pp.FreeFrames > 0)
                    continue;

                int clusterId0 = pp.ClusterId;
                if (clusterId0 <= 0 || clusterId0 > compCount)
                    continue;
                int compIdx0 = clusterId0 - 1;
                detachedParticleCountPerComp[compIdx0]++;

                if (recentFromEmitCountPerComp.IsCreated)
                {
                    bool inEmitProtection0 = pp.ControllerSlot > 0 && pp.Type == ParticleType.Separated && pp.FramesOutsideMain < 60;
                    if (inEmitProtection0)
                        recentFromEmitCountPerComp[compIdx0]++;
                }
            }

            PerformanceProfiler.End("Control_CCA_Grouping_ScanParticles");

            PerformanceProfiler.Begin("Control_CCA_Grouping_AssignControllers");

            int groupCandidateCount = 0;
            for (int ci = 0; ci < compCount; ci++)
            {
                if (ci == mainCompIdx)
                    continue;
                int particleCount = detachedParticleCountPerComp[ci];
                if (particleCount > 0 && particleCount >= minSeparateClusterSize)
                    groupCandidateCount++;
            }

            if (_controllerBuffer.IsCreated && groupCandidateCount > 0)
            {
                int expectedControllerCount = _controllerBuffer.Length + groupCandidateCount;
                if (_controllerBuffer.Capacity < expectedControllerCount)
                    _controllerBuffer.Capacity = expectedControllerCount;

                if (_controllerStepJumpTargetCenterY != null && _controllerStepJumpTargetCenterY.Length < expectedControllerCount)
                {
                    int oldLen = _controllerStepJumpTargetCenterY.Length;
                    int newLen = math.max(expectedControllerCount, oldLen * 2 + 1);
                    System.Array.Resize(ref _controllerStepJumpTargetCenterY, newLen);
                    for (int i = oldLen; i < newLen; i++)
                        _controllerStepJumpTargetCenterY[i] = float.NaN;
                }

                if (_controllerStepJumpTargetCenterY != null && _controllerStepJumpTargetExpireFrame == null)
                {
                    _controllerStepJumpTargetExpireFrame = new int[_controllerStepJumpTargetCenterY.Length];
                }

                if (_controllerStepJumpTargetExpireFrame != null && _controllerStepJumpTargetExpireFrame.Length < expectedControllerCount)
                {
                    int oldLen = _controllerStepJumpTargetExpireFrame.Length;
                    int newLen = math.max(expectedControllerCount, oldLen * 2 + 1);
                    System.Array.Resize(ref _controllerStepJumpTargetExpireFrame, newLen);
                    for (int i = oldLen; i < newLen; i++)
                        _controllerStepJumpTargetExpireFrame[i] = 0;
                }
            }

            long ticksAssignGetSlot = 0;
            long ticksAssignAvoid = 0;
            long ticksAssignWriteCtrl = 0;
            long ticksAssignNewBlob = 0;

            long ticksAvoidQuery = 0;
            long ticksAvoidSdf = 0;
            long ticksAvoidObb = 0;
            long ticksAvoidGround = 0;
            long ticksAvoidTotal = 0;
            for (int ci = 0; ci < compCount; ci++)
            {
                if (ci == mainCompIdx)
                {
                    _componentToGroup[ci] = 0;
                    continue;
                }

                int particleCount = detachedParticleCountPerComp[ci];
                if (particleCount <= 0 || particleCount < minSeparateClusterSize)
                {
                    _componentToGroup[ci] = 0;
                    continue;
                }

                var comp = _componentsBuffer[ci];
                float3 center = minPos + comp.Center * PBF_Utils.CellSize;
                float3 size = (comp.BoundsMax - comp.BoundsMin) * PBF_Utils.CellSize;
                float radiusXZ = math.max(size.x, size.z) * 0.5f;
                float halfHeight = size.y * 0.5f;
                radiusXZ = math.max(0.001f, radiusXZ);
                halfHeight = math.max(0.001f, halfHeight);
                float radius = math.max(2f, math.max(radiusXZ, halfHeight));

                int blobId = GetBlobIdForComponent(ci);
                bool suppressRecallByRecentProtection = false;
                if (_connect && recentFromEmitCountPerComp.IsCreated && _recallEligibleBlobIds.IsCreated)
                {
                    if (blobId > 0 && blobId < _recallEligibleBlobIds.Length && _recallEligibleBlobIds[blobId] != 0)
                    {
                        int recentCount = recentFromEmitCountPerComp[ci];
                        int minRecentCount = math.max(1, (int)math.ceil(particleCount * 0.8f));
                        if (particleCount > 0 && recentCount >= minRecentCount)
                        {
                            suppressRecallByRecentProtection = true;
                        }
                    }
                }

                long tGetSlot0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int controllerSlot = GetOrCreateControllerSlotForBlobId(blobId);
                ticksAssignGetSlot += System.Diagnostics.Stopwatch.GetTimestamp() - tGetSlot0;

                float3 toMain = float3.zero;
                bool allowRecallForController = _connect &&
                                               _recallEligibleBlobIds.IsCreated &&
                                               blobId > 0 && blobId < _recallEligibleBlobIds.Length &&
                                               _recallEligibleBlobIds[blobId] != 0 &&
                                               !suppressRecallByRecentProtection;

                if (allowRecallForController)
                {
                    float3 recallCenter = center;
                    if (_connect && recallUseGlobalFlowField && controllerSlot > 0 && controllerSlot < _controllerBuffer.Length && _controllerBuffer[controllerSlot].IsValid)
                        recallCenter = math.lerp(_controllerBuffer[controllerSlot].Center, center, 0.5f);

                    float distToMain = math.length(recallCenter - mainCenter);

                    const float minRecallSpeedSim = 2f;
                    const float maxRecallSpeedSim = 12f;
                    const float fadeDistanceSim = 15f;
                    float speedFactor = math.saturate(distToMain / math.max(0.001f, fadeDistanceSim));
                    float recallSpeedSim = math.lerp(minRecallSpeedSim, maxRecallSpeedSim, speedFactor);

                    float3 toMainXZ = mainCenter - recallCenter;
                    toMainXZ.y = 0;
                    float3 rawDir = math.normalizesafe(toMainXZ);
                    if (_connect && recallUseGlobalFlowField)
                    {
                        float3 flowDir = GetRecallGlobalFlowDirection(recallCenter, rawDir);
                        if (math.lengthsq(flowDir) > 1e-6f)
                            rawDir = flowDir;
                    }

                    if (_connect && recallUseGlobalFlowField && controllerSlot > 0 && controllerSlot < _controllerBuffer.Length && _controllerBuffer[controllerSlot].IsValid)
                    {
                        float3 prevVel = _controllerBuffer[controllerSlot].Velocity;
                        float3 prevDir = prevVel;
                        prevDir.y = 0f;
                        prevDir = math.normalizesafe(prevDir);
                        if (math.lengthsq(prevDir) > 1e-6f)
                        {
                            float3 blended = math.lerp(prevDir, rawDir, 0.35f);
                            blended.y = 0f;
                            blended = math.normalizesafe(blended);
                            if (math.lengthsq(blended) > 1e-6f)
                                rawDir = blended;
                        }
                    }
                    bool useAvoid = (useWorldStaticSdf && recallAvoidUseStaticSdf && _worldStaticSdf.IsCreated) || _currentColliderCount > 0;
                    if (useAvoid)
                    {
                        long tAvoid0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var perf = default(RecallAvoidPerf);
                        toMain = ComputeAvoidedRecallVelocity(controllerSlot, recallCenter, radiusXZ, halfHeight, mainCenter, rawDir, recallSpeedSim, ref perf);
                        ticksAssignAvoid += System.Diagnostics.Stopwatch.GetTimestamp() - tAvoid0;

                        ticksAvoidQuery += perf.TicksQuery;
                        ticksAvoidSdf += perf.TicksSdf;
                        ticksAvoidObb += perf.TicksObb;
                        ticksAvoidGround += perf.TicksGround;
                        ticksAvoidTotal += perf.TicksTotal;
                    }
                    else
                        toMain = recallSpeedSim * rawDir;
                }

                float3 currentCenter = center;
                float prevGroundY = 0f;
                float3 prevGroundPoint = float3.zero;
                float3 prevGroundNormal = new float3(0, 1, 0);
                if (controllerSlot > 0 && controllerSlot < _controllerBuffer.Length && _controllerBuffer[controllerSlot].IsValid)
                {
                    currentCenter = math.lerp(_controllerBuffer[controllerSlot].Center, center, 0.5f);
                    prevGroundY = _controllerBuffer[controllerSlot].GroundY;
                    prevGroundPoint = _controllerBuffer[controllerSlot].GroundPoint;
                    prevGroundNormal = _controllerBuffer[controllerSlot].GroundNormal;

                    float3 prevVel = _controllerBuffer[controllerSlot].Velocity;
                    float2 prevXZ = new float2(prevVel.x, prevVel.z);
                    float2 newXZ = new float2(toMain.x, toMain.z);
                    float2 smoothedXZ = math.lerp(prevXZ, newXZ, 0.35f);
                    toMain.x = smoothedXZ.x;
                    toMain.z = smoothedXZ.y;
                }
                long tWrite0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _controllerBuffer[controllerSlot] = new ParticleController()
                {
                    Center = currentCenter,
                    Radius = radius,
                    Velocity = toMain,
                    Concentration = concentration * 2.0f,
                    ParticleCount = 0,
                    FramesWithoutParticles = 0,
                    IsValid = true,
                    GroundY = prevGroundY,
                    GroundPoint = prevGroundPoint,
                    GroundNormal = prevGroundNormal,
                };

                ticksAssignWriteCtrl += System.Diagnostics.Stopwatch.GetTimestamp() - tWrite0;

                _componentToGroup[ci] = controllerSlot;
            }

            double invFreqMsCCA = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            PerformanceProfiler.Add("Control_CCA_Grouping_Assign_GetSlot", (float)(ticksAssignGetSlot * invFreqMsCCA));
            if (ticksAssignAvoid != 0)
            {
                PerformanceProfiler.Add("Control_CCA_Grouping_Assign_Avoid", (float)(ticksAssignAvoid * invFreqMsCCA));
                PerformanceProfiler.Add("Control_CCA_Grouping_Assign_Avoid_Query", (float)(ticksAvoidQuery * invFreqMsCCA));
                PerformanceProfiler.Add("Control_CCA_Grouping_Assign_Avoid_Sdf", (float)(ticksAvoidSdf * invFreqMsCCA));
                PerformanceProfiler.Add("Control_CCA_Grouping_Assign_Avoid_Obb", (float)(ticksAvoidObb * invFreqMsCCA));
                PerformanceProfiler.Add("Control_CCA_Grouping_Assign_Avoid_Ground", (float)(ticksAvoidGround * invFreqMsCCA));
                PerformanceProfiler.Add("Control_CCA_Grouping_Assign_Avoid_Total", (float)(ticksAvoidTotal * invFreqMsCCA));
            }
            PerformanceProfiler.Add("Control_CCA_Grouping_Assign_WriteCtrl", (float)(ticksAssignWriteCtrl * invFreqMsCCA));
            PerformanceProfiler.Add("Control_CCA_Grouping_Assign_NewBlob", (float)(ticksAssignNewBlob * invFreqMsCCA));

            PerformanceProfiler.End("Control_CCA_Grouping_AssignControllers");

            PerformanceProfiler.Begin("Control_CCA_Grouping_Dispose");
            detachedParticleCountPerComp.Dispose();
            if (recentFromEmitCountPerComp.IsCreated)
                recentFromEmitCountPerComp.Dispose();

            PerformanceProfiler.End("Control_CCA_Grouping_Dispose");

            PerformanceProfiler.End("Control_CCA_Grouping");

            // 统计分离/发射粒子数量并标记最近发射的团块
            PerformanceProfiler.Begin("Control_MapParticles_PassA");
            for (int p = 0; p < activeParticles; p++)
            {
                var particle = _particles[p];
                
                // 场景水珠使用主控制器
                if (particle.Type == ParticleType.SceneDroplet)
                {
                    particle.ControllerSlot = 0;
                    particle.BlobId = 0;
                    _particles[p] = particle;
                    continue;
                }

                if (particle.Type == ParticleType.FadingOut)
                {
                    particle.ClusterId = 0;
                    _particles[p] = particle;
                    continue;
                }
                
                // 主体和休眠粒子使用主控制器
                if (particle.Type == ParticleType.MainBody || particle.Type == ParticleType.Dormant)
                {
                    particle.ControllerSlot = 0;
                    particle.BlobId = 0;
                    _particles[p] = particle;
                    continue;
                }
                
                // 自由态粒子（发射中）：保持发射时绑定的 ControllerId，不重新分组
                if (particle.FreeFrames > 0)
                {
                    // 不修改 ControllerId，保持发射时的绑定
                    // 但需要更新控制器中心跟随粒子移动（在下面的步骤4中处理）
                    continue;
                }
                
                // 【关键】刚从发射状态转换的分离粒子：使用 FramesOutsideMain 作为 CCA 保护期
                // 在保护期内保持原 ControllerId，不被 CCA 重新分类为主体
                bool hasEmitController = particle.ControllerSlot > 0 && particle.Type == ParticleType.Separated;
                bool inEmitProtection = hasEmitController && particle.FramesOutsideMain < 60; // 60帧 ≈ 1秒保护期

                int prevControllerSlot = particle.ControllerSlot;
                int prevBlobId = particle.BlobId;
                
                // 分离/发射粒子：根据 ClusterId 映射到 CCA 分组的控制器
                int clusterId = particle.ClusterId;
                if (clusterId > 0 && clusterId <= compCount)
                {
                    int compIdx = clusterId - 1;
                    int newGroupId = _componentToGroup[compIdx];
                    int newBlobId = GetBlobIdForComponent(compIdx);
                    if (inEmitProtection && newGroupId == 0)
                    {
                        particle.FramesOutsideMain++;
                        _particles[p] = particle;
                        continue;
                    }

                    if (!inEmitProtection && newGroupId == 0)
                    {
                        ParticleStateManager.ConvertToMainBody(ref particle, mainCenter);
                        _particles[p] = particle;
                        continue;
                    }

                    // 映射到 0 表示 CCA 认为该组件属于“主体连通块/过近”，但不应在这里强制回归主体。
                    // 回归主体只能通过 MergeContactingParticles/EnableRecall。
                    particle.ControllerSlot = (newGroupId == 0) ? prevControllerSlot : newGroupId;
                    particle.BlobId = (newGroupId == 0) ? prevBlobId : newBlobId;

                    particle.FramesOutsideMain++;
                    _particles[p] = particle;
                }
                else
                {
                    if (inEmitProtection)
                    {
                        particle.FramesOutsideMain++;
                        _particles[p] = particle;
                        continue;
                    }

                    // 没有有效的 ClusterId：不强制回归主体，优先保留原控制器，避免被主体“吸回”。
                    // 如果没有原控制器，则回退到 0，但仍保持 Separated/Emitted 类型，等待 Merge/Recall。
                    int fallbackControllerSlot = prevControllerSlot > 0 ? prevControllerSlot : 0;
                    particle.ControllerSlot = fallbackControllerSlot;
                    particle.BlobId = prevBlobId > 0 ? prevBlobId : 0;
                    _particles[p] = particle;
                }
            }

            PerformanceProfiler.End("Control_MapParticles_PassA");

            // === 3. 更新粒子控制器 ID ===
            PerformanceProfiler.Begin("Control_MapParticles_PassB");
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                
                // 场景水珠使用主控制器
                if (p.SourceId >= 0 || p.Type == ParticleType.SceneDroplet)
                {
                    p.ControllerSlot = 0;
                    p.BlobId = 0;
                    _particles[i] = p;
                    continue;
                }

                if (p.Type == ParticleType.FadingOut)
                {
                    p.ClusterId = 0;
                    _particles[i] = p;
                    continue;
                }
                
                // 主体和休眠粒子使用主控制器
                if (p.Type == ParticleType.MainBody || p.Type == ParticleType.Dormant)
                {
                    p.ControllerSlot = 0;
                    p.BlobId = 0;
                    _particles[i] = p;
                    continue;
                }
                
                // 自由态粒子（发射中）：保持发射时绑定的 ControllerId，不重新分组
                if (p.FreeFrames > 0)
                {
                    // 不修改 ControllerId，保持发射时的绑定
                    // 但需要更新控制器中心跟随粒子移动（在下面的步骤4中处理）
                    continue;
                }
                
                // 分离粒子：根据 ClusterId 映射到 CCA 分组的控制器
                int clusterId = p.ClusterId;
                if (clusterId > 0 && clusterId <= compCount)
                {
                    int compIdx = clusterId - 1;
                    int newGroupId = _componentToGroup[compIdx];
                    int newBlobId = GetBlobIdForComponent(compIdx);
                    p.ControllerSlot = (newGroupId == 0) ? p.ControllerSlot : newGroupId;
                    p.BlobId = (newGroupId == 0) ? p.BlobId : newBlobId;

                    p.FramesOutsideMain++;
                    _particles[i] = p;
                }
                else
                {
                    // 没有有效的 ClusterId：不强制回归主体，优先保留原控制器，避免被主体“吸回”。
                    // 如果没有原控制器，则回退到 0，但仍保持 Separated/Emitted 类型，等待 Merge/Recall。
                    int fallbackControllerSlot = p.ControllerSlot > 0 ? p.ControllerSlot : 0;
                    p.ControllerSlot = fallbackControllerSlot;
                    p.BlobId = p.BlobId > 0 ? p.BlobId : 0;
                    _particles[i] = p;
                }
            }

            PerformanceProfiler.End("Control_MapParticles_PassB");

            // === 4. 更新自由飞行粒子和保护期粒子的控制器中心 ===
            // 收集每个控制器的自由飞行/保护期粒子质心
            PerformanceProfiler.Begin("Control_FreeParticleCenters");
            int controllerCountForTemp = _controllerBuffer.Length;
            if (controllerCountForTemp <= 0)
                controllerCountForTemp = 1;
            if (!_freeParticleCentroidTemp.IsCreated || _freeParticleCentroidTemp.Length < controllerCountForTemp)
            {
                if (_freeParticleCentroidTemp.IsCreated) _freeParticleCentroidTemp.Dispose();
                _freeParticleCentroidTemp = new NativeArray<float3>(math.max(8, controllerCountForTemp), Allocator.Persistent);
            }
            if (!_freeParticleCountTemp.IsCreated || _freeParticleCountTemp.Length < controllerCountForTemp)
            {
                if (_freeParticleCountTemp.IsCreated) _freeParticleCountTemp.Dispose();
                _freeParticleCountTemp = new NativeArray<int>(math.max(8, controllerCountForTemp), Allocator.Persistent);
            }

            var freeParticleCentroid = _freeParticleCentroidTemp.GetSubArray(0, controllerCountForTemp);
            var freeParticleCount = _freeParticleCountTemp.GetSubArray(0, controllerCountForTemp);
            for (int c = 0; c < controllerCountForTemp; c++)
            {
                freeParticleCentroid[c] = float3.zero;
                freeParticleCount[c] = 0;
            }

            if (_controllerFreeFramesCounts == null || _controllerFreeFramesCounts.Length < _controllerBuffer.Length)
                _controllerFreeFramesCounts = new int[_controllerBuffer.Length];
            else
                System.Array.Clear(_controllerFreeFramesCounts, 0, _controllerBuffer.Length);

            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                if (p.Type == ParticleType.FadingOut)
                    continue;
                // 自由飞行粒子 或 保护期内的分离粒子（ControllerId > 0 且 FramesOutsideMain < 60）
                bool isFreeFlying = p.FreeFrames > 0;
                bool isInProtection = p.Type == ParticleType.Separated && p.ControllerSlot > 0 && p.FramesOutsideMain < 60;
                if ((isFreeFlying || isInProtection) && p.ControllerSlot > 0 && p.ControllerSlot < _controllerBuffer.Length)
                {
                    freeParticleCentroid[p.ControllerSlot] += p.Position;
                    freeParticleCount[p.ControllerSlot]++;

                    if (isFreeFlying)
                        _controllerFreeFramesCounts[p.ControllerSlot]++;
                }
            }
            // 更新控制器中心跟随自由飞行/保护期粒子
            for (int c = 1; c < _controllerBuffer.Length; c++)
            {
                if (freeParticleCount[c] > 0)
                {
                    var ctrl = _controllerBuffer[c];
                    float3 newCenter = freeParticleCentroid[c] / freeParticleCount[c];

                    float3 lerpedCenter = math.lerp(ctrl.Center, newCenter, 0.3f); // 平滑过渡
                    ctrl.Center = lerpedCenter;
                    ctrl.IsValid = true; // 确保控制器保持有效
                    ctrl.ParticleCount = freeParticleCount[c]; // 临时设置粒子数，防止被标记为无效
                    _controllerBuffer[c] = ctrl;
                }
            }

            PerformanceProfiler.End("Control_FreeParticleCenters");
            
            // === 5. 统计每个控制器的实际粒子数 ===
            PerformanceProfiler.Begin("Control_ControllerCounts");
            EnsureControllerMainBodyCache(_controllerBuffer.Length);

            PerformanceProfiler.Begin("Control_ControllerCounts_Clear");
            for (int c = 0; c < _controllerBuffer.Length; c++)
            {
                _controllerMainBodyCentroid[c] = float3.zero;
                _controllerMainBodyCount[c] = 0;
                _controllerCentroidAll[c] = float3.zero;
                _controllerCountAll[c] = 0;
            }

            PerformanceProfiler.End("Control_ControllerCounts_Clear");

            PerformanceProfiler.Begin("Control_ControllerCounts_ScanParticles");
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                bool isFreeFlying = p.FreeFrames > 0;
                bool isInProtection = p.Type == ParticleType.Separated && p.ControllerSlot > 0 && p.FramesOutsideMain < 60;
                if (isFreeFlying || isInProtection)
                    continue;
                if (p.ControllerSlot >= 0 && p.ControllerSlot < _controllerBuffer.Length)
                {
                    var ctrl = _controllerBuffer[p.ControllerSlot];
                    ctrl.ParticleCount++;
                    _controllerBuffer[p.ControllerSlot] = ctrl;

                    // 统计所有粒子的质心（用于分离体眼睛中心）
                    _controllerCentroidAll[p.ControllerSlot] += p.Position;
                    _controllerCountAll[p.ControllerSlot]++;

                    if (p.Type == ParticleType.MainBody)
                    {
                        _controllerMainBodyCentroid[p.ControllerSlot] += p.Position;
                        _controllerMainBodyCount[p.ControllerSlot]++;
                    }
                }
            }

            PerformanceProfiler.End("Control_ControllerCounts_ScanParticles");

            PerformanceProfiler.End("Control_ControllerCounts");

            // 召回停止条件：只在5秒超时时自动停止（可以随时重新按按钮启动）
            PerformanceProfiler.Begin("Control_RecallTimeout");
            if (_connect && Time.time - _connectStartTime > 5f)
            {
                _connect = false;
            }

            PerformanceProfiler.End("Control_RecallTimeout");
            
            PerformanceProfiler.Begin("Control_AutoSeparate");
            AutoSeparateDistantParticles(mainCenter, mainRadius);
            PerformanceProfiler.End("Control_AutoSeparate");
            
            PerformanceProfiler.Begin("Control_MergeContact");
            MergeContactingParticles(mainCenter, mainRadius * mainOverlapThreshold);
            PerformanceProfiler.End("Control_MergeContact");
            
            PerformanceProfiler.Begin("Control_UpdateInstances");
            UpdateInstances();
            PerformanceProfiler.End("Control_UpdateInstances");

            PerformanceProfiler.Begin("Control_RearrangeInstances");
            bool shouldRearrange = _lastRearrangeControllerCount != _controllerBuffer.Length ||
                                   (Time.frameCount - _lastRearrangeFrame) >= rearrangeInstancesIntervalFrames;
            if (shouldRearrange)
            {
                RearrangeInstances();
                _lastRearrangeFrame = Time.frameCount;
                _lastRearrangeControllerCount = _controllerBuffer.Length;
            }
            PerformanceProfiler.End("Control_RearrangeInstances");

            PerformanceProfiler.Begin("Control_FadeOutSeparated");
            FadeOutSmallSeparatedControllers();
            PerformanceProfiler.End("Control_FadeOutSeparated");
            
            // 距离保护：丢弃离主体太远的粒子（场景水珠除外）
            if (enableDistanceCulling)
            {
                PerformanceProfiler.Begin("Control_DistanceCulling");
                CullDistantParticles(mainCenter);
                PerformanceProfiler.End("Control_DistanceCulling");
            }
        }
        
        /// <summary>
        /// 距离保护 - 将离主体过远的分离粒子休眠，防止性能问题
        /// 只检查分离粒子，主体粒子不检查
        /// 场景水珠（SourceId >= 0）不受此限制
        /// </summary>
        private void CullDistantParticles(float3 mainCenter)
        {
            // 秒间隔检查，因为很难出现所以不用太频繁
            float time = Time.time;
            if (time - _lastCullingTime < distanceCullingInterval)
                return;
            _lastCullingTime = time;
            
            int culledCount = 0;
            float maxDist2 = maxParticleDistance * maxParticleDistance;
            int count = math.min(activeParticles, _particles.Length);
            
            for (int i = count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                
                // 只处理分离粒子，主体粒子跑飞的可能性低
                if (p.Type == ParticleType.MainBody || p.Type == ParticleType.Dormant || p.Type == ParticleType.FadingOut) continue;
                
                // 跳过场景水珠（SourceId >= 0 表示来自 DropWater）
                if (p.SourceId >= 0) 
                {
                    continue;
                }
                
                // 统一世界坐标：直接使用 Position
                float3 worldPos = p.Position;
                float dist2 = math.lengthsq(worldPos - mainCenter);
                if (dist2 > maxDist2)
                {
                    // 休眠该粒子
                    ParticleStateManager.SetDormant(ref p);
                    _particles[i] = p;
                    
                    // 与最后一个活跃粒子交换，保持活跃粒子连续
                    if (i < activeParticles - 1)
                    {
                        // 使用ParticleStateManager安全交换粒子
                        ParticleStateManager.SwapParticles(ref _particles, i, activeParticles - 1);
                        // 同时交换速度
                        var tempVel = _velocityBuffer[i];
                        _velocityBuffer[i] = _velocityBuffer[activeParticles - 1];
                        _velocityBuffer[activeParticles - 1] = tempVel;
                    }
                    activeParticles--;
                    culledCount++;
                }
            }
            
            // 只要有粒子被休眠就立即警告（这是bug，必须报告）
        }
        
        /// <summary>
        /// 接触融合 - 检测分离粒子是否进入融合范围，如果是则融合回主体
        /// 使用 mainRadius * mainOverlapThreshold 作为融合半径，提供滞后效应
        /// 分离时使用 mainRadius，融合时使用更小的半径，避免边界抖动
        /// 场景水珠也参与接触融合：当接触到主体粒子时自动被吸收
        /// </summary>
        private void MergeContactingParticles(float3 mainCenter, float mergeRadius)
        {
            int mergedCount = 0;
            int dropletMergedCount = 0;

            int count = math.min(activeParticles, _particles.Length);
            float mergeRadius2 = mergeRadius * mergeRadius;
            // 接触距离使用 2 倍粒子间距，因为实际粒子分布可能稀疏
            float contactDist2 = PBF_Utils.h2 * 4; // (2h)^2 = 4h^2

            float contactDist = math.sqrt(contactDist2);
            float invContactDist = contactDist > 0f ? (1f / contactDist) : 1f;

            float gateMargin = math.sqrt(contactDist2) * 2f;

            int dropletActiveCount = _dropletSubsystem.ActiveCount;
            bool checkDropletPartition = false;
            bool hasCandidates = false;
            float3 absorbMin = float3.zero;
            float3 absorbMax = float3.zero;
            PerformanceProfiler.Begin("MergeContact_Broadphase");
            if (dropletActiveCount > 0 && _dropletSubsystem.TryGetActiveBounds(out float3 dropletMin, out float3 dropletMax))
            {
                absorbMin = mainCenter - new float3(mergeRadius + gateMargin);
                absorbMax = mainCenter + new float3(mergeRadius + gateMargin);
                if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 1)
                {
                    for (int c = 1; c < _controllerBuffer.Length; c++)
                    {
                        var ctrl = _controllerBuffer[c];
                        if (!ctrl.IsValid) continue;
                        float r = ctrl.Radius + gateMargin;
                        absorbMin = math.min(absorbMin, ctrl.Center - new float3(r));
                        absorbMax = math.max(absorbMax, ctrl.Center + new float3(r));
                    }
                }

                float3 dMin = dropletMin - new float3(gateMargin);
                float3 dMax = dropletMax + new float3(gateMargin);
                checkDropletPartition =
                    !(dMax.x < absorbMin.x || dMin.x > absorbMax.x ||
                      dMax.y < absorbMin.y || dMin.y > absorbMax.y ||
                      dMax.z < absorbMin.z || dMin.z > absorbMax.z);

                if (checkDropletPartition)
                    hasCandidates = true;
                else
                    dropletActiveCount = 0;
            }
            else
            {
                dropletActiveCount = 0;
            }
            if (!hasCandidates)
            {
                float broadphaseR = mergeRadius + gateMargin;
                float broadphaseR2 = broadphaseR * broadphaseR;
                for (int i = 0; i < count; i++)
                {
                    var p = _particles[i];
                    if (p.Type == ParticleType.MainBody || p.Type == ParticleType.Dormant || p.Type == ParticleType.FadingOut)
                        continue;
                    if (p.FreeFrames > 0)
                        continue;

                    bool isSceneDroplet = p.SourceId >= 0;
                    if (!isSceneDroplet && p.ControllerSlot <= 0)
                    {
                        float3 pos = p.Position;
                        float distToMain2 = math.lengthsq(pos - mainCenter);
                        if (distToMain2 > broadphaseR2)
                            continue;
                    }

                    hasCandidates = true;
                    break;
                }
            }

            PerformanceProfiler.End("MergeContact_Broadphase");

            if (!hasCandidates)
            {
                return;
            }
            
            // ========== 性能优化：为主体粒子构建临时空间哈希 ==========
            // 将 O(n×m) 暴力遍历优化为 O(n+m)
            // 所有粒子统一使用世界坐标
            PerformanceProfiler.Begin("MergeContact_BuildMainBodyLut");
            int desiredMergeLutCapacity = math.max(256, count * 2);
            if (_mergeMainBodyLut.Capacity < desiredMergeLutCapacity)
                _mergeMainBodyLut.Capacity = desiredMergeLutCapacity;
            _mergeMainBodyLut.Clear();
            var mainBodyLut = _mergeMainBodyLut;

            if (_mergeAbsorbLut.Capacity < desiredMergeLutCapacity)
                _mergeAbsorbLut.Capacity = desiredMergeLutCapacity;
            _mergeAbsorbLut.Clear();
            var absorbLut = _mergeAbsorbLut;
            int mainBodyCount = 0;
            int absorbCount = 0;

            int3 dbgCoordMin = new int3(int.MaxValue, int.MaxValue, int.MaxValue);
            int3 dbgCoordMax = new int3(int.MinValue, int.MinValue, int.MinValue);

            int3 dbgMainBodyCoordMin = new int3(int.MaxValue, int.MaxValue, int.MaxValue);
            int3 dbgMainBodyCoordMax = new int3(int.MinValue, int.MinValue, int.MinValue);

            int3 dbgAbsorbCoordMin = new int3(int.MaxValue, int.MaxValue, int.MaxValue);
            int3 dbgAbsorbCoordMax = new int3(int.MinValue, int.MinValue, int.MinValue);

            float3 dbgMainBodyMin = new float3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            float3 dbgMainBodyMax = new float3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            int3 GetMergeCoord(float3 p) => (int3)math.floor(p * invContactDist);
            
            for (int i = 0; i < count; i++)
            {
                if (_particles[i].Type != ParticleType.MainBody) continue;
                float3 pos = _particles[i].Position;

                dbgMainBodyMin = math.min(dbgMainBodyMin, pos);
                dbgMainBodyMax = math.max(dbgMainBodyMax, pos);

                int3 coord = GetMergeCoord(pos);
                dbgCoordMin = math.min(dbgCoordMin, coord);
                dbgCoordMax = math.max(dbgCoordMax, coord);

                dbgMainBodyCoordMin = math.min(dbgMainBodyCoordMin, coord);
                dbgMainBodyCoordMax = math.max(dbgMainBodyCoordMax, coord);
                int key = PBF_Utils.GetKey(coord);
                mainBodyLut.Add(key, i);
                mainBodyCount++;
            }
            PerformanceProfiler.End("MergeContact_BuildMainBodyLut");

            // 添加可吸收的分离粒子（独立控制器）
            PerformanceProfiler.Begin("MergeContact_BuildAbsorbLut");
            for (int i = 0; i < count; i++)
            {
                var p = _particles[i];
                if (p.Type != ParticleType.Separated || p.ControllerSlot <= 0) continue;
                float3 pos = p.Position;
                int3 coord = GetMergeCoord(pos);
                dbgCoordMin = math.min(dbgCoordMin, coord);
                dbgCoordMax = math.max(dbgCoordMax, coord);

                dbgAbsorbCoordMin = math.min(dbgAbsorbCoordMin, coord);
                dbgAbsorbCoordMax = math.max(dbgAbsorbCoordMax, coord);
                int key = PBF_Utils.GetKey(coord);
                absorbLut.Add(key, i);
                absorbCount++;
            }
            PerformanceProfiler.End("MergeContact_BuildAbsorbLut");


            _absorbedSourceIds.Clear();
            if (allSources != null && allSources.Count > 0)
            {
                if (_absorbedFromSourceCounts == null || _absorbedFromSourceCounts.Length < allSources.Count)
                    System.Array.Resize(ref _absorbedFromSourceCounts, allSources.Count);
            }

            int sourceCount = allSources != null ? allSources.Count : 0;
            bool enableSourceGate = false;
            if (enableSourceGate)
            {
                if (_sourceMayContact == null || _sourceMayContact.Length < sourceCount)
                    _sourceMayContact = new bool[sourceCount];

                for (int s = 0; s < sourceCount; s++)
                {
                    _sourceMayContact[s] = false;  // 默认不检测

                    var src = allSources[s];
                    if (src == null || src.State == DropWater.DropletSourceState.Dormant)
                        continue;

                    // Consumed 状态的源：水珠已经在场景中自由移动，跳过门控直接检测
                    if (src.State == DropWater.DropletSourceState.Consumed)
                    {
                        _sourceMayContact[s] = true;
                        continue;
                    }

                    // 直接从源获取位置，不依赖 _sourceControllers（水珠是独立系统）
                    float3 srcCenter = (float3)src.transform.position * WorldToSimScale;
                    float srcRadius = math.max(2f, src.AdaptiveRadius * WorldToSimScale);
                    
                    // 门控：水珠源中心到主体中心的距离 < 主体半径 + 水珠半径 + 接触距离 + 余量
                    float gateRadius = mergeRadius + srcRadius + contactDist * 2;
                    float srcDistToMain = math.length(srcCenter - mainCenter);
                    
                    bool mayContact = srcDistToMain <= gateRadius;

                    // 添加分离控制器门控：分离团距离水珠足够近时也可以视为可接触
                    if (!mayContact && _controllerBuffer.IsCreated && _controllerBuffer.Length > 1)
                    {
                        for (int c = 1; c < _controllerBuffer.Length; c++)
                        {
                            var ctrl = _controllerBuffer[c];
                            if (!ctrl.IsValid) continue;
                            float ctrlGateRadius = ctrl.Radius + srcRadius + contactDist * 2;
                            float dist = math.length(srcCenter - ctrl.Center);
                            if (dist <= ctrlGateRadius)
                            {
                                mayContact = true;
                                break;
                            }
                        }
                    }
                    
                    if (mayContact)
                        _sourceMayContact[s] = true;
                }
            }

            PerformanceProfiler.Begin("MergeContact_ScanMainRange");
            long mergeScanT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int dbgTotalIters = 0;
            int dbgSkipTypeMainDormantFade = 0;
            int dbgSkipFreeFrames = 0;
            int dbgSkipDistToMain = 0;
            int dbgSkipNoController = 0;
            int dbgSkipMainBodyAabb = 0;
            int dbgSceneDropletCount = 0;
            int dbgNormalSeparatedCount = 0;
            int dbgNeighborKeys = 0;
            int dbgMainFirstHits = 0;
            int dbgMainVisited = 0;
            int dbgMainMaxChain = 0;
            int dbgAbsorbFirstHits = 0;
            int dbgAbsorbVisited = 0;
            int dbgAbsorbMaxChain = 0;
            int dbgMerged = 0;
            absorbMin = mainCenter - new float3(mergeRadius + gateMargin);
            absorbMax = mainCenter + new float3(mergeRadius + gateMargin);
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 1)
            {
                for (int c = 1; c < _controllerBuffer.Length; c++)
                {
                    var ctrl = _controllerBuffer[c];
                    if (!ctrl.IsValid) continue;
                    float r = ctrl.Radius + gateMargin;
                    absorbMin = math.min(absorbMin, ctrl.Center - new float3(r));
                    absorbMax = math.max(absorbMax, ctrl.Center + new float3(r));
                }
            }
            for (int i = 0; i < count; i++)
            {
                dbgTotalIters++;
                // 只处理分离粒子
                if (_particles[i].Type == ParticleType.MainBody || _particles[i].Type == ParticleType.Dormant || _particles[i].Type == ParticleType.FadingOut)
                {
                    dbgSkipTypeMainDormantFade++;
                    continue;
                }
                
                // 跳过还在自由飞行的粒子（FreeFrames > 0）
                if (_particles[i].FreeFrames > 0) 
                {
                    dbgSkipFreeFrames++;
                    continue;
                }
                
                bool isSceneDroplet = _particles[i].SourceId >= 0;
                if (isSceneDroplet) dbgSceneDropletCount++; else dbgNormalSeparatedCount++;
                
                // 分离粒子的 Position 是世界坐标
                float3 pos = _particles[i].Position;
                float distToMain2 = math.lengthsq(pos - mainCenter);
                
                // 场景水珠的特殊处理：接触主体时可以融合
                if (!isSceneDroplet)
                {
                    // 普通分离粒子的合并检测（接触融合）
                    if (distToMain2 > mergeRadius2)
                    {
                        dbgSkipDistToMain++;
                        continue;
                    }
                    if (_particles[i].ControllerSlot <= 0)
                    {
                        dbgSkipNoController++;
                        continue;
                    }

                    // 方案B：MainBody AABB(+contactDist) early-out。
                    // 若点在 AABB 外，则不可能在 contactDist 内接触任意主体粒子。
                    // 注意：场景水珠还可能接触可吸收分离团，因此不使用该门控。
                    if (mainBodyCount > 0)
                    {
                        if (pos.x < (dbgMainBodyMin.x - contactDist) || pos.x > (dbgMainBodyMax.x + contactDist) ||
                            pos.y < (dbgMainBodyMin.y - contactDist) || pos.y > (dbgMainBodyMax.y + contactDist) ||
                            pos.z < (dbgMainBodyMin.z - contactDist) || pos.z > (dbgMainBodyMax.z + contactDist))
                        {
                            dbgSkipMainBodyAabb++;
                            continue;
                        }
                    }
                }
                
                // 使用空间哈希加速接触检测（O(1) 邻域查询）
                bool shouldMerge = false;
                int3 coord = GetMergeCoord(pos);
                
                // 遍历 3x3x3 邻域
                int contactIdx = -1;
                float actualContactDist = 0;
                const int mergeNeighborRange = 1;
                for (int dz = -mergeNeighborRange; dz <= mergeNeighborRange && !shouldMerge; ++dz)
                for (int dy = -mergeNeighborRange; dy <= mergeNeighborRange && !shouldMerge; ++dy)
                for (int dx = -mergeNeighborRange; dx <= mergeNeighborRange && !shouldMerge; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + new int3(dx, dy, dz));
                    dbgNeighborKeys++;
                    
                    // 场景水珠查找“可吸收体”集合（主体+合法分离团），普通分离粒子只查主体
                if (isSceneDroplet)
                {
                    if (mainBodyLut.TryGetFirstValue(key, out int j, out var it))
                    {
                        dbgMainFirstHits++;
                        int chain = 0;
                        do
                        {
                            chain++;
                            float r2 = math.lengthsq(pos - _particles[j].Position);
                            if (r2 <= contactDist2)
                            {
                                shouldMerge = true;
                                contactIdx = j;
                                actualContactDist = math.sqrt(r2);
                                break;
                            }
                        } while (mainBodyLut.TryGetNextValue(out j, ref it));

                        dbgMainVisited += chain;
                        if (chain > dbgMainMaxChain) dbgMainMaxChain = chain;
                    }

                    if (!shouldMerge)
                    {
                        if (absorbLut.TryGetFirstValue(key, out int j2, out var it2))
                        {
                            dbgAbsorbFirstHits++;
                            int chain2 = 0;
                            do
                            {
                                chain2++;
                                float r2 = math.lengthsq(pos - _particles[j2].Position);
                                if (r2 <= contactDist2)
                                {
                                    shouldMerge = true;
                                    contactIdx = j2;
                                    actualContactDist = math.sqrt(r2);
                                    break;
                                }
                            } while (absorbLut.TryGetNextValue(out j2, ref it2));

                            dbgAbsorbVisited += chain2;
                            if (chain2 > dbgAbsorbMaxChain) dbgAbsorbMaxChain = chain2;
                        }
                    }
                }
                else
                {
                    if (mainBodyLut.TryGetFirstValue(key, out int j, out var it))
                    {
                        dbgMainFirstHits++;
                        int chain = 0;
                        do
                        {
                            chain++;
                            float r2 = math.lengthsq(pos - _particles[j].Position);
                            if (r2 <= contactDist2)
                            {
                                shouldMerge = true;
                                contactIdx = j;
                                actualContactDist = math.sqrt(r2);
                                break;
                            }
                        } while (mainBodyLut.TryGetNextValue(out j, ref it));

                        dbgMainVisited += chain;
                        if (chain > dbgMainMaxChain) dbgMainMaxChain = chain;
                    }
                }
                }
                if (!shouldMerge) 
                {
                    continue;
                }
                
                var p = _particles[i];
                int sourceId = p.SourceId;
                
                if (isSceneDroplet && contactIdx >= 0)
                {
                    var target = _particles[contactIdx];
                    if (target.Type == ParticleType.Separated && target.ControllerSlot > 0)
                    {
                        // 直接并入分离团：继承控制器/稳定ID/Cluster等信息
                        p.Type = ParticleType.Separated;
                        p.ControllerSlot = target.ControllerSlot;
                        p.BlobId = target.BlobId;
                        p.ClusterId = target.ClusterId;
                        p.FramesOutsideMain = target.FramesOutsideMain;
                        p.SourceId = -1;
                        p.FreeFrames = 0;
                    }
                    else
                    {
                        ParticleStateManager.ConvertToMainBody(ref p, mainCenter);
                        p.ClusterId = 0;
                        p.FramesOutsideMain = 0;
                    }
                }
                else
                {
                    // 使用ParticleStateManager合并粒子
                    ParticleStateManager.ConvertToMainBody(ref p, mainCenter);
                    p.ClusterId = 0;
                    p.FramesOutsideMain = 0;
                }
                _particles[i] = p;
                
                // 融合时抑制爆炸：去掉水平速度和向上分量，保留向下速度避免“下落变慢”
                if (_velocityBuffer.IsCreated && i < _velocityBuffer.Length)
                {
                    var v = _velocityBuffer[i];
                    v.x = 0f;
                    v.z = 0f;
                    if (v.y > 0f)
                        v.y = 0f;
                    _velocityBuffer[i] = v;
                }
                
                
                if (isSceneDroplet)
                {
                    dropletMergedCount++;
                    if (_absorbedFromSourceCounts != null && sourceId >= 0 && sourceId < _absorbedFromSourceCounts.Length)
                    {
                        if (_absorbedFromSourceCounts[sourceId] == 0)
                            _absorbedSourceIds.Add(sourceId);
                        _absorbedFromSourceCounts[sourceId]++;
                    }
                }
                else
                {
                    mergedCount++;
                }

                dbgMerged++;
            }

            PerformanceProfiler.End("MergeContact_ScanMainRange");

            double invFreqMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            float mergeScanMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - mergeScanT0) * invFreqMs);
            bool forceMergeScanLog = mergeScanMs > 10f;
            if (PerformanceProfiler.VerboseMode &&
                (forceMergeScanLog || (mergeScanMs > 0.05f || dbgMainMaxChain > 64 || dbgAbsorbMaxChain > 64)) &&
                (forceMergeScanLog || (Time.frameCount - _lastMergeScanMainRangeLogFrame) > 30))
            {
                _lastMergeScanMainRangeLogFrame = Time.frameCount;
                int3 dbgCoordSpan = dbgCoordMax - dbgCoordMin;
                Debug.Log(
                    $"[MergeScanDbg] frame={Time.frameCount} ms={mergeScanMs:F2} count={count} iters={dbgTotalIters} merged={dbgMerged} " +
                    $"skipType={dbgSkipTypeMainDormantFade} skipFree={dbgSkipFreeFrames} skipDist={dbgSkipDistToMain} skipNoCtrl={dbgSkipNoController} skipAabb={dbgSkipMainBodyAabb} " +
                    $"sceneDroplet={dbgSceneDropletCount} normalSep={dbgNormalSeparatedCount} " +
                    $"keys={dbgNeighborKeys} mainFirstHits={dbgMainFirstHits} mainVisited={dbgMainVisited} mainMaxChain={dbgMainMaxChain} " +
                    $"absFirstHits={dbgAbsorbFirstHits} absVisited={dbgAbsorbVisited} absMaxChain={dbgAbsorbMaxChain} " +
                    $"coordMin={dbgCoordMin} coordMax={dbgCoordMax} coordSpan={dbgCoordSpan} " +
                    $"mergeRadius={mergeRadius:F2} contactDist={contactDist:F2} mainCenter={mainCenter} absorbMin={absorbMin} absorbMax={absorbMax}");
            }

            // ========== 处理水珠独立分区 [8192-16383] ==========
            // 复用与分离粒子相同的接触融合逻辑
            {
                PerformanceProfiler.Begin("MergeContact_ScanDropletPartition");
                long mergeDropletScanT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int dbgDropletProcessed = 0;
                int dbgDropletSkippedAabb = 0;
                int dbgDropletSkippedCoordGate = 0;
                int dbgDropletNeighborKeys = 0;
                int dbgDropletMainFirstHits = 0;
                int dbgDropletMainVisited = 0;
                int dbgDropletMainMaxChain = 0;
                int dbgDropletAbsFirstHits = 0;
                int dbgDropletAbsVisited = 0;
                int dbgDropletAbsMaxChain = 0;
                int dbgDropletMerged = 0;
                bool dbgDropletUsedFallback = false;
                if (checkDropletPartition && dropletActiveCount > 0)
                {
                    int dropletStart = DropletSubsystem.DROPLET_START;
                    int dropletEnd = dropletStart + dropletActiveCount - 1;
                    bool usedFallback = false;
                    
                    void ProcessDropletGlobalIndex(int i)
                    {
                        dbgDropletProcessed++;
                        if (_particles[i].Type != ParticleType.SceneDroplet)
                            return;
                        
                        var p = _particles[i];
                        if (p.FreeFrames > 0)
                            return;
                        
                        int sourceId = p.SourceId;
                        if (enableSourceGate && sourceId >= 0 && sourceId < sourceCount && !_sourceMayContact[sourceId])
                            return;
                        
                        float3 dropletPos = p.Position;
                        if (dropletPos.x < absorbMin.x || dropletPos.x > absorbMax.x ||
                            dropletPos.y < absorbMin.y || dropletPos.y > absorbMax.y ||
                            dropletPos.z < absorbMin.z || dropletPos.z > absorbMax.z)
                        {
                            dbgDropletSkippedAabb++;
                            return;
                        }
                        
                        bool shouldMerge = false;
                        int3 dropletCoord = GetMergeCoord(dropletPos);
                        int contactIdx = -1;

                        const int dropletMergeNeighborRange = 1;

                        // 方案B(扩展到水珠分区)：coord-space 门控。
                        // 如果 dropletCoord 不在主体/可吸收团块的 coord AABB(+neighborRange) 内，则完全不可能命中任何桶。
                        // 这在你观测到的 mainFirstHits=0 场景下，可以直接省掉大量空查找。
                        bool mayHitMain = mainBodyCount > 0;
                        bool mayHitAbsorb = absorbCount > 0;
                        if (mayHitMain)
                        {
                            if (dropletCoord.x < (dbgMainBodyCoordMin.x - dropletMergeNeighborRange) || dropletCoord.x > (dbgMainBodyCoordMax.x + dropletMergeNeighborRange) ||
                                dropletCoord.y < (dbgMainBodyCoordMin.y - dropletMergeNeighborRange) || dropletCoord.y > (dbgMainBodyCoordMax.y + dropletMergeNeighborRange) ||
                                dropletCoord.z < (dbgMainBodyCoordMin.z - dropletMergeNeighborRange) || dropletCoord.z > (dbgMainBodyCoordMax.z + dropletMergeNeighborRange))
                            {
                                mayHitMain = false;
                            }
                        }
                        if (mayHitAbsorb)
                        {
                            if (dropletCoord.x < (dbgAbsorbCoordMin.x - dropletMergeNeighborRange) || dropletCoord.x > (dbgAbsorbCoordMax.x + dropletMergeNeighborRange) ||
                                dropletCoord.y < (dbgAbsorbCoordMin.y - dropletMergeNeighborRange) || dropletCoord.y > (dbgAbsorbCoordMax.y + dropletMergeNeighborRange) ||
                                dropletCoord.z < (dbgAbsorbCoordMin.z - dropletMergeNeighborRange) || dropletCoord.z > (dbgAbsorbCoordMax.z + dropletMergeNeighborRange))
                            {
                                mayHitAbsorb = false;
                            }
                        }
                        if (!mayHitMain && !mayHitAbsorb)
                        {
                            dbgDropletSkippedCoordGate++;
                            return;
                        }
                        
                        for (int dz = -dropletMergeNeighborRange; dz <= dropletMergeNeighborRange && !shouldMerge; ++dz)
                        for (int dy = -dropletMergeNeighborRange; dy <= dropletMergeNeighborRange && !shouldMerge; ++dy)
                        for (int dx = -dropletMergeNeighborRange; dx <= dropletMergeNeighborRange && !shouldMerge; ++dx)
                        {
                            int key = PBF_Utils.GetKey(dropletCoord + new int3(dx, dy, dz));
                            dbgDropletNeighborKeys++;
                            if (mayHitMain && mainBodyLut.TryGetFirstValue(key, out int j, out var it))
                            {
                                dbgDropletMainFirstHits++;
                                int chain = 0;
                                do
                                {
                                    chain++;
                                    float r2 = math.lengthsq(dropletPos - _particles[j].Position);
                                    if (r2 <= contactDist2)
                                    {
                                        shouldMerge = true;
                                        contactIdx = j;
                                        break;
                                    }
                                } while (mainBodyLut.TryGetNextValue(out j, ref it));

                                dbgDropletMainVisited += chain;
                                if (chain > dbgDropletMainMaxChain) dbgDropletMainMaxChain = chain;
                            }
                            if (!shouldMerge)
                            {
                                if (mayHitAbsorb && absorbLut.TryGetFirstValue(key, out int j2, out var it2))
                                {
                                    dbgDropletAbsFirstHits++;
                                    int chain2 = 0;
                                    do
                                    {
                                        chain2++;
                                        float r2 = math.lengthsq(dropletPos - _particles[j2].Position);
                                        if (r2 <= contactDist2)
                                        {
                                            shouldMerge = true;
                                            contactIdx = j2;
                                            break;
                                        }
                                    } while (absorbLut.TryGetNextValue(out j2, ref it2));

                                    dbgDropletAbsVisited += chain2;
                                    if (chain2 > dbgDropletAbsMaxChain) dbgDropletAbsMaxChain = chain2;
                                }
                            }
                        }
                        
                        if (!shouldMerge)
                            return;
                        if (activeParticles >= DropletSubsystem.DROPLET_START)
                            return;
                        if (!_dropletSubsystem.MigrateToMainBody(i, out float3 migratedPos, out float3 migratedVel))
                            return;
                        
                        var target = contactIdx >= 0 ? _particles[contactIdx] : default;
                        int newMainIdx = activeParticles;
                        var newParticle = new Particle
                        {
                            Position = migratedPos,
                            Type = ParticleType.MainBody,
                            ControllerSlot = 0,
                            BlobId = 0,
                            FreeFrames = 0,
                            SourceId = -1,
                            ClusterId = 0,
                            FramesOutsideMain = 0
                        };
                        if (contactIdx >= 0 && target.Type == ParticleType.Separated && target.ControllerSlot > 0)
                        {
                            newParticle.Type = ParticleType.Separated;
                            newParticle.ControllerSlot = target.ControllerSlot;
                            newParticle.BlobId = target.BlobId;
                            newParticle.ClusterId = target.ClusterId;
                            newParticle.FramesOutsideMain = target.FramesOutsideMain;
                        }
                        
                        _particles[newMainIdx] = newParticle;
                        _velocityBuffer[newMainIdx] = float3.zero;
                        activeParticles++;
                        
                        dropletMergedCount++;
                        dbgDropletMerged++;
                        if (_absorbedFromSourceCounts != null && sourceId >= 0 && sourceId < _absorbedFromSourceCounts.Length)
                        {
                            if (_absorbedFromSourceCounts[sourceId] == 0)
                                _absorbedSourceIds.Add(sourceId);
                            _absorbedFromSourceCounts[sourceId]++;
                        }
                    }
                    
                    bool canUsePerSource = sourceCount > 0;
                    if (canUsePerSource)
                    {
                        for (int sid = 0; sid < sourceCount; sid++)
                        {
                            if (!_dropletSubsystem.TryGetSourceBounds(sid, out float3 srcMin, out float3 srcMax) ||
                                !_dropletSubsystem.TryGetSourceIndexRange(sid, out int baseOffset, out int srcCount))
                            {
                                canUsePerSource = false;
                                break;
                            }
                            
                            if (srcMax.x < absorbMin.x || srcMin.x > absorbMax.x ||
                                srcMax.y < absorbMin.y || srcMin.y > absorbMax.y ||
                                srcMax.z < absorbMin.z || srcMin.z > absorbMax.z)
                            {
                                continue;
                            }
                            
                            for (int k = 0; k < srcCount; k++)
                            {
                                int localIndex = _dropletSubsystem.GetSourceParticleLocalIndex(baseOffset + k);
                                int i = dropletStart + localIndex;
                                if (i < dropletStart || i > dropletEnd)
                                    continue;
                                ProcessDropletGlobalIndex(i);
                            }
                        }
                    }

                    if (!canUsePerSource)
                    {
                        usedFallback = true;
                    }
                    
                    if (usedFallback)
                    {
                        dbgDropletUsedFallback = true;
                        for (int i = dropletStart; i <= dropletEnd; i++)
                        {
                            ProcessDropletGlobalIndex(i);
                        }
                    }
                }
                PerformanceProfiler.End("MergeContact_ScanDropletPartition");

                double invFreqMs2 = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                float mergeDropletScanMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - mergeDropletScanT0) * invFreqMs2);
                bool forceMergeDropletScanLog = mergeDropletScanMs > 10f;
                if (PerformanceProfiler.VerboseMode &&
                    (forceMergeDropletScanLog || mergeDropletScanMs > 0.05f) &&
                    (forceMergeDropletScanLog || (Time.frameCount - _lastMergeScanDropletPartitionLogFrame) > 30))
                {
                    _lastMergeScanDropletPartitionLogFrame = Time.frameCount;
                    Debug.Log(
                        $"[MergeDropletScanDbg] frame={Time.frameCount} ms={mergeDropletScanMs:F2} processed={dbgDropletProcessed} skippedAabb={dbgDropletSkippedAabb} skippedCoord={dbgDropletSkippedCoordGate} merged={dbgDropletMerged} usedFallback={dbgDropletUsedFallback} " +
                        $"keys={dbgDropletNeighborKeys} mainFirstHits={dbgDropletMainFirstHits} mainVisited={dbgDropletMainVisited} mainMaxChain={dbgDropletMainMaxChain} " +
                        $"absFirstHits={dbgDropletAbsFirstHits} absVisited={dbgDropletAbsVisited} absMaxChain={dbgDropletAbsMaxChain} " +
                        $"dropletActiveCount={dropletActiveCount} sourceCount={sourceCount} absorbMin={absorbMin} absorbMax={absorbMax}");
                }
                
            }
            
            // 更新场景水珠源的剩余数量
            PerformanceProfiler.Begin("MergeContact_UpdateSources");
            if (_absorbedFromSourceCounts != null && _absorbedSourceIds.Count > 0 && allSources != null)
            {
                for (int idx = 0; idx < _absorbedSourceIds.Count; idx++)
                {
                    int sourceId = _absorbedSourceIds[idx];
                    int absorbed = _absorbedFromSourceCounts[sourceId];
                    _absorbedFromSourceCounts[sourceId] = 0;
                    if (sourceId >= 0 && sourceId < allSources.Count && allSources[sourceId] != null)
                    {
                        allSources[sourceId].AbsorbParticles(absorbed);
                    }
                }
            }
            PerformanceProfiler.End("MergeContact_UpdateSources");

            if (mergedCount > 0 || dropletMergedCount > 0)
            {
                TryPlayMergeSfx();
            }
            
        }

        private void TryPlayMergeSfx()
        {
            if (mergeSfx == null)
                return;

            if (_lastMergeSfxFixedSerial == _fixedUpdateSerial)
                return;

            float now = Time.unscaledTime;
            if (mergeSfxCooldownSeconds > 0f && (now - _lastMergeSfxUnscaledTime) < mergeSfxCooldownSeconds)
                return;

            _lastMergeSfxUnscaledTime = now;
            _lastMergeSfxFixedSerial = _fixedUpdateSerial;
            MMSfxEvent.Trigger(mergeSfx, null, mergeSfxVolume, 1f);
        }

        public void PlayMergeSfx()
        {
            TryPlayMergeSfx();
        }

        public void PlayEmitSfx(bool isFirstEmitInSequence)
        {
            AudioClip clip = isFirstEmitInSequence ? emitFirstSfx : emitRepeatSfx;
            if (clip == null)
                clip = isFirstEmitInSequence ? emitRepeatSfx : emitFirstSfx;
            if (clip == null)
                return;

            MMSfxEvent.Trigger(clip, null, 1f, 1f);
        }
        
        /// <summary>
        /// 验证所有粒子状态的合法性
        /// </summary>
        private void ValidateAllParticles()
        {
            int invalidCount = 0;
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                if (!ParticleStateManager.ValidateParticle(ref p, i))
                {
                    invalidCount++;
                    _particles[i] = p; // 保存修正后的粒子
                }
            }
            
            if (invalidCount > 0)
            {
                Debug.LogError($"[粒子验证] 发现并修正了 {invalidCount} 个非法粒子状态");
            }
        }
        
        /// <summary>
        /// 自动分离 - 使用CCA连通性判断：只有不在主连通组件的粒子才分离
        /// </summary>
        private void AutoSeparateDistantParticles(float3 mainCenter, float mainRadius)
        {
            // 如果禁用自动分离，直接返回
            if (!enableAutoSeparate) return;
            
            int autoSeparatedCount = 0;
            float maxSeparatedDist = 0;
            int particlesOutsideCluster = 0;  // 调试：不在主组件的粒子数

            int count = math.min(activeParticles, _particles.Length);
            
            // ========== 第一步：统计每个 ClusterId 的重心和粒子数 ==========
            const int maxClusters = 256;

            for (int i = 0; i < maxClusters; i++)
            {
                _autoSeparateClusterCounts[i] = 0;
                _autoSeparateClusterCenters[i] = float3.zero;
            }
            
            for (int i = 0; i < count; i++)
            {
                var p = _particles[i];
                if (p.Type == ParticleType.MainBody && p.ClusterId > 0 && p.ClusterId < maxClusters)
                {
                    _autoSeparateClusterCounts[p.ClusterId]++;
                    _autoSeparateClusterCenters[p.ClusterId] += p.Position;
                }
            }
            
            // 计算每个 cluster 的重心
            for (int i = 1; i < maxClusters; i++)
            {
                if (_autoSeparateClusterCounts[i] > 0)
                {
                    _autoSeparateClusterCenters[i] /= _autoSeparateClusterCounts[i];
                }
            }
            
            // ========== 第二步：找出重心距离 mainCenter 最近的 ClusterId（主组件） ==========
            // 这样即使玩家控制的部分较小，也不会被错误分离
            int mainClusterId = 0;
            float minDistToMain = float.MaxValue;
            for (int c = 1; c < maxClusters; c++)
            {
                if (_autoSeparateClusterCounts[c] > 0)
                {
                    float dist = math.length(_autoSeparateClusterCenters[c] - mainCenter);
                    if (dist < minDistToMain)
                    {
                        minDistToMain = dist;
                        mainClusterId = c;
                    }
                }
            }
            
            // 调试：输出主组件信息
            // ========== 第三步：只有不在主组件且组件足够大的粒子才分离 ==========
            for (int i = 0; i < count; i++)
            {
                var p = _particles[i];
                
                // 只处理主体粒子
                if (p.Type != ParticleType.MainBody) continue;
                
                // 判断是否在主连通组件中
                bool inMainCluster = (p.ClusterId == mainClusterId && mainClusterId > 0);
                
                if (!inMainCluster && p.ClusterId > 0)
                {
                    // 检查边界：ClusterId 超出范围时跳过
                    if (p.ClusterId >= maxClusters) continue;
                    
                    // 检查组件大小：太小的组件不分离，直接保留在主体
                    int clusterSize = _autoSeparateClusterCounts[p.ClusterId];
                    if (clusterSize < minSeparateClusterSize)
                    {
                        // 组件太小，重置计数器，不累计分离
                        if (p.FramesOutsideMain > 0)
                        {
                            p.FramesOutsideMain = 0;
                            _particles[i] = p;
                        }
                        continue;
                    }
                    
                    // 组件足够大，累计分离计数
                    p.FramesOutsideMain++;
                    particlesOutsideCluster++;
                    
                    // 达到延迟帧数才真正分离
                    if (p.FramesOutsideMain >= separateDelayFrames)
                    {
                        float dist = math.length(p.Position - mainCenter);
                        // 自动分离不给 FreeFrames，这样可以立刻被控制/合并回来
                        // freeFrames 默认值是给主动发射用的
                        ParticleStateManager.ConvertToSeparated(ref p, mainCenter, 0, 0);
                        autoSeparatedCount++;
                        maxSeparatedDist = math.max(maxSeparatedDist, dist);
                    }
                    _particles[i] = p;
                }
                else
                {
                    // 在主组件中或 ClusterId 无效，重置计数器
                    if (p.FramesOutsideMain > 0)
                    {
                        p.FramesOutsideMain = 0;
                        _particles[i] = p;
                    }
                }
            }
        }

        /// <summary>
        /// 发射粒子 - 从主体朝向鼠标方向选择粒子并赋予速度
        /// </summary>
        public bool EmitParticles()
        {
            // SlimeVolume 必须存在
            if (_slimeVolume == null)
            {
                Debug.LogError("[Emit] SlimeVolume 未绑定，无法发射");
                return false;
            }
            
            // 检查SlimeVolume是否允许发射
            if (!_slimeVolume.CanEmit(emitBatchSize))
            {
                return false;
            }
            
            // 获取主控制器中心（ID=0的粒子所属的控制器）
            float3 center = float3.zero;
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
            {
                center = _controllerBuffer[0].Center;
            }
            else
            {
                return false;
            }
            
            // 计算鼠标方向
            float3 emitDirection = new float3(0, 0, 1);
            var mainCam = Camera.main;
            if (mainCam != null)
            {
                Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);
                Vector3 planePoint = trans != null ? trans.position : (Vector3)(center * SimToWorldScale);
                Plane groundPlane = new Plane(Vector3.up, planePoint);

                if (groundPlane.Raycast(ray, out float distance) && distance > 0f)
                {
                    Vector3 mouseWorldPos = ray.GetPoint(distance);
                    float3 toMouse = (float3)(mouseWorldPos * WorldToSimScale) - center;
                    toMouse.y = 0;
                    if (math.lengthsq(toMouse) > 1e-4f)
                    {
                        emitDirection = math.normalizesafe(toMouse, new float3(0, 0, 1));
                    }
                }
            }
            
            // === 新发射逻辑：选择粒子并传送到发射点 ===
            
            // 1. 计算实际粒子分布半径（使用第95百分位，排除异常值）
            var distList = new NativeList<float>(1024, Allocator.Temp);
            for (int i = 0; i < activeParticles; i++)
            {
                if (_particles[i].Type != ParticleType.MainBody) continue;
                float dist = math.length(_particles[i].Position - center);
                distList.Add(dist);
            }
            
            int mainParticleCount = distList.Length;
            if (mainParticleCount == 0)
            {
                distList.Dispose();
                return false;
            }
            
            // 排序并取第95百分位作为实际半径（排除异常值）
            distList.Sort();
            int percentileIndex = (int)(mainParticleCount * 0.95f);
            percentileIndex = math.clamp(percentileIndex, 0, mainParticleCount - 1);
            float actualRadius = distList[percentileIndex];
            distList.Dispose();
            
            // 硬性上限：actualRadius 不能超过控制器半径的2倍（防止异常粒子导致巨大半径）
            float controllerRadius = _controllerBuffer.IsCreated && _controllerBuffer.Length > 0 ? _controllerBuffer[0].Radius : 20f;
            actualRadius = math.min(actualRadius, controllerRadius * 2f);
            
            // 使用实际粒子半径，而不是控制器设定半径
            float mainRadius = actualRadius;
            
            
            // 2. 发射方向：主要向前（瞄准方向）+ 向上形成抛物线
            float pitchRad = math.radians(emitPitchDegrees);
            float3 finalEmitDir = math.normalizesafe(
                emitDirection * math.cos(pitchRad) + new float3(0f, 1f, 0f) * math.sin(pitchRad));
            
            // 3. 限制发射数量
            int maxEmit = Mathf.Min(emitBatchSize, _slimeVolume.GetMaxEmitAmount());
            int emitted = 0;
            
            
            // 4. 【不传送】选择朝向瞄准方向的粒子，只给速度
            // 【关键】只选择真正在主体附近的粒子
            // 使用控制器半径作为硬性上限，不使用可能被异常粒子污染的 actualRadius
            float maxSelectDist = math.min(actualRadius * 1.2f, controllerRadius * 1.5f);
            float maxSelectDistSq = maxSelectDist * maxSelectDist;
            float3 emitDirXZForSelect = emitDirection;
            emitDirXZForSelect.y = 0;
            float3 spawnCenter = center + emitDirection * (mainRadius * emitSpawnForwardRadiusFactor);
            spawnCenter.y = center.y + mainRadius * emitSpawnUpRadiusFactor;
            
            var candidateIndices = new System.Collections.Generic.List<int>(maxEmit * 3);
            var candidateMask = new NativeArray<byte>(activeParticles, Allocator.Temp);
            
            // 第一轮：优先选朝向正确且在上半部分的粒子
            for (int i = 0; i < activeParticles; i++)
            {
                if (_particles[i].Type != ParticleType.MainBody) continue;
                
                float3 toParticle = _particles[i].Position - center;
                float distSq = math.lengthsq(toParticle);
                
                // 【关键】排除远离主体的"假"MainBody粒子
                if (distSq > maxSelectDistSq) continue;
                
                // 只选朝向瞄准方向的粒子
                float3 particleDirXZ = toParticle;
                particleDirXZ.y = 0;
                
                float dot = math.dot(math.normalizesafe(particleDirXZ), math.normalizesafe(emitDirXZForSelect));
                
                // 只选朝向瞄准方向的粒子（dot > 0.3 表示在前方约70度扇形内）
                if (dot < emitAngleThreshold) continue;
                
                // 优先选择上半部分
                if (toParticle.y < 0) continue;

                if (candidateMask[i] == 0)
                {
                    candidateMask[i] = 1;
                    candidateIndices.Add(i);
                }
            }
            
            // 第二轮：放宽条件，允许下半部分
            if (candidateIndices.Count < maxEmit)
            {
                for (int i = 0; i < activeParticles; i++)
                {
                    if (_particles[i].Type != ParticleType.MainBody) continue;
                    
                    float3 toParticle = _particles[i].Position - center;
                    float distSq = math.lengthsq(toParticle);
                    
                    // 【关键】排除远离主体的粒子
                    if (distSq > maxSelectDistSq) continue;
                    
                    float3 particleDirXZ = toParticle;
                    particleDirXZ.y = 0;
                    
                    float dot = math.dot(math.normalizesafe(particleDirXZ), math.normalizesafe(emitDirXZForSelect));
                    
                    // 只选朝向瞄准方向的粒子（dot > 0.3）
                    if (dot < emitAngleThreshold) continue;

                    if (candidateMask[i] == 0)
                    {
                        candidateMask[i] = 1;
                        candidateIndices.Add(i);
                    }
                }
            }
            
            // 第三轮：如果还不够，进一步放宽方向条件（但仍要检查距离）
            if (candidateIndices.Count < maxEmit)
            {
                for (int i = 0; i < activeParticles; i++)
                {
                    if (_particles[i].Type != ParticleType.MainBody) continue;
                    
                    // 【关键】排除远离主体的粒子
                    float3 toParticle = _particles[i].Position - center;
                    float distSq = math.lengthsq(toParticle);
                    if (distSq > maxSelectDistSq) continue;
                    
                    if (candidateMask[i] == 0)
                    {
                        candidateMask[i] = 1;
                        candidateIndices.Add(i);
                    }
                }
            }

            int candidateCount = candidateIndices.Count;

            // 候选数量不足直接跳过发射，避免“先发一小撮再回滚”的闪烁
            if (candidateCount < minSeparateClusterSize)
            {
                // 宽松补选：放宽角度/高度限制，仅按距离选最近的主体粒子补足到 minSeparateClusterSize
                float fallbackMaxDist = math.max(maxSelectDist, controllerRadius * 2f);
                float fallbackMaxDistSq = fallbackMaxDist * fallbackMaxDist;
                var extra = new System.Collections.Generic.List<int>(minSeparateClusterSize - candidateCount);
                for (int i = 0; i < activeParticles; i++)
                {
                    if (_particles[i].Type != ParticleType.MainBody) continue;
                    if (candidateMask[i] != 0) continue;
                    float distSq = math.lengthsq(_particles[i].Position - spawnCenter);
                    if (distSq > fallbackMaxDistSq) continue;
                    extra.Add(i);
                }
                if (extra.Count > 0)
                {
                    extra.Sort((a, b) =>
                    {
                        float da = math.lengthsq(_particles[a].Position - spawnCenter);
                        float db = math.lengthsq(_particles[b].Position - spawnCenter);
                        return da.CompareTo(db);
                    });
                    for (int k = 0; k < extra.Count && candidateIndices.Count < minSeparateClusterSize; k++)
                    {
                        int idx = extra[k];
                        candidateMask[idx] = 1;
                        candidateIndices.Add(idx);
                    }
                }

                candidateCount = candidateIndices.Count;
                if (candidateCount < minSeparateClusterSize)
                {
                    candidateMask.Dispose();
                    return false;
                }
            }

            if (candidateCount > 0)
            {
                int[] idxArr = candidateIndices.ToArray();
                float[] distArr = new float[idxArr.Length];
                for (int k = 0; k < idxArr.Length; k++)
                {
                    int idx = idxArr[k];
                    distArr[k] = math.lengthsq(_particles[idx].Position - spawnCenter);
                }
                System.Array.Sort(distArr, idxArr);

                float3 emitVel = finalEmitDir * emitSpeed * WorldToSimScale;
                for (int k = 0; k < idxArr.Length && emitted < maxEmit; k++)
                {
                    int idx = idxArr[k];
                    var p = _particles[idx];
                    p.Type = ParticleType.Emitted;
                    p.FreeFrames = emitFreeFrames;
                    p.FramesOutsideMain = 0; // 初始化 CCA 保护期计数器
                    _velocityBuffer[idx] = emitVel;
                    _particles[idx] = p;
                    emitted++;
                }
            }
            candidateMask.Dispose();
            
            // 用于后续控制器创建
            
            // 检查发射数量是否达到最小切割粒子数
            if (emitted > 0 && emitted < minSeparateClusterSize)
            {
                // 发射数量不足，撤销发射，将粒子恢复为主体状态
                for (int i = 0; i < activeParticles; i++)
                {
                    var p = _particles[i];
                    if (p.Type == ParticleType.Emitted && p.FreeFrames > 0)
                    {
                        // 恢复为主体粒子
                        p.Type = ParticleType.MainBody;
                        p.FreeFrames = 0;
                        p.ControllerSlot = 0;
                        _particles[i] = p;
                        _velocityBuffer[i] = float3.zero;
                    }
                }
                return false;
            }
            
            // === 为发射的粒子创建独立控制器 ===
            if (emitted >= minSeparateClusterSize)
            {
                // 计算发射粒子的实际质心
                float3 emitCentroid = float3.zero;
                int emitCount = 0;
                for (int i = 0; i < activeParticles; i++)
                {
                    var p = _particles[i];
                    if (p.Type == ParticleType.Emitted && p.FreeFrames > 0)
                    {
                        emitCentroid += p.Position;
                        emitCount++;
                    }
                }
                if (emitCount > 0)
                    emitCentroid /= emitCount;

                float maxEmitDist = 0f;
                for (int i = 0; i < activeParticles; i++)
                {
                    var p = _particles[i];
                    if (p.Type == ParticleType.Emitted && p.FreeFrames > 0)
                    {
                        float dist = math.length(p.Position - emitCentroid);
                        maxEmitDist = math.max(maxEmitDist, dist);
                    }
                }

                float emitControllerRadius = math.max(2f, maxEmitDist + PBF_Utils.h * 2f);
                
                int newControllerSlot = _controllerBuffer.Length;
                _controllerBuffer.Add(new ParticleController
                {
                    Center = emitCentroid, // 使用实际质心
                    Radius = emitControllerRadius, // 足够大的半径包住所有发射粒子
                    Velocity = finalEmitDir * emitSpeed * WorldToSimScale, // 控制器跟随发射方向移动
                    Concentration = concentration * 3.0f, // 凝聚力
                    ParticleCount = emitCount,
                    FramesWithoutParticles = 0,
                    IsValid = true,
                });
                
                // 绑定所有发射粒子到新控制器
                for (int i = 0; i < activeParticles; i++)
                {
                    var p = _particles[i];
                    if (p.Type == ParticleType.Emitted && p.FreeFrames > 0)
                    {
                        p.ControllerSlot = newControllerSlot;
                        _particles[i] = p;
                    }
                }
            }
            
            // 强制更新体积统计
            if (_slimeVolume != null && emitted > 0)
            {
                _slimeVolume.UpdateFromParticles(_particles, true);
            }

            return emitted >= minSeparateClusterSize;
        }
        
        private void RearrangeInstances()
        {
            if (_slimeInstances.Length - _instancePool.Count > _controllerBuffer.Length)
            {
                var used = new NativeArray<bool>(_slimeInstances.Length, Allocator.Temp);
                for (int controllerID = 0; controllerID < _controllerBuffer.Length; controllerID++)
                {
                    var controller = _controllerBuffer[controllerID];
                    // 跳过无效或粒子不足的控制器（主体控制器始终有效）
                    if (controllerID > 0 && (!controller.IsValid || controller.ParticleCount < minSeparateClusterSize)) continue;
                    
                    var center = controller.Center;
                    int instanceID = -1;
                    float minDst = float.MaxValue;
                    for (int j = 0; j < _slimeInstances.Length; j++)
                    {
                        var slime = _slimeInstances[j];
                        if (used[j] || !slime.Active) continue;
                        var pos = slime.Center;
                        float dst = math.lengthsq(center - pos);
                        if (dst < minDst)
                        {
                            minDst = dst;
                            instanceID = j;
                        }
                    }
                    
                    used[instanceID] = true;
                    bool prevDbgFromRearrange = _dbgEyeJitterCallFromRearrange;
                    _dbgEyeJitterCallFromRearrange = true;
                    UpdateInstanceController(instanceID, controllerID);
                    _dbgEyeJitterCallFromRearrange = prevDbgFromRearrange;
                }

                for (int i = 0; i < _slimeInstances.Length; i++)
                {
                    var slime = _slimeInstances[i];
                    if (used[i] || !slime.Active) continue;
                    slime.Active = false;
                    _slimeInstances[i] = slime;
                    _instancePool.Push(i);
                }
                used.Dispose();

                if (!_slimeInstances[_controlledInstance].Active)
                {
                    float3 pos = trans.position * WorldToSimScale;
                    float minDst = float.MaxValue;
                    for (int i = 0; i < _slimeInstances.Length; i++)
                    {
                        var slime = _slimeInstances[i];
                        if (!slime.Active) continue;

                        float dst = math.lengthsq(pos - slime.Center);
                        if (dst < minDst)
                        {
                            minDst = dst;
                            _controlledInstance = i;
                        }
                    }

                    int controllerID = _slimeInstances[_controlledInstance].ControllerSlot;
                    bool prevDbgFromRearrange = _dbgEyeJitterCallFromRearrange;
                    _dbgEyeJitterCallFromRearrange = true;
                    UpdateInstanceController(_controlledInstance, controllerID);
                    _dbgEyeJitterCallFromRearrange = prevDbgFromRearrange;
                }
            }
            else
            {
                var used = new NativeArray<bool>(_controllerBuffer.Length, Allocator.Temp);
                for (int instanceID = 0; instanceID < _slimeInstances.Length; instanceID++)
                {
                    var slime = _slimeInstances[instanceID];
                    if (!slime.Active)  continue;
                    
                    // 关键修复：受控实例始终绑定到主体控制器（index 0）
                    // 原版行为：主体控制器以 trans 为中心，不受粒子位置影响
                    if (instanceID == _controlledInstance)
                    {
                        used[0] = true;
                        bool prevDbgFromRearrangeControlled = _dbgEyeJitterCallFromRearrange;
                        _dbgEyeJitterCallFromRearrange = true;
                        UpdateInstanceController(instanceID, 0);
                        _dbgEyeJitterCallFromRearrange = prevDbgFromRearrangeControlled;
                        continue;
                    }
                    
                    var pos = slime.Center;
                    int controllerID = -1;
                    float minDst = float.MaxValue;
                    for (int j = 0; j < _controllerBuffer.Length; j++)
                    {
                        if (used[j]) continue;
                        var cl = _controllerBuffer[j];
                        // 只匹配有效且粒子足够的控制器（主体控制器始终有效，25粒子才算一组）
                        const int minParticlesForInstance = 25;
                        if (j > 0 && (!cl.IsValid || cl.ParticleCount < minParticlesForInstance)) continue;
                        var center = cl.Center;
                        float dst = math.lengthsq(center - pos);
                        if (dst < minDst)
                        {
                            minDst = dst;
                            controllerID = j;
                        }
                    }
                    
                    // 如果没有找到有效控制器，停用这个实例（除非是第一个实例，保留给主体）
                    if (controllerID < 0)
                    {
                        if (!used[0])
                        {
                            // 第一个找不到控制器的实例分配给主体
                            controllerID = 0;
                        }
                        else
                        {
                            // 多余的实例停用
                            slime.Active = false;
                            _slimeInstances[instanceID] = slime;
                            _instancePool.Push(instanceID);
                            continue;
                        }
                    }
                    
                    used[controllerID] = true;
                    bool prevDbgFromRearrange = _dbgEyeJitterCallFromRearrange;
                    _dbgEyeJitterCallFromRearrange = true;
                    UpdateInstanceController(instanceID, controllerID);
                    _dbgEyeJitterCallFromRearrange = prevDbgFromRearrange;
                }
                
                for (int i = 0; i < _controllerBuffer.Length; i++)
                {
                    if (used[i]) continue;
                    var controller = _controllerBuffer[i];
                    // 跳过无效或粒子不足的控制器（主体控制器始终创建实例，25粒子才算一组）
                    const int minParticlesForInstance = 25;
                    if (i > 0 && (!controller.IsValid || controller.ParticleCount < minParticlesForInstance)) continue;
                    if (blockNum <= 0) continue;
                    float3 dir = math.normalizesafe(
                        math.lengthsq(controller.Velocity) < 1e-3f
                            ? (float3)(trans.position * WorldToSimScale) - controller.Center
                            : controller.Velocity,
                        new float3(1, 0, 0));
                    // 动态计算射线起点偏移，适应不同大小的史莱姆（使用独立的脸部高度参数）
                    float yOffset = controller.Radius * faceHeightFactor;
                    float3 newPos = controller.Center + new float3(0, yOffset, 0) + dir * controller.Radius * 0.5f;
                    
                    SlimeInstance slime = new SlimeInstance()
                    {
                        Active = true,
                        Center =  controller.Center,
                        Radius = controller.Radius,
                        Dir = dir,
                        Pos = newPos,
                        ControllerSlot = i,
                    };
                    if (_instancePool.Count > 0)
                        _slimeInstances[_instancePool.Pop()] = slime;
                    else
                        _slimeInstances.Add(slime);
                }
                used.Dispose();
            }
            
            // 清理无效控制器对应的实例
            for (int i = 0; i < _slimeInstances.Length; i++)
            {
                var slime = _slimeInstances[i];
                if (!slime.Active) continue;
                
                int ctrlId = slime.ControllerSlot;
                // 检查这个实例对应的控制器是否有效
                if (ctrlId > 0) // 主体控制器始终有效
                {
                    // 25粒子才算一组
                    const int minParticlesForInstance = 25;
                    bool isValid = ctrlId < _controllerBuffer.Length && 
                                   _controllerBuffer[ctrlId].IsValid && 
                                   _controllerBuffer[ctrlId].ParticleCount >= minParticlesForInstance;
                    if (!isValid)
                    {
                        // 停用这个实例
                        slime.Active = false;
                        _slimeInstances[i] = slime;
                        _instancePool.Push(i);
                    }
                }
            }
            
        }

        private void UpdateInstanceController(int instanceID, int controllerID)
        {
            var slime = _slimeInstances[instanceID];
            var controller = _controllerBuffer[controllerID];
            
            bool isControlled = instanceID == _controlledInstance;
            if (_dbgEyeJitterCallFromRearrange && isControlled)
            {
                slime.ControllerSlot = controllerID;
                _slimeInstances[instanceID] = slime;
                return;
            }
            if (isControlled)
                controller.Velocity = _velocity * WorldToSimScale;

            slime.ControllerSlot = controllerID;
            // 受控实例用更快的平滑速度，减少滞后
            float speedK = isControlled ? 17.2600f : 6.3216f;
            float speed = 1f - Mathf.Exp(-speedK * Time.deltaTime);
            slime.Radius = math.lerp(slime.Radius, controller.Radius, speed);
            slime.Center = math.lerp(slime.Center, controller.Center, speed);
            Vector3 vec = controller.Velocity;
            Vector3 horizVec = new Vector3(vec.x, 0f, vec.z);

            if (isControlled && _topDownController3D != null)
            {
                Vector3 inputDir = _topDownController3D.InputMoveDirection;
                inputDir.y = 0f;
                if (inputDir.sqrMagnitude > 1e-4f)
                {
                    horizVec = inputDir;
                }
                else
                {
                    horizVec = Vector3.zero;
                }
            }
            if (horizVec.sqrMagnitude > 1e-4f)
            {
                var newDir = Vector3.Slerp(slime.Dir, horizVec.normalized, speed);
                newDir.y = 0;
                slime.Dir = newDir.sqrMagnitude > 1e-6f ? newDir.normalized : Vector3.forward;
            }
            else
            {
                var newDir = slime.Dir;
                newDir.y = 0;
                slime.Dir = newDir.sqrMagnitude > 1e-6f ? newDir.normalized : Vector3.forward;
            }
            
            // 方案C：射线检测表面 + 法线内嵌（贴合变形表面且不穿出）
            
            // 【关键】使用粒子质心而不是 transform 位置，跳跃时更准确
            float3 eyeCenter;
            if (controllerID == 0)
            {
                // 主体使用 MainBody 粒子质心
                if (_controllerMainBodyCount.IsCreated && controllerID >= 0 && controllerID < _controllerMainBodyCount.Length && _controllerMainBodyCount[controllerID] > 0)
                {
                    eyeCenter = _controllerMainBodyCentroid[controllerID] / _controllerMainBodyCount[controllerID];
                }
                else
                {
                    eyeCenter = controller.Center;
                }
            }
            else
            {
                // 分离体使用所有粒子的质心，确保在体内
                if (_controllerCountAll.IsCreated && controllerID >= 0 && controllerID < _controllerCountAll.Length && _controllerCountAll[controllerID] > 0)
                {
                    eyeCenter = _controllerCentroidAll[controllerID] / _controllerCountAll[controllerID];
                }
                else
                {
                    eyeCenter = controller.Center;
                }
            }

            if (controllerID == 0)
            {
                _mainBodyCentroidWorld = (Vector3)(eyeCenter * SimToWorldScale);
            }
            
            // 眼睛方向以水平为主，但允许在一定垂直范围内跟随速度（下落时可朝下）
            float3 horizVel = new float3(controller.Velocity.x, 0, controller.Velocity.z);
            float3 horizDir = new float3(slime.Dir.x, 0, slime.Dir.z);
            if (isControlled && _topDownController3D != null)
            {
                Vector3 inputDir = _topDownController3D.InputMoveDirection;
                inputDir.y = 0f;
                if (inputDir.sqrMagnitude > 1e-4f)
                {
                    horizDir = math.normalizesafe((float3)inputDir, horizDir);
                }
            }
            else
            {
                if (math.lengthsq(horizVel) > 1e-4f)
                {
                    horizDir = math.normalizesafe(horizVel, horizDir);
                }
            }

            float3 baseHorizDir = math.normalizesafe(horizDir, new float3(0, 0, 1));
            const float eyePitchPerVelY = 0.12f;
            const float eyePitchMaxDownRad = 0.60f;
            const float eyePitchMaxUpRad = 0.25f;
            float pitch = math.clamp(controller.Velocity.y * eyePitchPerVelY, -eyePitchMaxDownRad, eyePitchMaxUpRad);
            float3 rayDir = math.normalizesafe(baseHorizDir * math.cos(pitch) + new float3(0, 1, 0) * math.sin(pitch), baseHorizDir);
            
            // 直接从粒子质心发射射线（质心已经反映真实垂直分布，不需要额外偏移）
            float3 basePos = eyeCenter + rayDir * (controller.Radius * 0.7f);
            float3 newPos = basePos;
            float3 surfacePos = new float3(float.NaN);
            bool surfOk = false;
            bool doRaycast = renderMode == RenderMode.Surface && blockNum > 0 && (isControlled || (Time.frameCount % nonControlledRaycastIntervalFrames) == 0);
            if (doRaycast)
            {
                new Effects.RayInsectJob
                {
                    GridLut = _gridLut,
                    Grid = _gridTempBuffer,
                    Result = _boundsBuffer,
                    Threshold = threshold,
                    Pos = eyeCenter,
                    Dir = rayDir,
                    MinPos = minPos,
                    MaxRadius = controller.Radius,
                }.Schedule().Complete();

                surfacePos = _boundsBuffer[0];
                surfOk = math.all(math.isfinite(surfacePos));
                if (surfOk)
                {
                    float actualSurfaceDist = math.length(surfacePos - eyeCenter);
                    float insetDepth = actualSurfaceDist * 0.2f;
                    newPos = surfacePos - rayDir * insetDepth;
                }
            }
            
            // 平滑过渡眼睛位置
            float posLerpK = isControlled ? 21.4005f : 9.7511f;
            float posLerpSpeed = 1f - Mathf.Exp(-posLerpK * Time.deltaTime);
            if (!_dbgEyeJitterCallFromRearrange)
                slime.Pos = Vector3.Lerp(slime.Pos, newPos, posLerpSpeed);
            
            _slimeInstances[instanceID] = slime;
            
            if (isControlled)
            {
                // 只回写 Velocity，不改 Center（避免与 FixedUpdate 开头设置的 Center 不一致）
                _controllerBuffer[controllerID] = controller;
            }
        }

        private void EnsureControllerMainBodyCache(int controllerCount)
        {
            if (controllerCount <= 0)
                controllerCount = 1;

            if (!_controllerMainBodyCentroid.IsCreated || _controllerMainBodyCentroid.Length < controllerCount)
            {
                if (_controllerMainBodyCentroid.IsCreated) _controllerMainBodyCentroid.Dispose();
                _controllerMainBodyCentroid = new NativeArray<float3>(math.max(8, controllerCount), Allocator.Persistent);
            }

            if (!_controllerMainBodyCount.IsCreated || _controllerMainBodyCount.Length < controllerCount)
            {
                if (_controllerMainBodyCount.IsCreated) _controllerMainBodyCount.Dispose();
                _controllerMainBodyCount = new NativeArray<int>(math.max(8, controllerCount), Allocator.Persistent);
            }

            if (!_controllerCentroidAll.IsCreated || _controllerCentroidAll.Length < controllerCount)
            {
                if (_controllerCentroidAll.IsCreated) _controllerCentroidAll.Dispose();
                _controllerCentroidAll = new NativeArray<float3>(math.max(8, controllerCount), Allocator.Persistent);
            }

            if (!_controllerCountAll.IsCreated || _controllerCountAll.Length < controllerCount)
            {
                if (_controllerCountAll.IsCreated) _controllerCountAll.Dispose();
                _controllerCountAll = new NativeArray<int>(math.max(8, controllerCount), Allocator.Persistent);
            }
        }

        private void UpdateInstances()
        {
            const int minParticlesForInstance = 25;
            for (int instanceID = 0; instanceID < _slimeInstances.Length; instanceID++)
            {
                var slime = _slimeInstances[instanceID];
                if (!slime.Active) continue;

                int controllerID = instanceID == _controlledInstance ? 0 : slime.ControllerSlot;
                if (controllerID < 0 || controllerID >= _controllerBuffer.Length)
                    continue;

                if (controllerID > 0)
                {
                    var ctrl = _controllerBuffer[controllerID];
                    if (!ctrl.IsValid || ctrl.ParticleCount < minParticlesForInstance)
                        continue;
                }

                UpdateInstanceController(instanceID, controllerID);
            }
        }

        /// <summary>
        /// 将相对坐标转换为世界坐标用于渲染（连续打包：主体 + 水珠）
        /// </summary>
        /// <returns>总渲染粒子数（activeParticles + dropletCount）</returns>
        private int ConvertToWorldPositionsForRendering()
        {
            float3 renderOffsetSim = (float3)(GetRenderPredictOffsetWorld() * WorldToSimScale);

            // 1. 主体粒子 [0, activeParticles)
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                float3 worldPos = Simulation_PBF.GetWorldPosition(p, _controllerBuffer, _sourceControllers);
                if (p.Type == ParticleType.MainBody || p.Type == ParticleType.Separated)
                    worldPos += renderOffsetSim;
                p.Position = worldPos;
                _particlesRenderBuffer[i] = p;
            }
            
            // 2. 水珠紧跟主体后面 [activeParticles, activeParticles + dropletCount)
            // 水珠的 Position 已经是内部坐标（世界坐标 * InvScale）
            // 与主体粒子通过 GetWorldPosition() 得到的内部坐标一致，无需转换
            int dropletCount = _dropletSubsystem.CopyToRenderBuffer(_particlesRenderBuffer, activeParticles);
            
            return activeParticles + dropletCount;
        }

        private int ConvertMainToWorldPositionsForRendering()
        {
            if (activeParticles <= 0)
                return 0;

            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                float3 worldPos = Simulation_PBF.GetWorldPosition(p, _controllerBuffer, _sourceControllers);
                p.Position = worldPos;
                _particlesRenderBuffer[i] = p;
            }

            return activeParticles;
        }
        
        /// <summary>
        /// 为渲染构建邻域查询表（包含主体 + 水珠）
        /// </summary>
        private void BuildRenderLut(int totalParticles)
        {
            if (totalParticles <= 0)
            {
                _renderLut.Clear();
                return;
            }

            if (!_renderHashesBuffer.IsCreated || _renderHashesBuffer.Length < totalParticles)
            {
                if (_renderHashesBuffer.IsCreated) _renderHashesBuffer.Dispose();
                _renderHashesBuffer = new NativeArray<int2>(math.max(256, totalParticles), Allocator.Persistent);
            }

            var renderHashes = _renderHashesBuffer.GetSubArray(0, totalParticles);
            
            // 使用渲染专用邻域表，不破坏模拟用的 _lut
            _renderLut.Clear();
            
            // Shuffle _particlesRenderBuffer 以匹配排序后的顺序
            // 使用 _particlesTemp 作为临时缓冲
            NativeArray<Particle>.Copy(_particlesRenderBuffer, _particlesTemp, totalParticles);

            // 使用渲染专用 HashJob（_particlesRenderBuffer 已是世界坐标）
            var hashHandle = new Reconstruction.HashRenderJob
            {
                Ps = _particlesRenderBuffer,
                Hashes = renderHashes,
            }.Schedule(totalParticles, batchCount);
            
            // 排序
            var sortHandle = renderHashes.SortJob(new PBF_Utils.Int2Comparer()).Schedule(hashHandle);
            
            var shuffleHandle = new Reconstruction.ShuffleRenderJob
            {
                Hashes = renderHashes,
                PsRaw = _particlesTemp,
                PsShuffled = _particlesRenderBuffer,
            }.Schedule(totalParticles, batchCount, sortHandle);
            
            // 构建 LUT
            var lutHandle = new Simulation_PBF.BuildLutJob
            {
                Hashes = renderHashes,
                Lut = _renderLut
            }.Schedule(sortHandle);
            
            JobHandle.CombineDependencies(shuffleHandle, lutHandle).Complete();
        }

        private int GetGroupParticleCount(int groupId)
        {
            int count = 0;
            for (int i = 0; i < activeParticles; i++)
            {
                if (_particles[i].ControllerSlot == groupId)
                    count++;
            }
            return count;
        }


        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
                return;
            
            // 没有任何线框选项时直接返回
            if (!recallAvoidanceGizmos && !gridDebug && !ccaDebug && !colliderCollectDebug)
                return;
            
            // 模拟边界（黄色）
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(_bounds.center, _bounds.size);
            
            // 主体控制器范围（青色）
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0 && trans != null)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.6f);
                Gizmos.DrawWireSphere(trans.position, _controllerBuffer[0].Radius * SimToWorldScale);
            }

            if (recallAvoidanceGizmos && _controllerBuffer.IsCreated && _controllerBuffer.Length > 1)
            {
                Gizmos.color = new Color(1f, 0f, 1f, 0.5f);
                for (int i = 1; i < _controllerBuffer.Length; i++)
                {
                    var ctrl = _controllerBuffer[i];
                    if (!ctrl.IsValid)
                        continue;
                    Vector3 cW = (Vector3)(ctrl.Center * SimToWorldScale);
                    float rW = math.max(0.001f, ctrl.Radius * SimToWorldScale);
                    Gizmos.DrawWireSphere(cW, rW);
                }

                if (_dbgRecallSphereCastFrame > 0)
                {
                    if (_dbgRecallSphereHitFinal)
                        Gizmos.color = _dbgRecallSphereHitFromSdf ? new Color(0.2f, 0.9f, 1f, 0.95f) : new Color(1f, 0.2f, 0.2f, 0.9f);
                    else
                        Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
                    Vector3 startW = _dbgRecallSphereOriginW;
                    Vector3 endW = _dbgRecallSphereOriginW + _dbgRecallSphereDirW * _dbgRecallSphereCastDistW;
                    float rW = math.max(0.001f, _dbgRecallSphereRadiusW);
                    Gizmos.DrawWireSphere(startW, rW);
                    Gizmos.DrawWireSphere(endW, rW);
                    Gizmos.DrawLine(startW, endW);

                    if (_dbgRecallSphereHitRaw)
                    {
                        Gizmos.color = _dbgRecallSphereHitFilteredByNormal ? new Color(1f, 0.7f, 0f, 0.9f) : new Color(1f, 0.2f, 0.2f, 0.9f);
                        Gizmos.DrawWireSphere(_dbgRecallSphereHitPointW, rW * 0.35f);
                        Gizmos.DrawLine(_dbgRecallSphereHitPointW, _dbgRecallSphereHitPointW + _dbgRecallSphereHitNormalW * math.max(0.2f, rW));
                    }
                }
            }
            
            // 网格块（gridDebug）
            if (gridDebug)
            {
                Gizmos.color = Color.blue;
                for (var i = 0; i < blockNum; i++)
                {
                    var block = _blockBuffer[i];
                    Vector3 blockMinPos = new Vector3(block.x, block.y, block.z) * PBF_Utils.CellSize * (SimToWorldScale * 4f) +
                                          _bounds.min;
                    Vector3 size = new Vector3(PBF_Utils.CellSize, PBF_Utils.CellSize, PBF_Utils.CellSize) * (SimToWorldScale * 4f);
                    Gizmos.DrawWireCube(blockMinPos + size * 0.5f, size);
                }
            }
            
            // CCA组件盒（ccaDebug）
            if (ccaDebug && _componentsBuffer.IsCreated)
            {
                Gizmos.color = Color.green;
                for (var i = 0; i < _componentsBuffer.Length; i++)
                {
                    var c = _componentsBuffer[i];
                    var size = (c.BoundsMax - c.BoundsMin) * SimToWorldScale * PBF_Utils.CellSize;
                    var center = c.Center * SimToWorldScale * PBF_Utils.CellSize;
                    Gizmos.DrawWireCube(_bounds.min + (Vector3)center, size);
                }
            }
            
            // 碰撞体盒（colliderCollectDebug）
            if (colliderCollectDebug)
            {
                Gizmos.color = Color.cyan;
                for (var i = 0; i < _currentColliderCount; i++)
                {
                    var c = _colliderBuffer[i];
                    if (useWorldStaticSdf && _worldStaticSdf.IsCreated && disableStaticColliderFallbackWhenUsingSdf && c.IsDynamic == 0)
                        continue;

                    Vector3 centerW = (Vector3)(c.Center * SimToWorldScale);
                    Vector3 sizeW = (Vector3)(c.Extent * SimToWorldScale * 2);
                    if (c.Shape == ColliderShapes.Obb)
                    {
                        Matrix4x4 old = Gizmos.matrix;
                        Gizmos.matrix = Matrix4x4.TRS(centerW, (Quaternion)c.Rotation, Vector3.one);
                        Gizmos.DrawWireCube(Vector3.zero, sizeW);
                        Gizmos.matrix = old;
                    }
                    else
                    {
                        Gizmos.DrawWireCube(centerW, sizeW);
                    }
                }
            }
        }

        #endregion
        
        #region 碰撞体查询（索引缓存）
        
        /// <summary>
        /// 刷新近场碰撞体缓存：查询史莱姆和水珠附近的碰撞体
        /// </summary>
        private void RefreshMainNearbyColliders()
        {
            _currentColliderCount = 0;
            _colliderInstanceIds.Clear();

            if (!SlimeWorldColliderIndex.TryGetInstance(out var index))
                return;

            Transform ignoreRoot = trans != null ? trans.root : null;
            Transform ignoreRootB = (_pipeTravelAbility != null && _pipeTravelAbility.IsTravelling)
                ? _pipeTravelAbility.CurrentPathTransform
                : null;
            Vector3 slimeCenter = trans != null ? trans.position : Vector3.zero;

            index.AppendMyBoxColliders(slimeCenter, mainColliderIndexQueryRadiusWorld, WorldToSimScale, ignoreRoot, ignoreRootB, _colliderBuffer, ref _currentColliderCount, mainColliderIndexQueryCacheCapacity, _colliderInstanceIds);

            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 1)
            {
                for (int i = 1; i < _controllerBuffer.Length && _currentColliderCount < mainColliderIndexQueryCacheCapacity; i++)
                {
                    var ctrl = _controllerBuffer[i];
                    if (!ctrl.IsValid)
                        continue;

                    Vector3 cW = (Vector3)(ctrl.Center * SimToWorldScale);
                    float rW = math.max(mainColliderIndexQueryRadiusWorld, (ctrl.Radius * SimToWorldScale) + 2f);
                    index.AppendMyBoxColliders(cW, rW, WorldToSimScale, ignoreRoot, ignoreRootB, _colliderBuffer, ref _currentColliderCount, mainColliderIndexQueryCacheCapacity, _colliderInstanceIds);
                }
            }
        }
        
        /// <summary>
        /// 为每个有效控制器独立射线检测地面高度，存入 ctrl.GroundY
        /// </summary>
        private void RefreshDropletNearbyColliders()
        {
            _currentDropletColliderCount = 0;
            _dropletColliderInstanceIds.Clear();

            if (!SlimeWorldColliderIndex.TryGetInstance(out var index))
                return;

            Transform ignoreRoot = trans != null ? trans.root : null;
            Transform ignoreRootB = (_pipeTravelAbility != null && _pipeTravelAbility.IsTravelling)
                ? _pipeTravelAbility.CurrentPathTransform
                : null;
            if (_dropletSubsystem.TryGetActiveBounds(out float3 min, out float3 max))
            {
                float3 center = (min + max) * 0.5f;
                float3 extent = (max - min) * 0.5f;

                Vector3 centerWorld = (Vector3)(center * SimToWorldScale);
                Vector3 extentWorld = (Vector3)(extent * SimToWorldScale);
                float radiusWorld = math.max(dropletColliderIndexQueryRadiusWorld, extentWorld.magnitude + 2f);

                index.AppendMyBoxColliders(centerWorld, radiusWorld, WorldToSimScale, ignoreRoot, ignoreRootB,
                    _dropletColliderBuffer, ref _currentDropletColliderCount, dropletColliderIndexQueryCacheCapacity, _dropletColliderInstanceIds);
                return;
            }

            if (allSources == null || allSources.Count == 0)
                return;

            for (int s = 0; s < allSources.Count && _currentDropletColliderCount < dropletColliderIndexQueryCacheCapacity; s++)
            {
                var source = allSources[s];
                if (source == null || source.State != DropWater.DropletSourceState.Simulated)
                    continue;

                Vector3 sourceCenter = source.transform.position;
                index.AppendMyBoxColliders(sourceCenter, dropletColliderIndexQueryRadiusWorld, WorldToSimScale, ignoreRoot, ignoreRootB,
                    _dropletColliderBuffer, ref _currentDropletColliderCount, dropletColliderIndexQueryCacheCapacity, _dropletColliderInstanceIds);
            }
        }
        
        private void RefreshGroundY()
        {
            float fallbackGroundY = -10f; // 后备地面高度（模拟坐标）
            
            for (int c = 0; c < _controllerBuffer.Length; c++)
            {
                var ctrl = _controllerBuffer[c];
                
                // 主体控制器（ID=0）始终检测地面；分离控制器需要 IsValid
                bool shouldUpdate = (c == 0) || ctrl.IsValid;
                if (!shouldUpdate)
                {
                    ctrl.GroundY = fallbackGroundY;
                    ctrl.GroundPoint = new float3(ctrl.Center.x, fallbackGroundY, ctrl.Center.z);
                    ctrl.GroundNormal = new float3(0, 1, 0);
                    _controllerBuffer[c] = ctrl;
                    continue;
                }
                
                // 从控制器中心向下射线检测
                Vector3 worldPos = (Vector3)(ctrl.Center * SimToWorldScale);
                float rayStartY = worldPos.y;
                worldPos.y = rayStartY;
                
                bool hitGround = TryRaycastFiltered(worldPos, Vector3.down, 30f, GroundQueryMask, out RaycastHit hit);
                if (hitGround)
                {
                    const int logIntervalFrames = 30;
                    if (Time.frameCount - _lastGroundFallbackHitLogFrame >= logIntervalFrames)
                    {
                        _lastGroundFallbackHitLogFrame = Time.frameCount;
                        string colType = hit.collider != null ? hit.collider.GetType().Name : "null";
                        string colName = hit.collider != null ? hit.collider.name : "null";
                        string layerName = hit.collider != null ? LayerMask.LayerToName(hit.collider.gameObject.layer) : "null";
                        Debug.Log($"[GroundFallbackRay] frame={Time.frameCount} c={c} type={colType} name={colName} layer={layerName} distW={hit.distance:F3} p={hit.point}");
                    }

                    float oldGroundY = ctrl.GroundY;
                    float3 hitPointSim = (float3)(hit.point * WorldToSimScale);
                    float3 hitNormalSim = (float3)hit.normal;
                    float nLen2 = math.lengthsq(hitNormalSim);
                    if (nLen2 < 1e-6f)
                        hitNormalSim = new float3(0, 1, 0);
                    else
                        hitNormalSim *= math.rsqrt(nLen2);

                    const float minGroundNy = 0.55f;
                    if (hitNormalSim.y < minGroundNy)
                    {
                        hitGround = false;
                    }
                    else
                    {
                        float radiusSim = ParticleRadiusWorldScaled * WorldToSimScale;
                        float3 newGroundPoint = hitPointSim + hitNormalSim * radiusSim;
                        float newGroundY = newGroundPoint.y;
                        
                        // 平滑过渡：避免射线命中护动导致地面高度突变
                        // 只有新值更低时才立即采纳（避免穿透地面），否则平滑过渡
                        if (oldGroundY <= 0 || newGroundY < oldGroundY)
                        {
                            ctrl.GroundY = newGroundY;
                            ctrl.GroundPoint = newGroundPoint;
                            ctrl.GroundNormal = hitNormalSim;
                        }
                        else
                        {
                            ctrl.GroundY = math.lerp(oldGroundY, newGroundY, 0.1f); // 缓慢上升
                            ctrl.GroundPoint = math.lerp(ctrl.GroundPoint, newGroundPoint, 0.1f);
                            ctrl.GroundNormal = math.normalizesafe(math.lerp(ctrl.GroundNormal, hitNormalSim, 0.1f), new float3(0, 1, 0));
                        }
                    }
                }
                else
                {
                    // 未命中时使用控制器中心Y减去一定距离作为后备
                    ctrl.GroundY = ctrl.Center.y - 20f;
                    ctrl.GroundPoint = new float3(ctrl.Center.x, ctrl.GroundY, ctrl.Center.z);
                    ctrl.GroundNormal = new float3(0, 1, 0);
                }
                
                _controllerBuffer[c] = ctrl;
            }
        }
        
        #endregion
        
        #region 召回避障

        private struct RecallAvoidPerf
        {
            public long TicksTotal;
            public long TicksQuery;
            public long TicksSdf;
            public long TicksObb;
            public long TicksGround;

            public int SdfSamples;
            public int SdfInvalid;
            public int ObbChecked;
            public int RaycastCount;
            public byte HitFromSdf;
            public byte HitFromObb;
        }

        private bool TryRaycastFiltered(Vector3 originW, Vector3 dirW, float maxDistW, out RaycastHit best)
        {
            return TryRaycastFiltered(originW, dirW, maxDistW, ColliderQueryMask, out best);
        }

        private bool TryRaycastFiltered(Vector3 originW, Vector3 dirW, float maxDistW, int layerMask, out RaycastHit best)
        {
            best = default;
            if (_raycastHits == null || _raycastHits.Length == 0)
                return UnityEngine.Physics.Raycast(originW, dirW, out best, maxDistW, layerMask, QueryTriggerInteraction.Ignore);

            int count = UnityEngine.Physics.RaycastNonAlloc(originW, dirW, _raycastHits, maxDistW, layerMask, QueryTriggerInteraction.Ignore);
            float bestDist = float.PositiveInfinity;
            bool has = false;
            for (int i = 0; i < count; i++)
            {
                var h = _raycastHits[i];
                var col = h.collider;
                if (col == null)
                    continue;
                if (col.transform != null)
                {
                    if (trans != null && trans.root != null && col.transform.IsChildOf(trans.root))
                        continue;

                    if (_pipeTravelAbility != null && _pipeTravelAbility.IsTravelling)
                    {
                        var ignoreRootB = _pipeTravelAbility.CurrentPathTransform;
                        if (ignoreRootB != null && col.transform.IsChildOf(ignoreRootB))
                            continue;
                    }
                }
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    best = h;
                    has = true;
                }
            }
            return has;
        }

        private bool TryQueryRecallObstacle(float3 center, float radiusXZ, float halfHeight, float2 dirXZ, float checkDistSim,
            float recallObstacleKeepDistanceMarginSim, out float hitDistSim, out float3 hitNormalSim, out float hitTopYSim, out bool filteredByNormal, out bool hitFromSdf, ref RecallAvoidPerf perf)
        {
            hitDistSim = float.MaxValue;
            hitNormalSim = float3.zero;
            hitTopYSim = center.y - halfHeight;
            filteredByNormal = false;
            hitFromSdf = false;

            float3 rayDir = new float3(dirXZ.x, 0f, dirXZ.y);
            if (math.lengthsq(rayDir) < 1e-6f)
                return false;

            bool useSdf = recallAvoidUseStaticSdf && useWorldStaticSdf && _worldStaticSdf.IsCreated;
            if (useSdf)
            {
                long tSdf0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var v = _worldStaticSdf.Data;
                if (v.IsCreated)
                {
                    float minStep = math.max(v.VoxelSizeSim, 1e-3f);

                    float hitThreshold = radiusXZ + recallObstacleKeepDistanceMarginSim;

                    float3 rayEnd = center + rayDir * checkDistSim;
                    float3 segMin = math.min(center, rayEnd);
                    float3 segMax = math.max(center, rayEnd);
                    float y0 = center.y - halfHeight * 0.35f;
                    float y1 = center.y + halfHeight * 0.35f;
                    segMin.y = math.min(segMin.y, y0);
                    segMax.y = math.max(segMax.y, y1);

                    float3 expandedMin = v.AabbMinSim - new float3(hitThreshold);
                    float3 expandedMax = v.AabbMaxSim + new float3(hitThreshold);
                    bool mayHitSdf =
                        !(segMax.x < expandedMin.x || segMin.x > expandedMax.x ||
                          segMax.y < expandedMin.y || segMin.y > expandedMax.y ||
                          segMax.z < expandedMin.z || segMin.z > expandedMax.z);

                    if (!mayHitSdf)
                    {
                        useSdf = false;
                    }
                    else
                    {

                    float3 p0 = center;
                    float3 p1 = center + new float3(0f, halfHeight * 0.35f, 0f);
                    float3 p2 = center - new float3(0f, halfHeight * 0.35f, 0f);

                    for (float t = 0f; t <= checkDistSim;)
                    {
                        perf.SdfSamples++;
                        float3 pp0 = p0 + rayDir * t;
                        float3 pp1 = p1 + rayDir * t;
                        float3 pp2 = p2 + rayDir * t;

                        float d0 = v.SampleDistance(pp0);
                        float d1 = v.SampleDistance(pp1);
                        float d2 = v.SampleDistance(pp2);
                        bool ok0 = d0 < (v.MaxDistanceSim - 1e-5f);
                        bool ok1 = d1 < (v.MaxDistanceSim - 1e-5f);
                        bool ok2 = d2 < (v.MaxDistanceSim - 1e-5f);

                        float dMin = float.MaxValue;
                        bool anyValid = false;

                        float bestWallD = float.MaxValue;
                        float3 bestWallN = float3.zero;
                        bool hasWallHit = false;

                        if (ok0)
                        {
                            anyValid = true;
                            if (d0 < dMin) dMin = d0;
                            if (d0 <= hitThreshold)
                            {
                                float3 n0 = v.SampleNormalForward(pp0, d0);
                                if (math.abs(n0.y) > RecallWallNormalYThreshold)
                                {
                                    filteredByNormal = true;
                                }
                                else if (d0 < bestWallD)
                                {
                                    bestWallD = d0;
                                    bestWallN = n0;
                                    hasWallHit = true;
                                }
                            }
                        }

                        if (ok1)
                        {
                            anyValid = true;
                            if (d1 < dMin) dMin = d1;
                            if (d1 <= hitThreshold)
                            {
                                float3 n1 = v.SampleNormalForward(pp1, d1);
                                if (math.abs(n1.y) > RecallWallNormalYThreshold)
                                {
                                    filteredByNormal = true;
                                }
                                else if (d1 < bestWallD)
                                {
                                    bestWallD = d1;
                                    bestWallN = n1;
                                    hasWallHit = true;
                                }
                            }
                        }

                        if (ok2)
                        {
                            anyValid = true;
                            if (d2 < dMin) dMin = d2;
                            if (d2 <= hitThreshold)
                            {
                                float3 n2 = v.SampleNormalForward(pp2, d2);
                                if (math.abs(n2.y) > RecallWallNormalYThreshold)
                                {
                                    filteredByNormal = true;
                                }
                                else if (d2 < bestWallD)
                                {
                                    bestWallD = d2;
                                    bestWallN = n2;
                                    hasWallHit = true;
                                }
                            }
                        }

                        if (!anyValid)
                        {
                            perf.SdfInvalid++;
                            t += minStep;
                            continue;
                        }

                        if (hasWallHit)
                        {
                            hitDistSim = t;
                            hitNormalSim = bestWallN;
                            hitTopYSim = center.y - halfHeight;
                            hitFromSdf = true;
                            perf.TicksSdf += System.Diagnostics.Stopwatch.GetTimestamp() - tSdf0;
                            return true;
                        }

                        float adv = dMin - hitThreshold;
                        if (adv < minStep) adv = minStep;
                        t += adv;
                    }
                    }
                }

                perf.TicksSdf += System.Diagnostics.Stopwatch.GetTimestamp() - tSdf0;
            }

            float closestBlockDist = float.MaxValue;
            float3 closestBlockNormal = float3.zero;
            float closestBlockTopY = 0f;
            int closestBlockIdx = -1;

            float hitThresholdCollider = radiusXZ + recallObstacleKeepDistanceMarginSim;

            long tObb0 = System.Diagnostics.Stopwatch.GetTimestamp();
            float2 originXZ = new float2(center.x, center.z);
            for (int c = 0; c < _currentColliderCount; c++)
            {
                MyBoxCollider box = _colliderBuffer[c];
                if (useSdf && box.IsDynamic == 0)
                    continue;

                float2 boxXZ = new float2(box.Center.x, box.Center.z);
                float proj = math.dot(boxXZ - originXZ, dirXZ);
                proj = math.clamp(proj, 0f, checkDistSim);
                float2 closestXZ = originXZ + dirXZ * proj;
                float2 deltaXZ = boxXZ - closestXZ;
                float dist2XZ = math.dot(deltaXZ, deltaXZ);
                float gateR = math.length(box.Extent) + hitThresholdCollider;
                if (dist2XZ > gateR * gateR)
                    continue;

                perf.ObbChecked++;

                float topYSim;
                if (box.Shape == ColliderShapes.Obb)
                {
                    float3x3 m = new float3x3(box.Rotation);
                    float upHalf = math.abs(m.c0.y) * box.Extent.x + math.abs(m.c1.y) * box.Extent.y + math.abs(m.c2.y) * box.Extent.z;
                    topYSim = box.Center.y + upHalf;
                }
                else
                {
                    topYSim = box.Center.y + box.Extent.y;
                }

                switch (box.Shape)
                {
                    case ColliderShapes.Obb:
                    {
                        quaternion invRot = math.conjugate(box.Rotation);
                        float3 localDir = math.mul(invRot, rayDir);
                        float3 localOrigin = math.mul(invRot, (center - box.Center));

                        float3 expandedExtentLocal = box.Extent + new float3(hitThresholdCollider, halfHeight, hitThresholdCollider);

                        float3 absLocal = math.abs(localOrigin);
                        bool inside = math.all(absLocal <= expandedExtentLocal);
                        if (inside)
                        {
                            float considerPen = hitThresholdCollider * 1.25f;
                            float distToFaceX = expandedExtentLocal.x - absLocal.x;
                            float distToFaceZ = expandedExtentLocal.z - absLocal.z;
                            int axis = distToFaceX < distToFaceZ ? 0 : 2;

                            float distToFace = expandedExtentLocal[axis] - absLocal[axis];
                            if (distToFace > considerPen)
                                break;

                            float originAxis = localOrigin[axis];
                            float dirAxis = localDir[axis];
                            bool movingTowardBox = (dirAxis * originAxis) < 0f;
                            if (!movingTowardBox)
                            {
                                float originEps = math.max(1e-4f, hitThresholdCollider * 0.05f);
                                if (math.abs(originAxis) <= originEps && math.abs(dirAxis) > 1e-5f)
                                    movingTowardBox = true;
                            }
                            if (!movingTowardBox)
                                break;

                            float3 nLocal = float3.zero;
                            float nSign = math.sign(localOrigin[axis]);
                            if (nSign == 0f)
                                nSign = -math.sign(localDir[axis]);
                            if (nSign == 0f)
                                nSign = 1f;
                            nLocal[axis] = nSign;

                            float3 nWorld = math.mul(box.Rotation, nLocal);
                            if (math.abs(nWorld.y) > RecallWallNormalYThreshold)
                            {
                                break;
                            }

                            if (distToFace < closestBlockDist)
                            {
                                closestBlockDist = distToFace;
                                closestBlockIdx = c;
                                closestBlockNormal = nWorld;
                                closestBlockTopY = topYSim;
                            }

                            break;
                        }

                        float3 invDir = 1f / (localDir + new float3(0.0001f, 0.0001f, 0.0001f));
                        float3 t1 = (-expandedExtentLocal - localOrigin) * invDir;
                        float3 t2 = (expandedExtentLocal - localOrigin) * invDir;
                        float3 tmin3 = math.min(t1, t2);
                        float3 tmax3 = math.max(t1, t2);

                        float tNear = math.max(math.max(tmin3.x, tmin3.y), math.max(tmin3.z, 0f));
                        float tFar = math.min(math.min(tmax3.x, tmax3.y), tmax3.z);
                        if (tNear < tFar && tNear < checkDistSim)
                        {
                            int axis = 0;
                            if (tmin3.y > tmin3[axis]) axis = 1;
                            if (tmin3.z > tmin3[axis]) axis = 2;

                            float3 nLocal = float3.zero;
                            nLocal[axis] = -math.sign(localDir[axis]);
                            float3 nWorld = math.mul(box.Rotation, nLocal);
                            if (math.abs(nWorld.y) > RecallWallNormalYThreshold)
                            {
                                break;
                            }

                            if (tNear < closestBlockDist)
                            {
                                closestBlockDist = tNear;
                                closestBlockIdx = c;
                                closestBlockNormal = nWorld;
                                closestBlockTopY = topYSim;
                            }
                        }

                        break;
                    }
                    default:
                    {
                        float3 expandedExtent = box.Extent + new float3(hitThresholdCollider, halfHeight, hitThresholdCollider);
                        float3 rayOrigin = center;

                        float3 invDir = 1f / (rayDir + new float3(0.0001f, 0.0001f, 0.0001f));
                        float3 t1 = (box.Center - expandedExtent - rayOrigin) * invDir;
                        float3 t2 = (box.Center + expandedExtent - rayOrigin) * invDir;

                        float3 tmin3 = math.min(t1, t2);
                        float3 tmax3 = math.max(t1, t2);

                        float tNear = math.max(math.max(tmin3.x, tmin3.y), math.max(tmin3.z, 0f));
                        float tFar = math.min(math.min(tmax3.x, tmax3.y), tmax3.z);

                        if (tNear < tFar && tNear < checkDistSim)
                        {
                            int axis = 0;
                            if (tmin3.y > tmin3[axis]) axis = 1;
                            if (tmin3.z > tmin3[axis]) axis = 2;

                            float3 n = float3.zero;
                            n[axis] = -math.sign(rayDir[axis]);

                            if (math.abs(n.y) > RecallWallNormalYThreshold)
                            {
                                break;
                            }

                            if (tNear < closestBlockDist)
                            {
                                closestBlockDist = tNear;
                                closestBlockIdx = c;
                                closestBlockNormal = n;
                                closestBlockTopY = topYSim;
                            }
                        }

                        break;
                    }
                }
            }

            if (closestBlockIdx >= 0)
            {
                hitDistSim = closestBlockDist;
                hitNormalSim = closestBlockNormal;
                hitTopYSim = closestBlockTopY;
                perf.TicksObb += System.Diagnostics.Stopwatch.GetTimestamp() - tObb0;
                return true;
            }

            perf.TicksObb += System.Diagnostics.Stopwatch.GetTimestamp() - tObb0;

            return false;
        }

        private static void ProjectVelocityNotAwayFromDir(ref float3 vel, float3 dir)
        {
            float3 dir3 = new float3(dir.x, 0f, dir.z);
            float dirLen2 = math.lengthsq(dir3);
            if (dirLen2 > 1e-6f)
            {
                dir3 *= math.rsqrt(dirLen2);
                float3 velXZ = new float3(vel.x, 0f, vel.z);
                float dotToward = math.dot(velXZ, dir3);
                if (dotToward < 0f)
                {
                    velXZ -= dir3 * dotToward;
                    vel.x = velXZ.x;
                    vel.z = velXZ.z;
                }
            }
        }

        /// <summary>
        /// 计算带避障的召回速度
        /// 新逻辑：基础速度 = 指向主体的速度，叠加避障 steering 力
        /// </summary>
        /// <param name="center">分离组中心（模拟坐标）</param>
        /// <param name="radiusXZ">分离组半径</param>
        /// <param name="halfHeight">分离组半径</param>
        /// <param name="mainCenter">主体中心（模拟坐标）</param>
        /// <param name="rawDir">原始朝向主体的方向（归一化）</param>
        /// <param name="speed">召回速度</param>
        /// <returns>处理过避障的召回速度向量</returns>
        private float3 ComputeAvoidedRecallVelocity(int controllerId, float3 center, float radiusXZ, float halfHeight, float3 mainCenter, float3 rawDir, float speed, ref RecallAvoidPerf perf)
        {
            long tTotal0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // 基础速度：直接指向主体
            float3 baseVel = new float3(rawDir.x, 0f, rawDir.z) * speed;
            float3 steering = float3.zero;

            float dt = math.max(1e-4f, deltaTime);
            float maxUpSim = math.max(0.01f, recallStepMaxUpSpeed) * WorldToSimScale;
            bool hasStepJumpTarget = controllerId > 0 &&
                                    _controllerStepJumpTargetCenterY != null &&
                                    controllerId < _controllerStepJumpTargetCenterY.Length &&
                                    !float.IsNaN(_controllerStepJumpTargetCenterY[controllerId]);

            bool hasExpireFrame = controllerId > 0 &&
                                  _controllerStepJumpTargetExpireFrame != null &&
                                  controllerId < _controllerStepJumpTargetExpireFrame.Length;
            int expireFrame = hasExpireFrame ? _controllerStepJumpTargetExpireFrame[controllerId] : 0;
            if (hasStepJumpTarget)
            {
                if (!hasExpireFrame || expireFrame <= 0 || Time.frameCount > expireFrame)
                {
                    _controllerStepJumpTargetCenterY[controllerId] = float.NaN;
                    if (hasExpireFrame)
                        _controllerStepJumpTargetExpireFrame[controllerId] = 0;
                    hasStepJumpTarget = false;
                }
            }

            float recallObstacleCheckDistSim = recallObstacleCheckDist * WorldToSimScale;
            float recallObstacleKeepDistanceMarginSim = recallObstacleKeepDistanceMargin * WorldToSimScale;
            float recallStepMaxHeightSim = recallStepMaxHeight * WorldToSimScale;

            bool dbgSphereHitRaw = false;
            bool dbgSphereHitFinal = false;
            bool dbgSphereHitFilteredByNormal = false;
            
            // XZ平面上的方向
            float2 dirXZ = math.normalizesafe(new float2(rawDir.x, rawDir.z));
            if (math.lengthsq(dirXZ) < 0.001f)
            {
                perf.TicksTotal += System.Diagnostics.Stopwatch.GetTimestamp() - tTotal0;
                return baseVel; // 几乎垂直，不需要避障
            }

            float projectionCheckDist = recallObstacleCheckDistSim + radiusXZ;
            Vector3 originW = (Vector3)(center * SimToWorldScale);
            Vector3 dirW = new Vector3(dirXZ.x, 0f, dirXZ.y);
            float sphereRadiusW = math.max(0.001f, radiusXZ * SimToWorldScale);
            float castDistW = math.max(0.001f, projectionCheckDist * SimToWorldScale);
            float keepDistW = math.max(0f, (radiusXZ + recallObstacleKeepDistanceMarginSim) * SimToWorldScale);

            long tQuery0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool obstacleHit = TryQueryRecallObstacle(center, radiusXZ, halfHeight, dirXZ, projectionCheckDist, recallObstacleKeepDistanceMarginSim,
                out float obstacleDistSim, out float3 obstacleNormalSim, out float obstacleTopY, out bool obstacleFilteredByNormal, out bool obstacleHitFromSdf, ref perf);
            perf.TicksQuery += System.Diagnostics.Stopwatch.GetTimestamp() - tQuery0;
            dbgSphereHitRaw = obstacleHit || obstacleFilteredByNormal;
            dbgSphereHitFinal = obstacleHit;
            dbgSphereHitFilteredByNormal = obstacleFilteredByNormal;

            perf.HitFromSdf = (byte)(obstacleHitFromSdf ? 1 : 0);
            perf.HitFromObb = (byte)((obstacleHit && !obstacleHitFromSdf) ? 1 : 0);

            if (recallAvoidanceGizmos)
            {
                _dbgRecallSphereCastFrame = Time.frameCount;
                _dbgRecallSphereOriginW = originW;
                _dbgRecallSphereDirW = dirW;
                _dbgRecallSphereRadiusW = sphereRadiusW;
                _dbgRecallSphereCastDistW = castDistW;
                _dbgRecallSphereHitRaw = dbgSphereHitRaw;
                _dbgRecallSphereHitFinal = dbgSphereHitFinal;
                _dbgRecallSphereHitFilteredByNormal = dbgSphereHitFilteredByNormal;
                _dbgRecallSphereHitFromSdf = obstacleHitFromSdf;

                if (obstacleHit)
                {
                    float hitDistW = obstacleDistSim * SimToWorldScale;
                    _dbgRecallSphereHitPointW = originW + dirW * hitDistW;
                    _dbgRecallSphereHitNormalW = (Vector3)obstacleNormalSim;
                }
                else
                {
                    _dbgRecallSphereHitPointW = Vector3.zero;
                    _dbgRecallSphereHitNormalW = Vector3.up;
                }
            }

            float closestBlockDist = float.MaxValue;
            int closestBlockIdx = -1;
            float3 closestBlockNormal = float3.zero;
            float closestBlockTopY = 0f;
            if (obstacleHit)
            {
                closestBlockDist = obstacleDistSim;
                closestBlockIdx = 0;
                closestBlockNormal = obstacleNormalSim;
                closestBlockTopY = obstacleTopY;

                float3 nSim = closestBlockNormal;
                float3 nSimFlat = nSim;
                nSimFlat.y = 0;
                nSimFlat = math.normalizesafe(nSimFlat);

                float3 vXZ = speed * rawDir;
                vXZ.y = 0;
                float intoWall = math.dot(vXZ, nSimFlat);
                if (intoWall < 0f)
                    vXZ -= intoWall * nSimFlat;

                float hitDistW = closestBlockDist * SimToWorldScale;
                float distRemainW = math.max(0f, hitDistW - keepDistW);
                float denomW = math.max(0.001f, castDistW - keepDistW);
                float slow01Hit = math.saturate(distRemainW / denomW);
                vXZ *= slow01Hit;
                float push01 = 1f - slow01Hit;

                float3 slideVecHit = rawDir - math.dot(rawDir, nSimFlat) * nSimFlat;
                slideVecHit.y = 0;
                if (math.lengthsq(slideVecHit) < 1e-3f)
                    slideVecHit = new float3(-nSimFlat.z, 0f, nSimFlat.x);
                float3 slideDirHit = math.normalizesafe(slideVecHit);
                if (math.lengthsq(slideDirHit) < 1e-6f)
                {
                    float3 perp = new float3(-nSimFlat.z, 0f, nSimFlat.x);
                    slideDirHit = math.normalizesafe(perp);
                }

                vXZ += slideDirHit * (speed * 0.35f * push01);
                baseVel.x = vXZ.x;
                baseVel.z = vXZ.z;
            }

            float checkDist = projectionCheckDist;

            bool dbgHasCurGround = false;
            bool dbgHasAheadGround = false;
            float dbgCurGroundYW = 0f;
            float dbgAheadGroundYW = 0f;
            float dbgGroundDeltaW = 0f;
            bool dbgHasStepTop = false;
            float dbgStepTopYW = 0f;
            float dbgStepTopDeltaW = 0f;
            bool dbgCanStepUpByGround = false;
            bool dbgIsDropByGround = false;
            float dbgProbeFwdW = 0f;
            if (closestBlockIdx >= 0 && recallStepMaxHeightSim > 0f)
            {
                float maxStepWDbg = recallStepMaxHeight;
                float closestBlockDistWDbg = closestBlockDist * SimToWorldScale;
                dbgProbeFwdW = closestBlockDistWDbg + sphereRadiusW + 0.02f;
                // 下台阶时如果 closestBlockDist 很小，probe 点可能仍在上台阶平台上，导致 groundDeltaW=0。
                // 这里强制一个最小前移距离，尽量采样到边缘之后的地面。
                dbgProbeFwdW = math.max(dbgProbeFwdW, (sphereRadiusW + keepDistW) + 0.20f);
                dbgProbeFwdW = math.min(dbgProbeFwdW, castDistW + sphereRadiusW + 0.25f);

                Vector3 curRayOriginW = originW + Vector3.up * 2f;
                long tGround0 = System.Diagnostics.Stopwatch.GetTimestamp();
                perf.RaycastCount++;
                bool hasCurHit = TryRaycastFiltered(curRayOriginW, Vector3.down, 6f, out RaycastHit curGroundHit);
                perf.TicksGround += System.Diagnostics.Stopwatch.GetTimestamp() - tGround0;
                if (hasCurHit)
                {
                    dbgHasCurGround = true;
                    dbgCurGroundYW = curGroundHit.point.y;
                }

                Vector3 aheadPosW = originW + dirW * dbgProbeFwdW;
                Vector3 aheadRayOriginW = aheadPosW + Vector3.up * 2f;
                tGround0 = System.Diagnostics.Stopwatch.GetTimestamp();
                perf.RaycastCount++;
                bool hasAheadHit = TryRaycastFiltered(aheadRayOriginW, Vector3.down, 6f, out RaycastHit aheadGroundHit);
                perf.TicksGround += System.Diagnostics.Stopwatch.GetTimestamp() - tGround0;
                if (hasAheadHit)
                {
                    dbgHasAheadGround = true;
                    dbgAheadGroundYW = aheadGroundHit.point.y;
                }

                if (dbgHasCurGround && dbgHasAheadGround)
                {
                    dbgGroundDeltaW = dbgAheadGroundYW - dbgCurGroundYW;
                    dbgCanStepUpByGround = dbgGroundDeltaW > 0.01f && dbgGroundDeltaW <= maxStepWDbg;
                    dbgIsDropByGround = dbgGroundDeltaW < -0.05f;

                    if (dbgSphereHitFinal)
                    {
                        Vector3 stepTopProbePosW = originW + dirW * (closestBlockDistWDbg + sphereRadiusW + 0.02f);
                        Vector3 stepTopRayOriginW = stepTopProbePosW + Vector3.up * (maxStepWDbg + 0.35f);
                        float stepTopRayLenW = maxStepWDbg + 1.0f;
                        tGround0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        perf.RaycastCount++;
                        bool hasStepTopHit = TryRaycastFiltered(stepTopRayOriginW, Vector3.down, stepTopRayLenW, out RaycastHit stepTopHit);
                        perf.TicksGround += System.Diagnostics.Stopwatch.GetTimestamp() - tGround0;
                        if (hasStepTopHit)
                        {
                            dbgHasStepTop = true;
                            dbgStepTopYW = stepTopHit.point.y;
                            dbgStepTopDeltaW = dbgStepTopYW - dbgCurGroundYW;
                            dbgCanStepUpByGround = dbgStepTopDeltaW > 0.01f && dbgStepTopDeltaW <= maxStepWDbg;
                        }
                    }

                    float triggerAdvanceSimDbg = recallStepTriggerAdvance * WorldToSimScale;
                    float triggerDistWDbg = (radiusXZ + recallObstacleKeepDistanceMarginSim + triggerAdvanceSimDbg) * SimToWorldScale;
                    if (dbgIsDropByGround && closestBlockDistWDbg <= triggerDistWDbg)
                    {
                        float blockTopYW = closestBlockTopY * SimToWorldScale;
                        bool blockTopNotHigherThanGround = blockTopYW <= (dbgCurGroundYW + 0.02f);
                        if (blockTopNotHigherThanGround)
                        {
                            baseVel = new float3(rawDir.x, 0f, rawDir.z) * speed;
                            steering = float3.zero;
                            closestBlockIdx = -1;
                        }
                    }
                }
            }
            
            // 如果没有阻挡，直接返回基础速度
            if (closestBlockIdx < 0)
            {
                if (hasStepJumpTarget && _controllerStepJumpTargetCenterY != null && controllerId > 0 && controllerId < _controllerStepJumpTargetCenterY.Length)
                {
                    float targetY = _controllerStepJumpTargetCenterY[controllerId];
                    float dy = targetY - center.y;
                    if (dy <= 0.01f * WorldToSimScale)
                    {
                        _controllerStepJumpTargetCenterY[controllerId] = float.NaN;
                        if (hasExpireFrame)
                            _controllerStepJumpTargetExpireFrame[controllerId] = 0;
                        hasStepJumpTarget = false;
                    }
                    else
                    {
                        float upV = math.min(maxUpSim, dy / dt);
                        baseVel.y = math.max(baseVel.y, upV);
                    }
                }

                ProjectVelocityNotAwayFromDir(ref baseVel, rawDir);

                perf.TicksTotal += System.Diagnostics.Stopwatch.GetTimestamp() - tTotal0;
                return baseVel;
            }
            
            // === 阻挡处理：叠加 steering 力 ===
            
            // 1. 检查是否是可跨越的台阶
            float groupBottomY = center.y - halfHeight;
            float stepHeight = closestBlockTopY - groupBottomY;
            // 只要障碍物顶部低于主体，就尝试向上
            float stepHeightW = stepHeight * SimToWorldScale;
            bool canStepUp = stepHeight > 0 && stepHeight <= recallStepMaxHeightSim && stepHeightW > 0.03f;
            if (dbgHasCurGround && dbgHasAheadGround)
            {
                if (dbgCanStepUpByGround)
                {
                    stepHeightW = dbgHasStepTop ? dbgStepTopDeltaW : dbgGroundDeltaW;
                    canStepUp = true;
                }
            }

            if (canStepUp)
            {
                if (hasStepJumpTarget && _controllerStepJumpTargetCenterY != null && controllerId > 0 && controllerId < _controllerStepJumpTargetCenterY.Length)
                {
                    float targetY = _controllerStepJumpTargetCenterY[controllerId];
                    float dy = targetY - center.y;
                    if (dy <= 0.01f * WorldToSimScale)
                    {
                        _controllerStepJumpTargetCenterY[controllerId] = float.NaN;
                        if (hasExpireFrame)
                            _controllerStepJumpTargetExpireFrame[controllerId] = 0;
                        hasStepJumpTarget = false;
                    }
                    else
                    {
                        float upV = math.min(maxUpSim, dy / dt);
                        baseVel.y = math.max(baseVel.y, upV);
                    }
                }

                float triggerAdvanceSim = recallStepTriggerAdvance * WorldToSimScale;
                float triggerDist = radiusXZ + recallObstacleKeepDistanceMarginSim + triggerAdvanceSim;
                if (closestBlockDist <= triggerDist)
                {
                    baseVel.x = rawDir.x * speed;
                    baseVel.z = rawDir.z * speed;
                    float clearanceW = math.max(0.02f, sphereRadiusW * 0.5f);
                    float targetRiseW = stepHeightW * (1f + recallHeightCompPercent) + clearanceW;
                    float g = math.abs(UnityEngine.Physics.gravity.y);
                    float jumpVelW = math.sqrt(math.max(0f, 2f * g * targetRiseW)) * math.max(0.2f, recallStepJumpSpeedScale);
                    float jumpVelSim = jumpVelW * WorldToSimScale;
                    baseVel.y = math.max(baseVel.y, math.min(maxUpSim, jumpVelSim));

                    if (controllerId > 0)
                    {
                        if (_controllerStepJumpTargetCenterY != null && controllerId < _controllerStepJumpTargetCenterY.Length)
                        {
                            float clearanceSim = clearanceW * WorldToSimScale;
                            float extraUpSim = math.max(0f, stepHeight * recallHeightCompPercent);
                            float targetCenterYSim = closestBlockTopY + clearanceSim + halfHeight + extraUpSim;
                            float curTarget = _controllerStepJumpTargetCenterY[controllerId];
                            float nextTarget = float.IsNaN(curTarget) ? targetCenterYSim : math.max(curTarget, targetCenterYSim);
                            _controllerStepJumpTargetCenterY[controllerId] = nextTarget;

                            if (_controllerStepJumpTargetExpireFrame == null || _controllerStepJumpTargetExpireFrame.Length <= controllerId)
                            {
                                int oldLen = _controllerStepJumpTargetExpireFrame?.Length ?? 0;
                                int newLen = math.max(controllerId + 1, oldLen * 2 + 1);
                                System.Array.Resize(ref _controllerStepJumpTargetExpireFrame, newLen);
                                for (int i = oldLen; i < newLen; i++)
                                    _controllerStepJumpTargetExpireFrame[i] = 0;
                            }
                            if (_controllerStepJumpTargetExpireFrame != null && controllerId < _controllerStepJumpTargetExpireFrame.Length)
                            {
                                float dySim = nextTarget - center.y;
                                float upPerFrame = math.max(1e-5f, maxUpSim * dt);
                                int framesNeed = (int)math.ceil(math.max(0f, dySim) / upPerFrame);
                                framesNeed = math.clamp(framesNeed + 2, 2, 60);
                                int nextExpire = Time.frameCount + framesNeed;
                                int curExpire = _controllerStepJumpTargetExpireFrame[controllerId];
                                _controllerStepJumpTargetExpireFrame[controllerId] = math.max(curExpire, nextExpire);
                            }
                        }
                    }

                    float stepProximityFactor = 1f - math.saturate(closestBlockDist / checkDist);
                    float stepBaseSlow = 1f - stepProximityFactor * 0.75f;
                    stepBaseSlow = math.max(stepBaseSlow, 0.2f);
                    stepBaseSlow = math.saturate(stepBaseSlow);
                    baseVel.x *= stepBaseSlow;
                    baseVel.z *= stepBaseSlow;
                    perf.TicksTotal += System.Diagnostics.Stopwatch.GetTimestamp() - tTotal0;
                    return baseVel;
                }

                baseVel.x = rawDir.x * speed;
                baseVel.z = rawDir.z * speed;
                if (!hasStepJumpTarget)
                    baseVel.y = 0f;
                perf.TicksTotal += System.Diagnostics.Stopwatch.GetTimestamp() - tTotal0;
                return baseVel;
            }

            // 2. 水平方向：叠加沿墙滑动的 steering
            float3 slideVec = rawDir - math.dot(rawDir, closestBlockNormal) * closestBlockNormal;
            slideVec.y = 0;
            float3 slideDir = math.normalizesafe(slideVec);
            
            // 如果滑动方向几乎为零（正对墙），尝试绕行
            if (math.lengthsq(slideDir) < 0.1f)
            {
                float3 perpDir = new float3(-closestBlockNormal.z, 0f, closestBlockNormal.x);
                slideDir = math.normalizesafe(perpDir);
                if (math.lengthsq(slideDir) < 1e-6f)
                {
                    float3 alt = new float3(-rawDir.z, 0f, rawDir.x);
                    slideDir = math.normalizesafe(alt);
                }
            }
            
            // 叠加水平 steering（强度与障碍物距离成反比）
            float slideProximityFactor = 1f - math.saturate(closestBlockDist / checkDist);
            float slideBaseSlow = 1f - slideProximityFactor * 0.75f;
            slideBaseSlow = math.saturate(slideBaseSlow);
            baseVel.x *= slideBaseSlow;
            baseVel.z *= slideBaseSlow;
            steering += slideDir * speed * slideProximityFactor * recallSlideWeight;

            float distRemain = math.max(0f, closestBlockDist - (radiusXZ + recallObstacleKeepDistanceMarginSim));
            float slow01Slide = (checkDist > 1e-4f) ? math.saturate(distRemain / checkDist) : 0f;
            baseVel.x *= slow01Slide;
            baseVel.z *= slow01Slide;
            float3 pushDir = closestBlockNormal - math.dot(closestBlockNormal, rawDir) * rawDir;
            pushDir.y = 0;
            pushDir = math.normalizesafe(pushDir);
            if (math.lengthsq(pushDir) < 1e-6f)
                pushDir = slideDir;
            steering += pushDir * (speed * 0.35f * (1f - slow01Slide));
            
            // 最终速度 = 基础速度 + steering
            float3 resultVel = baseVel + steering;

            ProjectVelocityNotAwayFromDir(ref resultVel, rawDir);

            perf.TicksTotal += System.Diagnostics.Stopwatch.GetTimestamp() - tTotal0;
            return resultVel;
        }

        private const float RecallWallNormalYThreshold = 0.65f;
        private const int RecallGlobalFlowCostUnreachable = 0x3fffffff;
        private int _recallGlobalFlowGridSize;
        private float _recallGlobalFlowCellSizeSim;
        private float _recallGlobalFlowHalfExtentSim;
        private float3 _recallGlobalFlowCenterSim;
        private int[] _recallGlobalFlowCost;
        private byte[] _recallGlobalFlowBlocked;
        private int[] _recallGlobalFlowQueue;
        private int _recallGlobalFlowLastUpdateFrame = int.MinValue;

        private void EnsureRecallGlobalFlowBuffers(int gridSize)
        {
            if (gridSize <= 0)
                return;

            int n = gridSize * gridSize;
            if (_recallGlobalFlowCost == null || _recallGlobalFlowCost.Length != n)
            {
                _recallGlobalFlowCost = new int[n];
                _recallGlobalFlowBlocked = new byte[n];
                _recallGlobalFlowQueue = new int[n];
            }
            _recallGlobalFlowGridSize = gridSize;
        }

        private void UpdateRecallGlobalFlowField(float3 mainCenterSim)
        {
            if (!(useWorldStaticSdf && _worldStaticSdf.IsCreated && _worldStaticSdf.Data.IsCreated))
            {
                return;
            }

            int interval = math.max(1, recallGlobalFlowUpdateIntervalFrames);
            long dtFrame = (long)Time.frameCount - (long)_recallGlobalFlowLastUpdateFrame;
            if (dtFrame < interval)
            {
                return;
            }

            float rangeW = math.max(0.5f, recallGlobalFlowRange);
            float cellW = math.max(0.1f, recallGlobalFlowCellSize);
            int gridSize = (int)math.ceil((rangeW * 2f) / cellW) + 1;
            if ((gridSize & 1) == 0)
                gridSize += 1;
            gridSize = math.clamp(gridSize, 9, 201);

            EnsureRecallGlobalFlowBuffers(gridSize);

            _recallGlobalFlowCenterSim = mainCenterSim;
            _recallGlobalFlowCellSizeSim = cellW * WorldToSimScale;
            _recallGlobalFlowHalfExtentSim = rangeW * WorldToSimScale;

            var v = _worldStaticSdf.Data;
            float blockedDistSim = recallGlobalFlowBlockedDistance * WorldToSimScale;
            float yOffSim = recallGlobalFlowSampleHalfHeight * WorldToSimScale;
            int half = gridSize >> 1;

            bool IsBlockedAt(float3 sp)
            {
                float d = v.SampleDistance(sp);
                if (d >= (v.MaxDistanceSim - 1e-5f))
                    return false;
                if (d > blockedDistSim)
                    return false;
                float3 n = v.SampleNormalForward(sp, d);
                return math.abs(n.y) < RecallWallNormalYThreshold;
            }

            for (int z = 0; z < gridSize; z++)
            {
                float oz = (z - half) * _recallGlobalFlowCellSizeSim;
                for (int x = 0; x < gridSize; x++)
                {
                    float ox = (x - half) * _recallGlobalFlowCellSizeSim;
                    float3 p = new float3(mainCenterSim.x + ox, mainCenterSim.y, mainCenterSim.z + oz);
                    float3 p1 = p + new float3(0f, yOffSim, 0f);
                    float3 p2 = p - new float3(0f, yOffSim, 0f);
                    int idx = z * gridSize + x;
                    bool blocked = IsBlockedAt(p) || IsBlockedAt(p1) || IsBlockedAt(p2);
                    _recallGlobalFlowBlocked[idx] = (byte)(blocked ? 1 : 0);
                }
            }

            int goalIdx = half * gridSize + half;
            _recallGlobalFlowBlocked[goalIdx] = 0;

            int nAll = gridSize * gridSize;
            for (int i = 0; i < nAll; i++)
                _recallGlobalFlowCost[i] = RecallGlobalFlowCostUnreachable;

            int qh = 0;
            int qt = 0;
            _recallGlobalFlowCost[goalIdx] = 0;
            _recallGlobalFlowQueue[qt++] = goalIdx;

            while (qh < qt)
            {
                int cur = _recallGlobalFlowQueue[qh++];
                int curCost = _recallGlobalFlowCost[cur];
                int cx = cur % gridSize;
                int cz = cur / gridSize;

                if (cx > 0)
                {
                    int nidx = cz * gridSize + (cx - 1);
                    if (_recallGlobalFlowBlocked[nidx] == 0)
                    {
                        int nextCost = curCost + 1;
                        if (_recallGlobalFlowCost[nidx] > nextCost)
                        {
                            _recallGlobalFlowCost[nidx] = nextCost;
                            _recallGlobalFlowQueue[qt++] = nidx;
                        }
                    }
                }
                if (cx + 1 < gridSize)
                {
                    int nidx = cz * gridSize + (cx + 1);
                    if (_recallGlobalFlowBlocked[nidx] == 0)
                    {
                        int nextCost = curCost + 1;
                        if (_recallGlobalFlowCost[nidx] > nextCost)
                        {
                            _recallGlobalFlowCost[nidx] = nextCost;
                            _recallGlobalFlowQueue[qt++] = nidx;
                        }
                    }
                }
                if (cz > 0)
                {
                    int nidx = (cz - 1) * gridSize + cx;
                    if (_recallGlobalFlowBlocked[nidx] == 0)
                    {
                        int nextCost = curCost + 1;
                        if (_recallGlobalFlowCost[nidx] > nextCost)
                        {
                            _recallGlobalFlowCost[nidx] = nextCost;
                            _recallGlobalFlowQueue[qt++] = nidx;
                        }
                    }
                }
                if (cz + 1 < gridSize)
                {
                    int nidx = (cz + 1) * gridSize + cx;
                    if (_recallGlobalFlowBlocked[nidx] == 0)
                    {
                        int nextCost = curCost + 1;
                        if (_recallGlobalFlowCost[nidx] > nextCost)
                        {
                            _recallGlobalFlowCost[nidx] = nextCost;
                            _recallGlobalFlowQueue[qt++] = nidx;
                        }
                    }
                }
            }

            _recallGlobalFlowLastUpdateFrame = Time.frameCount;
        }

        private float3 GetRecallGlobalFlowDirection(float3 centerSim, float3 fallbackDirSim)
        {
            if (!recallUseGlobalFlowField)
                return fallbackDirSim;
            if (_recallGlobalFlowCost == null || _recallGlobalFlowBlocked == null || _recallGlobalFlowGridSize <= 0)
                return fallbackDirSim;

            float3 rel = centerSim - _recallGlobalFlowCenterSim;
            if (math.abs(rel.x) > _recallGlobalFlowHalfExtentSim || math.abs(rel.z) > _recallGlobalFlowHalfExtentSim)
            {
                return fallbackDirSim;
            }

            int size = _recallGlobalFlowGridSize;
            int half = size >> 1;
            float fx = rel.x / math.max(1e-5f, _recallGlobalFlowCellSizeSim) + half;
            float fz = rel.z / math.max(1e-5f, _recallGlobalFlowCellSizeSim) + half;

            int ix = (int)math.floor(fx);
            int iz = (int)math.floor(fz);
            ix = math.clamp(ix, 1, size - 2);
            iz = math.clamp(iz, 1, size - 2);

            int idxC = iz * size + ix;
            int cC = _recallGlobalFlowCost[idxC];
            if (cC >= RecallGlobalFlowCostUnreachable)
            {
                int bestCost = RecallGlobalFlowCostUnreachable;
                float bestDot = -2f;
                int bestDx = 0;
                int bestDz = 0;
                float2 fb2 = math.normalizesafe(new float2(fallbackDirSim.x, fallbackDirSim.z));
                const int searchRadius = 6;
                for (int r = 1; r <= searchRadius; r++)
                {
                    for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx == 0 && dz == 0)
                            continue;
                        int nx = ix + dx;
                        int nz = iz + dz;
                        if (nx < 1 || nx > size - 2 || nz < 1 || nz > size - 2)
                            continue;
                        int cN = _recallGlobalFlowCost[nz * size + nx];
                        if (cN >= RecallGlobalFlowCostUnreachable)
                            continue;

                        float2 d2 = math.normalizesafe(new float2(dx, dz));
                        float dot = d2.x * fb2.x + d2.y * fb2.y;
                        if (cN < bestCost || (cN == bestCost && dot > bestDot))
                        {
                            bestCost = cN;
                            bestDot = dot;
                            bestDx = dx;
                            bestDz = dz;
                        }
                    }

                    if (bestCost < RecallGlobalFlowCostUnreachable)
                        break;
                }

                if (bestCost < RecallGlobalFlowCostUnreachable)
                {
                    float2 dir2Unr = math.normalizesafe(new float2(bestDx, bestDz));
                    float3 dir3Unr = new float3(dir2Unr.x, 0f, dir2Unr.y);
                    if (math.lengthsq(dir3Unr) > 1e-6f)
                        return dir3Unr;
                }
                return fallbackDirSim;
            }

            int cXp = _recallGlobalFlowCost[iz * size + (ix + 1)];
            int cXm = _recallGlobalFlowCost[iz * size + (ix - 1)];
            int cZp = _recallGlobalFlowCost[(iz + 1) * size + ix];
            int cZm = _recallGlobalFlowCost[(iz - 1) * size + ix];

            if (cXp >= RecallGlobalFlowCostUnreachable) cXp = cC;
            if (cXm >= RecallGlobalFlowCostUnreachable) cXm = cC;
            if (cZp >= RecallGlobalFlowCostUnreachable) cZp = cC;
            if (cZm >= RecallGlobalFlowCostUnreachable) cZm = cC;

            float gx = (float)(cXp - cXm);
            float gz = (float)(cZp - cZm);
            float2 g = new float2(-gx, -gz);
            if (math.lengthsq(g) < 1e-6f)
            {
                int bestCost = cC;
                float bestDot = -2f;
                int bestDx = 0;
                int bestDz = 0;

                float2 fb2 = math.normalizesafe(new float2(fallbackDirSim.x, fallbackDirSim.z));

                float dotXp = fb2.x;
                float dotXm = -fb2.x;
                float dotZp = fb2.y;
                float dotZm = -fb2.y;

                if (cXp < bestCost || (cXp == bestCost && cXp < cC && dotXp > bestDot))
                {
                    bestCost = cXp;
                    bestDot = dotXp;
                    bestDx = 1;
                    bestDz = 0;
                }
                if (cXm < bestCost || (cXm == bestCost && cXm < cC && dotXm > bestDot))
                {
                    bestCost = cXm;
                    bestDot = dotXm;
                    bestDx = -1;
                    bestDz = 0;
                }
                if (cZp < bestCost || (cZp == bestCost && cZp < cC && dotZp > bestDot))
                {
                    bestCost = cZp;
                    bestDot = dotZp;
                    bestDx = 0;
                    bestDz = 1;
                }
                if (cZm < bestCost || (cZm == bestCost && cZm < cC && dotZm > bestDot))
                {
                    bestCost = cZm;
                    bestDot = dotZm;
                    bestDx = 0;
                    bestDz = -1;
                }

                if (bestCost < cC)
                {
                    float2 dir2Zg = math.normalizesafe(new float2(bestDx, bestDz));
                    float3 dir3Zg = new float3(dir2Zg.x, 0f, dir2Zg.y);
                    if (math.lengthsq(dir3Zg) > 1e-6f)
                        return dir3Zg;
                }
                return fallbackDirSim;
            }

            float2 dir2 = math.normalizesafe(g);
            float3 dir3 = new float3(dir2.x, 0f, dir2.y);
            if (math.lengthsq(dir3) < 1e-6f)
                return fallbackDirSim;
            return dir3;
        }
        
        #endregion
        
        #region 配置管理
        
        /// <summary>
        /// 重置所有参数为默认值
        /// </summary>
        [ContextMenu("重置参数为默认值")]
        public void ResetToDefaults()
        {
            ConfigResetHelper.ResetToDefaults(this);
        }
        
        #endregion
        
        #region 网格投票（CCA ID稳定）
        
        // 临时字典用于投票统计（避免每帧分配）
        private Dictionary<int, Dictionary<int, int>> _voteDict;  // newCompId -> (oldStableId -> count)
        private Dictionary<int, int> _compTotalCells;              // newCompId -> totalCells
        
        // CCA组件ID -> 稳定ID 映射（每帧更新，供Control()使用）
        private int[] _compToBlobId;
        // 稳定ID -> 控制器索引 映射（跨帧持久，用于控制器绑定）
        private Dictionary<int, int> _blobIdToControllerSlotMap;
        // 稳定ID -> 组件中心位置 映射（用于位置匹配）
        private Dictionary<int, float3> _blobIdToCenter;
        private HashSet<int> _tmpStableIdSet;
        private List<int> _tmpStableIdList;
        private HashSet<int> _tmpControllerIdSet;

        private float3[] _stableResolveCenters;
        private int[] _stableResolveCellCounts;
        private int[] _stableResolveAssignedStable;
        private List<int> _stableResolveAllStableIds;
        private List<float3> _stableResolveAllStableCenters;
        private List<int> _stableResolveCandComp;
        private List<int> _stableResolveCandStable;
        private List<float> _stableResolveCandDist2;
        private List<int> _stableResolveOrder;
        private Dictionary<int, int> _stableResolveStableAssignedToComp;
        private CandidateDistComparer _stableResolveCandComparer;

        private sealed class CandidateDistComparer : System.Collections.Generic.IComparer<int>
        {
            public List<float> Dist2;
            public int Compare(int a, int b)
            {
                return Dist2[a].CompareTo(Dist2[b]);
            }
        }

        private int GetOrCreateControllerSlotForBlobId(int blobId)
        {
            if (_blobIdToControllerSlotMap == null)
                _blobIdToControllerSlotMap = new Dictionary<int, int>(16);
            if (_freeSeparatedControllerIds == null)
                _freeSeparatedControllerIds = new Stack<int>();

            if (_blobIdToControllerSlotMap.TryGetValue(blobId, out int existingId) && existingId > 0 && existingId < _controllerBuffer.Length)
                return existingId;

            int newId;
            // 召回期间禁止复用 controllerId，避免新团块复用到召回快照中的 eligible id
            if (!_connect && _freeSeparatedControllerIds.Count > 0)
                newId = _freeSeparatedControllerIds.Pop();
            else
            {
                newId = _controllerBuffer.Length;
                _controllerBuffer.Add(default);
            }

            if (_controllerStepJumpTargetCenterY == null || _controllerStepJumpTargetCenterY.Length <= newId)
            {
                int oldLen = _controllerStepJumpTargetCenterY?.Length ?? 0;
                int newLen = math.max(newId + 1, oldLen * 2 + 1);
                System.Array.Resize(ref _controllerStepJumpTargetCenterY, newLen);
                for (int i = oldLen; i < newLen; i++)
                    _controllerStepJumpTargetCenterY[i] = float.NaN;
            }
            if (newId > 0 && newId < _controllerStepJumpTargetCenterY.Length)
                _controllerStepJumpTargetCenterY[newId] = float.NaN;

            if (_controllerStepJumpTargetExpireFrame == null || _controllerStepJumpTargetExpireFrame.Length <= newId)
            {
                int oldLen = _controllerStepJumpTargetExpireFrame?.Length ?? 0;
                int newLen = math.max(newId + 1, oldLen * 2 + 1);
                System.Array.Resize(ref _controllerStepJumpTargetExpireFrame, newLen);
                for (int i = oldLen; i < newLen; i++)
                    _controllerStepJumpTargetExpireFrame[i] = 0;
            }
            if (newId > 0 && newId < _controllerStepJumpTargetExpireFrame.Length)
                _controllerStepJumpTargetExpireFrame[newId] = 0;

            _blobIdToControllerSlotMap[blobId] = newId;
            return newId;
        }

        private void RefreshBlobIdToControllerSlotMapping()
        {
            int required = math.max(1, _nextBlobId);

            if (_connect && _recallEligibleBlobIds.IsCreated && _recallEligibleBlobIds.Length < required)
            {
                var old = _recallEligibleBlobIds;
                _recallEligibleBlobIds = new NativeArray<byte>(required, Allocator.Persistent);
                for (int i = 0; i < _recallEligibleBlobIds.Length; i++)
                    _recallEligibleBlobIds[i] = (i < old.Length) ? old[i] : (byte)0;
                old.Dispose();
            }

            if (!_blobIdToControllerSlot.IsCreated || _blobIdToControllerSlot.Length < required)
            {
                if (_blobIdToControllerSlot.IsCreated)
                    _blobIdToControllerSlot.Dispose();
                _blobIdToControllerSlot = new NativeArray<int>(required, Allocator.Persistent);
            }

            for (int i = 0; i < _blobIdToControllerSlot.Length; i++)
                _blobIdToControllerSlot[i] = -1;
            _blobIdToControllerSlot[0] = 0;

            if (_blobIdToControllerSlotMap == null)
                return;

            foreach (var kv in _blobIdToControllerSlotMap)
            {
                int blobId = kv.Key;
                int slot = kv.Value;
                if (blobId <= 0 || blobId >= _blobIdToControllerSlot.Length)
                    continue;
                if (slot <= 0 || !_controllerBuffer.IsCreated || slot >= _controllerBuffer.Length)
                    continue;
                _blobIdToControllerSlot[blobId] = slot;
            }
        }
        
        /// <summary>
        /// 解析稳定ID：通过组件位置匹配让CCA组件ID跨帧稳定
        /// 优化版本：不遍历网格，直接用组件中心位置匹配
        /// </summary>
        private void ResolveBlobIds()
        {
            int compCount = _componentsBuffer.Length;
            if (compCount == 0)
            {
                return; // 无组件，跳过
            }
            
            // 确保_compToStableId数组足够大
            if (_compToBlobId == null || _compToBlobId.Length < compCount)
                _compToBlobId = new int[math.max(32, compCount)];
            
            // 初始化稳定ID->组件中心映射（首次）
            if (_blobIdToCenter == null)
                _blobIdToCenter = new Dictionary<int, float3>(16);
            
            if (_tmpStableIdSet == null)
                _tmpStableIdSet = new HashSet<int>();
            _tmpStableIdSet.Clear();
            _tmpStableIdSet.Add(0);
            
            float matchThreshold = 20f * PBF_Utils.CellSize;
            float matchThreshold2 = matchThreshold * matchThreshold;
            
            // 调试日志：输出组件数量和匹配情况
            int newIdCount = 0;
            int inheritedCount = 0;
            
            // 使用全局最小距离匹配，避免按遍历顺序导致的 stableId“抢占”误分配
            if (_stableResolveCenters == null || _stableResolveCenters.Length < compCount)
                _stableResolveCenters = new float3[math.max(32, compCount)];
            if (_stableResolveCellCounts == null || _stableResolveCellCounts.Length < compCount)
                _stableResolveCellCounts = new int[math.max(32, compCount)];
            for (int c = 0; c < compCount; c++)
            {
                var comp = _componentsBuffer[c];
                _stableResolveCenters[c] = minPos + comp.Center * PBF_Utils.CellSize;
                _stableResolveCellCounts[c] = comp.CellCount;
            }

            if (_stableResolveAllStableIds == null)
                _stableResolveAllStableIds = new List<int>(math.max(16, _blobIdToCenter.Count));
            if (_stableResolveAllStableCenters == null)
                _stableResolveAllStableCenters = new List<float3>(math.max(16, _blobIdToCenter.Count));
            _stableResolveAllStableIds.Clear();
            _stableResolveAllStableCenters.Clear();
            if (_stableResolveAllStableIds.Capacity < _blobIdToCenter.Count)
                _stableResolveAllStableIds.Capacity = _blobIdToCenter.Count;
            if (_stableResolveAllStableCenters.Capacity < _blobIdToCenter.Count)
                _stableResolveAllStableCenters.Capacity = _blobIdToCenter.Count;
            foreach (var kv in _blobIdToCenter)
            {
                if (kv.Key == 0)
                    continue;
                _stableResolveAllStableIds.Add(kv.Key);
                _stableResolveAllStableCenters.Add(kv.Value);
            }

            // 候选边：comp -> stableId, dist2
            if (_stableResolveCandComp == null)
                _stableResolveCandComp = new List<int>(compCount * 2);
            if (_stableResolveCandStable == null)
                _stableResolveCandStable = new List<int>(compCount * 2);
            if (_stableResolveCandDist2 == null)
                _stableResolveCandDist2 = new List<float>(compCount * 2);
            _stableResolveCandComp.Clear();
            _stableResolveCandStable.Clear();
            _stableResolveCandDist2.Clear();
            int expectCand = compCount * 2;
            if (_stableResolveCandComp.Capacity < expectCand)
                _stableResolveCandComp.Capacity = expectCand;
            if (_stableResolveCandStable.Capacity < expectCand)
                _stableResolveCandStable.Capacity = expectCand;
            if (_stableResolveCandDist2.Capacity < expectCand)
                _stableResolveCandDist2.Capacity = expectCand;
            for (int c = 0; c < compCount; c++)
            {
                float3 center = _stableResolveCenters[c];
                for (int s = 0; s < _stableResolveAllStableIds.Count; s++)
                {
                    float dist2 = math.lengthsq(center - _stableResolveAllStableCenters[s]);
                    if (dist2 < matchThreshold2)
                    {
                        _stableResolveCandComp.Add(c);
                        _stableResolveCandStable.Add(_stableResolveAllStableIds[s]);
                        _stableResolveCandDist2.Add(dist2);
                    }
                }
            }

            // 对候选按距离从近到远排序（索引排序，避免引入新类型）
            if (_stableResolveOrder == null)
                _stableResolveOrder = new List<int>(_stableResolveCandDist2.Count);
            _stableResolveOrder.Clear();
            if (_stableResolveOrder.Capacity < _stableResolveCandDist2.Count)
                _stableResolveOrder.Capacity = _stableResolveCandDist2.Count;
            for (int i = 0; i < _stableResolveCandDist2.Count; i++)
                _stableResolveOrder.Add(i);
            if (_stableResolveCandComparer == null)
                _stableResolveCandComparer = new CandidateDistComparer();
            _stableResolveCandComparer.Dist2 = _stableResolveCandDist2;
            _stableResolveOrder.Sort(_stableResolveCandComparer);

            if (_stableResolveAssignedStable == null || _stableResolveAssignedStable.Length < compCount)
                _stableResolveAssignedStable = new int[math.max(32, compCount)];
            System.Array.Clear(_stableResolveAssignedStable, 0, compCount);
            if (_stableResolveStableAssignedToComp == null)
                _stableResolveStableAssignedToComp = new Dictionary<int, int>(math.max(16, _stableResolveAllStableIds.Count));
            else
                _stableResolveStableAssignedToComp.Clear();

            // 先按最小距离贪心匹配（全局），避免“遍历顺序抢占”
            for (int oi = 0; oi < _stableResolveOrder.Count; oi++)
            {
                int idx = _stableResolveOrder[oi];
                int c = _stableResolveCandComp[idx];
                int sid = _stableResolveCandStable[idx];
                if (_stableResolveAssignedStable[c] != 0)
                    continue;

                // 召回期间：eligible 的稳定ID一旦缺席过（>0帧），就不允许再被新组件继承。
                // 这样“召回快照”不会被后续发射出来的新团块通过 stableId 复用而扩大作用范围。
                if (_connect && _recallEligibleBlobAbsentFrames != null &&
                    sid > 0 && sid < _recallEligibleBlobAbsentFrames.Length &&
                    _recallEligibleBlobAbsentFrames[sid] > 0)
                {
                    continue;
                }
                if (_tmpStableIdSet.Contains(sid))
                    continue;
                _stableResolveAssignedStable[c] = sid;
                _tmpStableIdSet.Add(sid);
                _stableResolveStableAssignedToComp[sid] = c;
            }

            // 第二遍：为每个组件落定 stableId（未匹配到则新分配）
            for (int c = 0; c < compCount; c++)
            {
                int finalBlobId;
                if (_stableResolveAssignedStable[c] != 0)
                {
                    finalBlobId = _stableResolveAssignedStable[c];
                    inheritedCount++;
                }
                else
                {
                    finalBlobId = _nextBlobId++;
                    newIdCount++;
                    _tmpStableIdSet.Add(finalBlobId);
                }

                _compToBlobId[c] = finalBlobId;
                _blobIdToCenter[finalBlobId] = _stableResolveCenters[c];
            }

            // 召回期间：更新 eligible blobId 的缺席帧数。
            // - 若该 id 本帧被匹配到组件：缺席=0
            // - 若该 id 本帧未匹配到组件：缺席++（一旦>0，将被上面的规则阻止再次被新组件继承）
            if (_connect && _recallEligibleBlobAbsentFrames != null && _recallEligibleBlobIds.IsCreated)
            {
                int max = math.min(_recallEligibleBlobAbsentFrames.Length, _recallEligibleBlobIds.Length);
                for (int sid = 1; sid < max; sid++)
                {
                    if (_recallEligibleBlobAbsentFrames[sid] < 0)
                        continue; // 非召回快照 eligible id

                    bool assignedThisFrame = _stableResolveStableAssignedToComp != null && _stableResolveStableAssignedToComp.ContainsKey(sid);
                    if (assignedThisFrame)
                        _recallEligibleBlobAbsentFrames[sid] = 0;
                    else
                        _recallEligibleBlobAbsentFrames[sid] = _recallEligibleBlobAbsentFrames[sid] + 1;
                }
            }
            
            // 清理不再使用的稳定ID（超过32个时）
            if (_blobIdToCenter.Count > 32)
            {
                if (_tmpStableIdList == null)
                    _tmpStableIdList = new List<int>(32);
                _tmpStableIdList.Clear();
                foreach (var id in _blobIdToCenter.Keys)
                {
                    if (!_tmpStableIdSet.Contains(id))
                        _tmpStableIdList.Add(id);
                }
                foreach (var id in _tmpStableIdList)
                    _blobIdToCenter.Remove(id);
            }
        }
        
        /// <summary>
        /// 获取CCA组件对应的稳定ID
        /// </summary>
        /// <param name="compIdx">CCA组件索引（从0开始）</param>
        /// <returns>稳定ID（0=主体，1+=分离组）</returns>
        public int GetBlobIdForComponent(int compIdx)
        {
            if (_compToBlobId == null || compIdx < 0 || compIdx >= _compToBlobId.Length)
                return 0;
            return _compToBlobId[compIdx];
        }
        
        #endregion
    }
}
