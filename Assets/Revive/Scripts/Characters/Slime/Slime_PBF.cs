using System.Collections.Generic;
using MoreMountains.TopDownEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace Slime
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
        
        [Header("【核心物理参数】")]
        [ChineseLabel("气泡速度"), Tooltip("控制内部气泡的移动速度")]
        [SerializeField, Range(0, 1), DefaultValue(0.1f)] 
        private float bubbleSpeed = 0.1f;
        
        [ChineseLabel("粘性强度"), Tooltip("控制粒子之间的粘性力，值越大史莱姆越粘稠")]
        [SerializeField, Range(0, 100), DefaultValue(10f)] 
        private float viscosityStrength = 10f;
        
        [ChineseLabel("聚集强度"), Tooltip("控制粒子向中心聚集的力度，值越大史莱姆越紧密")]
        [SerializeField, Range(0.1f, 100), DefaultValue(30f)] 
        private float concentration = 30f;
        
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
        
        [Header("【模拟参数】")]
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
        
        [ChineseLabel("垂直偏移"), Tooltip("控制中心的垂直偏移系数")]
        [SerializeField, Range(0f, 0.5f), DefaultValue(0.05f)]
        private float verticalOffset = 0.05f;
        
        #endregion
        
        #region 【交互参数】
        
        [Header("【交互参数】")]
        [ChineseLabel("发射速度"), Tooltip("粒子被发射时的初始速度")]
        [SerializeField, Range(5f, 30f), DefaultValue(15f)]
        private float emitSpeed = 15f;
        
        [ChineseLabel("发射角度阈值"), Tooltip("值越高发射范围越窄（0=全方向, 1=正前方）")]
        [SerializeField, Range(0.5f, 1.0f), DefaultValue(0.7f)]
        private float emitAngleThreshold = 0.7f;
        
        [ChineseLabel("发射冷却"), Tooltip("两次发射之间的最小间隔（秒）")]
        [SerializeField, Range(0.05f, 1f), DefaultValue(0.15f)]
        private float emitCooldown = 0.15f;
        
        [ChineseLabel("发射批量"), Tooltip("单次发射的粒子数量")]
        [SerializeField, Range(1, 200), DefaultValue(25)]
        private int emitBatchSize = 25;
        
        /// <summary>发射冷却时间（秒）</summary>
        public float EmitCooldown => emitCooldown;
        
        /// <summary>单次发射粒子数量</summary>
        public int EmitBatchSize => emitBatchSize;
        
        #endregion
        
        #region 【控制器参数】
        
        [Header("【控制器参数】")]
        [ChineseLabel("核心范围"), Tooltip("此范围内的粒子被认为是主体")]
        [SerializeField, Range(5f, 30f), DefaultValue(15f)]
        private float coreRadius = 15f;
        
        #endregion
        
        #region 【粒子池扩展】
        
        [Header("【粒子池扩展（B2架构）】")]
        [ChineseLabel("最大粒子数"), Tooltip("预分配的最大粒子池容量")]
        [SerializeField, Range(2048, 8192), DefaultValue(4096)]
        private int maxParticles = 4096;
        
        [ChineseLabel("活跃粒子数"), Tooltip("当前参与模拟的粒子数（只读）")]
        [SerializeField]
        private int activeParticles = 800;
        
        #endregion
        
        #region 【召回参数】
        
        [Header("【召回参数】")]
        [ChineseLabel("召回最大速度"), Tooltip("远距离时的召回速度")]
        [SerializeField, Range(10f, 40f), DefaultValue(20f)]
        private float recallMaxSpeed = 20f;
        
        [ChineseLabel("召回最小速度"), Tooltip("靠近主体时的召回速度")]
        [SerializeField, Range(1f, 10f), DefaultValue(2f)]
        private float recallMinSpeed = 2f;
        
        [ChineseLabel("减速开始距离"), Tooltip("开始从最大速度减速到最小速度的距离")]
        [SerializeField, Range(10f, 60f), DefaultValue(30f)]
        private float recallSlowdownDist = 30f;
        
        #endregion
        
        #region 【CCA控制器参数】
        
        [Header("【CCA控制器参数】")]
        [ChineseLabel("主体半径扩展系数"), Tooltip("主体实际半径 = 最大粒子距离 × 此系数")]
        [SerializeField, Range(1.0f, 2.0f), DefaultValue(1.2f)]
        private float mainRadiusScale = 1.2f;
        
        [ChineseLabel("分离组件半径系数"), Tooltip("分离组件的半径计算系数")]
        [SerializeField, Range(0.3f, 1.0f), DefaultValue(0.6f)]
        private float separatedRadiusScale = 0.6f;
        
        [ChineseLabel("主体重叠判断系数"), Tooltip("距离 < 主体半径 × 此系数 时认为是主体的一部分")]
        [SerializeField, Range(0.5f, 1.0f), DefaultValue(0.8f)]
        private float mainOverlapThreshold = 0.8f;
        
        [ChineseLabel("最大分离组数"), Tooltip("TopK分离组上限，小碎片会归并到最近的分离组")]
        [SerializeField, Range(4, 32), DefaultValue(8)]
        private int maxDetachedGroups = 8;
        
        [ChineseLabel("发射表面阈值"), Tooltip("选择外层多少比例的粒子作为发射候选")]
        [SerializeField, Range(0.5f, 0.95f), DefaultValue(0.8f)]
        private float emitSurfaceThreshold = 0.8f;
        
        #endregion
        
        #region 【渲染资源】
        
        [Header("【渲染资源】")]
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
        
        [Header("【渲染设置】")]
        [Tooltip("控制目标 - 史莱姆跟随的Transform")]
        public Transform trans;
        
        [Tooltip("速度控制器 - 从 TopDownEngine 获取速度（优先于 Rigidbody）")]
        public TopDownController3D velocityController;
        
        [Tooltip("渲染模式 - Particles显示粒子，Surface显示表面")]
        public RenderMode renderMode = RenderMode.Surface;
        
        #endregion
        
        #region 【运行时状态】（只读）
        
        [Header("【运行时状态】（只读）")]
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
        
        [Header("【碰撞体采集设置（近场缓存）】")]
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
        
        [Header("【调试设置】")]
        [Tooltip("显示网格调试信息")]
        public bool gridDebug;
        
        [Tooltip("显示组件调试信息（史莱姆分离检测）")]
        public bool componentDebug;

        [Tooltip("输出碰撞体采集调试信息（会产生大量日志，影响启动性能）")]
        public bool colliderCollectDebug;

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
        private NativeArray<float3> _posPredict;
        private NativeArray<float3> _posOld;
        private NativeArray<float> _lambdaBuffer;
        private NativeArray<float3> _velocityBuffer;
        private NativeArray<float3> _velocityTempBuffer;
        private NativeHashMap<int, int2> _lut;
        private NativeArray<int2> _hashes;
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
        private NativeList<ParticleController> _controllerBuffer;
        private NativeArray<ParticleController> _sourceControllers;
        private int[] _componentToGroup;
        
        private ComputeBuffer _particlePosBuffer;
        private ComputeBuffer _particleCovBuffer;
        private ComputeBuffer _bubblesDataBuffer;
        
        #endregion
        
        private float3 _lastMousePos;
        private bool _mouseDown;
        private float3 _velocityY = float3.zero;
        private Bounds _bounds;
        private Vector3 _velocity = Vector3.zero;

        private LMarchingCubes _marchingCubes;
        private Mesh _mesh;
        
        private int batchCount = 64;
        private bool _connect;
        private float _connectStartTime;
        private NativeList<SlimeInstance> _slimeInstances;
        private int _controlledInstance;
        private Stack<int> _instancePool;


        void Awake()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
        }
        
        public void StartRecall()
        {
            _connect = true;
            _connectStartTime = Time.time;
            Debug.Log("[Recall] 启动CCA融合回收");
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
        
        void Start()
        {
            double startupT0 = 0;
            double startupPrev = 0;
            if (startupProfileDebug)
            {
                startupT0 = Time.realtimeSinceStartupAsDouble;
                startupPrev = startupT0;
                Debug.Log("[Startup] Slime_PBF.Start begin");
            }

            // 使用 maxParticles 替代 PBF_Utils.Num
            _particles = new NativeArray<Particle>(maxParticles, Allocator.Persistent);
            
            // 从 VolumeManager 获取初始主体粒子数
            int initialMainCount = 800; // 默认值
            if (VolumeManager.Instance != null)
            {
                initialMainCount = Mathf.Min(VolumeManager.Instance.initialMainVolume, maxParticles);
                if (startupProfileDebug)
                    Debug.Log($"[Slime_PBF] 初始主体粒子数: {initialMainCount} / {maxParticles}");
            }
            initialMainCount = Mathf.Min(initialMainCount, PBF_Utils.Num);
            
            // 初始化主体粒子
            float half = PBF_Utils.Width / 2.0f;
            // 获取玩家位置作为粒子生成中心（转换为粒子坐标系）
            float3 spawnCenter = trans != null ? (float3)(trans.position * PBF_Utils.InvScale) : float3.zero;
            int particleIndex = 0;
            for (int i = 0; i < PBF_Utils.Width / 2; i++)
            for (int j = 0; j < PBF_Utils.Width; j++)
            for (int k = 0; k < PBF_Utils.Width; k++)
            {
                var idx = i * PBF_Utils.Width * PBF_Utils.Width + j * PBF_Utils.Width + k;
                if (particleIndex < initialMainCount)
                {
                    _particles[idx] = new Particle
                    {
                        Position = spawnCenter + new float3(k - half, j, i - half) * 0.5f,
                        ID = 0,
                        BodyState = 0, // 主体粒子
                        SourceId = -1, // 无源（主体）
                    };
                }
                else
                {
                    _particles[idx] = new Particle
                    {
                        Position = new float3(0, -1000, 0), // 远离视野
                        ID = 0,
                        BodyState = 2, // 休眠状态
                        SourceId = -1, // 无源
                    };
                }
                particleIndex++;
            }
            
            // 剩余粒子设为休眠状态（超过 PBF_Utils.Num 的部分）
            for (int i = PBF_Utils.Num; i < maxParticles; i++)
            {
                _particles[i] = new Particle
                {
                    Position = new float3(0, -1000, 0), // 远离视野
                    ID = 0,
                    BodyState = 2, // 休眠状态
                    SourceId = -1, // 无源
                };
            }
            
            // 初始活跃粒子数
            activeParticles = initialMainCount;

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
                    Debug.Log($"[Slime_PBF] 找到 {allSources.Count} 个场景水珠源");
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

            // 所有缓冲区使用 maxParticles 分配
            _particlesTemp = new NativeArray<Particle>(maxParticles, Allocator.Persistent);
            _posPredict = new NativeArray<float3>(maxParticles, Allocator.Persistent);
            _posOld = new NativeArray<float3>(maxParticles, Allocator.Persistent);
            _lambdaBuffer = new NativeArray<float>(maxParticles, Allocator.Persistent);
            _velocityBuffer = new NativeArray<float3>(maxParticles, Allocator.Persistent);
            _velocityTempBuffer = new NativeArray<float3>(maxParticles, Allocator.Persistent);
            _boundsBuffer = new NativeArray<float3>(2, Allocator.Persistent);
            
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

            _lut = new NativeHashMap<int, int2>(maxParticles, Allocator.Persistent);
            _hashes = new NativeArray<int2>(maxParticles, Allocator.Persistent);
            
            _componentsBuffer = new NativeList<Effects.Component>(16, Allocator.Persistent);
            _gridIDBuffer = new NativeArray<int>(_gridBuffer.Length, Allocator.Persistent);
            _controllerBuffer = new NativeList<ParticleController>(16, Allocator.Persistent);
            _controllerBuffer.Add(new ParticleController
            {
                Center = float3.zero,
                Radius = PBF_Utils.InvScale,
                Velocity = float3.zero,
                Concentration = concentration,
            });

            _marchingCubes = new LMarchingCubes();

            if (startupProfileDebug)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                Debug.Log($"[Startup] AllocateBuffers+MarchingCubes {(now - startupPrev) * 1000.0:F1}ms");
                startupPrev = now;
            }

            _particlePosBuffer = new ComputeBuffer(maxParticles, sizeof(float) * 6); // Particle: float3 Position + int ID + int BodyState + int SourceId
            _particleCovBuffer = new ComputeBuffer(maxParticles, sizeof(float) * 16);
            _bubblesDataBuffer  = new ComputeBuffer(PBF_Utils.BubblesCount, sizeof(float) * 8);
            particleMat.SetBuffer("_ParticleBuffer", _particlePosBuffer);
            particleMat.SetBuffer("_CovarianceBuffer", _particleCovBuffer);
            bubblesMat.SetBuffer("_BubblesBuffer", _bubblesDataBuffer);

            _slimeInstances = new NativeList<SlimeInstance>(16,  Allocator.Persistent);
            _slimeInstances.Add(new SlimeInstance()
            {
                // Active = true,
                Center = Vector3.zero,
                Pos = Vector3.zero,
                Dir = Vector3.right,
                Radius = 1
            });
            _instancePool = new Stack<int>();
            
            int sourceCapacity = math.max(16, allSources.Count);
            _sourceControllers = new NativeArray<ParticleController>(sourceCapacity, Allocator.Persistent);
            
            // 近场缓存：固定容量分配碰撞体缓冲区
            _colliderBuffer = new NativeArray<MyBoxCollider>(maxNearbyColliders, Allocator.Persistent);
            _overlapResults = new Collider[maxNearbyColliders];
            _currentColliderCount = 0;
            _colliderRefreshTimer = 0;
            
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

            if (VolumeManager.Instance == null)
            {
                Debug.LogWarning("[Slime_PBF] 场景中未找到VolumeManager，体积管理功能将不可用");
            }
            else
            {
                VolumeManager.Instance.UpdateVolume(_particles, true);
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
            if (_particlesTemp.IsCreated) _particlesTemp.Dispose();
            if (_lut.IsCreated) _lut.Dispose();
            if (_hashes.IsCreated) _hashes.Dispose();
            if (_posPredict.IsCreated) _posPredict.Dispose();
            if (_posOld.IsCreated) _posOld.Dispose();
            if (_lambdaBuffer.IsCreated) _lambdaBuffer.Dispose();
            if (_velocityBuffer.IsCreated) _velocityBuffer.Dispose();
            if (_velocityTempBuffer.IsCreated) _velocityTempBuffer.Dispose();
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
            if (_controllerBuffer.IsCreated) _controllerBuffer.Dispose();
            if (_slimeInstances.IsCreated) _slimeInstances.Dispose();
            if (_colliderBuffer.IsCreated)  _colliderBuffer.Dispose();
            if (_sourceControllers.IsCreated) _sourceControllers.Dispose();

            _marchingCubes.Dispose();
            _particlePosBuffer.Release();
            _particleCovBuffer.Release();
            _bubblesDataBuffer.Release();

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
                
                // 计算粒子分布
                if (_particles.IsCreated && activeParticles > 0)
                {
                    float3 center = (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0) ? _controllerBuffer[0].Center : float3.zero;
                    float minDist = float.MaxValue;
                    float maxDist = 0;
                    int count = math.min(activeParticles, _particles.Length);
                    for (int i = 0; i < count; i++)
                    {
                        float dist = math.length(_particles[i].Position - center);
                        minDist = math.min(minDist, dist);
                        maxDist = math.max(maxDist, dist);
                    }
                    Debug.Log($"[Status] 粒子分布: 最近={minDist:F2}, 最远={maxDist:F2}");
                }
            }
            
            HandleMouseInteraction();

            if (renderMode == RenderMode.Particles)
            {
                Graphics.DrawMeshInstancedProcedural(particleMesh, 0, particleMat, _bounds, activeParticles);
            }
            else if (renderMode == RenderMode.Surface)
            {
                if (_mesh != null)
                    Graphics.DrawMesh(_mesh, Matrix4x4.TRS(_bounds.min, Quaternion.identity, Vector3.one), mat, 0);

                Graphics.DrawMeshInstancedProcedural(particleMesh, 0, bubblesMat, _bounds, PBF_Utils.BubblesCount);
            }

            if (concentration > 5)
            {
                foreach (var slime in _slimeInstances)
                {
                    if (!slime.Active) continue;

                    if (math.lengthsq(slime.Dir) > 0.001f)
                    {
                        Graphics.DrawMesh(faceMesh, Matrix4x4.TRS(slime.Pos * PBF_Utils.Scale,
                            Quaternion.LookRotation(-slime.Dir),
                            0.2f * math.sqrt(slime.Radius * PBF_Utils.Scale) * Vector3.one), faceMat, 0);
                    }
                }
            }
        }

        private void FixedUpdate()
        {
            PerformanceProfiler.BeginFrame();
            
            // 近场碰撞体刷新（每 N 帧一次）
            _colliderRefreshTimer++;
            if (_colliderRefreshTimer >= colliderRefreshInterval)
            {
                _colliderRefreshTimer = 0;
                RefreshNearbyColliders();
            }
            
            for (int i = 0; i < 2; i++)
            {
                Profiler.BeginSample("Simulate");
                PerformanceProfiler.Begin($"Simulate_{i}");
                Simulate();
                PerformanceProfiler.End($"Simulate_{i}");
                Profiler.EndSample();
            }

            PerformanceProfiler.Begin("Surface");
            Surface();
            PerformanceProfiler.End("Surface");
            
            PerformanceProfiler.Begin("Control");
            Control();
            PerformanceProfiler.End("Control");
            
            PerformanceProfiler.Begin("Bubbles");
            Bubbles();
            PerformanceProfiler.End("Bubbles");
            
            bubblesNum = PBF_Utils.BubblesCount - _bubblesPoolBuffer.Length;
            
            if (renderMode == RenderMode.Particles)
            {
                _particlePosBuffer.SetData(_particles);
                particleMat.SetInt("_Aniso", 0);
            }
            else
                _bubblesDataBuffer.SetData(_bubblesBuffer);
            
            _bounds = new Bounds()
            {
                min = minPos * PBF_Utils.Scale,
                max = maxPos * PBF_Utils.Scale
            };

            if (VolumeManager.Instance != null)
                VolumeManager.Instance.UpdateVolume(_particles);
            
            PerformanceProfiler.EndFrame();
        }

        private void Surface()
        {
            Profiler.BeginSample("Render");
            PerformanceProfiler.Begin("Surface_MeanPos");

            var handle = new Reconstruction.ComputeMeanPosJob
            {
                Lut = _lut,
                Ps = _particles,
                MeanPos = _particlesTemp,
            }.Schedule(activeParticles, batchCount);

            if (useAnisotropic)
            {
                handle = new Reconstruction.ComputeCovarianceJob()
                {
                    Lut = _lut,
                    Ps = _particles,
                    MeanPos = _particlesTemp,
                    GMatrix = _covBuffer,
                }.Schedule(activeParticles, batchCount, handle);
            }

            new Reconstruction.CalcBoundsJob()
            {
                Ps = _particles,
                Bounds = _boundsBuffer,
                ActiveCount = activeParticles,
            }.Schedule(handle).Complete();
            PerformanceProfiler.End("Surface_MeanPos");

            Profiler.EndSample();

            _gridLut.Clear();
            float blockSize = PBF_Utils.CellSize * 4;
            minPos = math.floor(_boundsBuffer[0] / blockSize) * blockSize;
            maxPos = math.ceil(_boundsBuffer[1] / blockSize) * blockSize;

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
                ActiveCount = activeParticles,
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
                    ParticleLut = _lut,
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
                ParticleLut = _lut,
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
                Threshold = 1e-4f,
            }.Schedule();
            
            handle = new Effects.ParticleIDJob()
            {
                GridLut = _gridLut,
                GridID = _gridIDBuffer,
                Particles = _particles,
                MinPos = minPos,
            }.Schedule(activeParticles, batchCount, handle);
            
            handle.Complete();
            PerformanceProfiler.End("Surface_CCA");
            Profiler.EndSample();

            keys.Dispose();
        }

        private void Simulate()
        {
            _lut.Clear();
            
            PerformanceProfiler.Begin("ApplyForce");
            new Simulation_PBF.ApplyForceJob
            {
                Ps = _particles,
                Velocity = _velocityBuffer,
                PsNew = _particlesTemp,
                Controllers = _controllerBuffer,
                SourceControllers = _sourceControllers,
                Gravity = new float3(0, gravity, 0),
                DeltaTime = deltaTime,
                PredictStep = predictStep,
                VelocityDamping = velocityDamping,
                VerticalOffset = verticalOffset,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("ApplyForce");

            PerformanceProfiler.Begin("Hash");
            new Simulation_PBF.HashJob
            {
                Ps = _particlesTemp,
                Hashes = _hashes,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("Hash");

            PerformanceProfiler.Begin("Sort");
            // 只排序活跃粒子的哈希值
            var activeHashes = _hashes.GetSubArray(0, activeParticles);
            activeHashes.SortJob(new PBF_Utils.Int2Comparer()).Schedule().Complete();
            PerformanceProfiler.End("Sort");

            PerformanceProfiler.Begin("BuildLut");
            // 只用活跃粒子的哈希构建 LUT，避免未排序尾部污染
            new Simulation_PBF.BuildLutJob
            {
                Hashes = activeHashes,
                Lut = _lut
            }.Schedule().Complete();
            PerformanceProfiler.End("BuildLut");

            PerformanceProfiler.Begin("Shuffle");
            new Simulation_PBF.ShuffleJob
            {
                Hashes = _hashes,
                PsRaw = _particles,
                PsNew = _particlesTemp,
                Velocity = _velocityBuffer,
                PosOld = _posOld,
                PosPredict = _posPredict,
                VelocityOut = _velocityTempBuffer,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("Shuffle");

            PerformanceProfiler.Begin("ComputeLambda");
            new Simulation_PBF.ComputeLambdaJob
            {
                Lut = _lut,
                PosPredict = _posPredict,
                Lambda = _lambdaBuffer,
                TargetDensity = targetDensity,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("ComputeLambda");

            PerformanceProfiler.Begin("ComputeDeltaPos");
            new Simulation_PBF.ComputeDeltaPosJob
            {
                Lut = _lut,
                PosPredict = _posPredict,
                Lambda = _lambdaBuffer,
                Hashes = _hashes,
                PsOriginal = _particlesTemp,  // 从 ApplyForceJob 输出读取（包含原始 ID）
                PsNew = _particles,
                TargetDensity = targetDensity,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("ComputeDeltaPos");

            PerformanceProfiler.Begin("Update");
            // 动态地面高度：基于控制器位置 - 偏移量
            float minGroundY = _controllerBuffer.IsCreated && _controllerBuffer.Length > 0 
                ? _controllerBuffer[0].Center.y - _controllerBuffer[0].Radius * 2 
                : 0f;
            
            new Simulation_PBF.UpdateJob
            {
                Ps = _particles,
                PosOld = _posOld,
                Colliders = _colliderBuffer,
                ColliderCount = _currentColliderCount, // 只遍历有效碰撞体
                Velocity = _velocityTempBuffer,
                MaxVelocity = maxVelocity,
                DeltaTime = deltaTime,
                MinGroundY = minGroundY, // 动态地面高度
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("Update");

            PerformanceProfiler.Begin("ApplyViscosity");
            new Simulation_PBF.ApplyViscosityJob
            {
                Lut = _lut,
                PosPredict = _posPredict,
                VelocityR = _velocityTempBuffer,
                VelocityW = _velocityBuffer,
                ViscosityStrength = viscosityStrength,
                TargetDensity = targetDensity,
                DeltaTime = deltaTime,
            }.Schedule(activeParticles, batchCount).Complete();
            PerformanceProfiler.End("ApplyViscosity");
        }

        /// <summary>
        /// 激活场景水珠源
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
            
            // 检查是否有足够的粒子池空间
            int requiredCount = source.particleCount;
            if (activeParticles + requiredCount > maxParticles)
            {
                Debug.LogWarning($"[Slime_PBF] 粒子池空间不足，无法激活 {source.name}");
                return;
            }
            
            // 从休眠粒子中分配（找到 BodyState=2 的粒子）
            float3 sourcePos = (float3)source.transform.position * PBF_Utils.InvScale;
            int startIndex = activeParticles;
            int allocated = 0;
            float maxDist2 = 0;
            
            // 按网格生成粒子（参考主体粒子初始化方式）
            int gridSize = Mathf.CeilToInt(Mathf.Pow(requiredCount, 1f / 3f));
            float spacing = 0.5f;
            float halfGrid = gridSize * spacing * 0.5f;
            
            for (int i = startIndex; i < maxParticles && allocated < requiredCount; i++)
            {
                if (_particles[i].BodyState == 2) // 休眠粒子
                {
                    // 计算网格位置
                    int gx = allocated % gridSize;
                    int gy = (allocated / gridSize) % gridSize;
                    int gz = allocated / (gridSize * gridSize);
                    
                    float3 gridOffset = new float3(
                        gx * spacing - halfGrid,
                        gy * spacing,
                        gz * spacing - halfGrid
                    );
                    
                    _particles[i] = new Particle
                    {
                        Position = sourcePos + gridOffset,
                        ID = 0,
                        BodyState = 1, // 分离粒子状态
                        SourceId = sourceId, // 关联到该水滴源
                    };
                    _velocityBuffer[i] = float3.zero;
                    maxDist2 = math.max(maxDist2, math.lengthsq(gridOffset));
                    allocated++;
                }
            }
            
            if (allocated < requiredCount)
            {
                Debug.LogWarning($"[Slime_PBF] 只能分配 {allocated}/{requiredCount} 个粒子给 {source.name}");
            }
            
            // 更新活跃粒子数和源状态
            activeParticles += allocated;
            source.AssignParticles(startIndex, allocated);
            source.SetGroupId(sourceId);
            
            // 计算自适应半径（基于粒子实际分布，只在激活时计算一次）
            float maxDist = math.sqrt(maxDist2);
            source.SetAdaptiveRadius(math.max(source.spawnRadius, maxDist * 1.2f * PBF_Utils.Scale));
            
            source.SetState(DropWater.DropletSourceState.Simulated);
            if (componentDebug)
                Debug.Log($"[Slime_PBF] 激活 {source.name}：{allocated} 个粒子，自适应半径={source.AdaptiveRadius:F1}");
        }

        /// <summary>
        /// 休眠场景水珠源
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
            int deactivatedCount = 0;
            
            // 遍历活跃粒子，找到属于该源的粒子并回收（与末尾交换保持活跃区间连续）
            for (int i = 0; i < activeParticles;)
            {
                if (_particles[i].SourceId == sourceId && _particles[i].BodyState != 2)
                {
                    int lastIndex = activeParticles - 1;

                    // 将末尾活跃粒子移到当前位置
                    _particles[i] = _particles[lastIndex];
                    _velocityBuffer[i] = _velocityBuffer[lastIndex];

                    // 末尾置为休眠
                    var p = _particles[lastIndex];
                    p.Position = new float3(0, -1000, 0);
                    p.ID = 0;
                    p.BodyState = 2; // 休眠状态
                    p.SourceId = -1; // 清除源关联
                    _particles[lastIndex] = p;
                    _velocityBuffer[lastIndex] = float3.zero;

                    activeParticles--;
                    deactivatedCount++;
                    continue;
                }

                i++;
            }
            source.Reset(); // 重置源状态
            
            if (componentDebug)
                Debug.Log($"[Slime_PBF] 休眠 {source.name}：{deactivatedCount} 个粒子，activeParticles={activeParticles}");
        }

        // 注：AbsorbFromSource 方法已移除，吸收改为通过 MergeContactingParticles 接触融合自动进行

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
                            ActivateSource(source);
                        }
                        break;
                    
                    case DropWater.DropletSourceState.Simulated:
                        // 只检查休眠，吸收改为通过接触融合自动进行
                        if (distanceWorld > source.deactivationRadius)
                        {
                            DeactivateSource(source);
                        }
                        // 注意：不再使用 absorbRadius 距离判断吸收
                        // 场景水珠现在通过 MergeContactingParticles 的接触融合自动吸收
                        break;
                }
            }
        }

        private void Control()
        {
            _controllerBuffer.Clear();

            // === 1. 主体控制器始终基于 trans（用户控制的核） ===
            // 当使用 TopDownController 时，需要补偿 CharacterController 与 SphereCollider 的高度差
            float yOffset = velocityController != null ? verticalOffset : 0f;
            float3 mainCenter = trans != null ? 
                (float3)(trans.position * PBF_Utils.InvScale) + new float3(0, yOffset, 0) : float3.zero;
            
            // 计算主体半径：找到距离 trans 最近的粒子群的范围
            float mainMaxDist = 0;
            int count = math.min(activeParticles, _particles.Length);
            
            for (int i = 0; i < count; i++)
            {
                float dist = math.length(_particles[i].Position - mainCenter);
                if (dist < coreRadius)
                {
                    mainMaxDist = math.max(mainMaxDist, dist);
                }
            }
            
            float mainRadius = math.max(coreRadius, mainMaxDist * mainRadiusScale);
            
            // 主体控制器始终在 index 0
            _controllerBuffer.Add(new ParticleController()
            {
                Center = mainCenter,
                Radius = mainRadius,
                Velocity = float3.zero,
                Concentration = concentration,
            });

            // === 2. TopK 分离组：从 CCA 组件中选择最大的 maxDetachedGroups 个 ===
            int separatedCount = 0;
            int skippedOverlapCount = 0;
            int fragmentReassignedCount = 0;
            
            // 2.1 收集所有非主体重叠的 CCA 组件
            int compCount = _componentsBuffer.Length;
            if (_componentToGroup == null || _componentToGroup.Length < compCount)
                _componentToGroup = new int[math.max(16, compCount)];
            
            // 临时数组：存储组件信息用于排序
            var candidateIndices = new NativeList<int>(compCount, Allocator.Temp);
            var candidateSizes = new NativeList<float>(compCount, Allocator.Temp);
            var candidateCenters = new NativeList<float3>(compCount, Allocator.Temp);
            var candidateRadii = new NativeList<float>(compCount, Allocator.Temp);
            try
            {
                for (int i = 0; i < compCount; i++)
                {
                    var component = _componentsBuffer[i];
                    float3 extent = component.BoundsMax - component.Center;
                    float radius = math.max(1, (extent.x + extent.y + extent.z) * PBF_Utils.CellSize * separatedRadiusScale);
                    float3 center = minPos + component.Center * PBF_Utils.CellSize;
                    if (extent.y < 3)
                        center.y += extent.y * PBF_Utils.Scale * PBF_Utils.CellSize;
                    
                    float distToMain = math.length(center - mainCenter);
                    // 注：不再用距离判断重叠，改为在第3步用网格邻域检测实际接触
                    // 这里只跳过完全在主体内部的组件（防止主体内部碎片形成分离组）
                    if (distToMain + radius < mainRadius * 0.5f)
                    {
                        skippedOverlapCount++;
                        _componentToGroup[i] = 0; // 完全在主体内部，映射到主体
                        continue;
                    }
                    
                    // 计算组件大小（用 Bounds 体积近似）
                    float size = extent.x * extent.y * extent.z;
                    candidateIndices.Add(i);
                    candidateSizes.Add(size);
                    candidateCenters.Add(center);
                    candidateRadii.Add(radius);
                }
                
                // 2.2 按大小排序选择 TopK
                int candidateCount = candidateIndices.Length;
                int topK = math.min(maxDetachedGroups, candidateCount);
                
                // 简单选择排序找 TopK（候选数通常不大）
                for (int k = 0; k < topK; k++)
                {
                    int maxIdx = k;
                    float maxSize = candidateSizes[k];
                    for (int j = k + 1; j < candidateCount; j++)
                    {
                        if (candidateSizes[j] > maxSize)
                        {
                            maxIdx = j;
                            maxSize = candidateSizes[j];
                        }
                    }
                    // 交换
                    if (maxIdx != k)
                    {
                        int tmpIdx = candidateIndices[k]; candidateIndices[k] = candidateIndices[maxIdx]; candidateIndices[maxIdx] = tmpIdx;
                        float tmpSize = candidateSizes[k]; candidateSizes[k] = candidateSizes[maxIdx]; candidateSizes[maxIdx] = tmpSize;
                        float3 tmpCenter = candidateCenters[k]; candidateCenters[k] = candidateCenters[maxIdx]; candidateCenters[maxIdx] = tmpCenter;
                        float tmpRadius = candidateRadii[k]; candidateRadii[k] = candidateRadii[maxIdx]; candidateRadii[maxIdx] = tmpRadius;
                    }
                }
                
                // 2.3 为 TopK 创建分离组控制器（index 1..topK）
                for (int k = 0; k < topK; k++)
                {
                    int compIdx = candidateIndices[k];
                    float3 center = candidateCenters[k];
                    float radius = candidateRadii[k];
                    float distToMain = math.length(center - mainCenter);
                    
                    float3 toMain = float3.zero;
                    if (_connect)
                    {
                        float speedFactor = math.saturate(distToMain / recallSlowdownDist);
                        float speed = math.lerp(recallMinSpeed, recallMaxSpeed, math.sqrt(speedFactor));
                        toMain = speed * math.normalizesafe(mainCenter - center);
                    }
                    
                    int groupId = k + 1; // groupId 1..topK
                    while (_controllerBuffer.Length <= groupId)
                        _controllerBuffer.Add(default);
                    
                    _controllerBuffer[groupId] = new ParticleController()
                    {
                        Center = center,
                        Radius = radius,
                        Velocity = toMain,
                        Concentration = concentration,
                    };
                    
                    _componentToGroup[compIdx] = groupId;
                    separatedCount++;
                }
                
                // 2.4 小碎片归并到最近的 TopK 分离组
                if (topK > 0)
                {
                    for (int c = topK; c < candidateCount; c++)
                    {
                        int compIdx = candidateIndices[c];
                        float3 fragCenter = candidateCenters[c];
                        
                        // 找最近的 TopK 分离组
                        int nearestGroup = 1; // 默认第一个分离组
                        float nearestDist = float.MaxValue;
                        for (int k = 0; k < topK; k++)
                        {
                            float d = math.lengthsq(fragCenter - candidateCenters[k]);
                            if (d < nearestDist)
                            {
                                nearestDist = d;
                                nearestGroup = k + 1;
                            }
                        }
                        
                        _componentToGroup[compIdx] = nearestGroup;
                        fragmentReassignedCount++;
                    }
                }
                
                // 如果没有 TopK 分离组但有碎片，碎片映射到主体
                if (topK == 0 && candidateCount > 0)
                {
                    for (int c = 0; c < candidateCount; c++)
                    {
                        _componentToGroup[candidateIndices[c]] = 0;
                        fragmentReassignedCount++;
                    }
                }
            }
            finally
            {
                candidateIndices.Dispose();
                candidateSizes.Dispose();
                candidateCenters.Dispose();
                candidateRadii.Dispose();
            }
            
            // === 3. 更新粒子的 ID 为 groupId ===
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                if (p.BodyState != 1 || p.SourceId >= 0) continue; // 只处理非场景水珠的分离粒子
                
                int oldId = p.ID;
                if (oldId > 0 && oldId <= compCount)
                {
                    int newGroupId = _componentToGroup[oldId - 1];
                    if (newGroupId == 0)
                    {
                        // 完全在主体内部的组件，直接合并
                        p.BodyState = 0;
                        p.ID = 0;
                        _particles[i] = p;
                    }
                    else if (newGroupId != oldId)
                    {
                        p.ID = newGroupId;
                        _particles[i] = p;
                    }
                }
            }
            
            // === 3.5 接触检测：分离粒子与主体粒子相邻时合并 ===
            int contactMergedCount = 0;
            float contactDist2 = PBF_Utils.h2;
            for (int i = 0; i < activeParticles; i++)
            {
                var p = _particles[i];
                if (p.BodyState != 1 || p.SourceId >= 0) continue;
                
                // 检查是否与主体粒子接触
                float3 pos = p.Position;
                int3 coord = PBF_Utils.GetCoord(pos);
                bool contacted = false;
                
                for (int dz = -1; dz <= 1 && !contacted; ++dz)
                for (int dy = -1; dy <= 1 && !contacted; ++dy)
                for (int dx = -1; dx <= 1 && !contacted; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + math.int3(dx, dy, dz));
                    if (!_lut.TryGetValue(key, out int2 range)) continue;
                    
                    for (int j = range.x; j < range.y; j++)
                    {
                        if (i == j) continue;
                        if (_particles[j].BodyState != 0) continue; // 只检测主体粒子
                        
                        float r2 = math.lengthsq(pos - _particles[j].Position);
                        if (r2 <= contactDist2)
                        {
                            contacted = true;
                            break;
                        }
                    }
                }
                
                if (contacted)
                {
                    p.BodyState = 0;
                    p.ID = 0;
                    _particles[i] = p;
                    contactMergedCount++;
                }
            }
            
            if (componentDebug && contactMergedCount > 0 && Time.frameCount % 60 == 0)
                Debug.Log($"[Control] 接触合并: {contactMergedCount} 个粒子");
            
            // === 4. 填充 SourceControllers（场景水珠凝聚控制器，不进 _controllerBuffer） ===
            if (allSources != null && allSources.Count > 0)
            {
                // 确保容量足够
                if (!_sourceControllers.IsCreated || _sourceControllers.Length < allSources.Count)
                {
                    if (_sourceControllers.IsCreated) _sourceControllers.Dispose();
                    _sourceControllers = new NativeArray<ParticleController>(allSources.Count, Allocator.Persistent);
                }
                
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
                        Concentration = concentration * 0.5f,
                    };
                }
            }
            
            if (componentDebug && Time.frameCount % 60 == 0)
            {
                int activeSourceCount = 0;
                if (allSources != null)
                {
                    foreach (var source in allSources)
                    {
                        if (source != null && source.State == DropWater.DropletSourceState.Simulated)
                            activeSourceCount++;
                    }
                }
                Debug.Log($"[Control] connect={_connect}, 控制器数={_controllerBuffer.Length}(上限{1+maxDetachedGroups}), CCA组件={compCount}, TopK分离组={separatedCount}, overlap跳过={skippedOverlapCount}, 碎片归并={fragmentReassignedCount}, 场景水珠={activeSourceCount}");
            }
            
            if (separatedCount == 0 || (_connect && Time.time - _connectStartTime > 5f))
            {
                if (_connect && Time.time - _connectStartTime > 5f)
                    Debug.Log("[Recall] 5秒超时，强制停止召回");
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
            float contactDist2 = PBF_Utils.h2;

            // 预过滤：场景水珠只允许“接触融合”，当它远离主体包围半径时，
            // 跳过昂贵的邻域接触检测（不会影响正确性）
            float mainRadiusForPrefilter = mergeRadius;
            if (mainOverlapThreshold > 1e-5f)
                mainRadiusForPrefilter = mergeRadius / mainOverlapThreshold;
            float sceneDropletPrefilterRadius = mainRadiusForPrefilter + PBF_Utils.h;
            float sceneDropletPrefilterRadius2 = sceneDropletPrefilterRadius * sceneDropletPrefilterRadius;

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

                float gateMargin = PBF_Utils.h;
                for (int s = 0; s < sourceCount; s++)
                {
                    _sourceMayContact[s] = true;

                    var src = allSources[s];
                    if (src == null || src.State != DropWater.DropletSourceState.Simulated)
                        continue;

                    ParticleController cl = _sourceControllers[s];
                    float gateRadius = sceneDropletPrefilterRadius + cl.Radius + gateMargin;
                    float gateRadius2 = gateRadius * gateRadius;
                    if (math.lengthsq(cl.Center - mainCenter) > gateRadius2)
                        _sourceMayContact[s] = false;
                }
            }

            for (int i = 0; i < count; i++)
            {
                // 只处理分离粒子（BodyState=1）
                if (_particles[i].BodyState != 1) continue;
                
                bool isSceneDroplet = _particles[i].SourceId >= 0;
                
                // 非场景水珠的分离粒子已经通过 CCA 连通性在第3步处理了
                // 这里只处理场景水珠的接触检测
                if (!isSceneDroplet) continue;
                
                if (enableSourceGate)
                {
                    int sid = _particles[i].SourceId;
                    if ((uint)sid < (uint)sourceCount && !_sourceMayContact[sid])
                        continue;
                }

                float3 pos = _posPredict[i];
                float distToMain2 = math.lengthsq(pos - mainCenter);

                // 场景水珠使用接触检测来判断是否合并
                if (distToMain2 > sceneDropletPrefilterRadius2)
                    continue;

                bool shouldMerge = false;
                int3 coord = PBF_Utils.GetCoord(pos);

                for (int dz = -1; dz <= 1 && !shouldMerge; ++dz)
                for (int dy = -1; dy <= 1 && !shouldMerge; ++dy)
                for (int dx = -1; dx <= 1 && !shouldMerge; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + math.int3(dx, dy, dz));
                    if (!_lut.TryGetValue(key, out int2 range)) continue;

                    for (int j = range.x; j < range.y; j++)
                    {
                        if (i == j) continue;
                        if (_particles[j].BodyState != 0) continue; // 只检测主体粒子

                        float r2 = math.lengthsq(pos - _posPredict[j]);
                        if (r2 > contactDist2) continue;

                        shouldMerge = true;
                        break;
                    }
                }

                if (!shouldMerge) continue;

                var p = _particles[i];
                int sourceId = p.SourceId;
                p.BodyState = 0;
                p.ID = 0;
                p.SourceId = -1; // 清除源关联
                _particles[i] = p;
                
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
            
            if ((mergedCount > 0 || dropletMergedCount > 0) && componentDebug)
            {
                Debug.Log($"[Merge] 融合了 {mergedCount} 个分离粒子, {dropletMergedCount} 个场景水珠粒子");
            }
        }
        
        /// <summary>
        /// 自动分离 - 超出主体半径的粒子自动标记为分离状态
        /// </summary>
        private void AutoSeparateDistantParticles(float3 mainCenter, float mainRadius)
        {
            int autoSeparatedCount = 0;
            
            for (int i = 0; i < _particles.Length; i++)
            {
                // 只处理主体粒子
                if (_particles[i].BodyState != 0) continue;
                
                float dist = math.length(_particles[i].Position - mainCenter);
                
                // 超出主体半径，标记为分离
                if (dist > mainRadius)
                {
                    var p = _particles[i];
                    p.BodyState = 1;
                    _particles[i] = p;
                    autoSeparatedCount++;
                }
            }
            
            if (autoSeparatedCount > 0 && componentDebug)
            {
                Debug.Log($"[AutoSeparate] 自动分离了 {autoSeparatedCount} 个粒子 (主体半径: {mainRadius:F1})");
            }
        }

        /// <summary>
        /// 发射粒子 - 从主体朝向鼠标方向选择粒子并赋予速度
        /// </summary>
        public void EmitParticles()
        {
            // VolumeManager 必须存在
            if (VolumeManager.Instance == null)
            {
                Debug.LogError("[Emit] VolumeManager.Instance 不存在，无法发射");
                return;
            }
            
            // 检查VolumeManager是否允许发射
            if (!VolumeManager.Instance.CanEmit(emitBatchSize))
            {
                Debug.LogWarning($"[Emit] 体积不足，无法发射 (当前: {VolumeManager.Instance.CurrentVolume}, 最小: {VolumeManager.Instance.minVolume})");
                return;
            }
            
            // 获取主控制器中心（ID=0的粒子所属的控制器）
            float3 center = float3.zero;
            if (trans != null)
            {
                center = (float3)(trans.position * PBF_Utils.InvScale);
            }
            else if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
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
            
            // 选择属于主体（ID=0）的表面粒子
            float maxDist = 0;
            float maxProj = float.MinValue;
            int mainParticleCount = 0;
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i].BodyState != 0) continue;  // 只考虑主体粒子
                mainParticleCount++;
                float3 toParticle = _particles[i].Position - center;
                float dist = math.length(toParticle);
                maxDist = math.max(maxDist, dist);

                float3 toParticleXZ = toParticle;
                toParticleXZ.y = 0;
                float proj = math.dot(toParticleXZ, emitDirection);
                maxProj = math.max(maxProj, proj);
            }
            
            if (mainParticleCount == 0)
            {
                Debug.LogWarning("[Emit] 没有主体粒子可发射");
                return;
            }
            
            // 选择表面粒子并发射
            float surfaceThreshold = maxDist * emitSurfaceThreshold;  // 选择外层粒子作为发射候选
            bool useDirectionalSurface = maxProj > 1e-4f;
            float projThreshold = maxProj * emitSurfaceThreshold;
            int emitted = 0;
            int candidateCount = 0;
            
            // 限制实际发射数量（发射数量由本组件 emitBatchSize 控制）
            int maxEmit = Mathf.Min(emitBatchSize, VolumeManager.Instance.GetMaxEmitAmount());
            
            for (int i = 0; i < _particles.Length && emitted < maxEmit; i++)
            {
                if (_particles[i].BodyState != 0) continue;  // 只选择主体粒子（BodyState=0）
                
                float3 toParticle = _particles[i].Position - center;
                float dist = math.length(toParticle);
                float3 toParticleXZ = toParticle;
                toParticleXZ.y = 0;
                float proj = math.dot(toParticleXZ, emitDirection);

                bool isSurface = useDirectionalSurface ? (proj >= projThreshold) : (dist > surfaceThreshold);
                if (!isSurface) continue;
                
                candidateCount++;
                float3 dir = math.normalizesafe(toParticleXZ);
                float dotProduct = math.dot(dir, emitDirection);
                if (!math.isfinite(dotProduct)) continue;
                
                // 选择朝向鼠标方向的粒子
                if (dotProduct > emitAngleThreshold)
                {
                    // 给予发射速度
                    _velocityBuffer[i] = emitDirection * emitSpeed;
                    
                    // 标记粒子为分离状态（BodyState=1, ID=0 表示刚发射）
                    var p = _particles[i];
                    p.BodyState = 1;
                    p.ID = 0;  // 重置ID，让 ApplyForceJob 豁免聚集力
                    _particles[i] = p;
                    
                    emitted++;
                }
            }

            if (emitted < maxEmit)
            {
                float relaxedThreshold = 0f;
                for (int i = 0; i < _particles.Length && emitted < maxEmit; i++)
                {
                    if (_particles[i].BodyState != 0) continue;

                    float3 toParticle = _particles[i].Position - center;
                    float dist = math.length(toParticle);

                    float3 toParticleXZ = toParticle;
                    toParticleXZ.y = 0;
                    float proj = math.dot(toParticleXZ, emitDirection);

                    bool isSurface = useDirectionalSurface ? (proj >= projThreshold) : (dist > surfaceThreshold);
                    if (!isSurface) continue;
                    float3 dir = math.normalizesafe(toParticleXZ);
                    float dotProduct = math.dot(dir, emitDirection);
                    if (!math.isfinite(dotProduct)) continue;

                    if (dotProduct > relaxedThreshold)
                    {
                        _velocityBuffer[i] = emitDirection * emitSpeed;

                        var p = _particles[i];
                        p.BodyState = 1;
                        p.ID = 0;
                        _particles[i] = p;

                        emitted++;
                    }
                }

                for (int i = 0; i < _particles.Length && emitted < maxEmit; i++)
                {
                    if (_particles[i].BodyState != 0) continue;

                    float3 toParticleXZ = _particles[i].Position - center;
                    toParticleXZ.y = 0;
                    float3 dir = math.normalizesafe(toParticleXZ);
                    float dotProduct = math.dot(dir, emitDirection);
                    if (!math.isfinite(dotProduct)) continue;

                    if (dotProduct > relaxedThreshold)
                    {
                        _velocityBuffer[i] = emitDirection * emitSpeed;

                        var p = _particles[i];
                        p.BodyState = 1;
                        p.ID = 0;
                        _particles[i] = p;

                        emitted++;
                    }
                }

                while (emitted < maxEmit)
                {
                    int bestIndex = -1;
                    float bestDot = float.MinValue;

                    for (int i = 0; i < _particles.Length; i++)
                    {
                        if (_particles[i].BodyState != 0) continue;

                        float3 toParticleXZ = _particles[i].Position - center;
                        toParticleXZ.y = 0;
                        float3 dir = math.normalizesafe(toParticleXZ);
                        float dotProduct = math.dot(dir, emitDirection);
                        if (!math.isfinite(dotProduct)) continue;

                        if (dotProduct > bestDot)
                        {
                            bestDot = dotProduct;
                            bestIndex = i;
                        }
                    }

                    if (bestIndex < 0)
                        break;

                    _velocityBuffer[bestIndex] = emitDirection * emitSpeed;

                    var p = _particles[bestIndex];
                    p.BodyState = 1;
                    p.ID = 0;
                    _particles[bestIndex] = p;

                    emitted++;
                }
            }
            
            // 强制更新体积统计
            if (VolumeManager.Instance != null && emitted > 0)
            {
                VolumeManager.Instance.UpdateVolume(_particles, true);
            }
            
            Debug.Log($"[Emit] 发射了 {emitted} 个粒子 (候选表面粒子: {candidateCount})");
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
                    var pos = slime.Center;
                    int controllerID = -1;
                    float minDst = float.MaxValue;
                    for (int j = 0; j < _controllerBuffer.Length; j++)
                    {
                        if (used[j]) continue;
                        var cl = _controllerBuffer[j];
                        var center = cl.Center;
                        float dst = math.lengthsq(center - pos);
                        if (dst < minDst)
                        {
                            minDst = dst;
                            controllerID = j;
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
                    float3 dir = math.normalizesafe(
                        math.lengthsq(controller.Velocity) < 1e-3f
                            ? (float3)trans.position - controller.Center
                            : controller.Velocity,
                        new float3(1, 0, 0));
                    new Effects.RayInsectJob
                    {
                        GridLut = _gridLut,
                        Grid = _gridBuffer,
                        Result = _boundsBuffer,
                        Threshold = threshold,
                        Pos = controller.Center,
                        Dir = dir,
                        MinPos = minPos,
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
            
            float rearrangeMs = (Time.realtimeSinceStartup - rearrangeStart) * 1000f;
            if (componentDebug && (Time.frameCount % 60 == 0 || rearrangeMs > 5f))
            {
                Debug.Log($"[RearrangeInstances] 耗时={rearrangeMs:F2}ms, RayInsectJob次数={rayInsectCallCount}, controllerBuffer.Length={_controllerBuffer.Length}, activeInstances={_slimeInstances.Length - _instancePool.Count}");
            }
        }

        private void UpdateInstanceController(int instanceID, int controllerID)
        {
            var slime = _slimeInstances[instanceID];
            var controller = _controllerBuffer[controllerID];
            
            if (instanceID == _controlledInstance)
                controller.Velocity = _velocity * PBF_Utils.InvScale;

            slime.ControllerID = controllerID;
            float speed = 0.1f;
            slime.Radius = math.lerp(slime.Radius, controller.Radius, speed);
            slime.Center = math.lerp(slime.Center, controller.Center, speed);
            Vector3 vec = controller.Velocity;
            if (vec.sqrMagnitude > 1e-4f)
            {
                var newDir = Vector3.Slerp(slime.Dir, vec.normalized, speed);
                newDir.y = math.clamp(newDir.y, -0.2f, 0.5f);
                slime.Dir = newDir.normalized;
            }
            else
                slime.Dir = Vector3.Slerp(slime.Dir, new Vector3(slime.Dir.x, 0, slime.Dir.z), speed);
            
            new Effects.RayInsectJob
            {
                GridLut = _gridLut,
                Grid = _gridBuffer,
                Result = _boundsBuffer,
                Threshold = threshold,
                Pos = controller.Center,
                Dir = slime.Dir,
                MinPos = minPos,
            }.Schedule().Complete();
            
            float3 newPos = _boundsBuffer[0];
            if (math.all(math.isfinite(newPos)))
                slime.Pos = Vector3.Lerp(slime.Pos + vec * deltaTime, newPos, 0.1f);
            else
                slime.Pos = controller.Center;
            
            _slimeInstances[instanceID] = slime;
            
            if (instanceID == _controlledInstance)
            {
                controller.Center = trans.position * PBF_Utils.InvScale;
                _controllerBuffer[controllerID] = controller;
            }
        }

        private void Bubbles()
        {
            var handle = new Effects.GenerateBubblesJobs()
            {
                GridLut = _gridLut,
                Keys = _blockBuffer,
                Grid = _gridBuffer,
                BubblesStack = _bubblesPoolBuffer,
                BubblesBuffer = _bubblesBuffer,
                Speed = 0.01f * bubbleSpeed,
                Threshold = threshold * 1.2f,
                BlockCount = blockNum,
                MinPos = minPos,
                Seed = (uint)Time.frameCount,
            }.Schedule();

            handle = new Effects.BubblesViscosityJob()
            {
                Lut = _lut,
                Particles = _particles,
                VelocityR = _velocityBuffer,
                BubblesBuffer = _bubblesBuffer,
                Controllers = _controllerBuffer,
                ViscosityStrength = viscosityStrength / 50,
            }.Schedule(_bubblesBuffer.Length, batchCount, handle);

            handle = new Effects.UpdateBubblesJob()
            {
                GridLut = _gridLut,
                Grid = _gridBuffer,
                BubblesStack = _bubblesPoolBuffer,
                BubblesBuffer = _bubblesBuffer,
                Threshold = threshold * 1.2f,
                MinPos = minPos,
                DeltaTime = deltaTime,
            }.Schedule(handle);
            
            handle.Complete();
        }

        void HandleMouseInteraction()
        {
            // 优先使用 TopDownController3D（TopDownEngine 集成）
            if (velocityController != null)
            {
                // 只使用 XZ 速度，忽略 Y（TopDownController 的 Y 包含重力，会与 PBF 重力叠加）
                var v = velocityController.Velocity;
                _velocity = new Vector3(v.x, 0, v.z);
            }
            // 回退到 Rigidbody（兼容测试场景）
            else if (trans != null)
            {
                var rb = trans.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    _velocity = rb.linearVelocity;
                }
            }
            else
            {
                _velocity = Vector3.zero;
            }
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
                return;
            
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(_bounds.center, _bounds.size);
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

            if (componentDebug)
            {
                // 显示分离/吸收范围圈（以trans为中心）
                if (trans != null)
                {
                    Vector3 transPos = trans.position;
                    
                    // 青色：核心范围（mainRadius基于此计算，超出即分离）
                    Gizmos.color = new Color(0f, 1f, 1f, 0.8f);
                    Gizmos.DrawWireSphere(transPos, coreRadius * PBF_Utils.Scale);
                    
                    // 粉色：主体控制器范围（mainRadius，用于分离判断）
                    if (_controllerBuffer.IsCreated && _controllerBuffer.Length > 0)
                    {
                        Gizmos.color = new Color(1f, 0.4f, 0.7f, 0.8f);
                        Gizmos.DrawWireSphere(transPos, _controllerBuffer[0].Radius * PBF_Utils.Scale);
                        
                        // 橙色：mergeRadius（分离粒子进入此范围会被合并回主体）
                        Gizmos.color = new Color(1f, 0.6f, 0f, 0.8f);
                        Gizmos.DrawWireSphere(transPos, _controllerBuffer[0].Radius * mainOverlapThreshold * PBF_Utils.Scale);
                    }
                }
                
                Gizmos.color = Color.green;
                for (var i = 0; i < _componentsBuffer.Length; i++)
                {
                    var c = _componentsBuffer[i];
                    var size = (c.BoundsMax - c.BoundsMin) * PBF_Utils.Scale * PBF_Utils.CellSize;
                    var center = c.Center * PBF_Utils.Scale * PBF_Utils.CellSize;
                    Gizmos.DrawWireCube(_bounds.min + (Vector3)center, size);
                }
                
                for (var i = 0; i < _slimeInstances.Length; i++)
                {
                    var slime = _slimeInstances[i];
                    if (!slime.Active) continue;
                    Gizmos.DrawWireSphere(slime.Center * PBF_Utils.Scale, slime.Radius * PBF_Utils.Scale);
                    UnityEditor.Handles.Label(slime.Center * PBF_Utils.Scale, $"id:{i}");
                    if (_connect)
                        Gizmos.DrawLine(slime.Center * PBF_Utils.Scale + new float3(0, 0.1f, 0), trans.position + new Vector3(0, 0.1f, 0));
                }

                // 青色：有效碰撞体（近场缓存）
                Gizmos.color = Color.cyan;
                for (var i = 0; i < _currentColliderCount; i++)
                {
                    var c = _colliderBuffer[i];
                    Gizmos.DrawWireCube(c.Center * PBF_Utils.Scale, c.Extent * PBF_Utils.Scale * 2);
                }
                
                // 绿色：近场查询范围
                if (trans != null)
                {
                    Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
                    Gizmos.DrawWireSphere(trans.position, colliderQueryRadius);
                }
            }
        }
        
        #region 碰撞体采集（近场缓存）
        
        /// <summary>
        /// 刷新近场碰撞体缓存：使用 Physics.OverlapSphereNonAlloc 查询史莱姆附近的碰撞体
        /// </summary>
        private void RefreshNearbyColliders()
        {
            // 获取史莱姆中心位置（世界坐标）
            Vector3 slimeCenter = trans != null ? trans.position : Vector3.zero;
            
            // 查询附近的碰撞体
            int foundCount = Physics.OverlapSphereNonAlloc(
                slimeCenter, 
                colliderQueryRadius, 
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
    }
}
