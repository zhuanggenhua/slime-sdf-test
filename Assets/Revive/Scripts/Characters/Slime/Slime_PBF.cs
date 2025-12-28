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
            public int ControllerID;
        }
        
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
        [SerializeField, Range(0f, 2f), DefaultValue(1f)]
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
        
        /// <summary>发射冷却时间（秒）</summary>
        public float EmitCooldown => emitCooldown;
        
        /// <summary>单次发射粒子数量</summary>
        public int EmitBatchSize => emitBatchSize;
        
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
        [SerializeField, Range(16384, 32768), DefaultValue(16384)]  // 最小值改为16384
        private int maxParticles = 16384;  // 必须≥16384，水珠使用固定分区[8192-16383]
        
        [ChineseLabel("活跃粒子数"), Tooltip("当前参与模拟的粒子数（只读）")]
        [SerializeField]
        private int activeParticles = 800;
        
        #endregion
        
        #region 【召回参数】
        
        [ChineseHeader("召回参数")]
        [ChineseLabel("召回最大速度"), Tooltip("远距离时的召回速度（米/秒，世界坐标）")]
        [SerializeField, Range(0.5f, 10f), DefaultValue(5f)]
        private float recallMaxSpeed = 5f;
        
        [ChineseLabel("召回最小速度"), Tooltip("靠近主体时的召回速度（米/秒，世界坐标）")]
        [SerializeField, Range(0.1f, 5f), DefaultValue(0.5f)]
        private float recallMinSpeed = 0.5f;
        
        [ChineseLabel("减速开始距离"), Tooltip("开始从最大速度减速到最小速度的距离（米，世界坐标）")]
        [SerializeField, Range(0.5f, 10f), DefaultValue(3f)]
        private float recallSlowdownDist = 3f;
        
        [ChineseHeader("召回避障")]
        [ChineseLabel("启用避障"), Tooltip("召回时自动绕开障碍物")]
        [SerializeField, DefaultValue(true)]
        private bool recallAvoidance = true;
        
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

        public Vector3 MainBodyCentroidWorld => _mainBodyCentroidWorld;

        private Vector3 _mainBodyCentroidWorld;
        
        #endregion
        
        #region 【碰撞体查询设置】
        
        [ChineseHeader("碰撞体查询设置（索引缓存）")]
        [ChineseLabel("主体缓存容量"), Tooltip("主体PBF使用的碰撞体缓存容量（由索引查询填充），越大开销越高")]
        [SerializeField, Range(16, 128), DefaultValue(64)]
        private int mainColliderCacheCapacity = 64;
        
        [ChineseLabel("主体查询半径"), Tooltip("以史莱姆为中心进行索引查询的半径（米，世界坐标）")]
        [SerializeField, Range(5f, 50f), DefaultValue(20f)]
        private float mainColliderQueryRadius = 20f;
        
        [SerializeField]
        private LayerMask worldStaticColliderLayers;
        
        [SerializeField]
        private LayerMask worldDynamicColliderLayers;

        private int ColliderQueryMask => (worldStaticColliderLayers.value != 0 ? worldStaticColliderLayers.value : ~0) | worldDynamicColliderLayers.value;
        
        [Header("Droplet Collider Query (Independent Cache)")]
        [SerializeField, Range(16, 512), DefaultValue(128)]
        private int dropletColliderCacheCapacity = 128;
        
        [SerializeField, Range(5f, 80f), DefaultValue(20f)]
        private float dropletColliderQueryRadius = 20f;

        #endregion
        
        #region 【调试设置】
        
        [ChineseHeader("调试设置")]
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
        
        // PBF物理系统（封装了_lut, _hashes, _posPredict, _lambdaBuffer等缓冲区）
        private PBFSystem _pbfSystem;
        
        // 渲染专用缓冲区（独立于物理）
        private NativeHashMap<int, int2> _renderLut; // 渲染专用邻域表
        private NativeArray<float4x4> _covBuffer;
        private NativeArray<MyBoxCollider> _colliderBuffer;
        private int _currentColliderCount; // 当前有效碰撞体数量
        private RaycastHit[] _raycastHits;
        private RaycastHit[] _sphereCastHits;

        private int _dbgRecallSphereCastFrame;
        private Vector3 _dbgRecallSphereOriginW;
        private Vector3 _dbgRecallSphereDirW;
        private float _dbgRecallSphereRadiusW;
        private float _dbgRecallSphereCastDistW;
        private bool _dbgRecallSphereHitRaw;
        private bool _dbgRecallSphereHitFinal;
        private bool _dbgRecallSphereHitFilteredByNormal;
        private Vector3 _dbgRecallSphereHitPointW;
        private Vector3 _dbgRecallSphereHitNormalW;

        private readonly HashSet<int> _colliderInstanceIds = new HashSet<int>(1024);

        private NativeArray<MyBoxCollider> _dropletColliderBuffer;
        private int _currentDropletColliderCount;
        private readonly HashSet<int> _dropletColliderInstanceIds = new HashSet<int>(256);

        private NativeArray<float3> _boundsBuffer;
        private NativeArray<float> _gridBuffer;
        private NativeArray<float> _gridTempBuffer;
        private NativeHashMap<int3, int> _gridLut;
        private NativeArray<float> _dropletGridBuffer;
        private NativeArray<float> _dropletGridTempBuffer;
        private NativeHashMap<int3, int> _dropletGridLut;
        private NativeArray<int> _dropletGridIDBuffer;
        private NativeArray<int4> _blockBuffer;
        private NativeArray<int> _blockColorBuffer;

        private NativeArray<int4> _dropletBlockBuffer;
        private NativeArray<int> _dropletBlockColorBuffer;
        
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
        private NativeParallelMultiHashMap<int, int> _mergeAbsorbLut;
        private NativeArray<int> _autoSeparateClusterCounts;
        private NativeArray<float3> _autoSeparateClusterCenters;
        
        #endregion
        
        private float3 _lastMousePos;
        private bool _mouseDown;
        private float3 _velocityY = float3.zero;
        private Bounds _bounds;
        private Vector3 _velocity = Vector3.zero;
        private Vector3 _prevTransPosition; // 上一帧 trans.position，用于计算速度
        private float _lastMainRadius; // 用于 mainRadius 防抖
        private float3 _prevMainControllerCenter; // 上一帧主控制器中心，用于正确计算PosOld

        private LMarchingCubes _marchingCubes;
        private Mesh _mesh;

        private LMarchingCubes _marchingCubesDroplet;
        private Mesh _dropletMesh;
        private Bounds _dropletBounds;

        private Camera _cachedMainCamera;
        private UnityEngine.Plane[] _cachedFrustumPlanes;
        
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

        private float[] _controllerStepJumpTargetCenterY;

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
            _colliderBuffer = new NativeArray<MyBoxCollider>(mainColliderCacheCapacity, Allocator.Persistent);
            _raycastHits = new RaycastHit[16];
            _sphereCastHits = new RaycastHit[16];
            _currentColliderCount = 0;

            _dropletColliderBuffer = new NativeArray<MyBoxCollider>(dropletColliderCacheCapacity, Allocator.Persistent);
            _currentDropletColliderCount = 0;

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
                return;
            }

            _connect = true;
            _connectStartTime = Time.time;
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
                if (p.Type != ParticleType.Separated && p.Type != ParticleType.Emitted)
                    continue;
                if (p.FreeFrames > 0)
                    continue;
                int sid = p.StableId;
                if (sid <= 0)
                    continue;
                if (sid < 0 || sid >= _recallEligibleStableIds.Length)
                    continue;
                if (_recallEligibleStableIds[sid] == 0)
                {
                    GetOrCreateControllerIdForStableId(sid);
                    _recallEligibleStableIds[sid] = 1;
                }
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

                            for (int i = 0; i < activeParticles; i++)
                            {
                                var p = _particles[i];
                                if (p.SourceId >= 0)
                                    continue;
                                if (p.FreeFrames > 0)
                                    continue;
                                if (p.ControllerId != c)
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
                int cid = p.ControllerId;
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

                    if (i < activeParticles)
                        i++;
                    continue;
                }

                if (_controllerFadeBudget[cid] <= 0)
                {
                    if (p.Type != ParticleType.FadingOut)
                        continue;
                    if (_controllerFadeRemainingFrames[cid] > 0)
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

                    if (i < activeParticles)
                        i++;
                    continue;
                }

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

                if (i < activeParticles)
                    i++;
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
            // 使用 maxParticles 替代 PBF_Utils.Num
            _particles = new NativeArray<Particle>(maxParticles, Allocator.Persistent);
            _particlesRenderBuffer = new NativeArray<Particle>(maxParticles, Allocator.Persistent);
            
            // 从 SlimeVolume 获取初始主体粒子数
            int initialMainCount = 800; // 默认值
            if (_slimeVolume != null)
            {
                initialMainCount = Mathf.Min(_slimeVolume.initialMainVolume, maxParticles);
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
            LayerMask staticLayers = worldStaticColliderLayers.value != 0 ? worldStaticColliderLayers : (LayerMask)(~0);
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
            _dropletGridBuffer = new NativeArray<float>(PBF_Utils.GridSize * gridNumExpanded, Allocator.Persistent);
            _dropletGridTempBuffer = new NativeArray<float>(PBF_Utils.GridSize * gridNumExpanded, Allocator.Persistent);
            _dropletGridLut = new NativeHashMap<int3, int>(gridNumExpanded, Allocator.Persistent);
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
            _marchingCubesDroplet = new LMarchingCubes();


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

        }

        private void OnDestroy()
        {
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
            if (_boundsBuffer.IsCreated) _boundsBuffer.Dispose();
            if (_gridBuffer.IsCreated) _gridBuffer.Dispose();
            if (_gridTempBuffer.IsCreated) _gridTempBuffer.Dispose();
            if (_dropletGridBuffer.IsCreated) _dropletGridBuffer.Dispose();
            if (_dropletGridTempBuffer.IsCreated) _dropletGridTempBuffer.Dispose();
            if (_covBuffer.IsCreated) _covBuffer.Dispose();
            if (_gridLut.IsCreated) _gridLut.Dispose();
            if (_dropletGridLut.IsCreated) _dropletGridLut.Dispose();
            if (_blockBuffer.IsCreated) _blockBuffer.Dispose();
            if (_blockColorBuffer.IsCreated) _blockColorBuffer.Dispose();
            if (_dropletBlockBuffer.IsCreated) _dropletBlockBuffer.Dispose();
            if (_dropletBlockColorBuffer.IsCreated) _dropletBlockColorBuffer.Dispose();
            if (_bubblesBuffer.IsCreated) _bubblesBuffer.Dispose();
            if (_bubblesPoolBuffer.IsCreated) _bubblesPoolBuffer.Dispose();
            if (_componentsBuffer.IsCreated) _componentsBuffer.Dispose();
            if (_gridIDBuffer.IsCreated) _gridIDBuffer.Dispose();
            if (_dropletGridIDBuffer.IsCreated) _dropletGridIDBuffer.Dispose();
            if (_prevGridStableId.IsCreated) _prevGridStableId.Dispose();
            if (_controllerBuffer.IsCreated) _controllerBuffer.Dispose();
            if (_stableIdToSlot.IsCreated) _stableIdToSlot.Dispose();
            if (_recallEligibleStableIds.IsCreated) _recallEligibleStableIds.Dispose();
            if (_slimeInstances.IsCreated) _slimeInstances.Dispose();
            if (_colliderBuffer.IsCreated)  _colliderBuffer.Dispose();
            if (_dropletColliderBuffer.IsCreated) _dropletColliderBuffer.Dispose();
            if (_sourceControllers.IsCreated) _sourceControllers.Dispose();
            
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

        }

        void Update()
        {
            // 更新场景水珠源的激活状态
            UpdateSceneDropletSources();
            
            
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

                if (_dropletMesh != null)
                    Graphics.DrawMesh(_dropletMesh, Matrix4x4.TRS(_dropletBounds.min, Quaternion.identity, Vector3.one), mat, 0);

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
            }
            
            // 原版时序：不在 Simulate() 前更新 Center，让 ApplyForceJob 使用上一帧的 Center 和 Radius
            // 这样 len 和 Radius 匹配，粒子更容易满足 len < Radius 条件受向心力
            // Center 和 Radius 统一在 Control() 中更新（Simulate 之后）
            
            // 主体粒子模拟（原版每帧执行2次）
            for (int i = 0; i < 2; i++)
            {
                Profiler.BeginSample("Simulate_Main");
                PerformanceProfiler.Begin(i == 0 ? "Simulate_0" : "Simulate_1");
                Simulate();
                PerformanceProfiler.End(i == 0 ? "Simulate_0" : "Simulate_1");
                Profiler.EndSample();
            }
            
            // 水珠独立模拟（独立分区，使用完整 PBF 物理 + 环境碰撞）
            Profiler.BeginSample("Simulate_Droplets");
            PerformanceProfiler.Begin("Simulate_Droplets");
            
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
                _dropletColliderBuffer, _currentDropletColliderCount);
            
            PerformanceProfiler.End("Simulate_Droplets");
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
                PerformanceProfiler.Begin("Particles_ConvertToWorld");
                int totalParticles = ConvertToWorldPositionsForRendering();
                PerformanceProfiler.End("Particles_ConvertToWorld");

                PerformanceProfiler.Begin("Particles_Upload");
                _particleRenderer.UploadParticles(_particlesRenderBuffer, totalParticles);
                PerformanceProfiler.End("Particles_Upload");

                PerformanceProfiler.Begin("Particles_CalcBounds");
                new Reconstruction.CalcBoundsJob()
                {
                    Ps = _particlesRenderBuffer,
                    Controllers = _controllerBuffer,
                    SourceControllers = _sourceControllers,
                    Bounds = _boundsBuffer,
                    ActiveCount = totalParticles,
                }.Schedule().Complete();
                PerformanceProfiler.End("Particles_CalcBounds");

                PerformanceProfiler.Begin("Particles_SetBounds");
                _particleRenderer.SetBounds(_boundsBuffer[0] * PBF_Utils.Scale, _boundsBuffer[1] * PBF_Utils.Scale);
                PerformanceProfiler.End("Particles_SetBounds");
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
            SurfaceMain();
            SurfaceDroplets();
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

            _gridLut.Clear();
            float blockSize = PBF_Utils.CellSize * 4;
            minPos = math.floor(_boundsBuffer[0] / blockSize) * blockSize;
            maxPos = math.ceil(_boundsBuffer[1] / blockSize) * blockSize;

            Profiler.BeginSample("Allocate");
            PerformanceProfiler.Begin("SurfaceMain_Allocate");
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
            PerformanceProfiler.End("SurfaceMain_Allocate");

            Profiler.EndSample();

            Profiler.BeginSample("Splat");
            PerformanceProfiler.Begin("SurfaceMain_Splat");

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
            PerformanceProfiler.End("SurfaceMain_Splat");
            Profiler.EndSample();

            Profiler.BeginSample("Blur");
            PerformanceProfiler.Begin("SurfaceMain_Blur");

            new Reconstruction.GridBlurJob()
            {
                Keys = keys,
                GridLut = _gridLut,
                GridRead = _gridBuffer,
                GridWrite = _gridTempBuffer,
            }.Schedule(keys.Length, batchCount, handle).Complete();
            PerformanceProfiler.End("SurfaceMain_Blur");

            Profiler.EndSample();

            Profiler.BeginSample("Marching cubes");
            PerformanceProfiler.Begin("SurfaceMain_MarchingCubes");
            _mesh = _marchingCubes.MarchingCubesParallel(keys, _gridLut, _gridTempBuffer, threshold, PBF_Utils.Scale * PBF_Utils.CellSize);
            PerformanceProfiler.End("SurfaceMain_MarchingCubes");
            Profiler.EndSample();

            Profiler.BeginSample("CCA");
            PerformanceProfiler.Begin("SurfaceMain_CCA");
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
            PerformanceProfiler.End("SurfaceMain_CCA");
            Profiler.EndSample();

            Profiler.BeginSample("StableID");
            PerformanceProfiler.Begin("SurfaceMain_StableID");
            ResolveStableIds();
            PerformanceProfiler.End("SurfaceMain_StableID");
            Profiler.EndSample();

            Profiler.BeginSample("ParticleID");
            PerformanceProfiler.Begin("SurfaceMain_ParticleID");
            handle = new Effects.ParticleIDJob()
            {
                GridLut = _gridLut,
                GridID = _gridIDBuffer,
                Controllers = _controllerBuffer,
                SourceControllers = _sourceControllers,
                Particles = _particles,
                MinPos = minPos,
            }.Schedule(activeParticles, batchCount);
            handle.Complete();
            PerformanceProfiler.End("SurfaceMain_ParticleID");
            Profiler.EndSample();

            keys.Dispose();
        }

        private void SurfaceDroplets()
        {
            int dropletCount = _dropletSubsystem.ActiveCount;
            if (dropletCount <= 0)
            {
                _dropletMesh = null;
                return;
            }

            if (_cachedMainCamera == null)
                _cachedMainCamera = Camera.main;
            var cam = _cachedMainCamera;
            if (cam != null)
            {
                if (_dropletSubsystem.TryGetActiveBounds(out float3 dropletMinSim, out float3 dropletMaxSim))
                {
                    Vector3 minW = (Vector3)(dropletMinSim * PBF_Utils.Scale);
                    Vector3 maxW = (Vector3)(dropletMaxSim * PBF_Utils.Scale);
                    Vector3 centerW = (minW + maxW) * 0.5f;
                    Vector3 sizeW = (maxW - minW);
                    float marginW = PBF_Utils.h * PBF_Utils.Scale * 4f;
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

            new Reconstruction.CalcBoundsJob()
            {
                Ps = _particlesRenderBuffer,
                Controllers = _controllerBuffer,
                SourceControllers = _sourceControllers,
                Bounds = _boundsBuffer,
                ActiveCount = dropletCount,
            }.Schedule(handle).Complete();
            PerformanceProfiler.End("SurfaceDroplet_MeanPos");

            float blockSize = PBF_Utils.CellSize * 4;
            float3 dropletMinPos = math.floor(_boundsBuffer[0] / blockSize) * blockSize;
            float3 dropletMaxPos = math.ceil(_boundsBuffer[1] / blockSize) * blockSize;
            _dropletBounds = new Bounds()
            {
                min = dropletMinPos * PBF_Utils.Scale,
                max = dropletMaxPos * PBF_Utils.Scale
            };

            _dropletGridLut.Clear();

            PerformanceProfiler.Begin("SurfaceDroplet_Allocate");
            handle = new Reconstruction.AllocateBlockJob()
            {
                Ps = _particlesTemp,
                GridLut = _dropletGridLut,
                MinPos = dropletMinPos,
                ActiveCount = dropletCount,
            }.Schedule();
            handle.Complete();

            var keys = _dropletGridLut.GetKeyArray(Allocator.TempJob);
            int dropletBlockNum = keys.Length;
            if (dropletBlockNum == 0)
            {
                keys.Dispose();
                _dropletMesh = null;
                PerformanceProfiler.End("SurfaceDroplet_Allocate");
                return;
            }

            int clearLen = dropletBlockNum * PBF_Utils.GridSize;
            new Reconstruction.ClearGridJob
            {
                Grid = _dropletGridBuffer.GetSubArray(0, clearLen),
                GridID = _dropletGridIDBuffer.GetSubArray(0, clearLen),
            }.Schedule(clearLen, batchCount).Complete();

            new Reconstruction.ColorBlockJob()
            {
                Keys = keys,
                Blocks = _dropletBlockBuffer,
                BlockColors = _dropletBlockColorBuffer,
            }.Schedule().Complete();
            PerformanceProfiler.End("SurfaceDroplet_Allocate");

            PerformanceProfiler.Begin("SurfaceDroplet_Splat");
#if USE_SPLAT_SINGLE_THREAD
            handle = new Reconstruction.DensityProjectionJob()
            {
                Ps = _particlesTemp,
                GMatrix = _covBuffer,
                Grid = _dropletGridBuffer,
                GridLut = _dropletGridLut,
                MinPos = dropletMinPos,
                UseAnisotropic = useAnisotropic,
            }.Schedule();
#elif USE_SPLAT_COLOR8
            for (int i = 0; i < 8; i++)
            {
                int2 slice = new int2(_dropletBlockColorBuffer[i], _dropletBlockColorBuffer[i + 1]);
                int count = slice.y - slice.x;
                handle = new Reconstruction.DensitySplatColoredJob()
                {
                    ParticleLut = _renderLut,
                    ColorKeys = _dropletBlockBuffer.Slice(slice.x, count),
                    Ps = _particlesTemp,
                    GMatrix = _covBuffer,
                    Grid = _dropletGridBuffer,
                    GridLut = _dropletGridLut,
                    MinPos = dropletMinPos,
                    UseAnisotropic = useAnisotropic,
                }.Schedule(count, count, handle);
            }
#else
            handle = new Reconstruction.DensityProjectionParallelJob()
            {
                Ps = _particlesTemp,
                GMatrix = _covBuffer,
                GridLut = _dropletGridLut,
                Grid = _dropletGridBuffer,
                ParticleLut = _renderLut,
                Keys = keys,
                UseAnisotropic = useAnisotropic,
                MinPos = dropletMinPos,
            }.Schedule(keys.Length, batchCount);
#endif
            handle.Complete();
            PerformanceProfiler.End("SurfaceDroplet_Splat");

            PerformanceProfiler.Begin("SurfaceDroplet_Blur");
            new Reconstruction.GridBlurJob()
            {
                Keys = keys,
                GridLut = _dropletGridLut,
                GridRead = _dropletGridBuffer,
                GridWrite = _dropletGridTempBuffer,
            }.Schedule(keys.Length, batchCount, handle).Complete();
            PerformanceProfiler.End("SurfaceDroplet_Blur");

            PerformanceProfiler.Begin("SurfaceDroplet_MarchingCubes");
            _dropletMesh = _marchingCubesDroplet.MarchingCubesParallel(keys, _dropletGridLut, _dropletGridTempBuffer, threshold, PBF_Utils.Scale * PBF_Utils.CellSize);
            PerformanceProfiler.End("SurfaceDroplet_MarchingCubes");

            keys.Dispose();
        }

        private void Simulate()
        {
            _pbfSystem.Lut.Clear();
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
            if (UnityEngine.Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 30f, ColliderQueryMask))
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
            
            float3 mainVelocity = float3.zero;
            // 动态 Concentration：速度越高，聚集力越强，保持相对响应时间不变
            // 推导：响应帧数 N ∝ 1/(1-lerpFactor) ∝ 1/concentration
            // 要保持 N/speed 不变，需要 concentration ∝ speed
            float speed = math.length(_velocity);  // 世界坐标速度
            float speedRatio = math.max(1f, speed / baseSpeed);
            // 【临时禁用】动态Concentration可能导致过度聚集→爆炸，先用固定值测试
            // float dynamicConcentration = concentration * speedRatio;
            float dynamicConcentration = concentration; // 固定值
            
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
            if (_connect)
                recentFromEmitCountPerComp = new NativeArray<int>(math.max(1, compCount), Allocator.Temp, NativeArrayOptions.ClearMemory);

            // 统计分离/发射粒子数量并标记最近发射的团块
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
                        int cid = particle.ControllerId;
                        bool hadFreeFlyingLastFrame = _controllerFreeFramesCounts != null &&
                                                      cid > 0 && cid < _controllerFreeFramesCounts.Length &&
                                                      _controllerFreeFramesCounts[cid] > 0;
                        bool looksLikeNewlyEmittedSeparated = particle.Type == ParticleType.Separated &&
                                                             particle.ControllerId > 0 &&
                                                             particle.FramesOutsideMain < 60 &&
                                                             hadFreeFlyingLastFrame;
                        if (looksLikeNewlyEmittedSeparated)
                            recentFromEmitCountPerComp[clusterId - 1]++;
                    }
                }
            }

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
                float radiusXZ = math.max(1f, math.length(new float2(extent.x, extent.z)) * PBF_Utils.CellSize * separatedRadiusScale);
                float halfHeight = math.max(0.001f, extent.y * PBF_Utils.CellSize * separatedRadiusScale);
                float radius = math.sqrt(radiusXZ * radiusXZ + halfHeight * halfHeight);
                float3 center = minPos + component.Center * PBF_Utils.CellSize;

                // 方案1：召回期间，如果“新发射/保护期团块”被位置匹配继承到了召回快照中的 stableId，
                // 则强制为该组件分配新 stableId，避免误召回。
                if (_connect && recentFromEmitCountPerComp.IsCreated && _recallEligibleStableIds.IsCreated)
                {
                    if (stableId > 0 && stableId < _recallEligibleStableIds.Length && _recallEligibleStableIds[stableId] != 0)
                    {
                        int recentCount = recentFromEmitCountPerComp[i];
                        int minRecentCount = math.max(1, (int)math.ceil(particleCount * 0.8f));
                        if (particleCount > 0 && recentCount >= minRecentCount)
                        {
                            int newStableId = _nextStableId++;
                            _compToStableId[i] = newStableId;
                            stableId = newStableId;

                            if (newStableId > 0 && newStableId < _recallEligibleStableIds.Length)
                                _recallEligibleStableIds[newStableId] = 0;

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
                    float recallSlowdownDistSim = math.max(0.001f, recallSlowdownDist * PBF_Utils.InvScale);
                    float recallMinSpeedSim = recallMinSpeed * PBF_Utils.InvScale;
                    float recallMaxSpeedSim = recallMaxSpeed * PBF_Utils.InvScale;
                    float speedFactor = math.saturate(distToMain / recallSlowdownDistSim);
                    float smoothFactor = speedFactor * speedFactor * (3f - 2f * speedFactor);
                    float recallSpeedSim = math.lerp(recallMinSpeedSim, recallMaxSpeedSim, smoothFactor);
                    // 召回只使用水平方向，Y分量置0
                    float3 toMainXZ = mainCenter - center;
                    toMainXZ.y = 0;
                    float3 rawDir = math.normalizesafe(toMainXZ);
                    
                    bool useAvoid = recallAvoidance && _currentColliderCount > 0;
                    if (useAvoid)
                    {
                        toMain = ComputeAvoidedRecallVelocity(controllerId, center, radiusXZ, halfHeight, mainCenter, rawDir, recallSpeedSim);
                    }
                    else
                    {
                        toMain = recallSpeedSim * rawDir;
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

                if (p.Type == ParticleType.FadingOut)
                {
                    p.ClusterId = 0;
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
                    int compIdx = clusterId - 1;
                    int newGroupId = _componentToGroup[compIdx];
                    int newStableId = GetStableIdForComponent(compIdx);
                    if (inEmitProtection && newGroupId == 0)
                    {
                        p.FramesOutsideMain++;
                        _particles[i] = p;
                        continue;
                    }

                    if (!inEmitProtection && newGroupId == 0)
                    {
                        ParticleStateManager.ConvertToMainBody(ref p, mainCenter);
                        _particles[i] = p;
                        continue;
                    }

                    // 映射到 0 表示 CCA 认为该组件属于“主体连通块/过近”，但不应在这里强制回归主体。
                    // 回归主体只能通过 MergeContactingParticles/EnableRecall。
                    p.ControllerId = (newGroupId == 0) ? prevControllerId : newGroupId;
                    p.StableId = (newGroupId == 0) ? prevStableId : newStableId;

                    p.FramesOutsideMain++;
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
                if (p.Type == ParticleType.FadingOut)
                    continue;
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
            PerformanceProfiler.Begin("MergeContact_Broadphase");
            if (dropletActiveCount > 0 && _dropletSubsystem.TryGetActiveBounds(out float3 dropletMin, out float3 dropletMax))
            {
                float3 absorbMin = mainCenter - new float3(mergeRadius + gateMargin);
                float3 absorbMax = mainCenter + new float3(mergeRadius + gateMargin);
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
                    if (!isSceneDroplet && p.ControllerId <= 0)
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

            int3 GetMergeCoord(float3 p) => (int3)math.floor(p * invContactDist);
            
            for (int i = 0; i < count; i++)
            {
                if (_particles[i].Type != ParticleType.MainBody) continue;
                float3 pos = _particles[i].Position;
                int3 coord = GetMergeCoord(pos);
                int key = PBF_Utils.GetKey(coord);
                mainBodyLut.Add(key, i);
                absorbLut.Add(key, i);
                mainBodyCount++;
            }
            PerformanceProfiler.End("MergeContact_BuildMainBodyLut");

            // 添加可吸收的分离粒子（独立控制器）
            PerformanceProfiler.Begin("MergeContact_BuildAbsorbLut");
            for (int i = 0; i < count; i++)
            {
                var p = _particles[i];
                if (p.Type != ParticleType.Separated || p.ControllerId <= 0) continue;
                float3 pos = p.Position;
                int3 coord = GetMergeCoord(pos);
                int key = PBF_Utils.GetKey(coord);
                absorbLut.Add(key, i);
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
                    float3 srcCenter = (float3)src.transform.position * PBF_Utils.InvScale;
                    float srcRadius = math.max(2f, src.AdaptiveRadius * PBF_Utils.InvScale);
                    
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
            for (int i = 0; i < count; i++)
            {
                // 只处理分离粒子
                if (_particles[i].Type == ParticleType.MainBody || _particles[i].Type == ParticleType.Dormant || _particles[i].Type == ParticleType.FadingOut) continue;
                
                // 跳过还在自由飞行的粒子（FreeFrames > 0）
                if (_particles[i].FreeFrames > 0) 
                {
                    continue;
                }
                
                bool isSceneDroplet = _particles[i].SourceId >= 0;
                
                // 分离粒子的 Position 是世界坐标
                float3 pos = _particles[i].Position;
                float distToMain2 = math.lengthsq(pos - mainCenter);
                
                // 场景水珠的特殊处理：接触主体时可以融合
                if (!isSceneDroplet)
                {
                    // 普通分离粒子的合并检测（接触融合）
                    if (distToMain2 > mergeRadius2)
                    {
                        continue;
                    }

                    // 跳过没有独立控制器的分离粒子（ControllerId <= 0）
                    // 因为避障只对有控制器的粒子有效，没控制器的粒子不参与融合
                    if (_particles[i].ControllerId <= 0)
                    {
                        continue;
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
                    
                    // 场景水珠查找“可吸收体”集合（主体+合法分离团），普通分离粒子只查主体
                    if (isSceneDroplet)
                    {
                        if (absorbLut.TryGetFirstValue(key, out int j, out var it))
                        {
                            do
                            {
                                if (i == j) continue;
                                float r2 = math.lengthsq(pos - _particles[j].Position);
                                if (r2 <= contactDist2)
                                {
                                    shouldMerge = true;
                                    contactIdx = j;
                                    actualContactDist = math.sqrt(r2);
                                    break;
                                }
                            } while (absorbLut.TryGetNextValue(out j, ref it));
                        }
                    }
                    else
                    {
                        if (mainBodyLut.TryGetFirstValue(key, out int j, out var it))
                        {
                            do
                            {
                                if (i == j) continue;
                                float r2 = math.lengthsq(pos - _particles[j].Position);
                                if (r2 <= contactDist2)
                                {
                                    shouldMerge = true;
                                    contactIdx = j;
                                    actualContactDist = math.sqrt(r2);
                                    break;
                                }
                            } while (mainBodyLut.TryGetNextValue(out j, ref it));
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
                    if (target.Type == ParticleType.Separated && target.ControllerId > 0)
                    {
                        // 直接并入分离团：继承控制器/稳定ID/Cluster等信息
                        p.Type = ParticleType.Separated;
                        p.ControllerId = target.ControllerId;
                        p.StableId = target.StableId;
                        p.ClusterId = target.ClusterId;
                        p.FramesOutsideMain = target.FramesOutsideMain;
                        p.SourceId = -1;
                        p.FreeFrames = 0;
                    }
                    else
                    {
                        ParticleStateManager.ConvertToMainBody(ref p, mainCenter);
                    }
                }
                else
                {
                    // 使用ParticleStateManager合并粒子
                    ParticleStateManager.ConvertToMainBody(ref p, mainCenter);
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
            }

            PerformanceProfiler.End("MergeContact_ScanMainRange");
            
            // ========== 处理水珠独立分区 [8192-16383] ==========
            // 复用与分离粒子相同的接触融合逻辑
            {
                PerformanceProfiler.Begin("MergeContact_ScanDropletPartition");
                if (checkDropletPartition && dropletActiveCount > 0)
                {
                    // dropletActiveCount 个活跃水珠，索引范围 [8192..8192+dropletActiveCount)
                    int dropletStart = DropletSubsystem.DROPLET_START;
                    int dropletEnd = dropletStart + dropletActiveCount - 1;
                    
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
                        
                        // 使用空间哈希加速接触检测（O(1) 邻域查询）
                        bool shouldMerge = false;
                        int3 dropletCoord = GetMergeCoord(dropletPos);
                        int contactIdx = -1;
                        
                        const int dropletMergeNeighborRange = 1;
                        for (int dz = -dropletMergeNeighborRange; dz <= dropletMergeNeighborRange && !shouldMerge; ++dz)
                        for (int dy = -dropletMergeNeighborRange; dy <= dropletMergeNeighborRange && !shouldMerge; ++dy)
                        for (int dx = -dropletMergeNeighborRange; dx <= dropletMergeNeighborRange && !shouldMerge; ++dx)
                        {
                            int key = PBF_Utils.GetKey(dropletCoord + new int3(dx, dy, dz));
                            if (absorbLut.TryGetFirstValue(key, out int j, out var it))
                            {
                                do
                                {
                                    float r2 = math.lengthsq(dropletPos - _particles[j].Position);
                                    if (r2 <= contactDist2)
                                    {
                                        shouldMerge = true;
                                        contactIdx = j;
                                        break;
                                    }
                                } while (absorbLut.TryGetNextValue(out j, ref it));
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
                        
                        // 在主体分区末尾创建新的粒子（根据接触目标决定主体/分离）
                        var target = contactIdx >= 0 ? _particles[contactIdx] : default;
                        int newMainIdx = activeParticles;
                        var newParticle = new Particle
                        {
                            Position = migratedPos,
                            Type = ParticleType.MainBody,
                            ControllerId = 0,
                            StableId = 0,
                            FreeFrames = 0,
                            SourceId = -1,
                            ClusterId = 0,
                            FramesOutsideMain = 0
                        };

                        if (contactIdx >= 0 && target.Type == ParticleType.Separated && target.ControllerId > 0)
                        {
                            newParticle.Type = ParticleType.Separated;
                            newParticle.ControllerId = target.ControllerId;
                            newParticle.StableId = target.StableId;
                            newParticle.ClusterId = target.ClusterId;
                            newParticle.FramesOutsideMain = target.FramesOutsideMain;
                        }

                        _particles[newMainIdx] = newParticle;
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
                PerformanceProfiler.End("MergeContact_ScanDropletPartition");
                
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
                    return;
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
                    
                    float3 newPos = _boundsBuffer[0];
                    if (!math.all(math.isfinite(newPos)))
                        continue;
                    
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

            if (controllerID == 0)
            {
                _mainBodyCentroidWorld = (Vector3)(eyeCenter * PBF_Utils.Scale);
            }
            
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
                float insetDepth = actualSurfaceDist * 0.2f;
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
            
            return activeParticles + dropletCount;
        }

        private int ConvertMainToWorldPositionsForRendering()
        {
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
                Gizmos.DrawWireSphere(trans.position, _controllerBuffer[0].Radius * PBF_Utils.Scale);
            }

            if (recallAvoidanceGizmos && _controllerBuffer.IsCreated && _controllerBuffer.Length > 1)
            {
                Gizmos.color = new Color(1f, 0f, 1f, 0.5f);
                for (int i = 1; i < _controllerBuffer.Length; i++)
                {
                    var ctrl = _controllerBuffer[i];
                    if (!ctrl.IsValid)
                        continue;
                    Vector3 cW = (Vector3)(ctrl.Center * PBF_Utils.Scale);
                    float rW = math.max(0.001f, ctrl.Radius * PBF_Utils.Scale);
                    Gizmos.DrawWireSphere(cW, rW);
                }

                if (_dbgRecallSphereCastFrame > 0)
                {
                    Gizmos.color = _dbgRecallSphereHitFinal ? new Color(1f, 0.2f, 0.2f, 0.9f) : new Color(1f, 1f, 1f, 0.35f);
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
            Vector3 slimeCenter = trans != null ? trans.position : Vector3.zero;

            index.AppendMyBoxColliders(slimeCenter, mainColliderQueryRadius, ignoreRoot, _colliderBuffer, ref _currentColliderCount, mainColliderCacheCapacity, _colliderInstanceIds);

            if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 1)
            {
                for (int i = 1; i < _controllerBuffer.Length && _currentColliderCount < mainColliderCacheCapacity; i++)
                {
                    var ctrl = _controllerBuffer[i];
                    if (!ctrl.IsValid)
                        continue;

                    Vector3 cW = (Vector3)(ctrl.Center * PBF_Utils.Scale);
                    float rW = math.max(mainColliderQueryRadius, (ctrl.Radius * PBF_Utils.Scale) + 2f);
                    index.AppendMyBoxColliders(cW, rW, ignoreRoot, _colliderBuffer, ref _currentColliderCount, mainColliderCacheCapacity, _colliderInstanceIds);
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
            if (_dropletSubsystem.TryGetActiveBounds(out float3 min, out float3 max))
            {
                float3 center = (min + max) * 0.5f;
                float3 extent = (max - min) * 0.5f;

                Vector3 centerWorld = (Vector3)(center * PBF_Utils.Scale);
                Vector3 extentWorld = (Vector3)(extent * PBF_Utils.Scale);
                float radiusWorld = math.max(dropletColliderQueryRadius, extentWorld.magnitude + 2f);

                index.AppendMyBoxColliders(centerWorld, radiusWorld, ignoreRoot,
                    _dropletColliderBuffer, ref _currentDropletColliderCount, dropletColliderCacheCapacity, _dropletColliderInstanceIds);
                return;
            }

            if (allSources == null || allSources.Count == 0)
                return;

            for (int s = 0; s < allSources.Count && _currentDropletColliderCount < dropletColliderCacheCapacity; s++)
            {
                var source = allSources[s];
                if (source == null || source.State != DropWater.DropletSourceState.Simulated)
                    continue;

                Vector3 sourceCenter = source.transform.position;
                index.AppendMyBoxColliders(sourceCenter, dropletColliderQueryRadius, ignoreRoot,
                    _dropletColliderBuffer, ref _currentDropletColliderCount, dropletColliderCacheCapacity, _dropletColliderInstanceIds);
            }
        }

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
                
                bool hitGround = TryRaycastFiltered(worldPos, Vector3.down, 30f, out RaycastHit hit);
                if (hitGround)
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
                    
                }
                else
                {
                    // 未命中时使用控制器中心Y减去一定距离作为后备
                    ctrl.GroundY = ctrl.Center.y - 20f;
                }
                
                _controllerBuffer[c] = ctrl;
            }
        }
        
        #endregion
        
        #region 召回避障

        private bool TryRaycastFiltered(Vector3 originW, Vector3 dirW, float maxDistW, out RaycastHit best)
        {
            best = default;
            if (_raycastHits == null || _raycastHits.Length == 0)
                return UnityEngine.Physics.Raycast(originW, dirW, out best, maxDistW, ColliderQueryMask, QueryTriggerInteraction.Ignore);

            int count = UnityEngine.Physics.RaycastNonAlloc(originW, dirW, _raycastHits, maxDistW, ColliderQueryMask, QueryTriggerInteraction.Ignore);
            float bestDist = float.PositiveInfinity;
            bool has = false;
            for (int i = 0; i < count; i++)
            {
                var h = _raycastHits[i];
                var col = h.collider;
                if (col == null)
                    continue;
                if (trans != null && col.transform != null && col.transform.root == trans.root)
                    continue;
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    best = h;
                    has = true;
                }
            }
            return has;
        }

        private bool TrySphereCastFiltered(Vector3 originW, float radiusW, Vector3 dirW, float maxDistW, out RaycastHit best)
        {
            best = default;
            if (_sphereCastHits == null || _sphereCastHits.Length == 0)
                return UnityEngine.Physics.SphereCast(originW, radiusW, dirW, out best, maxDistW, ColliderQueryMask, QueryTriggerInteraction.Ignore);

            int count = UnityEngine.Physics.SphereCastNonAlloc(originW, radiusW, dirW, _sphereCastHits, maxDistW, ColliderQueryMask, QueryTriggerInteraction.Ignore);
            float bestDist = float.PositiveInfinity;
            bool has = false;
            for (int i = 0; i < count; i++)
            {
                var h = _sphereCastHits[i];
                var col = h.collider;
                if (col == null)
                    continue;
                if (trans != null && col.transform != null && col.transform.root == trans.root)
                    continue;
                if (h.distance <= 1e-4f)
                    continue;
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    best = h;
                    has = true;
                }
            }
            return has;
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
        private float3 ComputeAvoidedRecallVelocity(int controllerId, float3 center, float radiusXZ, float halfHeight, float3 mainCenter, float3 rawDir, float speed)
        {
            // 基础速度：直接指向主体
            float3 baseVel = new float3(rawDir.x, 0f, rawDir.z) * speed;
            float3 steering = float3.zero;

            float dt = math.max(1e-4f, deltaTime);
            float maxUpSim = math.max(0.01f, recallStepMaxUpSpeed) * PBF_Utils.InvScale;
            bool hasStepJumpTarget = controllerId > 0 &&
                                    _controllerStepJumpTargetCenterY != null &&
                                    controllerId < _controllerStepJumpTargetCenterY.Length &&
                                    !float.IsNaN(_controllerStepJumpTargetCenterY[controllerId]);

            float recallObstacleCheckDistSim = recallObstacleCheckDist * PBF_Utils.InvScale;
            float recallObstacleKeepDistanceMarginSim = recallObstacleKeepDistanceMargin * PBF_Utils.InvScale;
            float recallStepMaxHeightSim = recallStepMaxHeight * PBF_Utils.InvScale;

            bool dbgSphereHitRaw = false;
            bool dbgSphereHitFinal = false;
            bool dbgSphereHitFilteredByNormal = false;
            Collider dbgSphereCol = null;
            int dbgSphereLayer = -1;
            Vector3 dbgSphereNormalW = Vector3.zero;
            float dbgSphereDistW = -1f;
            
            // XZ平面上的方向
            float2 dirXZ = math.normalizesafe(new float2(rawDir.x, rawDir.z));
            if (math.lengthsq(dirXZ) < 0.001f)
            {
                return baseVel; // 几乎垂直，不需要避障
            }

            float projectionCheckDist = recallObstacleCheckDistSim + radiusXZ;
            Vector3 originW = (Vector3)(center * PBF_Utils.Scale);
            Vector3 dirW = new Vector3(dirXZ.x, 0f, dirXZ.y);
            float sphereRadiusW = math.max(0.001f, radiusXZ * PBF_Utils.Scale);
            float castDistW = math.max(0.001f, projectionCheckDist * PBF_Utils.Scale);
            float keepDistW = math.max(0f, (radiusXZ + recallObstacleKeepDistanceMarginSim) * PBF_Utils.Scale);
            bool sphereHitRaw = TrySphereCastFiltered(originW, sphereRadiusW, dirW, castDistW, out RaycastHit hit);
            bool sphereHit = sphereHitRaw;
            dbgSphereHitRaw = sphereHitRaw;
            if (sphereHitRaw)
            {
                dbgSphereCol = hit.collider;
                dbgSphereLayer = (dbgSphereCol != null) ? dbgSphereCol.gameObject.layer : -1;
                dbgSphereNormalW = hit.normal;
                dbgSphereDistW = hit.distance;
            }

            if (sphereHit)
            {
                // 过滤地面/坡面：水平召回避障只关心“墙”的水平法线
                if (math.abs(hit.normal.y) > 0.65f)
                {
                    sphereHit = false;
                    dbgSphereHitFilteredByNormal = true;
                }
            }
            dbgSphereHitFinal = sphereHit;

            if (recallAvoidanceGizmos)
            {
                _dbgRecallSphereCastFrame = Time.frameCount;
                _dbgRecallSphereOriginW = originW;
                _dbgRecallSphereDirW = dirW;
                _dbgRecallSphereRadiusW = sphereRadiusW;
                _dbgRecallSphereCastDistW = castDistW;
                _dbgRecallSphereHitRaw = sphereHitRaw;
                _dbgRecallSphereHitFinal = sphereHit;
                _dbgRecallSphereHitFilteredByNormal = dbgSphereHitFilteredByNormal;
                if (sphereHitRaw)
                {
                    _dbgRecallSphereHitPointW = hit.point;
                    _dbgRecallSphereHitNormalW = hit.normal;
                }
                else
                {
                    _dbgRecallSphereHitPointW = Vector3.zero;
                    _dbgRecallSphereHitNormalW = Vector3.up;
                }
            }

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
                float slow01Hit = math.saturate(distRemainW / denomW);
                vXZ *= slow01Hit;
                float push01 = 1f - slow01Hit;

                float3 slideVecHit = rawDir - math.dot(rawDir, nSim) * nSim;
                slideVecHit.y = 0;
                if (math.lengthsq(slideVecHit) < 1e-3f)
                    slideVecHit = new float3(-nSim.z, 0f, nSim.x);
                float3 slideDirHit = math.normalizesafe(slideVecHit);
                if (math.lengthsq(slideDirHit) < 1e-6f)
                {
                    float3 perp = new float3(-nSim.z, 0f, nSim.x);
                    slideDirHit = math.normalizesafe(perp);
                }

                vXZ += slideDirHit * (speed * 0.35f * push01);
                baseVel.x = vXZ.x;
                baseVel.z = vXZ.z;
            }
            
            // 检测前方是否有障碍物阻挡
            float checkDist = recallObstacleCheckDistSim + radiusXZ;
            float closestBlockDist = float.MaxValue;
            int closestBlockIdx = -1;
            float3 closestBlockNormal = float3.zero;
            float closestBlockTopY = 0;
            
            for (int c = 0; c < _currentColliderCount; c++)
            {
                MyBoxCollider box = _colliderBuffer[c];
                
                // 扩展后的 AABB（考虑分离组半径）
                float3 expandedExtent = box.Extent + new float3(radiusXZ, 0, radiusXZ);
                
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
                float groupBottom = center.y - halfHeight;
                float groupTop = center.y + halfHeight;
                float boxBottom = box.Center.y - box.Extent.y;
                float boxTop = box.Center.y + box.Extent.y;
                
                bool yOverlap = groupBottom < boxTop && groupTop > boxBottom;
                
                float3 deltaToBox = rayOrigin - box.Center;
                bool insideXZ = math.abs(deltaToBox.x) <= expandedExtent.x && math.abs(deltaToBox.z) <= expandedExtent.z;
                if (insideXZ && yOverlap)
                {
                    float penX = expandedExtent.x - math.abs(deltaToBox.x);
                    float penZ = expandedExtent.z - math.abs(deltaToBox.z);

                    // 重要：insideXZ 仅表示“在投影盒子内部”，对很大的BoxCollider（地面/关卡边界）会导致误判“贴墙”。
                    // 只有当确实靠近盒子侧面（penetration 足够小）时才当作阻挡。
                    float considerPen = (radiusXZ + recallObstacleKeepDistanceMarginSim) * 1.25f;
                    bool moveAxisX = math.abs(rayDir.x) > math.abs(rayDir.z);
                    float distToFace = moveAxisX ? penX : penZ;
                    if (distToFace > considerPen)
                        continue;

                    bool movingTowardBox;
                    if (moveAxisX)
                        movingTowardBox = (rayDir.x * deltaToBox.x) < 0f;
                    else
                        movingTowardBox = (rayDir.z * deltaToBox.z) < 0f;
                    if (!movingTowardBox)
                        continue;

                    if (moveAxisX)
                    {
                        float nSign = math.sign(deltaToBox.x);
                        if (nSign == 0f)
                            nSign = -math.sign(rayDir.x);
                        closestBlockNormal = new float3(nSign, 0, 0);
                    }
                    else
                    {
                        float nSign = math.sign(deltaToBox.z);
                        if (nSign == 0f)
                            nSign = -math.sign(rayDir.z);
                        closestBlockNormal = new float3(0, 0, nSign);
                    }
                    closestBlockDist = distToFace;
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
                float closestBlockDistWDbg = closestBlockDist * PBF_Utils.Scale;
                dbgProbeFwdW = closestBlockDistWDbg + sphereRadiusW + 0.02f;
                if (dbgSphereHitFinal)
                    dbgProbeFwdW = math.max(dbgProbeFwdW, hit.distance + sphereRadiusW + 0.02f);
                // 下台阶时如果 closestBlockDist 很小，probe 点可能仍在上台阶平台上，导致 groundDeltaW=0。
                // 这里强制一个最小前移距离，尽量采样到边缘之后的地面。
                dbgProbeFwdW = math.max(dbgProbeFwdW, (sphereRadiusW + keepDistW) + 0.20f);
                dbgProbeFwdW = math.min(dbgProbeFwdW, castDistW + sphereRadiusW + 0.25f);

                Vector3 curRayOriginW = originW + Vector3.up * 2f;
                if (TryRaycastFiltered(curRayOriginW, Vector3.down, 6f, out RaycastHit curGroundHit))
                {
                    dbgHasCurGround = true;
                    dbgCurGroundYW = curGroundHit.point.y;
                }

                Vector3 aheadPosW = originW + dirW * dbgProbeFwdW;
                Vector3 aheadRayOriginW = aheadPosW + Vector3.up * 2f;
                if (TryRaycastFiltered(aheadRayOriginW, Vector3.down, 6f, out RaycastHit aheadGroundHit))
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
                        Vector3 stepTopProbePosW = originW + dirW * (hit.distance + sphereRadiusW + 0.02f);
                        Vector3 stepTopRayOriginW = stepTopProbePosW + Vector3.up * (maxStepWDbg + 0.35f);
                        float stepTopRayLenW = maxStepWDbg + 1.0f;
                        if (TryRaycastFiltered(stepTopRayOriginW, Vector3.down, stepTopRayLenW, out RaycastHit stepTopHit))
                        {
                            dbgHasStepTop = true;
                            dbgStepTopYW = stepTopHit.point.y;
                            dbgStepTopDeltaW = dbgStepTopYW - dbgCurGroundYW;
                            dbgCanStepUpByGround = dbgStepTopDeltaW > 0.01f && dbgStepTopDeltaW <= maxStepWDbg;
                        }
                    }

                    float triggerAdvanceSimDbg = recallStepTriggerAdvance * PBF_Utils.InvScale;
                    float triggerDistWDbg = (radiusXZ + recallObstacleKeepDistanceMarginSim + triggerAdvanceSimDbg) * PBF_Utils.Scale;
                    if (dbgIsDropByGround && closestBlockDistWDbg <= triggerDistWDbg)
                    {
                        baseVel = new float3(rawDir.x, 0f, rawDir.z) * speed;
                        steering = float3.zero;
                        closestBlockIdx = -1;
                    }
                }
            }
            
            // 如果没有阻挡，直接返回基础速度
            if (closestBlockIdx < 0)
            {
                if (hasStepJumpTarget && _controllerStepJumpTargetCenterY != null && controllerId > 0 && controllerId < _controllerStepJumpTargetCenterY.Length)
                    _controllerStepJumpTargetCenterY[controllerId] = float.NaN;

                float3 dir3 = new float3(rawDir.x, 0f, rawDir.z);
                float dirLen2 = math.lengthsq(dir3);
                if (dirLen2 > 1e-6f)
                {
                    dir3 /= math.sqrt(dirLen2);
                    float3 velXZ = new float3(baseVel.x, 0f, baseVel.z);
                    float dotToward = math.dot(velXZ, dir3);
                    if (dotToward < 0f)
                    {
                        velXZ -= dir3 * dotToward;
                        baseVel.x = velXZ.x;
                        baseVel.z = velXZ.z;
                    }
                }
                return baseVel;
            }
            
            // === 阻挡处理：叠加 steering 力 ===
            
            // 1. 检查是否是可跨越的台阶
            float groupBottomY = center.y - halfHeight;
            float stepHeight = closestBlockTopY - groupBottomY;
            // 只要障碍物顶部低于主体，就尝试向上
            float stepHeightW = stepHeight * PBF_Utils.Scale;
            bool canStepUp = stepHeight > 0 && stepHeight <= recallStepMaxHeightSim && stepHeightW > 0.03f;
            if (dbgHasCurGround && dbgHasAheadGround)
            {
                stepHeightW = dbgHasStepTop ? dbgStepTopDeltaW : dbgGroundDeltaW;
                canStepUp = dbgCanStepUpByGround;
            }

            if (canStepUp)
            {
                if (hasStepJumpTarget && _controllerStepJumpTargetCenterY != null && controllerId > 0 && controllerId < _controllerStepJumpTargetCenterY.Length)
                {
                    float targetY = _controllerStepJumpTargetCenterY[controllerId];
                    float dy = targetY - center.y;
                    if (dy <= 0.01f * PBF_Utils.InvScale)
                    {
                        _controllerStepJumpTargetCenterY[controllerId] = float.NaN;
                        hasStepJumpTarget = false;
                    }
                    else
                    {
                        float upV = math.min(maxUpSim, dy / dt);
                        baseVel.y = math.max(baseVel.y, upV);
                    }
                }

                float triggerAdvanceSim = recallStepTriggerAdvance * PBF_Utils.InvScale;
                float triggerDist = radiusXZ + recallObstacleKeepDistanceMarginSim + triggerAdvanceSim;
                if (closestBlockDist <= triggerDist)
                {
                    baseVel.x = rawDir.x * speed;
                    baseVel.z = rawDir.z * speed;
                    float clearanceW = math.max(0.02f, sphereRadiusW * 0.5f);
                    float targetRiseW = stepHeightW * (1f + recallHeightCompPercent) + clearanceW;
                    float g = math.abs(UnityEngine.Physics.gravity.y);
                    float jumpVelW = math.sqrt(math.max(0f, 2f * g * targetRiseW)) * math.max(0.2f, recallStepJumpSpeedScale);
                    float jumpVelSim = jumpVelW * PBF_Utils.InvScale;
                    baseVel.y = math.max(baseVel.y, math.min(maxUpSim, jumpVelSim));

                    if (controllerId > 0)
                    {
                        if (_controllerStepJumpTargetCenterY != null && controllerId < _controllerStepJumpTargetCenterY.Length)
                        {
                            float clearanceSim = clearanceW * PBF_Utils.InvScale;
                            float extraUpSim = math.max(0f, stepHeight * recallHeightCompPercent);
                            float targetCenterYSim = closestBlockTopY + clearanceSim + halfHeight + extraUpSim;
                            float curTarget = _controllerStepJumpTargetCenterY[controllerId];
                            float nextTarget = float.IsNaN(curTarget) ? targetCenterYSim : math.max(curTarget, targetCenterYSim);
                            _controllerStepJumpTargetCenterY[controllerId] = nextTarget;
                        }
                    }

                    float stepProximityFactor = 1f - math.saturate(closestBlockDist / checkDist);
                    float stepBaseSlow = 1f - stepProximityFactor * 0.75f;
                    stepBaseSlow = math.max(stepBaseSlow, 0.2f);
                    stepBaseSlow = math.saturate(stepBaseSlow);
                    baseVel.x *= stepBaseSlow;
                    baseVel.z *= stepBaseSlow;
                    return baseVel;
                }

                baseVel.x = rawDir.x * speed;
                baseVel.z = rawDir.z * speed;
                if (!hasStepJumpTarget)
                    baseVel.y = 0f;
                return baseVel;
            }

            if (hasStepJumpTarget && _controllerStepJumpTargetCenterY != null && controllerId > 0 && controllerId < _controllerStepJumpTargetCenterY.Length)
                _controllerStepJumpTargetCenterY[controllerId] = float.NaN;
            
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

            {
                float3 dir3 = new float3(rawDir.x, 0f, rawDir.z);
                float dirLen2 = math.lengthsq(dir3);
                if (dirLen2 > 1e-6f)
                {
                    dir3 /= math.sqrt(dirLen2);
                    float3 velXZ = new float3(resultVel.x, 0f, resultVel.z);
                    float dotToward = math.dot(velXZ, dir3);
                    if (dotToward < 0f)
                    {
                        velXZ -= dir3 * dotToward;
                        resultVel.x = velXZ.x;
                        resultVel.z = velXZ.z;
                    }
                }
            }

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
            ConfigResetHelper.ResetToDefaults(this);
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

            _stableIdToController[stableId] = newId;
            return newId;
        }

        private void RefreshStableIdToSlotMapping()
        {
            int required = math.max(1, _nextStableId);

            if (_connect && _recallEligibleStableIds.IsCreated && _recallEligibleStableIds.Length < required)
            {
                var old = _recallEligibleStableIds;
                _recallEligibleStableIds = new NativeArray<byte>(required, Allocator.Persistent);
                for (int i = 0; i < _recallEligibleStableIds.Length; i++)
                    _recallEligibleStableIds[i] = (i < old.Length) ? old[i] : (byte)0;
                old.Dispose();
            }

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
                _stableResolveAllStableIds = new List<int>(math.max(16, _stableIdToCenter.Count));
            if (_stableResolveAllStableCenters == null)
                _stableResolveAllStableCenters = new List<float3>(math.max(16, _stableIdToCenter.Count));
            _stableResolveAllStableIds.Clear();
            _stableResolveAllStableCenters.Clear();
            if (_stableResolveAllStableIds.Capacity < _stableIdToCenter.Count)
                _stableResolveAllStableIds.Capacity = _stableIdToCenter.Count;
            if (_stableResolveAllStableCenters.Capacity < _stableIdToCenter.Count)
                _stableResolveAllStableCenters.Capacity = _stableIdToCenter.Count;
            foreach (var kv in _stableIdToCenter)
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
                if (_tmpStableIdSet.Contains(sid))
                    continue;
                _stableResolveAssignedStable[c] = sid;
                _tmpStableIdSet.Add(sid);
                _stableResolveStableAssignedToComp[sid] = c;
            }

            // 第二遍：为每个组件落定 stableId（未匹配到则新分配）
            for (int c = 0; c < compCount; c++)
            {
                int finalStableId;
                bool isNewId = false;
                if (_stableResolveAssignedStable[c] != 0)
                {
                    finalStableId = _stableResolveAssignedStable[c];
                    inheritedCount++;
                }
                else
                {
                    finalStableId = _nextStableId++;
                    isNewId = true;
                    newIdCount++;
                    _tmpStableIdSet.Add(finalStableId);
                }

                _compToStableId[c] = finalStableId;
                _stableIdToCenter[finalStableId] = _stableResolveCenters[c];
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
