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
        // 固定分区边界（仅用于模拟层内存管理）
        public const int DROPLET_START = 8192;
        public const int DROPLET_CAPACITY = 8192;
        public const int DROPLET_END = DROPLET_START + DROPLET_CAPACITY - 1;
        
        private NativeSlice<Particle> particles;
        private NativeSlice<float3> velocities;
        private int activeCount;
        private int maxSources;
        
        // PBF 物理系统（封装了所有缓冲区）
        private PBFSystem pbfSystem;
        
        // 凝聚力 Job 临时缓冲区
        private NativeArray<float3> _targetCenters;
        private NativeArray<float3> _positions;
        
        // 每个源的信息
        private struct SourceInfo
        {
            public int startIndex;
            public int count;
            public float3 center;
            public float groundY; // 该源的地面Y（内部坐标）
            public bool active;
            
            // 物理参数（从 DropWater 读取）
            public float cohesionStrength;
            public float cohesionRadius;
            public float velocityDamping;
            public float verticalCohesionScale;
        }
        private NativeArray<SourceInfo> sources;
        
        // 当前使用的物理参数（取第一个激活源的参数，在 Initialize 中初始化）
        private float _cohesionStrength;
        private float _cohesionRadius;
        private float _velocityDamping;
        private float _verticalCohesionScale;
        private bool _enableViscosity;
        private float _viscosityStrength;
        
        public int ActiveCount => activeCount;
        
        /// <summary>
        /// 实时更新物理参数（用于 Inspector 调整时生效）
        /// </summary>
        public void UpdatePhysicsParams(float cohesionStrength, float cohesionRadius, 
            float velocityDamping, float verticalCohesionScale,
            bool enableViscosity = true, float viscosityStrength = 10f)
        {
            // [DropletSubsystem] 物理参数更新日志已禁用
            
            _cohesionStrength = cohesionStrength;
            _cohesionRadius = cohesionRadius;
            _velocityDamping = velocityDamping;
            _verticalCohesionScale = verticalCohesionScale;
            _enableViscosity = enableViscosity;
            _viscosityStrength = viscosityStrength;
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
            
            // 初始化默认物理参数（struct 字段初始化器可能不生效）
            _cohesionStrength = 30f;
            _cohesionRadius = 10f;
            _velocityDamping = 0.99f;
            _verticalCohesionScale = 0.5f;
            _enableViscosity = true;
            _viscosityStrength = 10f;
            sources = new NativeArray<SourceInfo>(maxSourceCount, Allocator.Persistent);
            
            // 初始化 PBF 物理系统（使用水珠配置）
            pbfSystem = new PBFSystem(PBFSystem.Config.Droplet);
            
            // 初始化凝聚力 Job 临时缓冲区
            _targetCenters = new NativeArray<float3>(DROPLET_CAPACITY, Allocator.Persistent);
            _positions = new NativeArray<float3>(DROPLET_CAPACITY, Allocator.Persistent);
            
            // 初始化分区为休眠状态
            for (int i = 0; i < DROPLET_CAPACITY; i++)
            {
                particles[i] = new Particle
                {
                    Type = ParticleType.Dormant,
                    Position = new float3(0, -1000, 0),
                    SourceId = -1,
                    ControllerId = 0,
                    FreeFrames = 0
                };
                velocities[i] = float3.zero;
            }
        }
        
        public void Dispose()
        {
            if (sources.IsCreated) sources.Dispose();
            if (_targetCenters.IsCreated) _targetCenters.Dispose();
            if (_positions.IsCreated) _positions.Dispose();
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
        
        /// <summary>
        /// 激活水珠源（在固定分区中分配）
        /// </summary>
        /// <param name="sourceGroundY">源位置的地面高度（模拟坐标）</param>
        public int ActivateSource(int sourceId, float3 sourcePosition, int requestedCount,
            float cohesionStrength = 30f, float cohesionRadius = 10f, 
            float velocityDamping = 0.99f, float verticalCohesionScale = 0.5f,
            float sourceGroundY = 0f)
        {
            if (sourceId < 0 || sourceId >= maxSources)
            {
                Debug.LogError($"[DropletSubsystem] 无效的sourceId: {sourceId}, maxSources={maxSources}");
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
            float spacingInternal = 0.8f; // 增大间距，降低初始密度
            
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
                    ControllerId = -1,  // -1 让 shader 使用 _Color 而不是 colors 数组
                    FreeFrames = 60  // 保护期：约1秒内不会被吸收，让水珠有时间进行物理模拟
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
                cohesionStrength = cohesionStrength,
                cohesionRadius = cohesionRadius,
                velocityDamping = velocityDamping,
                verticalCohesionScale = verticalCohesionScale
            };
            
            // 更新当前使用的物理参数（取第一个激活源）
            _cohesionStrength = cohesionStrength;
            _cohesionRadius = cohesionRadius;
            _velocityDamping = velocityDamping;
            _verticalCohesionScale = verticalCohesionScale;
            
            activeCount += allocatedCount;
            
            // [DropletSubsystem] 激活源日志已禁用
            
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
                    ControllerId = 0,
                    FreeFrames = 0
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
            activeCount -= deactivatedCount;
            
            // Debug.Log($"[DropletSubsystem] 休眠源{sourceId}: {deactivatedCount}个水珠, 剩余活跃={activeCount}");
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
                ControllerId = 0,
                FreeFrames = 0
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
            float fallbackGroundY, float targetDensity = 1.0f, NativeArray<MyBoxCollider> colliders = default, int colliderCount = 0)
        {
            if (activeCount == 0)
                return;
            
            // [水珠诊断] 调试日志已禁用
            
            // 重力与主体史莱姆一致（-5f，不需要乘以 InvScale）
            float3 gravity = new float3(0, -5f, 0);
            
            // ========== 使用公共 ApplyCohesionWithPositionJob ==========
            // 优化：合并质心计算和 Job 数据准备为单次遍历
            // 由于 swap 压缩，[0..activeCount) 范围内都是活跃的 SceneDroplet
            float3 centroid = float3.zero;
            for (int i = 0; i < activeCount; i++)
            {
                float3 pos = particles[i].Position;
                centroid += pos;
                _positions[i] = pos;
            }
            centroid /= activeCount;
            
            // 目标中心 = 质心 + 向上偏移（让粒子向上聚集成球形，而不是被地面压扁）
            float3 targetCenter = centroid + new float3(0, 1.0f, 0);
            
            // 设置目标中心（单独遍历，因为需要先计算完质心）
            for (int i = 0; i < activeCount; i++)
            {
                _targetCenters[i] = targetCenter;
            }
            
            // 使用公共 Job 处理凝聚力、速度阻尼、重力
            // 水珠不使用垂直缩放（VerticalCohesionScale=1.0），保持各向同性凝聚力
            new Simulation_PBF.ApplyCohesionWithPositionJob
            {
                TargetCenters = _targetCenters,
                Positions = _positions,
                Velocities = velocities,
                CohesionStrength = _cohesionStrength,
                CohesionRadius = _cohesionRadius,
                VelocityDamping = _velocityDamping,
                Gravity = gravity,
                DeltaTime = deltaTime,
                VerticalCohesionScale = 1.0f  // 禁用垂直缩放，保持球形
            }.Schedule(activeCount, 64).Complete();
            
            // 创建活跃粒子的切片
            var activeParticles = new NativeSlice<Particle>(particles, 0, activeCount);
            var activeVelocities = new NativeSlice<float3>(velocities, 0, activeCount);
            
            // 使用PBFSystem进行物理模拟（内部已包含粘性处理，不再外部重复调用）
            pbfSystem.SimulateStep(activeParticles, activeVelocities, activeCount, deltaTime, float3.zero, _enableViscosity, _viscosityStrength);
            
            // 应用碰撞检测和地面限制
            // 优化：由于 swap 压缩，[0..activeCount) 范围内都是活跃的 SceneDroplet
            for (int i = 0; i < activeCount; i++)
            {
                var p = particles[i];
                float3 newPos = p.Position;
                
                // 环境碰撞体检测（盒子碰撞）
                if (colliders.IsCreated && colliderCount > 0)
                {
                    for (int c = 0; c < colliderCount; c++)
                    {
                        MyBoxCollider box = colliders[c];
                        float3 dir = newPos - box.Center;
                        float3 vec = math.abs(dir);
                        
                        if (math.all(vec < box.Extent))
                        {
                            // 推出碰撞体
                            float3 remain = box.Extent - vec;
                            int axis = 0;
                            if (remain.y < remain[axis]) axis = 1;
                            if (remain.z < remain[axis]) axis = 2;
                            
                            newPos[axis] = box.Center[axis] + math.sign(dir[axis]) * box.Extent[axis];
                            
                            // 清除该轴速度
                            velocities[i] = velocities[i] * new float3(axis != 0 ? 1 : 0, axis != 1 ? 1 : 0, axis != 2 ? 1 : 0);
                        }
                    }
                }
                
                // 【改进】地面限制：根据粒子的 SourceId 获取对应源的地面高度
                float groundY = fallbackGroundY;
                int sid = p.SourceId;
                if (sid >= 0 && sid < maxSources && sources[sid].active)
                {
                    groundY = sources[sid].groundY;
                }
                
                if (newPos.y < groundY)
                {
                    newPos.y = groundY;
                    var vel = velocities[i];
                    if (vel.y < 0) vel.y = 0;  // 只清除向下速度
                    velocities[i] = vel;
                }
                
                p.Position = newPos;
                particles[i] = p;
                mainParticles[DROPLET_START + i] = p;
            }
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
