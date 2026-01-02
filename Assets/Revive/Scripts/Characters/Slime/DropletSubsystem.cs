using Revive.Mathematics;
using System.Diagnostics;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using Unity.Jobs;
using UnityEngine; 

namespace Revive.Slime
{
    /// <summary>
    /// 场景水珠独立子系统 - 固定分区管理
    /// 模拟层：水珠在主数组[8192-16383]区间，不参与主体排序
    /// 渲染层：通过 CopyToRenderBuffer 连续打包到主体后面
    /// </summary>
    public struct DropletSubsystem
    {
        public struct InteractionSettings
        {
            public float DynamicDragStrength;
            public float DynamicDragRadius;
            public int LiquidSolverIterations;
            public float LiquidGravityY;
            public float DropletVerticalOffset;
        }

        // 固定分区边界（仅用于模拟层内存管理）
        public const int DROPLET_START = 8192;
        public const int DROPLET_CAPACITY = 8192;
        public const int DROPLET_END = DROPLET_START + DROPLET_CAPACITY - 1;

        /// <summary>
        /// 水珠初始生成间距（模拟坐标）。
        /// 调大：初始更稀疏、更稳定，但视觉/体积可能变薄。
        /// </summary>
        public const float DropletInitSpacingSim = 0.8f;

        /// <summary>
        /// 水珠保护期帧数（期间不会被主体吸收）。
        /// </summary>
        public const int DropletFreeFrames = 60;
        
        private NativeSlice<Particle> particles;
        private NativeSlice<float3> velocities;
        private int activeCount;
        private int maxSources;
        
        // PBF 物理系统（封装了所有缓冲区）
        private PBFSystem pbfSystem;
        
        // 凝聚力 Job 临时缓冲区
        private NativeArray<float3> _targetCenters;
        private NativeArray<float3> _positions;

        private NativeArray<int> _sourceCounts;
        private NativeArray<int> _sourceOffsets;
        private NativeArray<int> _sourceFill;
        private NativeArray<int> _sourceParticleIndices;
        private NativeArray<float3> _sourceBoundsMin;
        private NativeArray<float3> _sourceBoundsMax;

        private NativeArray<int> _sourceColliderCounts;
        private NativeArray<int> _sourceColliderOffsets;
        private NativeArray<int> _sourceColliderFill;
        private NativeArray<int> _sourceColliderIndices;
        private int _sourceColliderIndicesCapacity;

        private NativeList<int> _activeSourceIds;

        private NativeArray<byte> _sourceActiveFlags;
        private NativeArray<byte> _sourceLiquidFlags;

        private int _dbgPerfLogFrame;

        private NativeArray<Particle> _pbfParticlesTemp;
        private NativeArray<float3> _pbfVelocitiesTemp;
        private NativeArray<int> _pbfIndexMapTemp;
        
        // 每个源的信息
        private struct SourceInfo
        {
            public int startIndex;
            public int count;
            public float3 center;
            public float groundY; // 该源的地面Y（内部坐标）
            public bool active;

            public bool liquidMode;
            public bool lockLiquidMode;
            
            // 物理参数（从 DropWater 读取）
            public float cohesionStrength;
            public float cohesionRadius;
            public float velocityDamping;
            public float verticalCohesionScale;

            public bool enableViscosity;
            public float viscosityStrength;
        }
        private NativeArray<SourceInfo> sources;
        
        private InteractionSettings _interactionSettings;
        
        public int ActiveCount => activeCount;
        
        public void UpdateInteractionSettings(InteractionSettings settings)
        {
            _interactionSettings = settings;
        }

        public void UpdateSourcePhysicsParams(int sourceId, float cohesionStrength, float cohesionRadius,
            float velocityDamping, float verticalCohesionScale, bool enableViscosity, float viscosityStrength)
        {
            if (sourceId < 0 || sourceId >= maxSources) return;
            if (!sources.IsCreated) return;
            var info = sources[sourceId];
            if (!info.active) return;

            info.cohesionStrength = cohesionStrength;
            info.cohesionRadius = cohesionRadius;
            info.velocityDamping = velocityDamping;
            info.verticalCohesionScale = verticalCohesionScale;
            info.enableViscosity = enableViscosity;
            info.viscosityStrength = viscosityStrength;
            sources[sourceId] = info;
        }

         public void UpdateSourceLiquidMode(int sourceId, bool liquidMode)
         {
             if (sourceId < 0 || sourceId >= maxSources) return;
             if (!sources.IsCreated) return;
             var info = sources[sourceId];
             if (!info.active) return;

             if (info.lockLiquidMode)
                 return;

             info.liquidMode = liquidMode;
             sources[sourceId] = info;

             if (_sourceLiquidFlags.IsCreated && sourceId >= 0 && sourceId < _sourceLiquidFlags.Length)
                 _sourceLiquidFlags[sourceId] = (byte)(liquidMode ? 1 : 0);
         }

        public void ForceSourceLiquidMode(int sourceId)
        {
            if (sourceId < 0 || sourceId >= maxSources) return;
            if (!sources.IsCreated) return;
            var info = sources[sourceId];
            if (!info.active) return;

            info.liquidMode = true;
            info.lockLiquidMode = true;
            sources[sourceId] = info;

            if (_sourceLiquidFlags.IsCreated && sourceId >= 0 && sourceId < _sourceLiquidFlags.Length)
                _sourceLiquidFlags[sourceId] = 1;
        }
        
        /// <summary>
        /// 初始化子系统，绑定到主粒子数组的固定分区
        /// </summary>
        public void Initialize(NativeArray<Particle> mainParticles, NativeArray<float3> mainVelocities, int maxSourceCount)
        {
            // 创建指向固定分区的切片
            particles = new NativeSlice<Particle>(mainParticles, DROPLET_START, DROPLET_CAPACITY);
            velocities = new NativeSlice<float3>(mainVelocities, DROPLET_START, DROPLET_CAPACITY);
            
            activeCount = 0;
            maxSources = maxSourceCount;
            
            _interactionSettings = new InteractionSettings
            {
                DynamicDragStrength = 35f,
                DynamicDragRadius = 15f,
                LiquidSolverIterations = 3,
                LiquidGravityY = -10f
            };
            sources = new NativeArray<SourceInfo>(maxSourceCount, Allocator.Persistent);
            
            // 初始化 PBF 物理系统（使用水珠配置）
            pbfSystem = new PBFSystem(PBFSystem.Config.Droplet);
            
            // 初始化凝聚力 Job 临时缓冲区
            _targetCenters = new NativeArray<float3>(DROPLET_CAPACITY, Allocator.Persistent);
            _positions = new NativeArray<float3>(DROPLET_CAPACITY, Allocator.Persistent);

            _sourceCounts = new NativeArray<int>(maxSourceCount, Allocator.Persistent);
            _sourceOffsets = new NativeArray<int>(maxSourceCount, Allocator.Persistent);
            _sourceFill = new NativeArray<int>(maxSourceCount, Allocator.Persistent);
            _sourceParticleIndices = new NativeArray<int>(DROPLET_CAPACITY, Allocator.Persistent);
            _sourceBoundsMin = new NativeArray<float3>(maxSourceCount, Allocator.Persistent);
            _sourceBoundsMax = new NativeArray<float3>(maxSourceCount, Allocator.Persistent);

            _sourceColliderCounts = new NativeArray<int>(maxSourceCount, Allocator.Persistent);
            _sourceColliderOffsets = new NativeArray<int>(maxSourceCount, Allocator.Persistent);
            _sourceColliderFill = new NativeArray<int>(maxSourceCount, Allocator.Persistent);
            _sourceColliderIndicesCapacity = math.max(1, maxSourceCount * 1024);
            _sourceColliderIndices = new NativeArray<int>(_sourceColliderIndicesCapacity, Allocator.Persistent);

            _activeSourceIds = new NativeList<int>(maxSourceCount, Allocator.Persistent);

            _sourceActiveFlags = new NativeArray<byte>(maxSourceCount, Allocator.Persistent);
            _sourceLiquidFlags = new NativeArray<byte>(maxSourceCount, Allocator.Persistent);
            for (int i = 0; i < maxSourceCount; i++)
            {
                _sourceActiveFlags[i] = 0;
                _sourceLiquidFlags[i] = 0;
            }

            _pbfParticlesTemp = new NativeArray<Particle>(DROPLET_CAPACITY, Allocator.Persistent);
            _pbfVelocitiesTemp = new NativeArray<float3>(DROPLET_CAPACITY, Allocator.Persistent);
            _pbfIndexMapTemp = new NativeArray<int>(DROPLET_CAPACITY, Allocator.Persistent);
            
            // 初始化分区为休眠状态
            for (int i = 0; i < DROPLET_CAPACITY; i++)
            {
                particles[i] = new Particle
                {
                    Type = ParticleType.Dormant,
                    Position = new float3(0, -1000, 0),
                    SourceId = -1,
                    ControllerSlot = 0,
                    FreeFrames = 0,
                    ClusterId = 0,
                    BlobId = 0,
                    FramesOutsideMain = 0
                };
                velocities[i] = float3.zero;
            }
        }
        
        public void Dispose()
        {
            if (sources.IsCreated) sources.Dispose();
            if (_targetCenters.IsCreated) _targetCenters.Dispose();
            if (_positions.IsCreated) _positions.Dispose();
            if (_sourceCounts.IsCreated) _sourceCounts.Dispose();
            if (_sourceOffsets.IsCreated) _sourceOffsets.Dispose();
            if (_sourceFill.IsCreated) _sourceFill.Dispose();
            if (_sourceParticleIndices.IsCreated) _sourceParticleIndices.Dispose();
            if (_sourceBoundsMin.IsCreated) _sourceBoundsMin.Dispose();
            if (_sourceBoundsMax.IsCreated) _sourceBoundsMax.Dispose();
            if (_sourceColliderCounts.IsCreated) _sourceColliderCounts.Dispose();
            if (_sourceColliderOffsets.IsCreated) _sourceColliderOffsets.Dispose();
            if (_sourceColliderFill.IsCreated) _sourceColliderFill.Dispose();
            if (_sourceColliderIndices.IsCreated) _sourceColliderIndices.Dispose();
            if (_pbfParticlesTemp.IsCreated) _pbfParticlesTemp.Dispose();
            if (_pbfVelocitiesTemp.IsCreated) _pbfVelocitiesTemp.Dispose();
            if (_pbfIndexMapTemp.IsCreated) _pbfIndexMapTemp.Dispose();
            if (_activeSourceIds.IsCreated) _activeSourceIds.Dispose();
            if (_sourceActiveFlags.IsCreated) _sourceActiveFlags.Dispose();
            if (_sourceLiquidFlags.IsCreated) _sourceLiquidFlags.Dispose();
            pbfSystem?.Dispose();
        }

        public bool TryGetActiveBounds(out float3 min, out float3 max)
        {
            if (activeCount <= 0)
            {
                min = float3.zero;
                max = float3.zero;
                return false;
            }

            min = new float3(float.MaxValue);
            max = new float3(float.MinValue);
            for (int i = 0; i < activeCount; i++)
            {
                float3 p = particles[i].Position;
                min = math.min(min, p);
                max = math.max(max, p);
            }

            return true;
        }
        
        public bool TryGetSourceBounds(int sourceId, out float3 min, out float3 max)
        {
            if (sourceId < 0 || sourceId >= maxSources || !sources.IsCreated)
            {
                min = float3.zero;
                max = float3.zero;
                return false;
            }
            if (!sources[sourceId].active)
            {
                min = float3.zero;
                max = float3.zero;
                return false;
            }
            if (!_sourceCounts.IsCreated || !_sourceBoundsMin.IsCreated || !_sourceBoundsMax.IsCreated)
            {
                min = float3.zero;
                max = float3.zero;
                return false;
            }
            if (_sourceCounts[sourceId] <= 0)
            {
                min = float3.zero;
                max = float3.zero;
                return false;
            }
            min = _sourceBoundsMin[sourceId];
            max = _sourceBoundsMax[sourceId];
            return true;
        }
        
        public bool TryGetSourceIndexRange(int sourceId, out int baseOffset, out int count)
        {
            baseOffset = 0;
            count = 0;
            if (sourceId < 0 || sourceId >= maxSources || !sources.IsCreated)
                return false;
            if (!sources[sourceId].active)
                return false;
            if (!_sourceCounts.IsCreated || !_sourceOffsets.IsCreated || !_sourceParticleIndices.IsCreated)
                return false;
            count = _sourceCounts[sourceId];
            if (count <= 0)
                return false;
            baseOffset = _sourceOffsets[sourceId];
            return true;
        }
        
        public int GetSourceParticleLocalIndex(int packedIndex)
        {
            return _sourceParticleIndices[packedIndex];
        }
        
        /// <summary>
        /// 激活水珠源（在固定分区中分配）
        /// </summary>
        /// <param name="sourceGroundY">源位置的地面高度（模拟坐标）</param>
        public int ActivateSource(int sourceId, float3 sourcePosition, int requestedCount,
            float cohesionStrength = 30f, float cohesionRadius = 10f,
            float velocityDamping = 0.99f, float verticalCohesionScale = 0.5f,
            bool enableViscosity = true, float viscosityStrength = 10f,
            bool startAsLiquid = false,
            float sourceGroundY = 0f)
        {
            if (sourceId < 0 || sourceId >= maxSources)
            {
                UnityEngine.Debug.LogError($"[DropletSubsystem] 无效的sourceId: {sourceId}, maxSources={maxSources}");
                return 0;
            }
            
            // 检查是否已激活
            if (sources[sourceId].active)
            {
                return 0;
            }
            
            // 计算可分配数量
            int availableSpace = DROPLET_CAPACITY - activeCount;
            int allocatedCount = math.min(requestedCount, availableSpace);
            
            if (allocatedCount == 0)
            {
                return 0;
            }
            
            // 在固定分区内分配
            int startIdx = activeCount;
            
            // 生成水珠粒子
            // 间距需要匹配 PBF 目标密度（1.5），避免第一帧因密度不匹配而爆炸
            // 经验值：h/2 ≈ 0.5 太密会被推开，h*0.8 ≈ 0.8 更稳定
            int gridSize = Mathf.CeilToInt(Mathf.Pow(allocatedCount, 1f/3f));
            float spacingInternal = DropletInitSpacingSim;
            
            for (int i = 0; i < allocatedCount; i++)
            {
                int idx = startIdx + i;
                int gx = i % gridSize;
                int gy = (i / gridSize) % gridSize;  
                int gz = i / (gridSize * gridSize);
                
                // offset 直接使用内部坐标，Y从0开始向上（避免在地面以下）
                float3 offset = new float3(
                    (gx - gridSize/2) * spacingInternal,
                    gy * spacingInternal,  // Y从0开始，不居中
                    (gz - gridSize/2) * spacingInternal
                );
                
                particles[idx] = new Particle
                {
                    Type = ParticleType.SceneDroplet,
                    // sourcePosition 和 offset 都是内部坐标
                    Position = sourcePosition + offset,
                    SourceId = sourceId,
                    ControllerSlot = -1,  // -1 让 shader 使用 _Color 而不是 colors 数组
                    FreeFrames = DropletFreeFrames,
                    ClusterId = 0,
                    BlobId = 0,
                    FramesOutsideMain = 0
                };
                velocities[idx] = float3.zero;
            }
            
            // 更新源信息
            sources[sourceId] = new SourceInfo
            {
                startIndex = startIdx,
                count = allocatedCount,
                center = sourcePosition,
                groundY = sourceGroundY, // 使用传入的地面高度
                active = true,
                liquidMode = startAsLiquid,
                lockLiquidMode = startAsLiquid,
                cohesionStrength = cohesionStrength,
                cohesionRadius = cohesionRadius,
                velocityDamping = velocityDamping,
                verticalCohesionScale = verticalCohesionScale,
                enableViscosity = enableViscosity,
                viscosityStrength = viscosityStrength
            };

            if (_sourceActiveFlags.IsCreated && sourceId >= 0 && sourceId < _sourceActiveFlags.Length)
                _sourceActiveFlags[sourceId] = 1;
            if (_sourceLiquidFlags.IsCreated && sourceId >= 0 && sourceId < _sourceLiquidFlags.Length)
                _sourceLiquidFlags[sourceId] = (byte)(startAsLiquid ? 1 : 0);

            if (_activeSourceIds.IsCreated)
            {
                bool exists = false;
                for (int i = 0; i < _activeSourceIds.Length; i++)
                {
                    if (_activeSourceIds[i] == sourceId)
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                    _activeSourceIds.Add(sourceId);
            }
            
            activeCount += allocatedCount;
            
            return allocatedCount;
        }
        
        /// <summary>
        /// 休眠水珠源
        /// </summary>
        public int DeactivateSource(int sourceId)
        {
            if (sourceId < 0 || sourceId >= maxSources || !sources[sourceId].active)
                return 0;
                
            var info = sources[sourceId];
            int deactivatedCount = 0;
            
            // 将该源的水珠标记为休眠
            for (int i = info.startIndex; i < info.startIndex + info.count; i++)
            {
                particles[i] = new Particle
                {
                    Type = ParticleType.Dormant,
                    Position = new float3(0, -1000, 0),
                    SourceId = -1,
                    ControllerSlot = 0,
                    FreeFrames = 0,
                    ClusterId = 0,
                    BlobId = 0,
                    FramesOutsideMain = 0
                };
                velocities[i] = float3.zero;
                deactivatedCount++;
            }
            
            // 压缩活跃区域（将后面的水珠移到前面）
            if (info.startIndex + info.count < activeCount)
            {
                int moveCount = activeCount - (info.startIndex + info.count);
                for (int i = 0; i < moveCount; i++)
                {
                    int fromIdx = info.startIndex + info.count + i;
                    int toIdx = info.startIndex + i;
                    particles[toIdx] = particles[fromIdx];
                    velocities[toIdx] = velocities[fromIdx];
                    
                    // 更新被移动粒子的源信息
                    int movedSourceId = particles[toIdx].SourceId;
                    if (movedSourceId >= 0 && movedSourceId < maxSources && sources[movedSourceId].active)
                    {
                        if (sources[movedSourceId].startIndex == fromIdx - sources[movedSourceId].count + 1)
                        {
                            var movedInfo = sources[movedSourceId];
                            movedInfo.startIndex = toIdx - movedInfo.count + 1;
                            sources[movedSourceId] = movedInfo;
                        }
                    }
                }
            }
            
            // 清除源信息
            sources[sourceId] = new SourceInfo { active = false };

            if (_sourceActiveFlags.IsCreated && sourceId >= 0 && sourceId < _sourceActiveFlags.Length)
                _sourceActiveFlags[sourceId] = 0;
            if (_sourceLiquidFlags.IsCreated && sourceId >= 0 && sourceId < _sourceLiquidFlags.Length)
                _sourceLiquidFlags[sourceId] = 0;

            if (_activeSourceIds.IsCreated)
            {
                for (int i = 0; i < _activeSourceIds.Length; i++)
                {
                    if (_activeSourceIds[i] == sourceId)
                    {
                        _activeSourceIds.RemoveAtSwapBack(i);
                        break;
                    }
                }
            }
            activeCount -= deactivatedCount;
            
            return deactivatedCount;
        }
        
        /// <summary>
        /// 迁移粒子到主体分区（用于流体融合效果）
        /// 从水珠分区移除粒子，返回位置和速度供主体分区使用
        /// </summary>
        /// <param name="globalIndex">粒子的全局索引（8192+localIndex）</param>
        /// <param name="position">输出：粒子位置</param>
        /// <param name="velocity">输出：粒子速度</param>
        /// <returns>成功返回 true</returns>
        public bool MigrateToMainBody(int globalIndex, out float3 position, out float3 velocity)
        {
            int localIndex = globalIndex - DROPLET_START;
            
            // 检查索引有效性
            if (localIndex < 0 || localIndex >= DROPLET_CAPACITY)
            {
                position = float3.zero;
                velocity = float3.zero;
                return false;
            }
            
            // 检查粒子类型
            if (particles[localIndex].Type != ParticleType.SceneDroplet)
            {
                position = float3.zero;
                velocity = float3.zero;
                return false;
            }
            
            // 获取粒子数据
            position = particles[localIndex].Position;
            velocity = velocities[localIndex];
            
            // 优化：直接使用 activeCount-1 作为最后一个活跃粒子索引 O(1)
            int lastActiveIdx = activeCount - 1;
            
            if (lastActiveIdx >= 0 && lastActiveIdx != localIndex)
            {
                // swap：把最后一个活跃粒子移到当前位置
                particles[localIndex] = particles[lastActiveIdx];
                velocities[localIndex] = velocities[lastActiveIdx];
            }
            
            // 标记被移除的位置为休眠
            int clearIdx = (lastActiveIdx >= 0 && lastActiveIdx != localIndex) ? lastActiveIdx : localIndex;
            particles[clearIdx] = new Particle
            {
                Type = ParticleType.Dormant,
                Position = new float3(0, -1000, 0),
                SourceId = -1,
                ControllerSlot = 0,
                FreeFrames = 0,
                ClusterId = 0,
                BlobId = 0,
                FramesOutsideMain = 0
            };
            velocities[clearIdx] = float3.zero;
            
            // 减少活跃计数
            if (activeCount > 0)
                activeCount--;
            
            return true;
        }
        
        /// <summary>
        /// 执行水珠物理模拟（完整 PBF 物理 + 碰撞检测）
        /// 【改进】现在使用每个源独立的地面高度，而不是统一的 groundY
        /// </summary>
        public void Simulate(float deltaTime, NativeArray<Particle> mainParticles, NativeArray<float3> mainVelocities, 
            float fallbackGroundY, float targetDensity = 1.0f, NativeArray<MyBoxCollider> colliders = default, int colliderCount = 0,
            int useStaticSdf = 0, WorldSdfRuntime.Volume staticSdf = default, float particleRadiusSim = 0.5f, float staticFriction = 0.2f,
            int disableStaticColliderFallback = 0)
        {
            if (activeCount == 0)
                return;
            
            // 内部坐标重力：与主体史莱姆一致（默认 -5f，不需要乘以 InvScale）
            const float defaultGravityY = -5f;

            double invFreqMs = 1000.0 / Stopwatch.Frequency;
            long tStageStart;
            long ticksSourceClear = 0;
            long ticksSourceCount = 0;
            long ticksSourceOffsets = 0;
            long ticksSourceBuildIndices = 0;
            long ticksSourceCentroid = 0;
            long ticksSourceCohesion = 0;
            long ticksSourceCopyToTemp = 0;
            long ticksSourcePbfStep = 0;
            long ticksSourceWriteBack = 0;
            PBFSystem.StepTimings pbfStepTimings = default;
            long ticksLiquidCopyToTemp = 0;
            long ticksLiquidPbfStep = 0;
            long ticksLiquidWriteBack = 0;
            long ticksCollLoopTotal = 0;
            long ticksCollSdf = 0;
            long ticksCollSdfDistance = 0;
            long ticksCollSdfNormal = 0;
            long ticksCollBuildSourceCandidates = 0;
            long ticksCollColliderTotal = 0;
            long ticksCollDynamicDrag = 0;
            long ticksCollResolve = 0;
            long ticksCollFriction = 0;
            long ticksCollGroundClamp = 0;
            long ticksCollWriteBack = 0;

            int dbgSdfDistanceSamples = 0;
            int dbgSdfPenetrations = 0;
            int dbgSdfNormalSamples = 0;
            int dbgParticlesUsedCandidates = 0;
            int dbgParticlesCandidateEmpty = 0;
            int dbgParticlesFullScan = 0;
            int dbgColliderChecks = 0;
            int dbgCandidateColliderChecks = 0;
            int dbgFullScanColliderChecks = 0;
            bool dbgCandidatesOverflow = false;
            int dbgCandidatesTotal = 0;
            
            tStageStart = Stopwatch.GetTimestamp();
            int activeSourceCount = (_activeSourceIds.IsCreated ? _activeSourceIds.Length : 0);
            for (int si = 0; si < activeSourceCount; si++)
            {
                int s = _activeSourceIds[si];
                _sourceCounts[s] = 0;
                _sourceOffsets[s] = 0;
                _sourceFill[s] = 0;
                _sourceBoundsMin[s] = new float3(float.MaxValue);
                _sourceBoundsMax[s] = new float3(float.MinValue);

                _sourceColliderCounts[s] = 0;
                _sourceColliderOffsets[s] = 0;
                _sourceColliderFill[s] = 0;
            }

            ticksSourceClear += Stopwatch.GetTimestamp() - tStageStart;

            tStageStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < activeCount; i++)
            {
                int sid = particles[i].SourceId;
                if (sid >= 0 && sid < maxSources && _sourceActiveFlags.IsCreated && _sourceActiveFlags[sid] != 0)
                {
                    _sourceCounts[sid]++;
                    float3 p = particles[i].Position;
                    _sourceBoundsMin[sid] = math.min(_sourceBoundsMin[sid], p);
                    _sourceBoundsMax[sid] = math.max(_sourceBoundsMax[sid], p);
                }
            }

            ticksSourceCount += Stopwatch.GetTimestamp() - tStageStart;

            tStageStart = Stopwatch.GetTimestamp();
            int running = 0;
            for (int si = 0; si < activeSourceCount; si++)
            {
                int s = _activeSourceIds[si];
                _sourceOffsets[s] = running;
                running += _sourceCounts[s];
            }

            ticksSourceOffsets += Stopwatch.GetTimestamp() - tStageStart;

            tStageStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < activeCount; i++)
            {
                int sid = particles[i].SourceId;
                if (sid >= 0 && sid < maxSources && _sourceActiveFlags.IsCreated && _sourceActiveFlags[sid] != 0)
                {
                    int write = _sourceOffsets[sid] + _sourceFill[sid];
                    _sourceParticleIndices[write] = i;
                    _sourceFill[sid]++;
                }
            }

            ticksSourceBuildIndices += Stopwatch.GetTimestamp() - tStageStart;

            bool canUseSourceColliderCandidates = colliders.IsCreated && colliderCount > 0 &&
                                                 _sourceColliderCounts.IsCreated && _sourceColliderOffsets.IsCreated &&
                                                 _sourceColliderFill.IsCreated && _sourceColliderIndices.IsCreated;

            if (canUseSourceColliderCandidates)
            {
                long tBuildCand0 = Stopwatch.GetTimestamp();

                float expand = math.max(0.0001f, particleRadiusSim);

                for (int si = 0; si < activeSourceCount; si++)
                {
                    int sid = _activeSourceIds[si];
                    if (_sourceActiveFlags.IsCreated && _sourceActiveFlags[sid] == 0) continue;
                    if (_sourceCounts[sid] <= 0) continue;

                    float3 sMin = _sourceBoundsMin[sid] - expand;
                    float3 sMax = _sourceBoundsMax[sid] + expand;

                    int count = 0;
                    for (int c = 0; c < colliderCount; c++)
                    {
                        MyBoxCollider box = colliders[c];

                        float3 bMin;
                        float3 bMax;

                        if (box.Shape == ColliderShapes.Capsule)
                        {
                            ColliderShapeUtils.ComputeCapsule(in box, out float3 a, out float3 b, out float r);
                            float3 rr = new float3(r);
                            bMin = math.min(a, b) - rr;
                            bMax = math.max(a, b) + rr;
                        }
                        else
                        {
                            float3 e = box.Extent;
                            if (box.Shape == ColliderShapes.Obb)
                            {
                                float3x3 rot = new float3x3(box.Rotation);
                                float3x3 absRot = new float3x3(math.abs(rot.c0), math.abs(rot.c1), math.abs(rot.c2));
                                e = absRot.c0 * e.x + absRot.c1 * e.y + absRot.c2 * e.z;
                            }

                            bMin = box.Center - e;
                            bMax = box.Center + e;
                        }

                        bool overlap = (sMin.x <= bMax.x && sMax.x >= bMin.x) &&
                                       (sMin.y <= bMax.y && sMax.y >= bMin.y) &&
                                       (sMin.z <= bMax.z && sMax.z >= bMin.z);
                        if (overlap)
                        {
                            count++;
                        }
                    }

                    _sourceColliderCounts[sid] = count;
                }

                int runningColliders = 0;
                bool overflow = false;
                for (int si = 0; si < activeSourceCount; si++)
                {
                    int sid = _activeSourceIds[si];
                    _sourceColliderOffsets[sid] = runningColliders;
                    runningColliders += _sourceColliderCounts[sid];
                    if (runningColliders > _sourceColliderIndicesCapacity)
                    {
                        overflow = true;
                        break;
                    }
                }

                if (overflow)
                {
                    canUseSourceColliderCandidates = false;
                    dbgCandidatesOverflow = true;
                }
                else
                {
                    dbgCandidatesTotal = runningColliders;
                    float expand2 = math.max(0.0001f, particleRadiusSim);

                    for (int si = 0; si < activeSourceCount; si++)
                    {
                        int sid = _activeSourceIds[si];
                        if (_sourceActiveFlags.IsCreated && _sourceActiveFlags[sid] == 0) continue;
                        if (_sourceColliderCounts[sid] <= 0) continue;

                        float3 sMin = _sourceBoundsMin[sid] - expand2;
                        float3 sMax = _sourceBoundsMax[sid] + expand2;
                        int baseOffset = _sourceColliderOffsets[sid];

                        for (int c = 0; c < colliderCount; c++)
                        {
                            MyBoxCollider box = colliders[c];

                            float3 bMin;
                            float3 bMax;

                            if (box.Shape == ColliderShapes.Capsule)
                            {
                                ColliderShapeUtils.ComputeCapsule(in box, out float3 a, out float3 b, out float r);
                                float3 rr = new float3(r);
                                bMin = math.min(a, b) - rr;
                                bMax = math.max(a, b) + rr;
                            }
                            else
                            {
                                float3 e = box.Extent;
                                if (box.Shape == ColliderShapes.Obb)
                                {
                                    float3x3 rot = new float3x3(box.Rotation);
                                    float3x3 absRot = new float3x3(math.abs(rot.c0), math.abs(rot.c1), math.abs(rot.c2));
                                    e = absRot.c0 * e.x + absRot.c1 * e.y + absRot.c2 * e.z;
                                }

                                bMin = box.Center - e;
                                bMax = box.Center + e;
                            }

                            bool overlap = (sMin.x <= bMax.x && sMax.x >= bMin.x) &&
                                           (sMin.y <= bMax.y && sMax.y >= bMin.y) &&
                                           (sMin.z <= bMax.z && sMax.z >= bMin.z);
                            if (overlap)
                            {
                                int fill = _sourceColliderFill[sid];
                                if (fill < _sourceColliderCounts[sid])
                                {
                                    int write = baseOffset + fill;
                                    _sourceColliderIndices[write] = c;
                                    _sourceColliderFill[sid] = fill + 1;
                                }
                            }
                        }
                    }
                }

                ticksCollBuildSourceCandidates += Stopwatch.GetTimestamp() - tBuildCand0;
            }

            // 液体模式：合并所有 liquidMode 水珠，避免按 source 多次 PBF（hash/sort/solve）
            // SourceId 仍保留用于吸收/Streaming 管理。
            int liquidCountTotal = 0;
            if (_pbfIndexMapTemp.IsCreated)
            {
                tStageStart = Stopwatch.GetTimestamp();
                for (int i = 0; i < activeCount; i++)
                {
                    var p = particles[i];
                    int sid = p.SourceId;
                    bool isLiquid = sid >= 0 && sid < maxSources &&
                                    _sourceActiveFlags.IsCreated && _sourceActiveFlags[sid] != 0 &&
                                    _sourceLiquidFlags.IsCreated && _sourceLiquidFlags[sid] != 0;
                    if (!isLiquid)
                        continue;

                    var info = sources[sid];
                    float3 v = velocities[i];
                    v *= info.velocityDamping;
                    v += new float3(0f, _interactionSettings.LiquidGravityY, 0f) * deltaTime;
                    velocities[i] = v;

                    _pbfParticlesTemp[liquidCountTotal] = p;
                    _pbfVelocitiesTemp[liquidCountTotal] = v;
                    _pbfIndexMapTemp[liquidCountTotal] = i;
                    liquidCountTotal++;
                }

                ticksLiquidCopyToTemp += Stopwatch.GetTimestamp() - tStageStart;
            }

            if (liquidCountTotal > 0)
            {
                var pbfLiquidParticles = new NativeSlice<Particle>(_pbfParticlesTemp, 0, liquidCountTotal);
                var pbfLiquidVelocities = new NativeSlice<float3>(_pbfVelocitiesTemp, 0, liquidCountTotal);

                int solverIterationsOverride = _interactionSettings.LiquidSolverIterations;
                if (solverIterationsOverride <= 0) solverIterationsOverride = 1;

                tStageStart = Stopwatch.GetTimestamp();
                pbfSystem.SimulateStepProfiled(
                    pbfLiquidParticles,
                    pbfLiquidVelocities,
                    liquidCountTotal,
                    deltaTime,
                    float3.zero,
                    enableViscosity: false,
                    viscosityStrength: 0f,
                    solverIterationsOverride: solverIterationsOverride,
                    minCOverride: float.NaN,
                    tensileKOverride: float.NaN,
                    ref pbfStepTimings);
                ticksLiquidPbfStep += Stopwatch.GetTimestamp() - tStageStart;

                tStageStart = Stopwatch.GetTimestamp();
                for (int k = 0; k < liquidCountTotal; k++)
                {
                    int pi = _pbfIndexMapTemp[k];
                    particles[pi] = _pbfParticlesTemp[k];
                    velocities[pi] = _pbfVelocitiesTemp[k];
                }
                ticksLiquidWriteBack += Stopwatch.GetTimestamp() - tStageStart;
            }

            for (int si = 0; si < activeSourceCount; si++)
            {
                int sid = _activeSourceIds[si];
                if (_sourceActiveFlags.IsCreated && _sourceActiveFlags[sid] == 0) continue;
                int count = _sourceCounts[sid];
                if (count <= 0) continue;

                var info = sources[sid];
                bool sourceLiquid = info.liquidMode;

                if (sourceLiquid)
                    continue;

                float3 centroid = float3.zero;
                int baseOffset = _sourceOffsets[sid];

                tStageStart = Stopwatch.GetTimestamp();
                for (int k = 0; k < count; k++)
                {
                    int pi = _sourceParticleIndices[baseOffset + k];
                    centroid += particles[pi].Position;
                }
                centroid /= count;

                ticksSourceCentroid += Stopwatch.GetTimestamp() - tStageStart;

                float radius = math.max(0.1f, info.cohesionRadius);
                float3 targetCenter = centroid + new float3(0f, radius * _interactionSettings.DropletVerticalOffset, 0f);

                tStageStart = Stopwatch.GetTimestamp();
                for (int k = 0; k < count; k++)
                {
                    int pi = _sourceParticleIndices[baseOffset + k];
                    float3 pos = particles[pi].Position;
                    float3 v = velocities[pi];

                    float gravityY = sourceLiquid ? _interactionSettings.LiquidGravityY : defaultGravityY;
                    float3 gravity = new float3(0, gravityY, 0);

                    v *= info.velocityDamping;
                    v += gravity * deltaTime;

                    float cohesionStrength = sourceLiquid ? 0f : info.cohesionStrength;
                    if (cohesionStrength > 0f)
                    {
                        float3 toCenter = targetCenter - pos;
                        float dist = math.length(toCenter);
                        if (dist > 0.1f && dist < radius)
                        {
                            float3 dir = toCenter / dist;
                            float strength = cohesionStrength * deltaTime * math.min(1f, dist);
                            float3 dv = dir * strength;
                            if (dv.y > 0f)
                            {
                                float upDVMax = math.abs(gravity.y) * deltaTime * 0.5f;
                                dv.y = math.min(dv.y, upDVMax);
                            }
                            v += dv;
                        }
                    }

                    velocities[pi] = v;
                }

                ticksSourceCohesion += Stopwatch.GetTimestamp() - tStageStart;

                tStageStart = Stopwatch.GetTimestamp();
                for (int k = 0; k < count; k++)
                {
                    int pi = _sourceParticleIndices[baseOffset + k];
                    _pbfParticlesTemp[k] = particles[pi];
                    _pbfVelocitiesTemp[k] = velocities[pi];
                }

                ticksSourceCopyToTemp += Stopwatch.GetTimestamp() - tStageStart;

                bool enableViscosity = info.enableViscosity;
                float viscosityStrength = info.viscosityStrength;
                if (sourceLiquid)
                {
                    enableViscosity = false;
                    viscosityStrength = 0f;
                }

                int solverIterationsOverride = -1;
                float minCOverride = float.NaN;
                float tensileKOverride = float.NaN;
                if (sourceLiquid)
                {
                    solverIterationsOverride = _interactionSettings.LiquidSolverIterations;
                    if (solverIterationsOverride <= 0) solverIterationsOverride = 1;
                    if (solverIterationsOverride < 1) solverIterationsOverride = 1;
                    minCOverride = float.NaN;
                    tensileKOverride = float.NaN;
                }

                var pbfParticles = new NativeSlice<Particle>(_pbfParticlesTemp, 0, count);
                var pbfVelocities = new NativeSlice<float3>(_pbfVelocitiesTemp, 0, count);

                tStageStart = Stopwatch.GetTimestamp();
                pbfSystem.SimulateStepProfiled(pbfParticles, pbfVelocities, count, deltaTime, float3.zero, enableViscosity, viscosityStrength, solverIterationsOverride, minCOverride, tensileKOverride, ref pbfStepTimings);

                ticksSourcePbfStep += Stopwatch.GetTimestamp() - tStageStart;

                tStageStart = Stopwatch.GetTimestamp();
                for (int k = 0; k < count; k++)
                {
                    int pi = _sourceParticleIndices[baseOffset + k];
                    particles[pi] = _pbfParticlesTemp[k];
                    float3 vv = _pbfVelocitiesTemp[k];
                    velocities[pi] = vv;
                }

                ticksSourceWriteBack += Stopwatch.GetTimestamp() - tStageStart;
            }

            tStageStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < activeCount; i++)
            {
                long tLoopStart = Stopwatch.GetTimestamp();
                var p = particles[i];
                float3 newPos = p.Position;
                bool hadCollision = false;
                bool hadSdfCollision = false;
                float3 lastNormal = float3.zero;
                float3 lastColliderVelocity = float3.zero;
                float lastFriction = 0f;
                float3 v = velocities[i];

                bool canUseSdf = useStaticSdf != 0 && staticSdf.IsCreated;
                bool sdfInBoundsStrict = false;
                if (canUseSdf)
                {
                    float3 minSim = staticSdf.AabbMinSim;
                    float3 maxSim = staticSdf.AabbMaxSim;
                    sdfInBoundsStrict = !(newPos.x < minSim.x || newPos.y < minSim.y || newPos.z < minSim.z ||
                                        newPos.x > maxSim.x || newPos.y > maxSim.y || newPos.z > maxSim.z);
                }
                bool skipStaticColliders = canUseSdf && disableStaticColliderFallback != 0 && sdfInBoundsStrict;

                float3 sdfNormal = new float3(0, 1, 0);
                if (canUseSdf)
                {
                    long tSdf0 = Stopwatch.GetTimestamp();
                    long tSdfDist0 = Stopwatch.GetTimestamp();
                    dbgSdfDistanceSamples++;
                    float voxel = math.max(staticSdf.VoxelSizeSim, 1e-3f);
                    float contactSkin = math.min(voxel * 0.25f, particleRadiusSim * 0.25f);
                    float contactRadius = particleRadiusSim + contactSkin;
                    float contactSlop = math.min(voxel * 0.05f, particleRadiusSim * 0.1f);

                    float d = staticSdf.SampleDistanceDenseWithLocalAndIndices(newPos, out float3 sdfLocal, out int3 sdfI0, out int3 sdfI1, out float3 sdfF);
                    ticksCollSdfDistance += Stopwatch.GetTimestamp() - tSdfDist0;
                    if (d < contactRadius)
                    {
                        dbgSdfPenetrations++;
                        hadSdfCollision = true;
                        hadCollision = true;
                        lastFriction = math.max(lastFriction, staticFriction);

                        float maxStep = voxel * 1.25f;
                        for (int it = 0; it < 4 && d < contactRadius; it++)
                        {
                            long tSdfN0 = Stopwatch.GetTimestamp();
                            dbgSdfNormalSamples++;
                            float3 n = staticSdf.SampleNormalForwardDenseLocalFromIndices(sdfLocal, sdfI0, sdfI1, sdfF, d);
                            ticksCollSdfNormal += Stopwatch.GetTimestamp() - tSdfN0;

                            float stepOut = contactRadius - d;
                            if (stepOut <= contactSlop)
                            {
                                sdfNormal = math.normalizesafe(n, new float3(0, 1, 0));
                                break;
                            }

                            stepOut = math.min(stepOut, maxStep);
                            newPos += n * stepOut;
                            sdfNormal = math.normalizesafe(n, new float3(0, 1, 0));

                            long tSdfDist1 = Stopwatch.GetTimestamp();
                            d = staticSdf.SampleDistanceDenseWithLocalAndIndices(newPos, out sdfLocal, out sdfI0, out sdfI1, out sdfF);
                            ticksCollSdfDistance += Stopwatch.GetTimestamp() - tSdfDist1;
                        }

                        lastNormal = sdfNormal;
                        lastColliderVelocity = float3.zero;
                    }

                    ticksCollSdf += Stopwatch.GetTimestamp() - tSdf0;
                }

                int sourceId = p.SourceId;
                bool liquidMode = false;
                if (sourceId >= 0 && sourceId < maxSources && _sourceActiveFlags.IsCreated && _sourceActiveFlags[sourceId] != 0)
                    liquidMode = _sourceLiquidFlags.IsCreated && _sourceLiquidFlags[sourceId] != 0;
                
                // 环境碰撞体检测（盒子碰撞）
                if (colliders.IsCreated && colliderCount > 0)
                {
                    long tColliderTotal0 = Stopwatch.GetTimestamp();
                    bool canUseCandidatesForThisParticle = canUseSourceColliderCandidates &&
                                                          sourceId >= 0 && sourceId < maxSources;

                    if (canUseCandidatesForThisParticle)
                    {
                        int baseOffset = _sourceColliderOffsets[sourceId];
                        int count = _sourceColliderCounts[sourceId];
                        dbgParticlesUsedCandidates++;
                        if (count <= 0)
                        {
                            dbgParticlesCandidateEmpty++;
                        }
                        else
                        {
                            for (int ci = 0; ci < count; ci++)
                            {
                                int c = _sourceColliderIndices[baseOffset + ci];
                                MyBoxCollider box = colliders[c];
                                dbgColliderChecks++;
                                dbgCandidateColliderChecks++;
                                if (box.IsDynamic == 0 && (skipStaticColliders || hadSdfCollision))
                                    continue;

                                switch (box.Shape)
                                {
                                    case ColliderShapes.Obb:
                                    {
                                        float3 dir = newPos - box.Center;
                                        quaternion invRot = math.conjugate(box.Rotation);
                                        float3 local = math.mul(invRot, dir);
                                        float3 vecLocal = math.abs(local);

                                        if (liquidMode && box.IsDynamic != 0 && _interactionSettings.DynamicDragStrength > 0f && _interactionSettings.DynamicDragRadius > 0f)
                                        {
                                            long tDrag0 = Stopwatch.GetTimestamp();
                                            float3 outside = math.max(vecLocal - box.Extent, 0f);
                                            float dist = math.length(outside);
                                            if (dist < _interactionSettings.DynamicDragRadius)
                                            {
                                                float w = 1f - (dist / math.max(1e-5f, _interactionSettings.DynamicDragRadius));
                                                float alpha = 1f - math.exp(-_interactionSettings.DynamicDragStrength * deltaTime * w);
                                                v = math.lerp(v, box.Velocity, alpha);
                                            }

                                            ticksCollDynamicDrag += Stopwatch.GetTimestamp() - tDrag0;
                                        }

                                        if (math.all(vecLocal < box.Extent))
                                        {
                                            long tResolve0 = Stopwatch.GetTimestamp();
                                            float3 remain = box.Extent - vecLocal;
                                            int axis = 0;
                                            if (remain.y < remain[axis]) axis = 1;
                                            if (remain.z < remain[axis]) axis = 2;

                                            float sign = math.sign(local[axis]);
                                            if (sign == 0f) sign = 1f;
                                            local[axis] = sign * box.Extent[axis];
                                            newPos = box.Center + math.mul(box.Rotation, local);

                                            float3 nLocal = float3.zero;
                                            nLocal[axis] = sign;
                                            lastNormal = math.mul(box.Rotation, nLocal);
                                            lastColliderVelocity = (box.IsDynamic != 0) ? box.Velocity : float3.zero;
                                            lastFriction = math.max(lastFriction, box.Friction);
                                            hadCollision = true;

                                            if (box.IsDynamic != 0)
                                            {
                                                liquidMode = true;
                                                if (sourceId >= 0 && sourceId < maxSources)
                                                {
                                                    ForceSourceLiquidMode(sourceId);
                                                }
                                            }

                                            ticksCollResolve += Stopwatch.GetTimestamp() - tResolve0;
                                        }
                                        break;
                                    }
                                    case ColliderShapes.Capsule:
                                    {
                                        ColliderShapeUtils.ComputeCapsule(in box, out float3 a, out float3 b, out float r);
                                        float r2 = r * r;
                                        float d2 = newPos.udSqrSegment(a, b);

                                        if (liquidMode && box.IsDynamic != 0 && _interactionSettings.DynamicDragStrength > 0f && _interactionSettings.DynamicDragRadius > 0f)
                                        {
                                            long tDrag0 = Stopwatch.GetTimestamp();
                                            float dLen = math.sqrt(math.max(0f, d2));
                                            float distOutside = math.max(0f, dLen - r);
                                            if (distOutside < _interactionSettings.DynamicDragRadius)
                                            {
                                                float w = 1f - (distOutside / math.max(1e-5f, _interactionSettings.DynamicDragRadius));
                                                float alpha = 1f - math.exp(-_interactionSettings.DynamicDragStrength * deltaTime * w);
                                                v = math.lerp(v, box.Velocity, alpha);
                                            }

                                            ticksCollDynamicDrag += Stopwatch.GetTimestamp() - tDrag0;
                                        }

                                        if (d2 < r2)
                                        {
                                            long tResolve0 = Stopwatch.GetTimestamp();
                                            float3 closest = ColliderShapeUtils.ClosestPointOnSegment(newPos, a, b);
                                            float3 d = newPos - closest;

                                            float3 n;
                                            if (d2 < 1e-10f)
                                            {
                                                float3 fallback = math.mul(box.Rotation, new float3(0, 1, 0));
                                                float fb2 = math.lengthsq(fallback);
                                                n = fb2 < 1e-10f ? new float3(0, 1, 0) : fallback * math.rsqrt(fb2);
                                            }
                                            else
                                            {
                                                n = d * math.rsqrt(d2);
                                            }

                                            newPos = closest + n * r;
                                            lastNormal = n;
                                            lastColliderVelocity = (box.IsDynamic != 0) ? box.Velocity : float3.zero;
                                            lastFriction = math.max(lastFriction, box.Friction);
                                            hadCollision = true;

                                            if (box.IsDynamic != 0)
                                            {
                                                liquidMode = true;
                                                if (sourceId >= 0 && sourceId < maxSources)
                                                {
                                                    ForceSourceLiquidMode(sourceId);
                                                }
                                            }

                                            ticksCollResolve += Stopwatch.GetTimestamp() - tResolve0;
                                        }
                                        break;
                                    }
                                    default:
                                    {
                                        float3 dir = newPos - box.Center;
                                        float3 vec = math.abs(dir);

                                        if (liquidMode && box.IsDynamic != 0 && _interactionSettings.DynamicDragStrength > 0f && _interactionSettings.DynamicDragRadius > 0f)
                                        {
                                            long tDrag0 = Stopwatch.GetTimestamp();
                                            float3 outside = math.max(vec - box.Extent, 0f);
                                            float dist = math.length(outside);
                                            if (dist < _interactionSettings.DynamicDragRadius)
                                            {
                                                float w = 1f - (dist / math.max(1e-5f, _interactionSettings.DynamicDragRadius));
                                                float alpha = 1f - math.exp(-_interactionSettings.DynamicDragStrength * deltaTime * w);
                                                v = math.lerp(v, box.Velocity, alpha);
                                            }

                                            ticksCollDynamicDrag += Stopwatch.GetTimestamp() - tDrag0;
                                        }
                                        
                                        if (math.all(vec < box.Extent))
                                        {
                                            long tResolve0 = Stopwatch.GetTimestamp();
                                            float3 remain = box.Extent - vec;
                                            int axis = 0;
                                            if (remain.y < remain[axis]) axis = 1;
                                            if (remain.z < remain[axis]) axis = 2;

                                            float sign = math.sign(dir[axis]);
                                            if (sign == 0f) sign = 1f;
                                            float skin = (liquidMode && axis != 1) ? 0.02f : 0f;
                                            float target = box.Center[axis] + sign * (box.Extent[axis] + skin);
                                            float deltaAxis = target - newPos[axis];
                                            const float maxCorrection = 0.2f;
                                            if (math.abs(deltaAxis) > maxCorrection)
                                                deltaAxis = math.sign(deltaAxis) * maxCorrection;
                                            newPos[axis] += deltaAxis;

                                            lastNormal = float3.zero;
                                            lastNormal[axis] = sign;
                                            lastColliderVelocity = (box.IsDynamic != 0) ? box.Velocity : float3.zero;
                                            lastFriction = math.max(lastFriction, box.Friction);
                                            hadCollision = true;

                                            if (box.IsDynamic != 0)
                                            {
                                                liquidMode = true;
                                                if (sourceId >= 0 && sourceId < maxSources)
                                                {
                                                    ForceSourceLiquidMode(sourceId);
                                                }
                                            }

                                            ticksCollResolve += Stopwatch.GetTimestamp() - tResolve0;
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        dbgParticlesFullScan++;
                        for (int c = 0; c < colliderCount; c++)
                        {
                            MyBoxCollider box = colliders[c];
                            dbgColliderChecks++;
                            dbgFullScanColliderChecks++;
                            if (box.IsDynamic == 0 && (skipStaticColliders || hadSdfCollision))
                                continue;

                            switch (box.Shape)
                            {
                                case ColliderShapes.Obb:
                                {
                                    float3 dir = newPos - box.Center;
                                    quaternion invRot = math.conjugate(box.Rotation);
                                    float3 local = math.mul(invRot, dir);
                                    float3 vecLocal = math.abs(local);

                                    if (liquidMode && box.IsDynamic != 0 && _interactionSettings.DynamicDragStrength > 0f && _interactionSettings.DynamicDragRadius > 0f)
                                    {
                                        long tDrag0 = Stopwatch.GetTimestamp();
                                        float3 outside = math.max(vecLocal - box.Extent, 0f);
                                        float dist = math.length(outside);
                                        if (dist < _interactionSettings.DynamicDragRadius)
                                        {
                                            float w = 1f - (dist / math.max(1e-5f, _interactionSettings.DynamicDragRadius));
                                            float alpha = 1f - math.exp(-_interactionSettings.DynamicDragStrength * deltaTime * w);
                                            v = math.lerp(v, box.Velocity, alpha);
                                        }

                                        ticksCollDynamicDrag += Stopwatch.GetTimestamp() - tDrag0;
                                    }

                                    if (math.all(vecLocal < box.Extent))
                                    {
                                        long tResolve0 = Stopwatch.GetTimestamp();
                                        float3 remain = box.Extent - vecLocal;
                                        int axis = 0;
                                        if (remain.y < remain[axis]) axis = 1;
                                        if (remain.z < remain[axis]) axis = 2;

                                        float sign = math.sign(local[axis]);
                                        if (sign == 0f) sign = 1f;
                                        local[axis] = sign * box.Extent[axis];
                                        newPos = box.Center + math.mul(box.Rotation, local);

                                        float3 nLocal = float3.zero;
                                        nLocal[axis] = sign;
                                        lastNormal = math.mul(box.Rotation, nLocal);
                                        lastColliderVelocity = (box.IsDynamic != 0) ? box.Velocity : float3.zero;
                                        lastFriction = math.max(lastFriction, box.Friction);
                                        hadCollision = true;

                                        if (box.IsDynamic != 0)
                                        {
                                            liquidMode = true;
                                            if (sourceId >= 0 && sourceId < maxSources)
                                            {
                                                ForceSourceLiquidMode(sourceId);
                                            }
                                        }

                                        ticksCollResolve += Stopwatch.GetTimestamp() - tResolve0;
                                    }
                                    break;
                                }
                                case ColliderShapes.Capsule:
                                {
                                    ColliderShapeUtils.ComputeCapsule(in box, out float3 a, out float3 b, out float r);
                                    float r2 = r * r;
                                    float d2 = newPos.udSqrSegment(a, b);

                                    if (liquidMode && box.IsDynamic != 0 && _interactionSettings.DynamicDragStrength > 0f && _interactionSettings.DynamicDragRadius > 0f)
                                    {
                                        long tDrag0 = Stopwatch.GetTimestamp();
                                        float dLen = math.sqrt(math.max(0f, d2));
                                        float distOutside = math.max(0f, dLen - r);
                                        if (distOutside < _interactionSettings.DynamicDragRadius)
                                        {
                                            float w = 1f - (distOutside / math.max(1e-5f, _interactionSettings.DynamicDragRadius));
                                            float alpha = 1f - math.exp(-_interactionSettings.DynamicDragStrength * deltaTime * w);
                                            v = math.lerp(v, box.Velocity, alpha);
                                        }

                                        ticksCollDynamicDrag += Stopwatch.GetTimestamp() - tDrag0;
                                    }

                                    if (d2 < r2)
                                    {
                                        long tResolve0 = Stopwatch.GetTimestamp();
                                        float3 closest = ColliderShapeUtils.ClosestPointOnSegment(newPos, a, b);
                                        float3 d = newPos - closest;

                                        float3 n;
                                        if (d2 < 1e-10f)
                                        {
                                            float3 fallback = math.mul(box.Rotation, new float3(0, 1, 0));
                                            float fb2 = math.lengthsq(fallback);
                                            n = fb2 < 1e-10f ? new float3(0, 1, 0) : fallback * math.rsqrt(fb2);
                                        }
                                        else
                                        {
                                            n = d * math.rsqrt(d2);
                                        }

                                        newPos = closest + n * r;
                                        lastNormal = n;
                                        lastColliderVelocity = (box.IsDynamic != 0) ? box.Velocity : float3.zero;
                                        lastFriction = math.max(lastFriction, box.Friction);
                                        hadCollision = true;

                                        if (box.IsDynamic != 0)
                                        {
                                            liquidMode = true;
                                            if (sourceId >= 0 && sourceId < maxSources)
                                            {
                                                ForceSourceLiquidMode(sourceId);
                                            }
                                        }

                                        ticksCollResolve += Stopwatch.GetTimestamp() - tResolve0;
                                    }
                                    break;
                                }
                                default:
                                {
                                    float3 dir = newPos - box.Center;
                                    float3 vec = math.abs(dir);

                                    if (liquidMode && box.IsDynamic != 0 && _interactionSettings.DynamicDragStrength > 0f && _interactionSettings.DynamicDragRadius > 0f)
                                    {
                                        long tDrag0 = Stopwatch.GetTimestamp();
                                        float3 outside = math.max(vec - box.Extent, 0f);
                                        float dist = math.length(outside);
                                        if (dist < _interactionSettings.DynamicDragRadius)
                                        {
                                            float w = 1f - (dist / math.max(1e-5f, _interactionSettings.DynamicDragRadius));
                                            float alpha = 1f - math.exp(-_interactionSettings.DynamicDragStrength * deltaTime * w);
                                            v = math.lerp(v, box.Velocity, alpha);
                                        }

                                        ticksCollDynamicDrag += Stopwatch.GetTimestamp() - tDrag0;
                                    }
                                    
                                    if (math.all(vec < box.Extent))
                                    {
                                        long tResolve0 = Stopwatch.GetTimestamp();
                                        float3 remain = box.Extent - vec;
                                        int axis = 0;
                                        if (remain.y < remain[axis]) axis = 1;
                                        if (remain.z < remain[axis]) axis = 2;

                                        float sign = math.sign(dir[axis]);
                                        if (sign == 0f) sign = 1f;
                                        float skin = (liquidMode && axis != 1) ? 0.02f : 0f;
                                        float target = box.Center[axis] + sign * (box.Extent[axis] + skin);
                                        float deltaAxis = target - newPos[axis];
                                        const float maxCorrection = 0.2f;
                                        if (math.abs(deltaAxis) > maxCorrection)
                                            deltaAxis = math.sign(deltaAxis) * maxCorrection;
                                        newPos[axis] += deltaAxis;

                                        lastNormal = float3.zero;
                                        lastNormal[axis] = sign;
                                        lastColliderVelocity = (box.IsDynamic != 0) ? box.Velocity : float3.zero;
                                        lastFriction = math.max(lastFriction, box.Friction);
                                        hadCollision = true;

                                        if (box.IsDynamic != 0)
                                        {
                                            liquidMode = true;
                                            if (sourceId >= 0 && sourceId < maxSources)
                                            {
                                                ForceSourceLiquidMode(sourceId);
                                            }
                                        }

                                        ticksCollResolve += Stopwatch.GetTimestamp() - tResolve0;
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    ticksCollColliderTotal += Stopwatch.GetTimestamp() - tColliderTotal0;
                }

                {
                    long tGround0 = Stopwatch.GetTimestamp();
                    bool allowGroundClamp = PBF_Utils.AllowGroundClamp(useStaticSdf, disableStaticColliderFallback);
                    if (allowGroundClamp)
                    {
                        float groundY = fallbackGroundY;
                        if (sourceId >= 0 && sourceId < maxSources && sources.IsCreated && sources[sourceId].active)
                            groundY = sources[sourceId].groundY;

                        float3 groundPoint = new float3(newPos.x, groundY, newPos.z);
                        float3 groundNormal = new float3(0, 1, 0);
                        if (PBF_Utils.ClampToGroundPlane(ref newPos, groundY, groundPoint, groundNormal))
                        {
                            hadCollision = true;
                            lastNormal = groundNormal;
                            lastColliderVelocity = float3.zero;
                            lastFriction = math.max(lastFriction, staticFriction);
                        }
                    }
                    ticksCollGroundClamp += Stopwatch.GetTimestamp() - tGround0;
                }

                if (hadCollision)
                {
                    long tFriction0 = Stopwatch.GetTimestamp();
                    float3 n = lastNormal;
                    float n2 = math.lengthsq(n);
                    if (n2 > 1e-10f)
                    {
                        n *= math.rsqrt(n2);

                        float friction = math.saturate(lastFriction);
                        const float normalCollisionDamping = 0.1f;

                        if (!liquidMode)
                        {
                            float vnInto = math.dot(v, n);
                            if (vnInto < 0f)
                                v -= n * vnInto;

                            float vn = math.dot(v, n);
                            float3 vN = n * vn;
                            float3 vT = v - vN;
                            vN *= normalCollisionDamping;
                            vT *= math.max(0f, 1f - friction);
                            v = vN + vT;
                        }
                        else
                        {
                            float3 vRel = v - lastColliderVelocity;
                            float vnInto = math.dot(vRel, n);
                            if (vnInto < 0f)
                                vRel -= n * vnInto;

                            float vn = math.dot(vRel, n);
                            float3 vN = n * vn;
                            float3 vT = vRel - vN;
                            vN *= normalCollisionDamping;
                            vT *= math.max(0f, 1f - friction);
                            v = lastColliderVelocity + (vN + vT);
                        }
                    }

                    ticksCollFriction += Stopwatch.GetTimestamp() - tFriction0;
                }
                velocities[i] = v;

                long tWriteBack0 = Stopwatch.GetTimestamp();
                p.Position = newPos;
                particles[i] = p;
                mainParticles[DROPLET_START + i] = p;

                ticksCollWriteBack += Stopwatch.GetTimestamp() - tWriteBack0;

                ticksCollLoopTotal += Stopwatch.GetTimestamp() - tLoopStart;
            }

            PerformanceProfiler.Add("Droplets_Source_Clear", ticksSourceClear * invFreqMs);
            PerformanceProfiler.Add("Droplets_Source_Count", ticksSourceCount * invFreqMs);
            PerformanceProfiler.Add("Droplets_Source_Offsets", ticksSourceOffsets * invFreqMs);
            PerformanceProfiler.Add("Droplets_Source_BuildIndices", ticksSourceBuildIndices * invFreqMs);
            PerformanceProfiler.Add("Droplets_Source_Centroid", ticksSourceCentroid * invFreqMs);
            PerformanceProfiler.Add("Droplets_Source_Cohesion", ticksSourceCohesion * invFreqMs);
            PerformanceProfiler.Add("Droplets_Source_CopyToTemp", ticksSourceCopyToTemp * invFreqMs);
            PerformanceProfiler.Add("Droplets_Source_PbfStep", ticksSourcePbfStep * invFreqMs);
            PerformanceProfiler.Add("Droplets_Source_WriteBack", ticksSourceWriteBack * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_GravityPredict", pbfStepTimings.TicksGravityPredict * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_Neighborhood_Hash", pbfStepTimings.TicksNeighborhoodHash * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_Neighborhood_Sort", pbfStepTimings.TicksNeighborhoodSort * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_Neighborhood_BuildLut", pbfStepTimings.TicksNeighborhoodBuildLut * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_ShufflePositions", pbfStepTimings.TicksShufflePositions * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_SolveLambda", pbfStepTimings.TicksSolveLambda * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_SolveDelta", pbfStepTimings.TicksSolveDelta * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_UnshufflePositions", pbfStepTimings.TicksUnshufflePositions * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_ApplyPos", pbfStepTimings.TicksApplyPositionCorrections * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_Viscosity_ShuffleVel", pbfStepTimings.TicksViscosityShuffleVel * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_Viscosity_Job", pbfStepTimings.TicksViscosityJob * invFreqMs);
            PerformanceProfiler.Add("Droplets_Pbf_Viscosity_UnshuffleVel", pbfStepTimings.TicksViscosityUnshuffleVel * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_LoopTotal", ticksCollLoopTotal * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_Sdf", ticksCollSdf * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_SdfDistance", ticksCollSdfDistance * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_SdfNormal", ticksCollSdfNormal * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_BuildSourceCandidates", ticksCollBuildSourceCandidates * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_CollidersTotal", ticksCollColliderTotal * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_DynamicDrag", ticksCollDynamicDrag * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_Resolve", ticksCollResolve * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_Friction", ticksCollFriction * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_GroundClamp", ticksCollGroundClamp * invFreqMs);
            PerformanceProfiler.Add("Droplets_Collision_WriteBack", ticksCollWriteBack * invFreqMs);
            PerformanceProfiler.Add("Droplets_Liquid_CopyToTemp", ticksLiquidCopyToTemp * invFreqMs);
            PerformanceProfiler.Add("Droplets_Liquid_PbfStep", ticksLiquidPbfStep * invFreqMs);
            PerformanceProfiler.Add("Droplets_Liquid_WriteBack", ticksLiquidWriteBack * invFreqMs);
        }
        
        /// <summary>
        /// 检测与主体的接触（用于合并）
        /// </summary>
        public int GetContactingDroplets(float3 mainCenter, float mergeRadius, NativeList<int> result)
        {
            result.Clear();
            float mergeRadius2 = mergeRadius * mergeRadius;
            
            // 优化：由于 swap 压缩，无需类型检查
            for (int i = 0; i < activeCount; i++)
            {
                float dist2 = math.lengthsq(particles[i].Position - mainCenter);
                if (dist2 < mergeRadius2)
                {
                    result.Add(DROPLET_START + i); // 返回全局索引
                }
            }
            
            return result.Length;
        }
        
        /// <summary>
        /// 拷贝水珠渲染数据到连续位置（转换为世界坐标）
        /// 只拷贝 Type == SceneDroplet 的粒子，跳过已被吸收的粒子
        /// </summary>
        /// <param name="renderBuffer">目标渲染缓冲</param>
        /// <param name="startOffset">起始偏移（通常是 activeParticles）</param>
        /// <returns>拷贝的水珠数量</returns>
        public int CopyToRenderBuffer(NativeArray<Particle> renderBuffer, int startOffset)
        {
            // 优化：由于 swap 压缩，[0..activeCount) 范围内都是活跃的 SceneDroplet
            // 从遍历 8192 个优化到只遍历 activeCount 个
            for (int i = 0; i < activeCount; i++)
            {
                renderBuffer[startOffset + i] = particles[i];
            }
            
            return activeCount;
        }
        
        /// <summary>
        /// 获取调试信息
        /// </summary>
        public void GetDebugInfo(out int totalActive, out int sourceCount)
        {
            totalActive = activeCount;
            sourceCount = 0;
            for (int i = 0; i < maxSources; i++)
            {
                if (sources[i].active)
                    sourceCount++;
            }
        }
    }
}
