using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆PBF模拟器 - 基于Position Based Fluids的史莱姆物理模拟
    /// </summary>
    public class Slime_PBF : MonoBehaviour
    {
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
            public int ControllerID;
        }
        
        #region 【核心物理参数】
        
        [ChineseHeader("核心物理参数")]
        [ChineseLabel("粘性强度"), Tooltip("控制粒子之间的粘性力，值越大史莱姆越粘稠")]
        [SerializeField, Range(0, 100), DefaultValue(10f)] 
        private float viscosityStrength = 10f;
        
        [ChineseLabel("聚集强度"), Tooltip("控制粒子向中心聚集的力度，值越大史莱姆越紧密")]
        [SerializeField, Range(0.1f, 100), DefaultValue(30f)] 
        private float concentration = 30f;
        
        [ChineseLabel("基准速度(世界)"), Tooltip("当前参数对应的基准速度，高于此速度时动态增强聚集力")]
        [SerializeField, Range(0.5f, 5f), DefaultValue(1f)]
        private float baseSpeed = 1f;
        
        [ChineseLabel("水平形变上限(世界)"), Tooltip("XZ方向粒子距离中心的最大距离，0=禁用")]
        [SerializeField, Range(0f, 2f), DefaultValue(1f)]
        private float maxDeformDistXZ = 1f;
        
        [ChineseLabel("垂直形变上限(世界)"), Tooltip("Y方向粒子距离中心的最大距离，通常比水平更小以防止落地散开")]
        [SerializeField, Range(0f, 5f), DefaultValue(4f)]
        private float maxDeformDistY = 4f;
        
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
        private bool useAnisotropic = true;
        
        #endregion
        
        #region 【模拟参数】
        
        [ChineseHeader("模拟参数")]
        [ChineseLabel("时间步长"), Tooltip("物理模拟的时间步长")]
        [SerializeField, Range(0.01f, 0.05f), DefaultValue(0.02f)]
        private float deltaTime = 0.02f;
        
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
        
        [ChineseLabel("速度阈值"), Tooltip("超过此速度的部分用刚体补偿，低于此速度用流体形变")]
        [SerializeField, Range(0.5f, 5.0f), DefaultValue(1.8f)]
        private float manualThreshold = 1.8f;
        
        [ChineseLabel("垂直阈值"), Tooltip("垂直方向速度超过此值才触发刚体补偿（世界坐标）")]
        [SerializeField, Range(1.0f, 10.0f), DefaultValue(4.5f)]
        private float verticalThreshold = 4.5f;
        
        [ChineseLabel("脸部高度系数"), Tooltip("脸/眼睛在史莱姆表面的垂直位置，相对于半径的比例")]
        [SerializeField, Range(0f, 0.9f), DefaultValue(0.2f)]
        private float faceHeightFactor = 0.2f;
        
        [ChineseLabel("垂直偏移"), Tooltip("控制中心的垂直偏移系数，让史莱姆更立体")]
        [SerializeField, Range(0f, 0.5f), DefaultValue(0.05f)]
        private float verticalOffset = 0.05f;
        
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
        [SerializeField, Range(0.05f, 1f), DefaultValue(0.15f)]
        private float emitCooldown = 0.15f;
        
        [ChineseLabel("发射批量"), Tooltip("单次发射的粒子数量")]
        [SerializeField, Range(1, 200), DefaultValue(50)]
        private int emitBatchSize = 50;
        
        /// <summary>发射冷却时间（秒）</summary>
        public float EmitCooldown => emitCooldown;
        
        /// <summary>单次发射粒子数量</summary>
        public int EmitBatchSize => emitBatchSize;
        
        #endregion
        
        #region 【控制器参数】
        
        [ChineseHeader("控制器参数")]
        [ChineseLabel("核心范围"), Tooltip("此范围内的粒子被认为是主体")]
        [SerializeField, Range(5f, 30f), DefaultValue(15f)]
        private float coreRadius = 15f;
        
        #endregion
        
        #region 【粒子池扩展】
        
        [ChineseHeader("粒子池扩展（B2架构）")]
        [ChineseLabel("最大粒子数"), Tooltip("预分配的最大粒子池容量，最小16384以支持水珠固定分区")]
        [SerializeField, Range(16384, 32768), DefaultValue(16384)]  // 最小值改为16384
        private int maxParticles = 16384;  // 必须≥16384，水珠使用固定分区[8192-16383]
        
        [ChineseLabel("活跃粒子数"), Tooltip("当前参与模拟的粒子数（只读）")]
        [SerializeField]
        private int activeParticles = 800;
        
        #endregion
        
        #region 【召回参数】
        
        [ChineseHeader("召回参数")]
        [ChineseLabel("召回最大速度"), Tooltip("远距离时的召回速度")]
        [SerializeField, Range(10f, 40f), DefaultValue(20f)]
        private float recallMaxSpeed = 20f;
        
        [ChineseLabel("召回最小速度"), Tooltip("靠近主体时的召回速度")]
        [SerializeField, Range(1f, 10f), DefaultValue(2f)]
        private float recallMinSpeed = 2f;
        
        [ChineseLabel("减速开始距离"), Tooltip("开始从最大速度减速到最小速度的距离")]
        [SerializeField, Range(10f, 60f), DefaultValue(30f)]
        private float recallSlowdownDist = 30f;
        
        [ChineseHeader("召回避障")]
        [ChineseLabel("启用避障"), Tooltip("召回时自动绕开障碍物")]
        [SerializeField, DefaultValue(true)]
        private bool recallAvoidance = true;
        
        [ChineseLabel("台阶最大高度"), Tooltip("可自动跨越的台阶高度（模拟坐标）")]
        [SerializeField, Range(1f, 10f), DefaultValue(5f)]
        private float recallStepMaxHeight = 5f;
        
        [ChineseLabel("台阶上抬速度"), Tooltip("跨越台阶时的向上速度")]
        [SerializeField, Range(1f, 10f), DefaultValue(4f)]
        private float recallStepUpSpeed = 4f;
        
        [ChineseLabel("贴墙滑动权重"), Tooltip("沿障碍物表面滑动的权重")]
        [SerializeField, Range(0f, 1f), DefaultValue(0.8f)]
        private float recallSlideWeight = 0.8f;
        
        [ChineseLabel("障碍检测距离"), Tooltip("前方检测障碍物的距离（模拟坐标）")]
        [SerializeField, Range(1f, 10f), DefaultValue(3f)]
        private float recallObstacleCheckDist = 3f;

        [ChineseLabel("投影保持距离边距"), Tooltip("召回被墙阻挡时，目标点在墙前额外保持的距离（模拟坐标）")]
        [SerializeField, Range(0f, 2f), DefaultValue(0.2f)]
        private float recallObstacleKeepDistanceMargin = 0.2f;
        
        [ChineseLabel("高度差补偿系数"), Tooltip("主体比分离组高时，额外向上力 = 高度差 × 此系数")]
        [SerializeField, Range(0.1f, 3f), DefaultValue(1f)]
        private float recallHeightCompensation = 1f;
        
        #endregion
        
        #region 【CCA控制器参数】
        
        [ChineseHeader("CCA控制器参数")]
        [ChineseLabel("主体半径扩展系数"), Tooltip("主体实际半径 = 最大粒子距离 × 此系数")]
        [SerializeField, Range(1.0f, 2.0f), DefaultValue(1.1f)]
        private float mainRadiusScale = 1.1f;
        
        [ChineseLabel("分离组件半径系数"), Tooltip("分离组件的半径计算系数")]
        [SerializeField, Range(0.3f, 1.0f), DefaultValue(0.6f)]
        private float separatedRadiusScale = 0.6f;
        
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
        
        // 启动耗时调试用
        private double startupT0;
        
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
        
        [ChineseLabel("粒子材质"), Tooltip("Particles模式下粒子的渲染材质")]
        [SerializeField] private Material particleMat;
        
        [ChineseLabel("气泡材质"), Tooltip("史莱姆内部气泡效果的渲染材质")]
        [SerializeField] private Material bubblesMat;
        
        #endregion
        
        #region 【渲染设置】
        
        [ChineseHeader("渲染设置")]
        [Tooltip("控制目标 - 史莱姆跟随的Transform")]
        public Transform trans;
        
        
        [Tooltip("体积组件 - 管理史莱姆资源状态")]
        [SerializeField] private SlimeVolume _slimeVolume;
        
        [Tooltip("渲染模式 - Particles显示粒子，Surface显示表面")]
        public RenderMode renderMode = RenderMode.Surface;
        
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
        
        #endregion
        
        #region 【碰撞体采集设置】
        
        [ChineseHeader("碰撞体采集设置（近场缓存）")]
        [ChineseLabel("碰撞体Layer筛选"), Tooltip("只采集指定Layer的碰撞器")]
        [SerializeField]
        private LayerMask colliderLayers = ~0;  // 默认所有Layer
        
        [ChineseLabel("最大碰撞体数"), Tooltip("近场缓存的最大碰撞体容量，越大开销越高")]
        [SerializeField, Range(16, 128), DefaultValue(64)]
        private int maxNearbyColliders = 64;
        
        [ChineseLabel("查询半径"), Tooltip("以史莱姆为中心查询碰撞体的半径（世界坐标）")]
        [SerializeField, Range(5f, 50f), DefaultValue(20f)]
        private float colliderQueryRadius = 20f;
        
        [ChineseLabel("刷新间隔帧数"), Tooltip("每N帧刷新一次近场碰撞体，值越小越及时但开销越高")]
        [SerializeField, Range(1, 10), DefaultValue(3)]
        private int colliderRefreshInterval = 3;
        
        #endregion
        
        #region 【调试设置】
        
        [ChineseHeader("调试设置")]
        [Tooltip("显示网格调试信息")]
        public bool gridDebug;
        
        [Tooltip("显示组件调试信息（史莱姆分离检测）")]
        public bool componentDebug;

        [Tooltip("输出碰撞体采集调试信息（会产生大量日志，影响启动性能）")]
        public bool colliderCollectDebug;
        
        [Tooltip("显示CCA组件边界盒")]
        public bool ccaDebug;

        [Tooltip("输出启动分段耗时（一次性），用于定位点击Play卡顿原因")]
        public bool startupProfileDebug;
        
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
        
        // PBF物理系统（封装了_lut, _hashes, _posPredict, _lambdaBuffer等缓冲区）
        private Physics.PBFSystem _pbfSystem;
        
        // 渲染专用缓冲区（独立于物理）
        private NativeHashMap<int, int2> _renderLut; // 渲染专用邻域表
        private NativeArray<float4x4> _covBuffer;
        private NativeArray<MyBoxCollider> _colliderBuffer;
        private int _currentColliderCount; // 当前有效碰撞体数量
        private int _colliderRefreshTimer; // 碰撞体刷新计时器
        private Collider[] _overlapResults; // Physics.OverlapSphereNonAlloc 的结果缓冲
        
        private NativeArray<float3> _boundsBuffer;
        private NativeArray<float> _gridBuffer;
        private NativeArray<float> _gridTempBuffer;
        private NativeHashMap<int3, int> _gridLut;
        private NativeArray<int4> _blockBuffer;
        private NativeArray<int> _blockColorBuffer;
        
        private NativeArray<Effects.Bubble> _bubblesBuffer;
        private NativeList<int> _bubblesPoolBuffer;
        
        private NativeList<Effects.Component> _componentsBuffer;
        private NativeArray<int> _gridIDBuffer;
        private NativeArray<int> _prevGridStableId;  // 上一帧网格稳定ID（用于投票）
        private int _nextStableId = 1;  // 下一个可用的稳定ID（0=主体，1+=分离组）
        private NativeList<ParticleController> _controllerBuffer;
        private NativeArray<ParticleController> _sourceControllers;
        private int[] _componentToGroup;
        private Stack<int> _freeSeparatedControllerIds;
        
        private ComputeBuffer _bubblesDataBuffer;
        
        // 粒子渲染器（封装 GPU Buffer 管理）
        private SlimeParticleRenderer _particleRenderer;
        
        // 场景水珠独立管理器（固定分区[8192-16383]）
        private DropletSubsystem _dropletSubsystem;

        private NativeParallelMultiHashMap<int, int> _mergeMainBodyLut;
        private NativeArray<int> _autoSeparateClusterCounts;
        private NativeArray<float3> _autoSeparateClusterCenters;
        
        #endregion
        
        private float3 _lastMousePos;
        private bool _mouseDown;
        private float3 _velocityY = float3.zero;
        private Bounds _bounds;
        private Vector3 _velocity = Vector3.zero;
        private float3 _v_soft; // 限速后的流体驱动速度（内部坐标）
        private float3 _prevVelocitySim; // 上帧速度（模拟坐标），用于折返检测
        private bool _thresholdTriggered; // 阈值是否触发，用于决定控制器Velocity
        private Vector3 _prevTransPosition; // 上一帧 trans.position，用于计算速度
        private float _lastMainRadius; // 用于 mainRadius 防抖
        private float3 _prevMainControllerCenter; // 上一帧主控制器中心，用于正确计算PosOld

        private LMarchingCubes _marchingCubes;
        private Mesh _mesh;
        
        private bool _connect;
        private float _connectStartTime;
        private NativeArray<int> _stableIdToSlot;
        private NativeArray<byte> _recallEligibleStableIds;
        private NativeList<SlimeInstance> _slimeInstances;
        private int _controlledInstance;
        private Stack<int> _instancePool;

        private int[] _controllerFreeFramesCounts;
        private int[] _controllerSmallSeparatedFrames;
        private int[] _controllerFadeRemainingFrames;
        private int[] _controllerFadePerFrame;
        private int[] _controllerFadeBudget;


        // Job System 批处理大小
        private const int batchCount = 64;

        
        void Awake()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;

            // 确保maxParticles足够支持水珠分区[8192-16383]
            maxParticles = math.max(maxParticles, 16384);

            if (trans == null)
            {
                trans = transform;
            }

            
            // 初始化碰撞体缓冲区（必须在Awake中，避免FixedUpdate先于Start执行）
            _colliderBuffer = new NativeArray<MyBoxCollider>(maxNearbyColliders, Allocator.Persistent);
            _overlapResults = new Collider[maxNearbyColliders];
            _currentColliderCount = 0;
            _colliderRefreshTimer = 0;

            _boundsBuffer = new NativeArray<float3>(2, Allocator.Persistent);

            // Job 参数必须始终是有效容器：即使未启用召回也需要一个已构造的数组
            // 真正启用掩码由 UseRecallEligibleStableIds 控制
            if (!_stableIdToSlot.IsCreated)
                _stableIdToSlot = new NativeArray<int>(1, Allocator.Persistent);
            if (!_recallEligibleStableIds.IsCreated)
                _recallEligibleStableIds = new NativeArray<byte>(1, Allocator.Persistent);
        }
        
        public void StartRecall()
        {
            if (!PrepareRecallEligibleControllers())
            {
                if (componentDebug)
                    Debug.Log("[Recall] 无可召回目标");
                return;
            }

            _connect = true;
            _connectStartTime = Time.time;
            if (componentDebug)
            {
                int eligibleCount = 0;
                int sampleCount = 0;
                string sample = string.Empty;
                if (_recallEligibleStableIds.IsCreated)
                {
                    for (int i = 1; i < _recallEligibleStableIds.Length; i++)
                    {
                        if (_recallEligibleStableIds[i] == 0)
                            continue;
                        eligibleCount++;
                        if (sampleCount < 8)
                        {
                            sample = sampleCount == 0 ? i.ToString() : sample + "," + i;
                            sampleCount++;
                        }
                    }
                }
                Debug.Log($"[Recall] 启动CCA融合回收 eligible={eligibleCount} sample={sample}");
            }
        }

        private bool PrepareRecallEligibleControllers()
        {
            if (!_controllerBuffer.IsCreated || _controllerBuffer.Length <= 0)
                return false;
            if (!_particles.IsCreated || activeParticles <= 0)
                return false;

            RefreshStableIdToSlotMapping();

            if (!_recallEligibleStableIds.IsCreated || _recallEligibleStableIds.Length < _stableIdToSlot.Length)
            {
                if (_recallEligibleStableIds.IsCreated)
                    _recallEligibleStableIds.Dispose();
                _recallEligibleStableIds = new NativeArray<byte>(_stableIdToSlot.Length, Allocator.Persistent);
            }

            for (int i = 0; i < _recallEligibleStableIds.Length; i++)
                _recallEligibleStableIds[i] = 0;

            if (_recallEligibleStableIds.Length > 0)
                _recallEligibleStableIds[0] = 0;

            bool hasAny = false;
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                if (p.SourceId >= 0)
                    continue;
                if (p.Type != ParticleType.Separated)
                    continue;
                if (p.FreeFrames > 0)
                    continue;
                int sid = p.StableId;
                if (sid <= 0)
                    continue;
                if (sid < 0 || sid >= _recallEligibleStableIds.Length)
                    continue;
                _recallEligibleStableIds[sid] = 1;
                hasAny = true;
            }

            return hasAny;
        }
        
        public void SwitchInstance()
        {
            for (int i = 0; i < _slimeInstances.Length; i++)
            {
                if (!_slimeInstances[i].Active) continue;
                _controlledInstance = i;
                trans.position = _slimeInstances[i].Center * PBF_Utils.Scale;
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
                mainCenter = (float3)(trans.position * PBF_Utils.InvScale);

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
                int cid = p.ControllerId;
                if (cid <= 0 || cid >= controllerCount)
                    continue;
                if (_controllerFadeBudget[cid] <= 0)
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
                _controllerFadeBudget[cid]--;

                if (activeParticles < DropletSubsystem.DROPLET_START && activeParticles >= 0 && activeParticles < _particles.Length)
                {
                    float3 offset = new float3(
                        UnityEngine.Random.value - 0.5f,
                        UnityEngine.Random.value - 0.5f,
                        UnityEngine.Random.value - 0.5f
                    ) * (PBF_Utils.h * 0.5f);

                    _particles[activeParticles] = new Particle
                    {
                        Position = mainCenter + offset,
                        Type = ParticleType.MainBody,
                        ControllerId = 0,
                        StableId = 0,
                        SourceId = -1,
                        ClusterId = 0,
                        FreeFrames = 0,
                        FramesOutsideMain = 0,
                    };
                    _velocityBuffer[activeParticles] = float3.zero;
                    activeParticles++;
                }
            }

            for (int c = 1; c < controllerCount; c++)
            {
                if (_controllerFadeRemainingFrames[c] > 0)
                {
                    _controllerFadeRemainingFrames[c]--;
                    if (_controllerFadeRemainingFrames[c] <= 0)
                    {
                        _controllerSmallSeparatedFrames[c] = 0;
                        _controllerFadePerFrame[c] = 0;
                        _controllerFadeBudget[c] = 0;
                    }
                }
            }
        }
        
        void Start()
        {
            double startupPrev = 0;
            if (startupProfileDebug)
            {
                startupT0 = Time.realtimeSinceStartupAsDouble;
                startupPrev = startupT0;
                Debug.Log("[Startup] Slime_PBF.Start begin");
            }

            // 使用 maxParticles 替代 PBF_Utils.Num
            _particles = new NativeArray<Particle>(maxParticles, Allocator.Persistent);
            _particlesRenderBuffer = new NativeArray<Particle>(maxParticles, Allocator.Persistent);
            
            // 从 SlimeVolume 获取初始主体粒子数
            int initialMainCount = 800; // 默认值
            if (_slimeVolume != null)
            {
                initialMainCount = Mathf.Min(_slimeVolume.initialMainVolume, maxParticles);
                if (startupProfileDebug)
                    Debug.Log($"[Slime_PBF] 初始主体粒子数: {initialMainCount} / {maxParticles}");
            }
            // 主体粒子分区为[0-8191]，最大8192个
            initialMainCount = Mathf.Min(initialMainCount, 8192);
            
        // 初始化主体粒子 - 使用简单的线性循环，确保所有粒子都被初始化
            // 获取玩家位置作为粒子生成中心（转换为模拟坐标系）
            float3 spawnCenter = trans != null ? (float3)(trans.position * PBF_Utils.InvScale) : float3.zero;
            // 初始化 _prevTransPosition，避免第一帧速度计算错误
            _prevTransPosition = trans != null ? trans.position : Vector3.zero;
            
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
                    float3 offset = new float3(x - cubeHalf, y, z - cubeHalf) * 0.5f;
                    _particles[idx] = new Particle
                    {
                        Position = spawnCenter + offset,
                        Type = ParticleType.MainBody,
                        ControllerId = 0,
                        StableId = 0,
                        SourceId = -1,
                        ClusterId = 0,
                        FreeFrames = 0
                    };
                }
                else
                {
                    _particles[idx] = new Particle
                    {
                        Position = new float3(0, -1000, 0),
                        Type = ParticleType.Dormant,
                        ControllerId = 0,
                        StableId = 0,
                        SourceId = -1,
                        ClusterId = 0,
                        FreeFrames = 0
                    };
                }
            }
            
            // 初始活跃粒子数
            activeParticles = initialMainCount;
            
            // === 【初始化诊断】输出粒子分布信息 ===
            {
                float3 minPos = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
                float3 maxPos = new float3(float.MinValue, float.MinValue, float.MinValue);
                int mainBodyCount = 0, dormantCount = 0;
                
                for (int pi = 0; pi < _particles.Length; pi++)
                {
                    var p = _particles[pi];
                    if (p.Type == ParticleType.MainBody)
                    {
                        mainBodyCount++;
                        minPos = math.min(minPos, p.Position);
                        maxPos = math.max(maxPos, p.Position);
                    }
                    else if (p.Type == ParticleType.Dormant)
                    {
                        dormantCount++;
                    }
                }
                
                float3 center = (minPos + maxPos) * 0.5f;
                float3 extent = maxPos - minPos;
                
                Debug.Log($"[初始化诊断] trans.pos={trans?.position}, spawnCenter(模拟)={spawnCenter}\n" +
                         $"  initialMainCount={initialMainCount}, 实际MainBody={mainBodyCount}, Dormant={dormantCount}\n" +
                         $"  粒子范围: min={minPos}, max={maxPos}\n" +
                         $"  粒子中心={center}, 跨度={extent}\n" +
                         $"  循环参数: cubeSize={cubeSize}, cubeHalf={cubeHalf}");
            }

            if (startupProfileDebug)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                Debug.Log($"[Startup] InitParticles {(now - startupPrev) * 1000.0:F1}ms");
                startupPrev = now;
            }
            
            // 收集场景中的所有 SceneDropletSource
            allSources.Clear();
            allSources.AddRange(FindObjectsByType<DropWater>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            
            if (allSources.Count > 0)
            {
                if (startupProfileDebug || componentDebug)
                    // 找到场景水珠源
                foreach (var source in allSources)
                {
                    source.Reset(); // 重置到休眠状态
                }
            }

            if (startupProfileDebug)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                Debug.Log($"[Startup] FindDropWater {(now - startupPrev) * 1000.0:F1}ms");
                startupPrev = now;
            }

            _colliderRefreshTimer = colliderRefreshInterval;
            _overlapResults = new Collider[maxNearbyColliders * 2]; // 预分配足够大的缓冲
            
            // 初始化PBF物理系统（主体配置）
            _pbfSystem = new Physics.PBFSystem(Physics.PBFSystem.Config.MainBody);
            
            // 原始数据
            _particlesTemp = new NativeArray<Particle>(maxParticles, Allocator.Persistent);
            _posOld = new NativeArray<float3>(maxParticles, Allocator.Persistent);
            _clampDelta = new NativeArray<float3>(maxParticles, Allocator.Persistent); // 【P4】钳制位移量
            _velocityBuffer = new NativeArray<float3>(maxParticles, Allocator.Persistent);
            _velocityTempBuffer = new NativeArray<float3>(maxParticles, Allocator.Persistent);
            _renderLut = new NativeHashMap<int, int2>(maxParticles * 8, Allocator.Persistent);

            _mergeMainBodyLut = new NativeParallelMultiHashMap<int, int>(DropletSubsystem.DROPLET_START, Allocator.Persistent);
            _autoSeparateClusterCounts = new NativeArray<int>(256, Allocator.Persistent);
            _autoSeparateClusterCenters = new NativeArray<float3>(256, Allocator.Persistent);
            
            // 网格容量需要根据 maxParticles 调整
            // 场景水珠分散时需要更多网格块，使用 maxParticles * 2 确保足够
            int gridNumExpanded = Mathf.Max(PBF_Utils.GridNum * 4, maxParticles * 2);
            _gridBuffer = new NativeArray<float>(PBF_Utils.GridSize * gridNumExpanded, Allocator.Persistent);
            _gridTempBuffer = new NativeArray<float>(PBF_Utils.GridSize * gridNumExpanded, Allocator.Persistent);
            _gridLut = new NativeHashMap<int3, int>(gridNumExpanded, Allocator.Persistent);
            _covBuffer = new NativeArray<float4x4>(maxParticles, Allocator.Persistent);
            _blockBuffer = new NativeArray<int4>(gridNumExpanded, Allocator.Persistent);
            _blockColorBuffer = new NativeArray<int>(9, Allocator.Persistent);
            
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
            _prevGridStableId = new NativeArray<int>(_gridBuffer.Length, Allocator.Persistent);
            _controllerBuffer = new NativeList<ParticleController>(16, Allocator.Persistent);
            
            // 初始控制器中心必须与粒子初始化时的 spawnCenter 一致
            float3 initialCenter = trans != null ? (float3)(trans.position * PBF_Utils.InvScale) : float3.zero;
            var initialController = new ParticleController
            {
                Center = initialCenter,
                Radius = PBF_Utils.InvScale,
                Velocity = float3.zero,
                Concentration = concentration,
                IsValid = true,
            };
            _controllerBuffer.Add(initialController);

            _marchingCubes = new LMarchingCubes();

            if (startupProfileDebug)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                Debug.Log($"[Startup] AllocateBuffers+MarchingCubes {(now - startupPrev) * 1000.0:F1}ms");
                startupPrev = now;
            }

            _particleRenderer = new SlimeParticleRenderer(particleMat, particleMesh, maxParticles);
            _bubblesDataBuffer = new ComputeBuffer(PBF_Utils.BubblesCount, sizeof(float) * 8);
            bubblesMat.SetBuffer("_BubblesBuffer", _bubblesDataBuffer);

            _slimeInstances = new NativeList<SlimeInstance>(16,  Allocator.Persistent);
            float3 initialInstanceCenter = trans != null ? (float3)(trans.position * PBF_Utils.InvScale) : float3.zero;
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
            int sourceCapacityForDroplets = math.max(1, allSources.Count);
            _dropletSubsystem.Initialize(_particles, _velocityBuffer, sourceCapacityForDroplets);
            
            if (startupProfileDebug)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                Debug.Log($"[Startup] InitColliderBuffer (NearbyCache) capacity={maxNearbyColliders}");
                startupPrev = now;
            }
            
            // 首次刷新近场碰撞体
            RefreshNearbyColliders();
            
            if (startupProfileDebug || colliderCollectDebug)
                Debug.Log($"[碰撞体采集] 近场缓存初始化完成，当前有效碰撞体数={_currentColliderCount}/{maxNearbyColliders}");

            if (_slimeVolume == null)
            {
                Debug.LogWarning("[Slime_PBF] 未绑定 SlimeVolume 组件，体积管理功能将不可用");
            }
            else
            {
                _slimeVolume.UpdateFromParticles(_particles, true);
            }

            if (startupProfileDebug)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                Debug.Log($"[Startup] Total {(now - startupT0) * 1000.0:F1}ms");
            }
        }

        private void OnDestroy()
        {
            if (_particles.IsCreated) _particles.Dispose();
            if (_particlesRenderBuffer.IsCreated) _particlesRenderBuffer.Dispose();
            if (_particlesTemp.IsCreated) _particlesTemp.Dispose();
            if (_renderLut.IsCreated) _renderLut.Dispose();
            if (_posOld.IsCreated) _posOld.Dispose();
            if (_clampDelta.IsCreated) _clampDelta.Dispose(); // 【P4】
            if (_velocityBuffer.IsCreated) _velocityBuffer.Dispose();
            if (_velocityTempBuffer.IsCreated) _velocityTempBuffer.Dispose();

            if (_mergeMainBodyLut.IsCreated) _mergeMainBodyLut.Dispose();
            if (_autoSeparateClusterCounts.IsCreated) _autoSeparateClusterCounts.Dispose();
            if (_autoSeparateClusterCenters.IsCreated) _autoSeparateClusterCenters.Dispose();
            
            // 释放PBF物理系统
            _pbfSystem?.Dispose();
            if (_boundsBuffer.IsCreated) _boundsBuffer.Dispose();
            if (_gridBuffer.IsCreated) _gridBuffer.Dispose();
            if (_gridTempBuffer.IsCreated) _gridTempBuffer.Dispose();
            if (_covBuffer.IsCreated) _covBuffer.Dispose();
            if (_gridLut.IsCreated) _gridLut.Dispose();
            if (_blockBuffer.IsCreated) _blockBuffer.Dispose();
            if (_blockColorBuffer.IsCreated) _blockColorBuffer.Dispose();
            if (_bubblesBuffer.IsCreated) _bubblesBuffer.Dispose();
            if (_bubblesPoolBuffer.IsCreated) _bubblesPoolBuffer.Dispose();
            if (_componentsBuffer.IsCreated) _componentsBuffer.Dispose();
            if (_gridIDBuffer.IsCreated) _gridIDBuffer.Dispose();
            if (_prevGridStableId.IsCreated) _prevGridStableId.Dispose();
            if (_controllerBuffer.IsCreated) _controllerBuffer.Dispose();
            if (_stableIdToSlot.IsCreated) _stableIdToSlot.Dispose();
            if (_recallEligibleStableIds.IsCreated) _recallEligibleStableIds.Dispose();
            if (_slimeInstances.IsCreated) _slimeInstances.Dispose();
            if (_colliderBuffer.IsCreated)  _colliderBuffer.Dispose();
            if (_sourceControllers.IsCreated) _sourceControllers.Dispose();
            
            // 释放场景水珠子系统
            _dropletSubsystem.Dispose();

            if (_marchingCubes != null)
            {
                _marchingCubes.Dispose();
                _marchingCubes = null;
            }

            _particleRenderer?.Dispose();
            _particleRenderer = null;
            
            if (_bubblesDataBuffer != null)
            {
                _bubblesDataBuffer.Release();
                _bubblesDataBuffer = null;
            }

        }

        void Update()
        {
            // 更新场景水珠源的激活状态
            UpdateSceneDropletSources();
            
            // 定期输出状态信息
            if (componentDebug && Time.frameCount % 300 == 0)
            {
                if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
                {
                    var ctrl = _controllerBuffer[0];
                    Debug.Log($"[Status] 控制器: Center={ctrl.Center}, Radius={ctrl.Radius:F2}");
                }
                
                // 计算粒子分布（区分主体和场景水珠）
                if (_particles.IsCreated && activeParticles > 0)
                {
                    float3 mainCenter = _controllerBuffer[0].Center;
                    float mainMinDist = float.MaxValue, mainMaxDist = 0;
                    float dropletMinDist = float.MaxValue, dropletMaxDist = 0;
                    int mainCount = 0, dropletCount = 0;
                    int count = math.min(activeParticles, _particles.Length);
                    for (int i = 0; i < count; i++)
                    {
                        var p = _particles[i];
                        float dist = math.length(p.Position - mainCenter);  // 世界坐标：计算到中心距离
                        if (p.Type == ParticleType.MainBody)
                        {
                            mainMinDist = math.min(mainMinDist, dist);
                            mainMaxDist = math.max(mainMaxDist, dist);
                            mainCount++;
                        }
                        else if (p.SourceId >= 0)
                        {
                            dropletMinDist = math.min(dropletMinDist, dist);
                            dropletMaxDist = math.max(dropletMaxDist, dist);
                            dropletCount++;
                        }
                    }
                    Debug.Log($"[Status] 主体粒子({mainCount}): 最近={mainMinDist:F2}, 最远={mainMaxDist:F2}");
                }
            }
            
            
            // 更新控制器中心到最新位置（避免FixedUpdate和Update之间的延迟）
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0 && trans != null)
            {
                float3 mainCenter = (float3)(trans.position * PBF_Utils.InvScale);
                var mainController = _controllerBuffer[0];
                mainController.Center = mainCenter;
                _controllerBuffer[0] = mainController;
            }

            if (renderMode == RenderMode.Particles)
            {
                // 数据已在 FixedUpdate 中准备，这里只负责绘制
                int totalParticles = activeParticles + _dropletSubsystem.ActiveCount;
                _particleRenderer.Draw(totalParticles);
            }
            else if (renderMode == RenderMode.Surface)
            {
                // Surface 渲染：主体 + 水珠都通过 Marching Cubes 表面重建
                if (_mesh != null)
                    Graphics.DrawMesh(_mesh, Matrix4x4.TRS(_bounds.min, Quaternion.identity, Vector3.one), mat, 0);

                Graphics.DrawMeshInstancedProcedural(particleMesh, 0, bubblesMat, _bounds, PBF_Utils.BubblesCount);
            }

            if (concentration > 5)
            {
                foreach (var slime in _slimeInstances)
                {
                    if (!slime.Active) continue;

                    int controllerId = slime.ControllerID;
                    if (controllerId > 0 && _controllerFreeFramesCounts != null && controllerId < _controllerFreeFramesCounts.Length && _controllerFreeFramesCounts[controllerId] > 0)
                        continue;

                    Vector3 renderDir = slime.Dir;
                    renderDir.y = 0f;
                    if (renderDir.sqrMagnitude > 0.001f)
                    {
                        Graphics.DrawMesh(faceMesh, Matrix4x4.TRS(slime.Pos * PBF_Utils.Scale,
                            Quaternion.LookRotation(-renderDir),
                            0.2f * math.sqrt(slime.Radius * PBF_Utils.Scale) * Vector3.one), faceMat, 0);
                    }
                }
            }
        }

        private void FixedUpdate()
        {
            if (!isActiveAndEnabled)
                return;
            
            PerformanceProfiler.BeginFrame();
            
            // 不再移除场景水珠，让它们留在主粒子系统中以便渲染和交互
            
            // 近场碰撞体刷新（每 N 帧一次）
            _colliderRefreshTimer++;
            if (_colliderRefreshTimer >= colliderRefreshInterval)
            {
                _colliderRefreshTimer = 0;
                RefreshNearbyColliders();
            }

            // 使用 trans.position 变化量计算速度，保证位置和速度同步
            // 这样高速位置补偿的方向和控制器中心的移动方向一致，不会导致粒子飞散
            if (trans != null && Time.fixedDeltaTime > 0)
            {
                _velocity = (trans.position - _prevTransPosition) / Time.fixedDeltaTime;
                _prevTransPosition = trans.position;
            }
            
            // 原版时序：不在 Simulate() 前更新 Center，让 ApplyForceJob 使用上一帧的 Center 和 Radius
            // 这样 len 和 Radius 匹配，粒子更容易满足 len < Radius 条件受向心力
            // Center 和 Radius 统一在 Control() 中更新（Simulate 之后）
            
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0 && trans != null)
            {
            }
            
            // 主体粒子模拟（原版每帧执行2次）
            for (int i = 0; i < 2; i++)
            {
                Profiler.BeginSample("Simulate_Main");
                PerformanceProfiler.Begin($"Simulate_{i}");
                Simulate();
                PerformanceProfiler.End($"Simulate_{i}");
                Profiler.EndSample();
            }
            
            // 水珠独立模拟（独立分区，使用完整 PBF 物理 + 环境碰撞）
            Profiler.BeginSample("Simulate_Droplets");
            
            // 实时更新物理参数（从第一个激活的 DropWater 读取，支持 Inspector 调整）
            foreach (var source in allSources)
            {
                if (source != null && source.State == DropWater.DropletSourceState.Simulated)
                {
                    _dropletSubsystem.UpdatePhysicsParams(
                        source.cohesionStrength, source.cohesionRadius,
                        source.velocityDamping, source.verticalCohesionScale,
                        source.enableViscosity, source.viscosityStrength);
                    break; // 只取第一个激活源的参数
                }
            }
            
            // 地面高度：使用场景实际地面（世界Y=0）转为内部坐标
            float dropletGroundY = 0f; // 场景地面世界Y=0 → 内部Y=0
            // 恢复碰撞体，让水珠正常站在地面上
            _dropletSubsystem.Simulate(deltaTime, _particles, _velocityBuffer, dropletGroundY, targetDensity, 
                _colliderBuffer, _currentColliderCount);
            
            Profiler.EndSample();

            PerformanceProfiler.Begin("Surface");
            Surface();
            PerformanceProfiler.End("Surface");
            
            // Unshuffle 必须在 Surface() 之后执行，因为 Surface() 使用 Lut（基于排序后索引）
            // 关键修复：只处理 activeParticles 范围，避免越界读取垃圾数据覆盖有效粒子
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
            Control();
            PerformanceProfiler.End("Control");
            
            // Bubbles效果暂时禁用（方法未实现）
            // PerformanceProfiler.Begin("Bubbles");
            // Bubbles();
            // PerformanceProfiler.End("Bubbles");
            
            bubblesNum = PBF_Utils.BubblesCount - _bubblesPoolBuffer.Length;
            
            if (renderMode == RenderMode.Particles)
            {
                // 连续打包：主体 + 水珠
                int totalParticles = ConvertToWorldPositionsForRendering();
                _particleRenderer.UploadParticles(_particlesRenderBuffer, totalParticles);
                _particleRenderer.SetBounds(minPos * PBF_Utils.Scale, maxPos * PBF_Utils.Scale);
            }
            else
            {
                // Surface 模式：水珠数据在 Surface() 中一起处理
                _bubblesDataBuffer.SetData(_bubblesBuffer);
            }
            
            _bounds = new Bounds()
            {
                min = minPos * PBF_Utils.Scale,
                max = maxPos * PBF_Utils.Scale
            };

            if (_slimeVolume != null)
                _slimeVolume.UpdateFromParticles(_particles);
            
            PerformanceProfiler.EndFrame();
        }

        private void Surface()
        {
            Profiler.BeginSample("Render");
            
            // 先将粒子转换为世界坐标（Surface 渲染需要世界坐标）
            // 返回总粒子数：主体 + 水珠
            int totalParticles = ConvertToWorldPositionsForRendering();
            
            // 为渲染重新构建邻域表（包含水珠）
            PerformanceProfiler.Begin("Surface_BuildLut");
            BuildRenderLut(totalParticles);
            PerformanceProfiler.End("Surface_BuildLut");
            
            PerformanceProfiler.Begin("Surface_MeanPos");

            var handle = new Reconstruction.ComputeMeanPosJob
            {
                Lut = _renderLut,
                Ps = _particlesRenderBuffer, // 使用世界坐标
                MeanPos = _particlesTemp,
            }.Schedule(totalParticles, batchCount);

            if (useAnisotropic)
            {
                handle = new Reconstruction.ComputeCovarianceJob()
                {
                    Lut = _renderLut,
                    Ps = _particlesRenderBuffer, // 使用世界坐标
                    MeanPos = _particlesTemp,
                    GMatrix = _covBuffer,
                }.Schedule(totalParticles, batchCount, handle);
            }

            new Reconstruction.CalcBoundsJob()
            {
                Ps = _particlesRenderBuffer, // 使用世界坐标
                Controllers = _controllerBuffer,
                SourceControllers = _sourceControllers,
                Bounds = _boundsBuffer,
                ActiveCount = totalParticles,
            }.Schedule(handle).Complete();
            PerformanceProfiler.End("Surface_MeanPos");

            Profiler.EndSample();

            _gridLut.Clear();
            float blockSize = PBF_Utils.CellSize * 4;
            minPos = math.floor(_boundsBuffer[0] / blockSize) * blockSize;
            maxPos = math.ceil(_boundsBuffer[1] / blockSize) * blockSize;
            
            // 调试：检查 Bounds 是否包含水珠
            int dropletCount2 = totalParticles - activeParticles;
            if (dropletCount2 > 0 && Time.frameCount % 60 == 0)
            {
                var firstDroplet = _particlesRenderBuffer[activeParticles];
                bool inBounds = math.all(firstDroplet.Position >= _boundsBuffer[0]) && 
                               math.all(firstDroplet.Position <= _boundsBuffer[1]);
                Debug.Log($"[Surface.Bounds] rawMin=({_boundsBuffer[0].x:F1},{_boundsBuffer[0].y:F1},{_boundsBuffer[0].z:F1}), " +
                    $"rawMax=({_boundsBuffer[1].x:F1},{_boundsBuffer[1].y:F1},{_boundsBuffer[1].z:F1}), " +
                    $"水珠在Bounds内={inBounds}, 水珠位置=({firstDroplet.Position.x:F1},{firstDroplet.Position.y:F1},{firstDroplet.Position.z:F1})");
            }

            Profiler.BeginSample("Allocate");
            PerformanceProfiler.Begin("Surface_Allocate");
            handle = new Reconstruction.ClearGridJob
            {
                Grid = _gridBuffer,
                GridID = _gridIDBuffer,
            }.Schedule(_gridBuffer.Length, batchCount);

            handle = new Reconstruction.AllocateBlockJob()
            {
                Ps = _particlesTemp,
                GridLut = _gridLut,
                MinPos = minPos,
                ActiveCount = totalParticles,
            }.Schedule(handle);
            handle.Complete();

            var keys = _gridLut.GetKeyArray(Allocator.TempJob);
            blockNum = keys.Length;

            new Reconstruction.ColorBlockJob()
            {
                Keys = keys,
                Blocks = _blockBuffer,
                BlockColors = _blockColorBuffer,
            }.Schedule().Complete();
            PerformanceProfiler.End("Surface_Allocate");

            Profiler.EndSample();

            Profiler.BeginSample("Splat");
            PerformanceProfiler.Begin("Surface_Splat");

#if USE_SPLAT_SINGLE_THREAD
            handle = new Reconstruction.DensityProjectionJob()
            {
                Ps = _particlesTemp,
                GMatrix = _covBuffer,
                Grid = _gridBuffer,
                GridLut = _gridLut,
                MinPos = minPos,
                UseAnisotropic = useAnisotropic,
            }.Schedule();
#elif USE_SPLAT_COLOR8
            for (int i = 0; i < 8; i++)
            {
                int2 slice = new int2(_blockColorBuffer[i], _blockColorBuffer[i + 1]);
                int count = slice.y - slice.x;
                handle = new Reconstruction.DensitySplatColoredJob()
                {
                    ParticleLut = _renderLut,
                    ColorKeys = _blockBuffer.Slice(slice.x, count),
                    Ps = _particlesTemp,
                    GMatrix = _covBuffer,
                    Grid = _gridBuffer,
                    GridLut = _gridLut,
                    MinPos = minPos,
                    UseAnisotropic = useAnisotropic,
                }.Schedule(count, count, handle);
            }
#else
            handle = new Reconstruction.DensityProjectionParallelJob()
            {
                Ps = _particlesTemp,
                GMatrix = _covBuffer,
                GridLut = _gridLut,
                Grid = _gridBuffer,
                ParticleLut = _renderLut,
                Keys = keys,
                UseAnisotropic = useAnisotropic,
                MinPos = minPos,
            }.Schedule(keys.Length, batchCount);
#endif
            handle.Complete();
            PerformanceProfiler.End("Surface_Splat");
            Profiler.EndSample();

            Profiler.BeginSample("Blur");
            PerformanceProfiler.Begin("Surface_Blur");

            new Reconstruction.GridBlurJob()
            {
                Keys = keys,
                GridLut = _gridLut,
                GridRead = _gridBuffer,
                GridWrite = _gridTempBuffer,
            }.Schedule(keys.Length, batchCount, handle).Complete();
            PerformanceProfiler.End("Surface_Blur");

            Profiler.EndSample();

            Profiler.BeginSample("Marching cubes");
            PerformanceProfiler.Begin("Surface_MarchingCubes");
            _mesh = _marchingCubes.MarchingCubesParallel(keys, _gridLut, _gridTempBuffer, threshold, PBF_Utils.Scale * PBF_Utils.CellSize);
            PerformanceProfiler.End("Surface_MarchingCubes");
            Profiler.EndSample();
            
            Profiler.BeginSample("CCA");
            PerformanceProfiler.Begin("Surface_CCA");
            _componentsBuffer.Clear();
            handle = new Effects.ConnectComponentBlockJob()
            {
                Keys = keys,
                Grid = _gridBuffer,
                GridLut = _gridLut,
                Components = _componentsBuffer,
                GridID = _gridIDBuffer,
                Threshold = PBF_Utils.CCAThreshold,
            }.Schedule();
            
            handle.Complete();
            PerformanceProfiler.End("Surface_CCA");
            Profiler.EndSample();
            
            // 网格投票：让CCA组件ID跨帧稳定（必须在ParticleIDJob之前执行）
            Profiler.BeginSample("StableID");
            PerformanceProfiler.Begin("Surface_StableID");
            ResolveStableIds();
            PerformanceProfiler.End("Surface_StableID");
            Profiler.EndSample();
            
            // ParticleIDJob仍然使用CCA组件ID（ClusterId = compIdx+1）
            // 稳定ID通过_compToStableId映射在控制器分配时使用
            Profiler.BeginSample("ParticleID");
            PerformanceProfiler.Begin("Surface_ParticleID");
            handle = new Effects.ParticleIDJob()
            {
                GridLut = _gridLut,
                GridID = _gridIDBuffer,  // 仍使用原始CCA组件ID
                Controllers = _controllerBuffer,
                SourceControllers = _sourceControllers,
                Particles = _particles,
                MinPos = minPos,
            }.Schedule(activeParticles, batchCount);
            handle.Complete();
            PerformanceProfiler.End("Surface_ParticleID");
            Profiler.EndSample();

            keys.Dispose();
        }

        private void Simulate()
        {
            _pbfSystem.Lut.Clear();
            
            // 速度分解：v_soft（流体形变）+ v_board（刚体补偿）
            float3 v_real = (float3)_velocity * PBF_Utils.InvScale;
            float3 v_soft, v_board;
            
            float thresholdXZ_sim = manualThreshold * PBF_Utils.InvScale;
            float thresholdY_sim = verticalThreshold * PBF_Utils.InvScale;
            v_soft = v_real;
            v_board = float3.zero;
            
            // 水平方向（XZ）
            float speedXZ_sq = v_real.x * v_real.x + v_real.z * v_real.z;
            if (speedXZ_sq > thresholdXZ_sim * thresholdXZ_sim)
            {
                float speedXZ = math.sqrt(speedXZ_sq);
                float scale = thresholdXZ_sim / speedXZ;
                v_soft.x = v_real.x * scale;
                v_soft.z = v_real.z * scale;
                v_board.x = v_real.x - v_soft.x;
                v_board.z = v_real.z - v_soft.z;
            }
            
            // 垂直方向（Y）
            float absY = math.abs(v_real.y);
            if (absY > thresholdY_sim)
            {
                v_soft.y = math.sign(v_real.y) * thresholdY_sim;
                v_board.y = v_real.y - v_soft.y;
            }
            
            float3 boardDelta = v_board * Time.fixedDeltaTime;
            _v_soft = v_soft; // 保存供控制器更新使用
            
            // === 折返检测日志 ===
            float speedXZ_log = math.sqrt(speedXZ_sq);
            float2 dirXZ = speedXZ_log > 0.01f ? new float2(v_real.x, v_real.z) / speedXZ_log : float2.zero;
            float2 prevDirXZ = math.length(new float2(_prevVelocitySim.x, _prevVelocitySim.z)) > 0.01f 
                ? math.normalize(new float2(_prevVelocitySim.x, _prevVelocitySim.z)) : float2.zero;
            float dirDot = math.dot(dirXZ, prevDirXZ);
            bool isTurning = dirDot < 0.5f && speedXZ_log > 0.1f; // 方向变化>60度且有速度
            bool thresholdTriggered = speedXZ_sq > thresholdXZ_sim * thresholdXZ_sim;
            _thresholdTriggered = thresholdTriggered; // 保存供Control()使用
            
_prevVelocitySim = v_real;
            
            // 在 ApplyForceJob 之前更新控制器 Velocity
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
            {
                var mainCtrl = _controllerBuffer[0];
                mainCtrl.Velocity = (float3)_velocity * PBF_Utils.InvScale;
                _controllerBuffer[0] = mainCtrl;
            }
            
            PerformanceProfiler.Begin("ApplyForce");

            RefreshStableIdToSlotMapping();
             
             // 获取主体控制器中心，用于分离粒子排除指向主体的凝聚力分量
            float3 mainCenterForJob = _controllerBuffer.Length > 0 ? _controllerBuffer[0].Center : float3.zero;
            new Simulation_PBF.ApplyForceJob
            {
                Ps = _particles,
                Velocity = _velocityBuffer,
                PsNew = _particlesTemp,
                Controllers = _controllerBuffer,
                SourceControllers = _sourceControllers,
                StableIdToSlot = _stableIdToSlot,
                Gravity = new float3(0, gravity, 0),
                DeltaTime = deltaTime,
                PredictStep = predictStep,
                VelocityDamping = velocityDamping,
                VerticalOffset = verticalOffset,
                EnableRecall = _connect,
                RecallEligibleStableIds = _recallEligibleStableIds,
                UseRecallEligibleStableIds = _connect && _recallEligibleStableIds.IsCreated,
                MainCenter = mainCenterForJob,
                MainVelocity = _controllerBuffer.Length > 0 ? _controllerBuffer[0].Velocity : float3.zero,
                MaxDeformDistXZ = maxDeformDistXZ * PBF_Utils.InvScale, // 水平形变上限
                MaxDeformDistY = maxDeformDistY * PBF_Utils.InvScale,   // 垂直形变上限
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("ApplyForce");

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
                PsOriginal = _particlesTemp, // 排序后的粒子数组
                PsNew = _particles,
                ClampDelta = _clampDelta, // 【P4】输出钳制位移量
                TargetDensity = targetDensity,
                MaxDeformDistXZ = maxDeformDistXZ * PBF_Utils.InvScale, // 【P4】启用椭球钳制
                MaxDeformDistY = maxDeformDistY * PBF_Utils.InvScale,   // 【P4】启用椭球钳制
                MainCenter = mainCenterForJob,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("ComputeDeltaPos");

            PerformanceProfiler.Begin("Update");
            
            float dynamicMaxVelocity = maxVelocity;
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
            {
                float controllerSpeed = math.length(_controllerBuffer[0].Velocity);
                dynamicMaxVelocity = math.max(dynamicMaxVelocity, controllerSpeed * 1.5f);
            }
            
            // 【改进】为每个控制器独立检测地面高度
            UpdateControllerGroundHeights();
            
            // 后备地面高度（当粒子的 ControllerId 无效时使用）
            float fallbackGroundY = -10f;
            
            new Simulation_PBF.UpdateJob
            {
                Ps = _particles,
                PosOld = _posOld,
                ClampDelta = _clampDelta, // 【P4】钳制位移量，用于速度回算修正
                Colliders = _colliderBuffer,
                Controllers = _controllerBuffer, // 【新增】控制器数组，用于获取每个粒子对应的 GroundY
                ColliderCount = _currentColliderCount,
                Velocity = _velocityTempBuffer,
                MaxVelocity = dynamicMaxVelocity,
                DeltaTime = deltaTime,
                FallbackGroundY = fallbackGroundY,
                MaxDeformDistXZ = maxDeformDistXZ * PBF_Utils.InvScale, // 水平形变上限
                MaxDeformDistY = maxDeformDistY * PBF_Utils.InvScale,   // 垂直形变上限
                MainCenter = _controllerBuffer.Length > 0 ? _controllerBuffer[0].Center : float3.zero,
                MainVelocity = _controllerBuffer.Length > 0 ? _controllerBuffer[0].Velocity : float3.zero,
                EnableCollisionDeformLimit = enableCollisionDeformLimit,
                EnableP4VelocityConsistency = enableVelocityConsistency, // 【P4开关】
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("Update");
            
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
                DeltaTime = Time.fixedDeltaTime,
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
            if (UnityEngine.Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 30f, colliderLayers))
            {
                sourceGroundY = (hit.point.y ) * PBF_Utils.InvScale; // 加上粒子半径，转为模拟坐标
            }
            else
            {
                sourceGroundY = sourceWorldPos.y * PBF_Utils.InvScale - 2f; // 后备：源位置下方2单位
            }
            
            // 使用独立子系统激活（传入 DropWater 配置的物理参数 + 地面高度）
            float3 sourcePos = (float3)source.transform.position * PBF_Utils.InvScale;
            int allocated = _dropletSubsystem.ActivateSource(sourceId, sourcePos, source.particleCount,
                source.cohesionStrength, source.cohesionRadius, source.velocityDamping, source.verticalCohesionScale,
                sourceGroundY); // 【新增】传入源位置的地面高度
            
            if (allocated > 0)
            {
                source.SetState(DropWater.DropletSourceState.Simulated);
                source.SetAdaptiveRadius(source.spawnRadius);
                
                if (componentDebug)
                {
                    float3 mainCenter = trans != null ? (float3)(trans.position * PBF_Utils.InvScale) : float3.zero;
                    float distToMain = math.length(sourcePos - mainCenter);
                    Debug.Log($"[Slime_PBF] 激活 {source.name}：{allocated} 个粒子在独立分区，距主体={distToMain * PBF_Utils.Scale:F2}, 地面Y={sourceGroundY * PBF_Utils.Scale:F2}");
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
            
            int sourceId = allSources.IndexOf(source);
            if (sourceId < 0)
            {
                Debug.LogError($"[Slime_PBF] 源 {source.name} 未在 allSources 中找到");
                return;
            }
            
            // 使用独立子系统休眠
            int deactivatedCount = _dropletSubsystem.DeactivateSource(sourceId);
            source.Reset();
            
            if (componentDebug && deactivatedCount > 0)
                Debug.Log($"[Slime_PBF] 休眠 {source.name}：{deactivatedCount} 个粒子在独立分区");
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
                (float3)(trans.position * PBF_Utils.InvScale) : float3.zero;
            
            foreach (var source in allSources)
            {
                if (source == null) continue;
                
                float3 sourcePos = (float3)source.transform.position * PBF_Utils.InvScale;
                float distance = math.length(mainCenter - sourcePos);
                float distanceWorld = distance * PBF_Utils.Scale; // 转换回世界坐标
                
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
                if (componentDebug && Time.frameCount % 300 == 1)
                    Debug.Log($"[UpdateSourceControllers] frame={Time.frameCount} allSources 为空或无元素");
                return;
            }
            
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
                
                float3 dropletCenter = (float3)source.transform.position * PBF_Utils.InvScale;
                float dropletRadius = math.max(2f, source.AdaptiveRadius * PBF_Utils.InvScale);
                
                _sourceControllers[s] = new ParticleController()
                {
                    Center = dropletCenter,
                    Radius = dropletRadius,
                    Velocity = float3.zero,
                    Concentration = 5f, // 场景水珠使用固定的弱凝聚力，避免过度聚集
                };
                activeCount++;
            }
            
            if (componentDebug && Time.frameCount % 300 == 1)
                Debug.Log($"[UpdateSourceControllers] frame={Time.frameCount} 更新了 {activeCount}/{allSources.Count} 个 SourceController");
        }


        private void Control()
        {
            // === 稳定控制器：不再每帧清空，而是更新属性 ===
            
            // 确保至少有主体控制器
            if (_controllerBuffer.Length == 0)
            {
                _controllerBuffer.Add(default);
            }

            // === 1. 主体控制器始终基于 trans（用户控制的核） ===
            float3 mainCenter = trans != null ? 
                (float3)(trans.position * PBF_Utils.InvScale) : float3.zero;
            
            // 计算主体半径 - 基于主连通组件的最大距离
            float mainMaxDist = 0;
            int mainBodyCount = 0;
            int count = math.min(activeParticles, _particles.Length);
            
            // === 第一步：统计每个 ClusterId 的重心和粒子数 ===
            const int maxClusters = 256;
            var clusterCounts = new NativeArray<int>(maxClusters, Allocator.Temp);
            var clusterCenters = new NativeArray<float3>(maxClusters, Allocator.Temp);
            
            for (int i = 0; i < count; i++)
            {
                var p = _particles[i];
                if (p.Type == ParticleType.MainBody && p.ClusterId > 0 && p.ClusterId < maxClusters)
                {
                    clusterCounts[p.ClusterId]++;
                    clusterCenters[p.ClusterId] += p.Position;
                }
            }
            
            // 计算每个 cluster 的重心
            for (int i = 1; i < maxClusters; i++)
            {
                if (clusterCounts[i] > 0)
                {
                    clusterCenters[i] /= clusterCounts[i];
                }
            }
            
            // === 第二步：找出重心距离 mainCenter 最近的 ClusterId（主组件） ===
            // 这样即使玩家控制的部分较小，也不会被错误分离
            int mainClusterId = 0;
            float minDistToMain = float.MaxValue;
            for (int i = 1; i < maxClusters; i++)
            {
                if (clusterCounts[i] > 0)
                {
                    float dist = math.length(clusterCenters[i] - mainCenter);
                    if (dist < minDistToMain)
                    {
                        minDistToMain = dist;
                        mainClusterId = i;
                    }
                }
            }
            clusterCounts.Dispose();
            clusterCenters.Dispose();
            
            // === 第三步：用主连通组件内所有 MainBody 粒子的最大距离来算 mainMaxDist ===
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
            
            float3 mainVelocity = float3.zero;
            // 动态 Concentration：速度越高，聚集力越强，保持相对响应时间不变
            // 推导：响应帧数 N ∝ 1/(1-lerpFactor) ∝ 1/concentration
            // 要保持 N/speed 不变，需要 concentration ∝ speed
            float speed = math.length(_velocity);  // 世界坐标速度
            float speedRatio = math.max(1f, speed / baseSpeed);
            // 【临时禁用】动态Concentration可能导致过度聚集→爆炸，先用固定值测试
            // float dynamicConcentration = concentration * speedRatio;
            float dynamicConcentration = concentration; // 固定值
            
            if (componentDebug && speed > baseSpeed * 1.5f && Time.frameCount % 60 == 0)
            {
                Debug.Log($"[DynamicConc] speed={speed:F2}, ratio={speedRatio:F2}, " +
                         $"conc={concentration}→{dynamicConcentration:F1}");
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
            };
            
            // 重置所有分离控制器的 ParticleCount
            for (int i = 1; i < _controllerBuffer.Length; i++)
            {
                var ctrl = _controllerBuffer[i];
                ctrl.ParticleCount = 0;
                _controllerBuffer[i] = ctrl;
            }

            // === 2. CCA 分组：为每个分离的 CCA 组件创建控制器 ===
            int compCount = _componentsBuffer.Length;
            if (_componentToGroup == null || _componentToGroup.Length < compCount)
                _componentToGroup = new int[math.max(16, compCount)];
            
            // 统计每个 CCA 组件的分离粒子数
            var detachedParticleCountPerComp = new NativeArray<int>(math.max(1, compCount), Allocator.Temp, NativeArrayOptions.ClearMemory);
            NativeArray<int> recentFromEmitCountPerComp = default;
            if (componentDebug || _connect)
                recentFromEmitCountPerComp = new NativeArray<int>(math.max(1, compCount), Allocator.Temp, NativeArrayOptions.ClearMemory);
            for (int p = 0; p < activeParticles; p++)
            {
                var particle = _particles[p];
                if (particle.Type != ParticleType.Separated && particle.Type != ParticleType.Emitted) continue;
                if (particle.FreeFrames > 0) continue;
                
                int clusterId = particle.ClusterId;
                if (clusterId > 0 && clusterId <= compCount)
                {
                    int compIdx = clusterId - 1;
                    detachedParticleCountPerComp[compIdx]++;

                    if (recentFromEmitCountPerComp.IsCreated)
                    {
                        bool looksLikeNewlyEmittedSeparated = particle.Type == ParticleType.Separated &&
                                                             particle.ControllerId > 0 &&
                                                             particle.FramesOutsideMain < 60;
                        if (looksLikeNewlyEmittedSeparated)
                            recentFromEmitCountPerComp[compIdx]++;
                    }
                }
            }
            
            if (_tmpStableIdSet == null)
                _tmpStableIdSet = new HashSet<int>();
            _tmpStableIdSet.Clear();
            _tmpStableIdSet.Add(0);
            
            for (int i = 0; i < compCount; i++)
            {
                int particleCount = detachedParticleCountPerComp[i];
                
                if (particleCount <= 0 || particleCount < minSeparateClusterSize)
                {
                    _componentToGroup[i] = 0;
                    continue;
                }
                
                int stableId = (i >= 0 && i < _compToStableId.Length) ? _compToStableId[i] : 0;
                if (stableId <= 0)
                    continue;

                var component = _componentsBuffer[i];
                float3 extent = component.BoundsMax - component.Center;
                float radius = math.max(1, (extent.x + extent.y + extent.z) * PBF_Utils.CellSize * separatedRadiusScale);
                float3 center = minPos + component.Center * PBF_Utils.CellSize;

                // 方案1：召回期间，如果“新发射/保护期团块”被位置匹配继承到了召回快照中的 stableId，
                // 则强制为该组件分配新 stableId，避免误召回。
                if (_connect && recentFromEmitCountPerComp.IsCreated && _recallEligibleStableIds.IsCreated)
                {
                    if (stableId > 0 && stableId < _recallEligibleStableIds.Length && _recallEligibleStableIds[stableId] != 0)
                    {
                        int recentCount = recentFromEmitCountPerComp[i];
                        if (recentCount == particleCount && particleCount > 0)
                        {
                            int newStableId = _nextStableId++;
                            _compToStableId[i] = newStableId;
                            stableId = newStableId;

                            if (_stableIdToCenter == null)
                                _stableIdToCenter = new Dictionary<int, float3>(16);
                            _stableIdToCenter[stableId] = center;
                        }
                    }
                }
                int controllerId = GetOrCreateControllerIdForStableId(stableId);
                _tmpStableIdSet.Add(stableId);

                float3 toMain = float3.zero;
                bool allowRecallForController = _connect &&
                                                _recallEligibleStableIds.IsCreated &&
                                                stableId > 0 && stableId < _recallEligibleStableIds.Length &&
                                                _recallEligibleStableIds[stableId] != 0;
                if (allowRecallForController)
                {
                    float distToMain = math.length(center - mainCenter);
                    float speedFactor = math.saturate(distToMain / recallSlowdownDist);
                    float recallSpeed = math.lerp(recallMinSpeed, recallMaxSpeed, math.sqrt(speedFactor));
                    // 召回只使用水平方向，Y分量置0
                    float3 toMainXZ = mainCenter - center;
                    toMainXZ.y = 0;
                    float3 rawDir = math.normalizesafe(toMainXZ);
                    
                    bool useAvoid = recallAvoidance && _currentColliderCount > 0;
                    if (useAvoid)
                    {
                        toMain = ComputeAvoidedRecallVelocity(center, radius, mainCenter, rawDir, recallSpeed);
                    }
                    else
                    {
                        toMain = recallSpeed * rawDir;
                    }

                }

                float3 currentCenter = center;
                float prevGroundY = 0f;
                if (controllerId > 0 && controllerId < _controllerBuffer.Length && _controllerBuffer[controllerId].IsValid)
                {
                    currentCenter = math.lerp(_controllerBuffer[controllerId].Center, center, 0.5f);
                    prevGroundY = _controllerBuffer[controllerId].GroundY; // 保留上一帧的地面高度
                }

                _controllerBuffer[controllerId] = new ParticleController()
                {
                    Center = currentCenter,
                    Radius = radius,
                    Velocity = toMain,
                    Concentration = concentration * 2.0f,
                    ParticleCount = 0,
                    FramesWithoutParticles = 0,
                    IsValid = true,
                    GroundY = prevGroundY, // 保留上一帧的地面高度，避免被重置为0
                };

                _componentToGroup[i] = controllerId;
            }

            detachedParticleCountPerComp.Dispose();
            if (recentFromEmitCountPerComp.IsCreated)
                recentFromEmitCountPerComp.Dispose();
            
            // === 3. 更新粒子控制器 ID ===
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                
                // 场景水珠使用主控制器
                if (p.SourceId >= 0 || p.Type == ParticleType.SceneDroplet)
                {
                    p.ControllerId = 0;
                    p.StableId = 0;
                    _particles[i] = p;
                    continue;
                }
                
                // 主体和休眠粒子使用主控制器
                if (p.Type == ParticleType.MainBody || p.Type == ParticleType.Dormant)
                {
                    p.ControllerId = 0;
                    p.StableId = 0;
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
                
                // 【关键】刚从发射状态转换的分离粒子：使用 FramesOutsideMain 作为 CCA 保护期
                // 在保护期内保持原 ControllerId，不被 CCA 重新分类为主体
                bool hasEmitController = p.ControllerId > 0 && p.Type == ParticleType.Separated;
                bool inEmitProtection = hasEmitController && p.FramesOutsideMain < 60; // 60帧 ≈ 1秒保护期

                int prevControllerId = p.ControllerId;
                int prevStableId = p.StableId;
                
                // 分离/发射粒子：根据 ClusterId 映射到 CCA 分组的控制器
                int clusterId = p.ClusterId;
                if (clusterId > 0 && clusterId <= compCount)
                {
                    int newGroupId = _componentToGroup[clusterId - 1];
                    int newStableId = GetStableIdForComponent(clusterId - 1);
                    if (inEmitProtection && newGroupId == 0)
                    {
                        p.FramesOutsideMain++;
                        _particles[i] = p;
                        continue;
                    }

                    // 映射到 0 表示 CCA 认为该组件属于“主体连通块/过近”，但不应在这里强制回归主体。
                    // 回归主体只能通过 MergeContactingParticles/EnableRecall。
                    p.ControllerId = (newGroupId == 0) ? prevControllerId : newGroupId;
                    p.StableId = (newGroupId == 0) ? prevStableId : newStableId;
                    if (inEmitProtection)
                    {
                        p.FramesOutsideMain++;
                    }

                    _particles[i] = p;
                }
                else
                {
                    if (inEmitProtection)
                    {
                        p.FramesOutsideMain++;
                        _particles[i] = p;
                        continue;
                    }

                    // 没有有效的 ClusterId：不强制回归主体，优先保留原控制器，避免被主体“吸回”。
                    // 如果没有原控制器，则回退到 0，但仍保持 Separated/Emitted 类型，等待 Merge/Recall。
                    int fallbackControllerId = prevControllerId > 0 ? prevControllerId : 0;
                    p.ControllerId = fallbackControllerId;
                    p.StableId = prevStableId > 0 ? prevStableId : 0;
                    _particles[i] = p;
                }
            }

            // === 4. 更新自由飞行粒子和保护期粒子的控制器中心 ===
            // 收集每个控制器的自由飞行/保护期粒子质心
            var freeParticleCentroid = new NativeArray<float3>(_controllerBuffer.Length, Allocator.Temp);
            var freeParticleCount = new NativeArray<int>(_controllerBuffer.Length, Allocator.Temp);

            if (_controllerFreeFramesCounts == null || _controllerFreeFramesCounts.Length < _controllerBuffer.Length)
                _controllerFreeFramesCounts = new int[_controllerBuffer.Length];
            else
                System.Array.Clear(_controllerFreeFramesCounts, 0, _controllerBuffer.Length);

            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                // 自由飞行粒子 或 保护期内的分离粒子（ControllerId > 0 且 FramesOutsideMain < 60）
                bool isFreeFlying = p.FreeFrames > 0;
                bool isInProtection = p.Type == ParticleType.Separated && p.ControllerId > 0 && p.FramesOutsideMain < 60;
                if ((isFreeFlying || isInProtection) && p.ControllerId > 0 && p.ControllerId < _controllerBuffer.Length)
                {
                    freeParticleCentroid[p.ControllerId] += p.Position;
                    freeParticleCount[p.ControllerId]++;

                    if (isFreeFlying)
                        _controllerFreeFramesCounts[p.ControllerId]++;
                }
            }
            // 更新控制器中心跟随自由飞行/保护期粒子
            for (int c = 1; c < _controllerBuffer.Length; c++)
            {
                if (freeParticleCount[c] > 0)
                {
                    var ctrl = _controllerBuffer[c];
                    float3 newCenter = freeParticleCentroid[c] / freeParticleCount[c];
                    
                    float3 oldCenter = ctrl.Center;
                    float3 lerpedCenter = math.lerp(ctrl.Center, newCenter, 0.3f); // 平滑过渡
                    ctrl.Center = lerpedCenter;
                    ctrl.IsValid = true; // 确保控制器保持有效
                    ctrl.ParticleCount = freeParticleCount[c]; // 临时设置粒子数，防止被标记为无效
                    _controllerBuffer[c] = ctrl;
                }
            }
            
            // === 5. 统计每个控制器的实际粒子数 ===
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                bool isFreeFlying = p.FreeFrames > 0;
                bool isInProtection = p.Type == ParticleType.Separated && p.ControllerId > 0 && p.FramesOutsideMain < 60;
                if (isFreeFlying || isInProtection)
                    continue;
                if (p.ControllerId >= 0 && p.ControllerId < _controllerBuffer.Length)
                {
                    var ctrl = _controllerBuffer[p.ControllerId];
                    ctrl.ParticleCount++;
                    _controllerBuffer[p.ControllerId] = ctrl;
                }
            }
            
            freeParticleCentroid.Dispose();
            freeParticleCount.Dispose();
            
            
            // 召回停止条件：只在5秒超时时自动停止（可以随时重新按按钮启动）
            if (_connect && Time.time - _connectStartTime > 5f)
            {
                if (componentDebug)
                    Debug.Log($"[Recall停止] 5秒超时");
                _connect = false;
            }
            
            PerformanceProfiler.Begin("Control_AutoSeparate");
            AutoSeparateDistantParticles(mainCenter, mainRadius);
            PerformanceProfiler.End("Control_AutoSeparate");
            
            PerformanceProfiler.Begin("Control_MergeContact");
            MergeContactingParticles(mainCenter, mainRadius * mainOverlapThreshold);
            PerformanceProfiler.End("Control_MergeContact");
            
            PerformanceProfiler.Begin("Control_RearrangeInstances");
            RearrangeInstances();
            PerformanceProfiler.End("Control_RearrangeInstances");

            FadeOutSmallSeparatedControllers();
            
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
                if (p.Type == ParticleType.MainBody || p.Type == ParticleType.Dormant) continue;
                
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
            if (culledCount > 0)
            {
                Debug.LogWarning($"[DistanceCulling] 休眠了 {culledCount} 个过远分离粒子（距离>{maxParticleDistance:F0}），当前活跃={activeParticles}");
            }
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

            int dropletActiveCount = _dropletSubsystem.ActiveCount;
            bool hasCandidates = dropletActiveCount > 0;
            if (!hasCandidates)
            {
                for (int i = 0; i < count; i++)
                {
                    var p = _particles[i];
                    if (p.Type == ParticleType.MainBody || p.Type == ParticleType.Dormant)
                        continue;
                    if (p.FreeFrames > 0)
                        continue;

                    bool isSceneDroplet = p.SourceId >= 0;
                    if (!isSceneDroplet && p.ControllerId <= 0)
                        continue;

                    hasCandidates = true;
                    break;
                }
            }

            if (!hasCandidates)
                return;
            
            // ========== 性能优化：为主体粒子构建临时空间哈希 ==========
            // 将 O(n×m) 暴力遍历优化为 O(n+m)
            // 所有粒子统一使用世界坐标
            if (_mergeMainBodyLut.Capacity < count)
                _mergeMainBodyLut.Capacity = count;
            _mergeMainBodyLut.Clear();
            var mainBodyLut = _mergeMainBodyLut;
            int mainBodyCount = 0;
            float3 firstMainBodyPos = float3.zero;
            int3 firstMainBodyCoord = int3.zero;
            
            for (int i = 0; i < count; i++)
            {
                if (_particles[i].Type != ParticleType.MainBody) continue;
                float3 pos = _particles[i].Position;
                int3 coord = PBF_Utils.GetCoord(pos);
                int key = PBF_Utils.GetKey(coord);
                mainBodyLut.Add(key, i);
                if (mainBodyCount == 0)
                {
                    firstMainBodyPos = pos;
                    firstMainBodyCoord = coord;
                }
                mainBodyCount++;
            }

            // 预过滤：接触融合只发生在 contactDist (≈h) 范围内
            // 门控半径 = 主体半径 + 水珠源半径 + 接触距离 + 小余量
            float contactDist = math.sqrt(contactDist2);  // ≈ 1.0

            _absorbedSourceIds.Clear();
            if (allSources != null && allSources.Count > 0)
            {
                if (_absorbedFromSourceCounts == null || _absorbedFromSourceCounts.Length < allSources.Count)
                    System.Array.Resize(ref _absorbedFromSourceCounts, allSources.Count);
            }

            int sourceCount = allSources != null ? allSources.Count : 0;
            bool enableSourceGate = sourceCount > 0 && _sourceControllers.IsCreated && _sourceControllers.Length >= sourceCount;
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
                    float3 srcCenter = (float3)src.transform.position * PBF_Utils.InvScale;
                    float srcRadius = math.max(2f, src.AdaptiveRadius * PBF_Utils.InvScale);
                    
                    // 门控：水珠源中心到主体中心的距离 < 主体半径 + 水珠半径 + 接触距离 + 余量
                    float gateRadius = mergeRadius + srcRadius + contactDist * 2;
                    float srcDistToMain = math.length(srcCenter - mainCenter);
                    
                    if (srcDistToMain <= gateRadius)
                        _sourceMayContact[s] = true;
                }
            }

            // 添加调试统计
            int separatedChecked = 0;
            int separatedInRange = 0;
            int separatedMerged = 0;
            int noMainBodyNearby = 0;  // 统计没有主体粒子在附近的情况
            float minDistToMain = float.MaxValue;  // 最近的分离粒子到主体的距离
            
            int skippedFreeFrames = 0;
            int skippedNoController = 0;
            for (int i = 0; i < count; i++)
            {
                // 只处理分离粒子
                if (_particles[i].Type == ParticleType.MainBody || _particles[i].Type == ParticleType.Dormant) continue;
                
                // 跳过还在自由飞行的粒子（FreeFrames > 0）
                if (_particles[i].FreeFrames > 0) 
                {
                    skippedFreeFrames++;
                    continue;
                }
                
                bool isSceneDroplet = _particles[i].SourceId >= 0;
                
                // 跳过没有独立控制器的分离粒子（ControllerId <= 0）
                // 因为避障只对有控制器的粒子有效，没控制器的粒子不参与融合
                if (!isSceneDroplet && _particles[i].ControllerId <= 0)
                {
                    skippedNoController++;
                    continue;
                }
                separatedChecked++;
                
                // 分离粒子的 Position 是世界坐标
                float3 pos = _particles[i].Position;
                float distToMain2 = math.lengthsq(pos - mainCenter);
                
                // 场景水珠的特殊处理：接触主体时可以融合
                if (isSceneDroplet)
                {
                    // 场景水珠使用更大的检测范围
                    float dropletMergeRadius2 = mergeRadius2 * 4; // 2倍半径
                    if (distToMain2 > dropletMergeRadius2)
                        continue;
                }
                else
                {
                    // 普通分离粒子的合并检测（接触融合）
                    if (distToMain2 > mergeRadius2)
                        continue;
                }
                    
                separatedInRange++;

                // 使用空间哈希加速接触检测（O(1) 邻域查询）
                bool shouldMerge = false;
                int3 coord = PBF_Utils.GetCoord(pos);
                
                // 遍历 3x3x3 邻域
                int contactMainIdx = -1;
                float actualContactDist = 0;
                for (int dz = -1; dz <= 1 && !shouldMerge; ++dz)
                for (int dy = -1; dy <= 1 && !shouldMerge; ++dy)
                for (int dx = -1; dx <= 1 && !shouldMerge; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + new int3(dx, dy, dz));
                    if (mainBodyLut.TryGetFirstValue(key, out int j, out var it))
                    {
                        do
                        {
                            if (i == j) continue;
                            float r2 = math.lengthsq(pos - _particles[j].Position);
                            if (r2 <= contactDist2)
                            {
                                shouldMerge = true;
                                contactMainIdx = j;
                                actualContactDist = math.sqrt(r2);
                                break;
                            }
                        } while (mainBodyLut.TryGetNextValue(out j, ref it));
                    }
                }

                if (!shouldMerge) 
                {
                    noMainBodyNearby++;
                    float dist = math.sqrt(distToMain2);
                    minDistToMain = math.min(minDistToMain, dist);
                    continue;
                }
                
                separatedMerged++;

                var p = _particles[i];
                int sourceId = p.SourceId;
                
                // 使用ParticleStateManager合并粒子
                ParticleStateManager.ConvertToMainBody(ref p, mainCenter);
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
            }
            
            // ========== 处理水珠独立分区 [8192-16383] ==========
            // 复用与分离粒子相同的接触融合逻辑
            {
                _dropletSubsystem.GetDebugInfo(out int dropletActive, out _);
                
                if (dropletActive > 0)
                {
                    int dropletStart = DropletSubsystem.DROPLET_START;
                    // 只遍历活跃水珠范围，而非整个分区（性能优化：从8192次降到实际活跃数）
                    int dropletEnd = dropletStart + dropletActive - 1;
                    
                    for (int i = dropletStart; i <= dropletEnd; i++)
                    {
                        if (_particles[i].Type != ParticleType.SceneDroplet) 
                        {
                            continue; // 跳过已吸收或未激活的粒子
                        }
                        
                        var p = _particles[i];
                        // 【关键】保护期内的水珠不参与吸收（否则会出现“设置了FreeFrames但仍瞬间吸收”的循环问题）
                        if (p.FreeFrames > 0)
                        {
                            continue;
                        }
                        int sourceId = p.SourceId;
                        
                        // sourceId 门控
                        if (enableSourceGate && sourceId >= 0 && sourceId < sourceCount && !_sourceMayContact[sourceId])
                        {
                            continue;
                        }
                        
                        float3 dropletPos = p.Position;
                        
                        // 预过滤：使用与分离粒子相同的 mergeRadius2
                        float distToMain2 = math.lengthsq(dropletPos - mainCenter);
                        float distToMain = math.sqrt(distToMain2);
                        
                        if (distToMain2 > mergeRadius2 * 4) 
                        {
                            continue;
                        }
                        
                        // 使用空间哈希加速接触检测（O(1) 邻域查询）
                        bool shouldMerge = false;
                        int3 dropletCoord = PBF_Utils.GetCoord(dropletPos);
                        float minR2ToMainBody = float.MaxValue;
                        int neighborsFound = 0;
                        
                        for (int dz = -1; dz <= 1 && !shouldMerge; ++dz)
                        for (int dy = -1; dy <= 1 && !shouldMerge; ++dy)
                        for (int dx = -1; dx <= 1 && !shouldMerge; ++dx)
                        {
                            int key = PBF_Utils.GetKey(dropletCoord + new int3(dx, dy, dz));
                            if (mainBodyLut.TryGetFirstValue(key, out int j, out var it))
                            {
                                do
                                {
                                    neighborsFound++;
                                    float r2 = math.lengthsq(dropletPos - _particles[j].Position);
                                    minR2ToMainBody = math.min(minR2ToMainBody, r2);
                                    if (r2 <= contactDist2)
                                    {
                                        shouldMerge = true;
                                        break;
                                    }
                                } while (mainBodyLut.TryGetNextValue(out j, ref it));
                            }
                        }
                        
                        if (!shouldMerge) 
                        {
                            continue;
                        }
                    
                        // ========== 粒子迁移：从水珠分区迁移到主体分区 ==========
                        // 检查主体分区是否有空间
                        if (activeParticles >= DropletSubsystem.DROPLET_START)
                        {
                            continue;
                        }
                        
                        // 从水珠分区获取粒子数据并移除
                        if (!_dropletSubsystem.MigrateToMainBody(i, out float3 migratedPos, out float3 migratedVel))
                        {
                            continue; // 迁移失败，跳过
                        }
                        
                        // 在主体分区末尾创建新的 MainBody 粒子
                        int newMainIdx = activeParticles;
                        _particles[newMainIdx] = new Particle
                        {
                            Position = migratedPos,
                            Type = ParticleType.MainBody,
                            ControllerId = 0,
                            StableId = 0,
                            FreeFrames = 0,
                            SourceId = -1
                        };
                        // 【修复】融合时清零速度，防止高速粒子冲入主体导致爆炸
                        _velocityBuffer[newMainIdx] = float3.zero;
                        activeParticles++;
                        
                        dropletMergedCount++;
                        if (_absorbedFromSourceCounts != null && sourceId >= 0 && sourceId < _absorbedFromSourceCounts.Length)
                        {
                            if (_absorbedFromSourceCounts[sourceId] == 0)
                                _absorbedSourceIds.Add(sourceId);
                            _absorbedFromSourceCounts[sourceId]++;
                        }
                    }
                }
                
            }
            
            // 更新场景水珠源的剩余数量
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
        public void EmitParticles()
        {
            // SlimeVolume 必须存在
            if (_slimeVolume == null)
            {
                Debug.LogError("[Emit] SlimeVolume 未绑定，无法发射");
                return;
            }
            
            // 检查SlimeVolume是否允许发射
            if (!_slimeVolume.CanEmit(emitBatchSize))
            {
                Debug.LogWarning($"[Emit] 体积不足，无法发射 (当前: {_slimeVolume.CurrentVolume}, 最小: {_slimeVolume.MinVolume})");
                return;
            }
            
            // 获取主控制器中心（ID=0的粒子所属的控制器）
            float3 center = float3.zero;
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
            {
                center = _controllerBuffer[0].Center;
            }
            else
            {
                Debug.LogWarning("[Emit] 没有控制器也没有trans引用");
                return;
            }
            
            // 计算鼠标方向
            float3 emitDirection = new float3(0, 0, 1);
            var mainCam = Camera.main;
            if (mainCam != null)
            {
                Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);
                Vector3 planePoint = trans != null ? trans.position : (Vector3)(center * PBF_Utils.Scale);
                Plane groundPlane = new Plane(Vector3.up, planePoint);

                if (groundPlane.Raycast(ray, out float distance) && distance > 0f)
                {
                    Vector3 mouseWorldPos = ray.GetPoint(distance);
                    float3 toMouse = (float3)(mouseWorldPos * PBF_Utils.InvScale) - center;
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
                Debug.LogWarning("[Emit] 没有主体粒子可发射");
                return;
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
            float3 finalEmitDir = math.normalizesafe(emitDirection * 0.7f + new float3(0, 0.5f, 0));
            
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
            float3 spawnCenter = center + emitDirection * (mainRadius * 0.5f);
            spawnCenter.y = center.y + mainRadius * 0.5f;
            
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

                float3 emitVel = finalEmitDir * emitSpeed * PBF_Utils.InvScale;
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
                        p.ControllerId = 0;
                        _particles[i] = p;
                        _velocityBuffer[i] = float3.zero;
                    }
                }
                if (componentDebug)
                    Debug.Log($"[Emit] 发射数量不足 ({emitted} < {minSeparateClusterSize})，已撤销");
                return;
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
                
                int newControllerId = _controllerBuffer.Length;
                _controllerBuffer.Add(new ParticleController
                {
                    Center = emitCentroid, // 使用实际质心
                    Radius = emitControllerRadius, // 足够大的半径包住所有发射粒子
                    Velocity = finalEmitDir * emitSpeed * PBF_Utils.InvScale, // 控制器跟随发射方向移动
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
                        p.ControllerId = newControllerId;
                        _particles[i] = p;
                    }
                }
            }
            
            // 强制更新体积统计
            if (_slimeVolume != null && emitted > 0)
            {
                _slimeVolume.UpdateFromParticles(_particles, true);
            }
        }
        
        private void RearrangeInstances()
        {
            int rayInsectCallCount = 0;
            float rearrangeStart = Time.realtimeSinceStartup;
            
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
                    UpdateInstanceController(instanceID, controllerID);
                    rayInsectCallCount++;
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
                    float3 pos = trans.position * PBF_Utils.InvScale;
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

                    int controllerID = _slimeInstances[_controlledInstance].ControllerID;
                    UpdateInstanceController(_controlledInstance, controllerID);
                    rayInsectCallCount++;
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
                        UpdateInstanceController(instanceID, 0);
                        rayInsectCallCount++;
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
                    UpdateInstanceController(instanceID, controllerID);
                    rayInsectCallCount++;
                }
                
                for (int i = 0; i < _controllerBuffer.Length; i++)
                {
                    if (used[i]) continue;
                    var controller = _controllerBuffer[i];
                    // 跳过无效或粒子不足的控制器（主体控制器始终创建实例，25粒子才算一组）
                    const int minParticlesForInstance = 25;
                    if (i > 0 && (!controller.IsValid || controller.ParticleCount < minParticlesForInstance)) continue;
                    float3 dir = math.normalizesafe(
                        math.lengthsq(controller.Velocity) < 1e-3f
                            ? (float3)(trans.position * PBF_Utils.InvScale) - controller.Center
                            : controller.Velocity,
                        new float3(1, 0, 0));
                    // 动态计算射线起点偏移，适应不同大小的史莱姆（使用独立的脸部高度参数）
                    float yOffset = controller.Radius * faceHeightFactor;
                    new Effects.RayInsectJob
                    {
                        GridLut = _gridLut,
                        Grid = _gridTempBuffer, // 使用blur后的网格，与MarchingCubes表面一致
                        Result = _boundsBuffer,
                        Threshold = threshold,
                        Pos = controller.Center + new float3(0, yOffset, 0),
                        Dir = dir,
                        MinPos = minPos,
                        MaxRadius = controller.Radius,
                    }.Schedule().Complete();
                    rayInsectCallCount++;
                    
                    float3 newPos = _boundsBuffer[0];
                    if (!math.all(math.isfinite(newPos)))
                        newPos = controller.Center + dir * controller.Radius * 0.5f;
                    
                    SlimeInstance slime = new SlimeInstance()
                    {
                        Active = true,
                        Center =  controller.Center,
                        Radius = controller.Radius,
                        Dir = dir,
                        Pos = newPos,
                        ControllerID = i,
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
                
                int ctrlId = slime.ControllerID;
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
            
            float rearrangeMs = (Time.realtimeSinceStartup - rearrangeStart) * 1000f;
            if (componentDebug && (Time.frameCount % 60 == 0 || rearrangeMs > 5f))
            {
                // 统计有效控制器数量
                int validControllers = 0;
                string ctrlInfo = "";
                for (int c = 0; c < _controllerBuffer.Length; c++)
                {
                    var ctrl = _controllerBuffer[c];
                    // 25粒子才算一组
                    const int minParticlesForInstance = 25;
                    if (c == 0 || (ctrl.IsValid && ctrl.ParticleCount >= minParticlesForInstance))
                    {
                        validControllers++;
                        ctrlInfo += $"C{c}:{ctrl.ParticleCount} ";
                    }
                }
                Debug.Log($"[RearrangeInstances] 耗时={rearrangeMs:F2}ms, RayInsectJob={rayInsectCallCount}, " +
                         $"有效控制器={validControllers}/{_controllerBuffer.Length}, activeInstances={_slimeInstances.Length - _instancePool.Count}");
                Debug.Log($"[RearrangeInstances] 控制器粒子数: {ctrlInfo}");
            }
        }

        private void UpdateInstanceController(int instanceID, int controllerID)
        {
            var slime = _slimeInstances[instanceID];
            var controller = _controllerBuffer[controllerID];
            
            bool isControlled = instanceID == _controlledInstance;
            if (isControlled)
                controller.Velocity = _velocity * PBF_Utils.InvScale;

            slime.ControllerID = controllerID;
            // 受控实例用更快的平滑速度，减少滞后
            float speed = isControlled ? 0.25f : 0.1f;
            slime.Radius = math.lerp(slime.Radius, controller.Radius, speed);
            slime.Center = math.lerp(slime.Center, controller.Center, speed);
            Vector3 vec = controller.Velocity;
            Vector3 horizVec = new Vector3(vec.x, 0f, vec.z);
            if (horizVec.sqrMagnitude > 1e-4f)
            {
                var newDir = Vector3.Slerp(slime.Dir, horizVec.normalized, speed);
                newDir.y = 0f;
                slime.Dir = newDir.sqrMagnitude > 1e-6f ? newDir.normalized : Vector3.forward;
            }
            else
            {
                var newDir = slime.Dir;
                newDir.y = 0f;
                slime.Dir = newDir.sqrMagnitude > 1e-6f ? newDir.normalized : Vector3.forward;
            }
            
            // 方案C：射线检测表面 + 法线内嵌（贴合变形表面且不穿出）
            
            // 【关键】使用粒子质心而不是 transform 位置，跳跃时更准确
            float3 particleCentroid = float3.zero;
            int mainBodyCount = 0;
            for (int i = 0; i < activeParticles; i++)
            {
                if (_particles[i].ControllerId == controllerID && _particles[i].Type == ParticleType.MainBody)
                {
                    particleCentroid += _particles[i].Position;
                    mainBodyCount++;
                }
            }
            float3 eyeCenter = mainBodyCount > 0 ? particleCentroid / mainBodyCount : controller.Center;
            
            // 眼睛方向只跟随水平速度，忽略垂直分量（跳跃时保持朝向稳定）
            float3 horizVel = new float3(controller.Velocity.x, 0, controller.Velocity.z);
            float3 horizDir = new float3(slime.Dir.x, 0, slime.Dir.z);
            if (math.lengthsq(horizVel) > 1e-4f)
            {
                horizDir = math.normalizesafe(horizVel, horizDir);
            }
            float3 rayDir = math.normalizesafe(horizDir, new float3(0, 0, 1));
            
            // 直接从粒子质心发射射线（质心已经反映真实垂直分布，不需要额外偏移）
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
            
            float3 surfacePos = _boundsBuffer[0];
            float3 newPos;
            
            if (math.all(math.isfinite(surfacePos)))
            {
                // 找到表面：沿射线方向向内偏移（确保在表面内部）
                float actualSurfaceDist = math.length(surfacePos - eyeCenter);
                float insetDepth = actualSurfaceDist * 0.30f;
                newPos = surfacePos - rayDir * insetDepth;
            }
            else
            {
                // 没找到表面：fallback到质心投影
                newPos = eyeCenter + rayDir * (controller.Radius * 0.7f);
            }
            
            // 平滑过渡眼睛位置
            float posLerpSpeed = isControlled ? 0.3f : 0.15f;
            slime.Pos = Vector3.Lerp(slime.Pos, newPos, posLerpSpeed);
            
            _slimeInstances[instanceID] = slime;
            
            if (isControlled)
            {
                // 只回写 Velocity，不改 Center（避免与 FixedUpdate 开头设置的 Center 不一致）
                _controllerBuffer[controllerID] = controller;
            }
        }

        /// <summary>
        /// 将相对坐标转换为世界坐标用于渲染（连续打包：主体 + 水珠）
        /// </summary>
        /// <returns>总渲染粒子数（activeParticles + dropletCount）</returns>
        private int ConvertToWorldPositionsForRendering()
        {
            // 1. 主体粒子 [0, activeParticles)
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                float3 worldPos = Simulation_PBF.GetWorldPosition(p, _controllerBuffer, _sourceControllers);
                p.Position = worldPos;
                _particlesRenderBuffer[i] = p;
            }
            
            // 2. 水珠紧跟主体后面 [activeParticles, activeParticles + dropletCount)
            // 水珠的 Position 已经是内部坐标（世界坐标 * InvScale）
            // 与主体粒子通过 GetWorldPosition() 得到的内部坐标一致，无需转换
            int dropletCount = _dropletSubsystem.CopyToRenderBuffer(_particlesRenderBuffer, activeParticles);
            
            if (componentDebug && dropletCount > 0 && Time.frameCount % 60 == 0)
            {
                _dropletSubsystem.GetDebugInfo(out int dropletActive, out _);
            }
            
            return activeParticles + dropletCount;
        }
        
        /// <summary>
        /// 为渲染构建邻域查询表（包含主体 + 水珠）
        /// </summary>
        private void BuildRenderLut(int totalParticles)
        {
            // 使用临时哈希数组，避免破坏模拟用的 _hashes
            using var renderHashes = new NativeArray<int2>(totalParticles, Allocator.TempJob);
            
            // 使用渲染专用邻域表，不破坏模拟用的 _lut
            _renderLut.Clear();
            
            // 使用渲染专用 HashJob（_particlesRenderBuffer 已是世界坐标）
            new Reconstruction.HashRenderJob
            {
                Ps = _particlesRenderBuffer,
                Hashes = renderHashes,
            }.Schedule(totalParticles, batchCount).Complete();
            
            // 排序
            renderHashes.SortJob(new PBF_Utils.Int2Comparer()).Schedule().Complete();
            
            // Shuffle _particlesRenderBuffer 以匹配排序后的顺序
            // 使用 _particlesTemp 作为临时缓冲
            NativeArray<Particle>.Copy(_particlesRenderBuffer, _particlesTemp, totalParticles);
            new Reconstruction.ShuffleRenderJob
            {
                Hashes = renderHashes,
                PsRaw = _particlesTemp,
                PsShuffled = _particlesRenderBuffer,
            }.Schedule(totalParticles, batchCount).Complete();
            
            // 构建 LUT
            new Simulation_PBF.BuildLutJob
            {
                Hashes = renderHashes,
                Lut = _renderLut
            }.Schedule().Complete();
        }

        private int GetGroupParticleCount(int groupId)
        {
            int count = 0;
            for (int i = 0; i < activeParticles; i++)
            {
                if (_particles[i].ControllerId == groupId)
                    count++;
            }
            return count;
        }


        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
                return;
            
            if (!componentDebug)
                return;
            
            // 模拟边界（黄色）
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(_bounds.center, _bounds.size);
            
            // 主体控制器范围（青色）
            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0 && trans != null)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.6f);
                Gizmos.DrawWireSphere(trans.position, _controllerBuffer[0].Radius * PBF_Utils.Scale);
            }
            
            // 网格块（gridDebug）
            if (gridDebug)
            {
                Gizmos.color = Color.blue;
                for (var i = 0; i < blockNum; i++)
                {
                    var block = _blockBuffer[i];
                    Vector3 blockMinPos = new Vector3(block.x, block.y, block.z) * PBF_Utils.CellSize * 0.4f +
                                          _bounds.min;
                    Vector3 size = new Vector3(PBF_Utils.CellSize, PBF_Utils.CellSize, PBF_Utils.CellSize) * 0.4f;
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
                    var size = (c.BoundsMax - c.BoundsMin) * PBF_Utils.Scale * PBF_Utils.CellSize;
                    var center = c.Center * PBF_Utils.Scale * PBF_Utils.CellSize;
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
                    Gizmos.DrawWireCube(c.Center * PBF_Utils.Scale, c.Extent * PBF_Utils.Scale * 2);
                }
            }
        }
        
        #region 碰撞体采集（近场缓存）
        
        /// <summary>
        /// 刷新近场碰撞体缓存：查询史莱姆和水珠附近的碰撞体
        /// </summary>
        private void RefreshNearbyColliders()
        {
            // 获取史莱姆中心位置（世界坐标）
            Vector3 slimeCenter = trans != null ? trans.position : Vector3.zero;
            
            // 扩大查询半径以包含水珠（水珠可能离主体较远）
            float expandedRadius = colliderQueryRadius;
            
            // 考虑所有活跃的水珠源位置
            if (allSources != null && allSources.Count > 0)
            {
                foreach (var source in allSources)
                {
                    if (source != null && source.State == DropWater.DropletSourceState.Simulated)
                    {
                        float distToSource = Vector3.Distance(slimeCenter, source.transform.position);
                        expandedRadius = Mathf.Max(expandedRadius, distToSource + 5f); // 额外5米覆盖水珠散布范围
                    }
                }
            }
            
            // 查询扩大范围内的碰撞体
            int foundCount = UnityEngine.Physics.OverlapSphereNonAlloc(
                slimeCenter, 
                expandedRadius, 
                _overlapResults, 
                colliderLayers
            );
            
            if (colliderCollectDebug)
                Debug.Log($"[RefreshNearbyColliders] center={slimeCenter}, radius={colliderQueryRadius}, found={foundCount}");
            
            // 写入 _colliderBuffer
            int validCount = 0;
            for (int i = 0; i < foundCount && validCount < maxNearbyColliders; i++)
            {
                var col = _overlapResults[i];
                if (col == null) continue;
                
                // 跳过玩家角色自身的碰撞体（检查整个角色层级）
                if (trans != null && col.transform.root == trans.root)
                    continue;
                
                var info = col.GetComponent<SlimeColliderInfo>();
                int colliderType = info != null ? (int)info.colliderType : ColliderTypes.Ground;
                float friction = info != null ? info.surfaceFriction : 0.3f;
                
                // 计算 extent
                float3 extentRaw = (float3)(col.bounds.extents * PBF_Utils.InvScale);
                float3 margin = new float3(1f, 1f, 1f);
                
                _colliderBuffer[validCount] = new MyBoxCollider()
                {
                    Center = col.bounds.center * PBF_Utils.InvScale,
                    Extent = extentRaw + margin,
                    Type = colliderType,
                    Friction = friction,
                };
                
                validCount++;
                
                if (colliderCollectDebug)
                    Debug.Log($"  [{validCount}] {col.gameObject.name}: Type={colliderType}, Layer={col.gameObject.layer}");
            }
            
            _currentColliderCount = validCount;
            
            if (colliderCollectDebug || (startupProfileDebug && Time.frameCount < 10))
                Debug.Log($"[RefreshNearbyColliders] 有效碰撞体数={_currentColliderCount}/{maxNearbyColliders}");
        }
        
        /// <summary>
        /// 为每个有效控制器独立射线检测地面高度，存入 ctrl.GroundY
        /// </summary>
        private void UpdateControllerGroundHeights()
        {
            if (!_controllerBuffer.IsCreated || _controllerBuffer.Length == 0)
                return;
            
            float particleRadiusWorld = 0.05f; // 粒子半径（世界坐标）
            float fallbackGroundY = -10f; // 后备地面高度（模拟坐标）
            
            for (int c = 0; c < _controllerBuffer.Length; c++)
            {
                var ctrl = _controllerBuffer[c];
                
                // 主体控制器（ID=0）始终检测地面；分离控制器需要 IsValid
                bool shouldUpdate = (c == 0) || ctrl.IsValid;
                if (!shouldUpdate)
                {
                    ctrl.GroundY = fallbackGroundY;
                    _controllerBuffer[c] = ctrl;
                    continue;
                }
                
                // 从控制器中心向下射线检测
                Vector3 worldPos = (Vector3)(ctrl.Center * PBF_Utils.Scale);
                float rayStartY = worldPos.y + 2f; // 抬高起点
                worldPos.y = rayStartY;
                
                if (UnityEngine.Physics.Raycast(worldPos, Vector3.down, out RaycastHit hit, 30f, colliderLayers))
                {
                    float oldGroundY = ctrl.GroundY;
                    // 地面高度 + 粒子半径，转为模拟坐标
                    float newGroundY = (hit.point.y + particleRadiusWorld) * PBF_Utils.InvScale;
                    
                    // 平滑过渡：避免射线命中护动导致地面高度突变
                    // 只有新值更低时才立即采纳（避免穿透地面），否则平滑过渡
                    if (oldGroundY <= 0 || newGroundY < oldGroundY)
                    {
                        ctrl.GroundY = newGroundY;
                    }
                    else
                    {
                        ctrl.GroundY = math.lerp(oldGroundY, newGroundY, 0.1f); // 缓慢上升
                    }
                    
                    // 调试日志：检测地面高度突变（仅在实际应用后差异超过阈值时输出）
                    if (componentDebug && (Time.frameCount % 60 == 0) && c > 0 && math.abs(ctrl.GroundY - oldGroundY) > 0.5f)
                    {
                        Debug.Log($"[GroundDetect] C{c}: 地面变化 old={oldGroundY * PBF_Utils.Scale:F2} -> new={ctrl.GroundY * PBF_Utils.Scale:F2}, " +
                                  $"rayHit={newGroundY * PBF_Utils.Scale:F2}, hit={hit.collider.name}");
                    }
                }
                else
                {
                    // 未命中时使用控制器中心Y减去一定距离作为后备
                    float oldGroundY = ctrl.GroundY;
                    ctrl.GroundY = ctrl.Center.y - 20f;
                    
                    // 调试日志：射线未命中
                    if (componentDebug && (Time.frameCount % 60 == 0) && c > 0)
                    {
                        Debug.Log($"[GroundDetect] C{c}: 射线未命中! rayStart=({worldPos.x:F1},{rayStartY:F1},{worldPos.z:F1}), 使用后备={ctrl.GroundY * PBF_Utils.Scale:F2}");
                    }
                }
                
                _controllerBuffer[c] = ctrl;
            }
        }
        
        #endregion
        
        #region 召回避障
        
        /// <summary>
        /// 计算带避障的召回速度
        /// 新逻辑：基础速度 = 指向主体的速度，叠加避障 steering 力
        /// </summary>
        /// <param name="center">分离组中心（模拟坐标）</param>
        /// <param name="radius">分离组半径</param>
        /// <param name="mainCenter">主体中心（模拟坐标）</param>
        /// <param name="rawDir">原始朝向主体的方向（归一化）</param>
        /// <param name="speed">召回速度</param>
        /// <returns>处理过避障的召回速度向量</returns>
        private float3 ComputeAvoidedRecallVelocity(float3 center, float radius, float3 mainCenter, float3 rawDir, float speed)
        {
            // 基础速度：直接指向主体
            float3 baseVel = speed * rawDir;
            float3 steering = float3.zero;
            
            // XZ平面上的方向
            float2 dirXZ = math.normalizesafe(new float2(rawDir.x, rawDir.z));
            if (math.lengthsq(dirXZ) < 0.001f)
            {
                return baseVel; // 几乎垂直，不需要避障
            }

            float projectionCheckDist = recallObstacleCheckDist + radius;
            Vector3 originW = (Vector3)(center * PBF_Utils.Scale);
            Vector3 dirW = new Vector3(dirXZ.x, 0f, dirXZ.y);
            float sphereRadiusW = math.max(0.001f, radius * PBF_Utils.Scale);
            float castDistW = math.max(0.001f, projectionCheckDist * PBF_Utils.Scale);
            float keepDistW = math.max(0f, (radius + recallObstacleKeepDistanceMargin) * PBF_Utils.Scale);
            bool sphereHit = UnityEngine.Physics.SphereCast(originW, sphereRadiusW, dirW, out RaycastHit hit, castDistW, colliderLayers, QueryTriggerInteraction.Ignore);
            if (sphereHit)
            {
                float3 nSim = (float3)hit.normal;
                nSim.y = 0;
                nSim = math.normalizesafe(nSim);
                float3 vXZ = speed * rawDir;
                vXZ.y = 0;
                float intoWall = math.dot(vXZ, nSim);
                if (intoWall < 0f)
                    vXZ -= intoWall * nSim;
                float distRemainW = math.max(0f, hit.distance - keepDistW);
                float denomW = math.max(0.001f, castDistW - keepDistW);
                float slow01 = math.saturate(distRemainW / denomW);
                vXZ *= slow01;
                float push01 = 1f - slow01;
                vXZ += nSim * (speed * 0.35f * push01);
                baseVel.x = vXZ.x;
                baseVel.z = vXZ.z;
            }
            else
            {
                bool canOverlap = _overlapResults != null && _overlapResults.Length > 0;
                bool overlapped = canOverlap && UnityEngine.Physics.CheckSphere(originW, sphereRadiusW, colliderLayers, QueryTriggerInteraction.Ignore);
                if (overlapped)
                {
                    int overlapCount = UnityEngine.Physics.OverlapSphereNonAlloc(originW, sphereRadiusW, _overlapResults, colliderLayers, QueryTriggerInteraction.Ignore);
                    float bestDist2 = float.PositiveInfinity;
                    Vector3 bestPoint = originW;
                    for (int i = 0; i < overlapCount; i++)
                    {
                        var col = _overlapResults[i];
                        if (col == null)
                            continue;
                        if (trans != null && col.transform.root == trans.root)
                            continue;
                        Vector3 cp = col.ClosestPoint(originW);
                        float d2 = (originW - cp).sqrMagnitude;
                        if (d2 < bestDist2)
                        {
                            bestDist2 = d2;
                            bestPoint = cp;
                        }
                    }

                    Vector3 nW;
                    Vector3 delta = originW - bestPoint;
                    float deltaLen2 = delta.sqrMagnitude;
                    if (deltaLen2 > 1e-8f)
                        nW = delta / Mathf.Sqrt(deltaLen2);
                    else
                        nW = (dirW.sqrMagnitude > 1e-8f) ? (-dirW.normalized) : Vector3.up;

                    float3 nSim = (float3)nW;
                    nSim.y = 0;
                    nSim = math.normalizesafe(nSim);
                    float3 vXZ = speed * rawDir;
                    vXZ.y = 0;
                    float intoWall = math.dot(vXZ, nSim);
                    if (intoWall < 0f)
                        vXZ -= intoWall * nSim;
                    vXZ *= 0.15f;
                    vXZ += nSim * (speed * 0.50f);
                    baseVel.x = vXZ.x;
                    baseVel.z = vXZ.z;
                }
            }
            
            // 检测前方是否有障碍物阻挡
            float checkDist = recallObstacleCheckDist + radius;
            float closestBlockDist = float.MaxValue;
            int closestBlockIdx = -1;
            float3 closestBlockNormal = float3.zero;
            float closestBlockTopY = 0;
            
            for (int c = 0; c < _currentColliderCount; c++)
            {
                MyBoxCollider box = _colliderBuffer[c];
                
                // 扩展后的 AABB（考虑分离组半径）
                float3 expandedExtent = box.Extent + new float3(radius, 0, radius);
                
                // 射线-AABB相交测试（只考虑XZ平面）
                float3 rayOrigin = center;
                float3 rayDir = new float3(dirXZ.x, 0, dirXZ.y);
                
                // Slab method for ray-AABB intersection
                float3 invDir = 1f / (rayDir + new float3(0.0001f, 0.0001f, 0.0001f)); // 避免除零
                float3 t1 = (box.Center - expandedExtent - rayOrigin) * invDir;
                float3 t2 = (box.Center + expandedExtent - rayOrigin) * invDir;
                
                float3 tmin3 = math.min(t1, t2);
                float3 tmax3 = math.max(t1, t2);
                
                float tNear = math.max(math.max(tmin3.x, tmin3.z), 0); // 只用XZ
                float tFar = math.min(tmax3.x, tmax3.z);
                
                // 检查Y方向是否重叠（分离组高度范围与AABB高度范围）
                float groupBottom = center.y - radius;
                float groupTop = center.y + radius;
                float boxBottom = box.Center.y - box.Extent.y;
                float boxTop = box.Center.y + box.Extent.y;
                
                bool yOverlap = groupBottom < boxTop && groupTop > boxBottom;
                
                float3 deltaToBox = rayOrigin - box.Center;
                bool insideXZ = math.abs(deltaToBox.x) <= expandedExtent.x && math.abs(deltaToBox.z) <= expandedExtent.z;
                if (insideXZ && yOverlap)
                {
                    float penX = expandedExtent.x - math.abs(deltaToBox.x);
                    float penZ = expandedExtent.z - math.abs(deltaToBox.z);
                    float signX = math.sign(deltaToBox.x);
                    float signZ = math.sign(deltaToBox.z);
                    if (penX < penZ)
                        closestBlockNormal = new float3(signX == 0f ? -math.sign(rayDir.x) : signX, 0, 0);
                    else
                        closestBlockNormal = new float3(0, 0, signZ == 0f ? -math.sign(rayDir.z) : signZ);
                    closestBlockDist = 0f;
                    closestBlockIdx = c;
                    closestBlockTopY = boxTop;
                    continue;
                }
                
                if (tNear < tFar && tNear < checkDist && yOverlap)
                {
                    if (tNear < closestBlockDist)
                    {
                        closestBlockDist = tNear;
                        closestBlockIdx = c;
                        closestBlockTopY = boxTop;
                        
                        // 计算碰撞法线（基于最先碰到的面）
                        if (tmin3.x > tmin3.z)
                            closestBlockNormal = new float3(-math.sign(rayDir.x), 0, 0);
                        else
                            closestBlockNormal = new float3(0, 0, -math.sign(rayDir.z));
                    }
                }
            }
            
            // 如果没有阻挡，直接返回基础速度
            if (closestBlockIdx < 0)
            {
                float targetYOffset = math.min(radius, recallStepMaxHeight);
                float dyToMain = (mainCenter.y + targetYOffset) - center.y;
                if (dyToMain > 0f)
                {
                    float up = math.min(recallStepUpSpeed, dyToMain * recallHeightCompensation);
                    baseVel.y = math.max(baseVel.y, up);
                }
                return baseVel;
            }
            
            // === 阻挡处理：叠加 steering 力 ===
            
            // 1. 检查是否是可跨越的台阶
            float groupBottomY = center.y - radius;
            float stepHeight = closestBlockTopY - groupBottomY;
            // 只要障碍物顶部低于主体，就尝试向上
            bool canStepUp = stepHeight > 0 && stepHeight <= recallStepMaxHeight;
            bool mainIsHigher = mainCenter.y > center.y - 1f; // 主体在分离组上方或差不多高度
            
            if (canStepUp || mainIsHigher)
            {
                // 台阶/高度差处理：叠加向上的 steering
                float upStrength = recallStepUpSpeed;
                // 如果主体更高，根据高度差和配置系数增强向上力
                if (mainCenter.y > center.y)
                {
                    upStrength = math.max(upStrength, (mainCenter.y - center.y) * recallHeightCompensation);
                }
                steering.y = math.max(steering.y, upStrength);
            }
            
            // 2. 水平方向：叠加沿墙滑动的 steering
            float3 slideVec = rawDir - math.dot(rawDir, closestBlockNormal) * closestBlockNormal;
            slideVec.y = 0;
            float3 slideDir = math.normalizesafe(slideVec);
            
            // 如果滑动方向几乎为零（正对墙），尝试绕行
            if (math.lengthsq(slideDir) < 0.1f)
            {
                float3 perpDir = new float3(-rawDir.z, 0, rawDir.x);
                slideDir = math.normalizesafe(perpDir);
            }
            
            // 叠加水平 steering（强度与障碍物距离成反比）
            float proximityFactor = 1f - math.saturate(closestBlockDist / checkDist);
            float baseSlow = 1f - proximityFactor * 0.75f;
            if (canStepUp)
                baseSlow = math.min(baseSlow, 0.2f);
            baseSlow = math.saturate(baseSlow);
            baseVel.x *= baseSlow;
            baseVel.z *= baseSlow;
            steering += slideDir * speed * proximityFactor * recallSlideWeight;
            
            // 最终速度 = 基础速度 + steering
            float3 resultVel = baseVel + steering;
            
            return resultVel;
        }
        
        #endregion
        
        #region 配置管理
        
        /// <summary>
        /// 重置所有参数为默认值
        /// </summary>
        [ContextMenu("重置参数为默认值")]
        public void ResetToDefaults()
        {
            int count = ConfigResetHelper.ResetToDefaults(this);
            Debug.Log($"[Slime_PBF] 已重置 {count} 个参数为默认值");
        }
        
        #endregion
        
        #region 网格投票（CCA ID稳定）
        
        // 临时字典用于投票统计（避免每帧分配）
        private Dictionary<int, Dictionary<int, int>> _voteDict;  // newCompId -> (oldStableId -> count)
        private Dictionary<int, int> _compTotalCells;              // newCompId -> totalCells
        
        // CCA组件ID -> 稳定ID 映射（每帧更新，供Control()使用）
        private int[] _compToStableId;
        // 稳定ID -> 控制器索引 映射（跨帧持久，用于控制器绑定）
        private Dictionary<int, int> _stableIdToController;
        // 稳定ID -> 组件中心位置 映射（用于位置匹配）
        private Dictionary<int, float3> _stableIdToCenter;
        private HashSet<int> _tmpStableIdSet;
        private List<int> _tmpStableIdList;
        private HashSet<int> _tmpControllerIdSet;

        private int GetOrCreateControllerIdForStableId(int stableId)
        {
            if (_stableIdToController == null)
                _stableIdToController = new Dictionary<int, int>(16);
            if (_freeSeparatedControllerIds == null)
                _freeSeparatedControllerIds = new Stack<int>();

            if (_stableIdToController.TryGetValue(stableId, out int existingId) && existingId > 0 && existingId < _controllerBuffer.Length)
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

            _stableIdToController[stableId] = newId;
            return newId;
        }

        private void RefreshStableIdToSlotMapping()
        {
            int required = math.max(1, _nextStableId);

            if (!_stableIdToSlot.IsCreated || _stableIdToSlot.Length < required)
            {
                if (_stableIdToSlot.IsCreated)
                    _stableIdToSlot.Dispose();
                _stableIdToSlot = new NativeArray<int>(required, Allocator.Persistent);
            }

            for (int i = 0; i < _stableIdToSlot.Length; i++)
                _stableIdToSlot[i] = -1;
            _stableIdToSlot[0] = 0;

            if (_stableIdToController == null)
                return;

            foreach (var kv in _stableIdToController)
            {
                int stableId = kv.Key;
                int slot = kv.Value;
                if (stableId <= 0 || stableId >= _stableIdToSlot.Length)
                    continue;
                if (slot <= 0 || !_controllerBuffer.IsCreated || slot >= _controllerBuffer.Length)
                    continue;
                _stableIdToSlot[stableId] = slot;
            }
        }
        
        /// <summary>
        /// 解析稳定ID：通过组件位置匹配让CCA组件ID跨帧稳定
        /// 优化版本：不遍历网格，直接用组件中心位置匹配
        /// </summary>
        private void ResolveStableIds()
        {
            int compCount = _componentsBuffer.Length;
            if (compCount == 0)
            {
                return; // 无组件，跳过
            }
            
            // 确保_compToStableId数组足够大
            if (_compToStableId == null || _compToStableId.Length < compCount)
                _compToStableId = new int[math.max(32, compCount)];
            
            // 初始化稳定ID->组件中心映射（首次）
            if (_stableIdToCenter == null)
                _stableIdToCenter = new Dictionary<int, float3>(16);
            
            if (_tmpStableIdSet == null)
                _tmpStableIdSet = new HashSet<int>();
            _tmpStableIdSet.Clear();
            _tmpStableIdSet.Add(0);
            
            float matchThreshold = 20f * PBF_Utils.CellSize;
            float matchThreshold2 = matchThreshold * matchThreshold;
            
            // 调试日志：输出组件数量和匹配情况
            int newIdCount = 0;
            int inheritedCount = 0;
            
            // 遍历每个CCA组件，基于位置匹配已有的稳定ID
            for (int c = 0; c < compCount; c++)
            {
                var comp = _componentsBuffer[c];
                float3 center = minPos + comp.Center * PBF_Utils.CellSize;
                
                // 找最近的已有稳定ID
                int bestStableId = 0;
                float bestDist = float.MaxValue;
                
                foreach (var kv in _stableIdToCenter)
                {
                    if (_tmpStableIdSet.Contains(kv.Key)) continue;
                    float dist = math.lengthsq(center - kv.Value);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestStableId = kv.Key;
                    }
                }
                
                int finalStableId;
                bool isNewId = false;
                if (bestStableId > 0 && bestDist < matchThreshold2)
                {
                    // 继承已有ID
                    finalStableId = bestStableId;
                    inheritedCount++;
                }
                else
                {
                    // 分配新ID
                    finalStableId = _nextStableId++;
                    isNewId = true;
                    newIdCount++;
                }
                
                _compToStableId[c] = finalStableId;
                _tmpStableIdSet.Add(finalStableId);
                
                // 更新稳定ID的中心位置
                _stableIdToCenter[finalStableId] = center;
                
                // 调试日志：新分配ID时输出详细信息
                if (componentDebug && isNewId && comp.CellCount > 10)
                {
                    Debug.Log($"[StableID] 新分配! comp={c}, stableId={finalStableId}, center={center * PBF_Utils.Scale}, cells={comp.CellCount}, bestDist={math.sqrt(bestDist) * PBF_Utils.Scale:F2}, threshold={matchThreshold * PBF_Utils.Scale:F2}");
                }
            }
            
            // 调试日志：每60帧输出概要
            if (componentDebug && Time.frameCount % 60 == 0 && compCount > 1)
            {
                Debug.Log($"[StableID] frame={Time.frameCount} 组件数={compCount}, 继承={inheritedCount}, 新分配={newIdCount}, 稳定ID池={_stableIdToCenter.Count}, nextId={_nextStableId}");
            }
            
            // 清理不再使用的稳定ID（超过32个时）
            if (_stableIdToCenter.Count > 32)
            {
                if (_tmpStableIdList == null)
                    _tmpStableIdList = new List<int>(32);
                _tmpStableIdList.Clear();
                foreach (var id in _stableIdToCenter.Keys)
                {
                    if (!_tmpStableIdSet.Contains(id))
                        _tmpStableIdList.Add(id);
                }
                foreach (var id in _tmpStableIdList)
                    _stableIdToCenter.Remove(id);
            }
        }
        
        /// <summary>
        /// 获取CCA组件对应的稳定ID
        /// </summary>
        /// <param name="compIdx">CCA组件索引（从0开始）</param>
        /// <returns>稳定ID（0=主体，1+=分离组）</returns>
        public int GetStableIdForComponent(int compIdx)
        {
            if (_compToStableId == null || compIdx < 0 || compIdx >= _compToStableId.Length)
                return 0;
            return _compToStableId[compIdx];
        }
        
        #endregion
    }
}
