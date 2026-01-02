using System.Collections.Generic;
using Revive.Environment;
using Revive.Mathematics;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Revive.Slime
{
    public struct Particle 
    {
        /// <summary>
        /// 位置：所有粒子统一使用模拟坐标（内部坐标，InvScale=10）
        /// </summary>
        public float3 Position;
        public ParticleType Type;  // 粒子类型
        public int ControllerSlot;   // 所属控制器槽位（0=主体，1+=分离组）
        public int SourceId;       // 场景水珠源ID（-1=非场景水珠）
        public int ClusterId;      // CCA cluster id
        public int FreeFrames;     // 发射倒计时（仅Emitted类型使用）
        public int BlobId;       // 分离团块稳定ID（0=主体，1+=分离团块）
        public int FramesOutsideMain; // 连续离开主体的帧数（用于延迟分离判定）
        
        /// <summary>
        /// 是否可以合并到主体
        /// </summary>
        public bool CanMergeToMain()
        {
            return Type == ParticleType.Separated;
        }
        
        /// <summary>
        /// 是否是活跃粒子（参与物理模拟）
        /// </summary>
        public bool IsActive()
        {
            return Type != ParticleType.Dormant && Type != ParticleType.FadingOut;
        }
    }
    
    public struct ParticleController
    {
        public float3 Center;
        public float Radius;
        public float3 Velocity;
        public float Concentration;
        public int ParticleCount;      // 当前帧归属的粒子数
        public int FramesWithoutParticles; // 无粒子归属的帧数（用于延迟删除）
        public bool IsValid;           // 是否有效（未被删除）
        public float GroundY;          // 该控制器位置的地面高度（模拟坐标）
        public float3 GroundPoint;
        public float3 GroundNormal;
    }

    /// <summary>
    /// 碰撞体类型常量
    /// </summary>
    public static class ColliderTypes
    {
        public const int Ground = 0;     // 普通地面/障碍物
        public const int Climbable = 1;  // 可攀爬表面
        public const int Water = 2;      // 水体
        public const int Sticky = 3;     // 粘性表面
    }
    
    public struct MyBoxCollider
    {
        public float3 Center;
        public float3 Extent;
        public int Type;       // 碰撞体类型（见 ColliderTypes）
        public float Friction; // 表面摩擦力（0-1）
        public int IsDynamic;  // 1=动态（可投掷/可移动），0=静态
        public float3 Velocity; // 动态碰撞体速度（模拟坐标），静态为0
        public int Shape;      // 0=AABB, 1=OBB
        public quaternion Rotation; // OBB: local-to-world rotation (sim/world share the same rotation)
    }

    public static class PBF_Utils
    {
        public const int Width = 16;
        public const int Num = Width * Width * Width / 2;
        public const int BubblesCount = 2048;
        public const int GridSize = 4 * 4 * 4;
        public const int GridNum = 768;

        /// <summary>
        /// PBF 核半径（模拟坐标）。
        /// 调大：邻域更大、形体更“糊/粘”，但需要重新标定 targetDensity/threshold 等参数；风险高。
        /// 调小：邻域更小、更“散”，也可能变不稳定。
        /// </summary>
        public const float h = 1.0f;
        public const float h2 = h * h;
        /// <summary>
        /// 空间哈希/网格的单元尺寸（模拟坐标）。通常与 h 绑定。
        /// </summary>
        public const float CellSize = 0.5f * h;

        /// <summary>
        /// 模拟坐标 -> 世界坐标缩放（world = sim * Scale）。
        /// </summary>
        public const float Scale = 0.1f;

        /// <summary>
        /// 世界坐标 -> 模拟坐标缩放（sim = world * InvScale）。
        /// </summary>
        public const float InvScale = 10f;

        /// <summary>
        /// 邻域判定/归一化的 r^2 下限（避免 0 距离导致的数值问题）。
        /// </summary>
        public const float NeighborR2Epsilon = 1e-10f;

        /// <summary>
        /// Lambda/约束求解除零保护项（越大越“软”，越小越可能数值爆炸）。
        /// </summary>
        public const float LambdaEpsilon = 1e-5f;

        /// <summary>
        /// 主体粒子密度约束 c 的下限（用于避免过强的拉伸导致不稳定）。
        /// </summary>
        public const float MainBodyMinC = -0.2f;

        /// <summary>
        /// Tensile 修正采样点系数（dq = TensileDqFactor * h）。
        /// </summary>
        public const float TensileDqFactor = 0.25f;

        /// <summary>
        /// Tensile 修正强度（越大越抗“结团/起泡”，但也可能带来抖动）。
        /// </summary>
        public const float TensileK = 0.1f;
        
        /// <summary>CCA连通组件判定阈值：网格密度高于此值才被视为有效单元</summary>
        public const float CCAThreshold = 1e-4f;
    
        public struct Int2Comparer : IComparer<int2>
        {
            public int Compare(int2 lhs, int2 rhs) => lhs.x - rhs.x;
        }
        public struct BlockComparer : IComparer<int4>
        {
            public int Compare(int4 lhs, int4 rhs) => lhs.w - rhs.w;
        }

        public static int GetKey(int3 coord)
        {
            unchecked
            {
                int key = coord.x & 1023;
                key = (key << 10) | (coord.y & 1023);
                key = (key << 10) | (coord.z & 1023);
                return key;
            }
        }

        public static int3 GetCoord(float3 pos)
        {
            return (int3)math.floor(pos / h);
        }
    
        private const float KernelPoly6 = 315 / (64 * math.PI * h2 * h2 * h2 * h2 * h);

        public static float SmoothingKernelPoly6(float r2)
        {
            if (r2 < h2)
            {
                float v = h2 - r2;
                return v * v * v * KernelPoly6;
            }
            return 0;
        }
    
        private const float Spiky3 = 15 / (h2*h2*h2 * math.PI);
        public static float DerivativeSpikyPow3(float r)
        {
            if (r <= h)
            {
                float v = h - r;
                return -v * v * 3 * Spiky3;
            }
            return 0;
        }
        public static float SpikyKernelPow3(float r)
        {
            if (r < h)
            {
                float v = h - r;
                return v * v * v * Spiky3;
            }
            return 0;
        }

        public static bool AllowGroundClamp(int useStaticSdf, int disableStaticColliderFallback)
        {
            return (useStaticSdf == 0) || (useStaticSdf != 0 && disableStaticColliderFallback != 0);
        }

        public static bool ClampToGroundPlane(ref float3 simPos, float groundY, float3 groundPoint, float3 groundNormal)
        {
            float nLen2 = math.lengthsq(groundNormal);
            if (nLen2 < 1e-6f)
            {
                groundNormal = new float3(0, 1, 0);
                groundPoint = new float3(simPos.x, groundY, simPos.z);
            }
            else
            {
                groundNormal *= math.rsqrt(nLen2);
            }

            const float minGroundNy = 0.55f;
            if (groundNormal.y < minGroundNy)
            {
                groundNormal = new float3(0, 1, 0);
                groundPoint = new float3(simPos.x, groundY, simPos.z);
            }

            float planeDist = math.dot(groundNormal, simPos - groundPoint);
            if (planeDist < 0f)
            {
                simPos -= groundNormal * planeDist;
                return true;
            }

            return false;
        }
    }

    public static class Simulation_PBF
    {
        [BurstCompile]
        public struct HashJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Particle> Ps;
            [WriteOnly] public NativeArray<int2> Hashes;

            public void Execute(int i)
            {
                int3 gridPos = PBF_Utils.GetCoord(Ps[i].Position);
                int hash = PBF_Utils.GetKey(gridPos);
                Hashes[i] = math.int2(hash, i);
            }
        }
        
        /// <summary>
        /// 获取粒子模拟坐标（所有粒子统一使用模拟坐标，直接返回Position）
        /// 注意：返回的是模拟坐标，转换世界坐标需要 * PBF_Utils.Scale
        /// </summary>
        public static float3 GetWorldPosition(Particle p, NativeList<ParticleController> controllers, NativeArray<ParticleController> sourceControllers)
        {
            return p.Position;
        }

        [BurstCompile]
        public struct BuildLutJob : IJob
        {
            [ReadOnly] public NativeArray<int2> Hashes;
            public NativeHashMap<int, int2> Lut;

            public void Execute()
            {
                // 空数组保护：activeParticles=0 时直接返回
                if (Hashes.Length == 0)
                    return;
                
                int currentKey = Hashes[0].x;
                int start = 0;
                for (int i = 1; i < Hashes.Length; ++i)
                {
                    if (Hashes[i].x == currentKey) continue;
                    Lut.TryAdd(currentKey, new int2(start, i));
                    currentKey = Hashes[i].x;
                    start = i;
                }

                Lut.TryAdd(currentKey, new int2(start, Hashes.Length));
            }
        }

        [BurstCompile]
        public struct ShuffleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int2> Hashes;
            [ReadOnly] public NativeArray<Particle> PsRaw;
            [ReadOnly] public NativeArray<Particle> PsNew;
            [ReadOnly] public NativeArray<float3> Velocity;

            [WriteOnly] public NativeArray<float3> PosOld;
            [WriteOnly] public NativeArray<float3> PosPredict;
            [WriteOnly] public NativeArray<float3> VelocityOut;

            public void Execute(int i)
            {
                int id = Hashes[i].y;
                PosPredict[i] = PsNew[id].Position;
                PosOld[i] = PsRaw[id].Position;
                VelocityOut[i] = Velocity[id];
            }
        }

        [BurstCompile]
        public struct ApplyForceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Particle> Ps;
            [ReadOnly] public NativeArray<ParticleController> Controllers;
            [ReadOnly] public NativeArray<ParticleController> SourceControllers;
            [ReadOnly] public NativeArray<int> BlobIdToControllerSlot;
            [ReadOnly] public NativeArray<WindFieldZoneData> WindZones;
            public int WindTargetLayerBit;
            public float ParticleRadiusSim;
            [WriteOnly] public NativeArray<Particle> PsNew;
            public NativeArray<float3> Velocity;
            public float3 Gravity;
            public float DeltaTime;
            public float PredictStep;
            public float VelocityDamping;
            public float VerticalOffset;
            public float DropletVerticalOffset;
            public bool EnableRecall;
            [ReadOnly] public NativeArray<byte> RecallEligibleBlobIds;
            public bool UseRecallEligibleBlobIds;
            public float3 MainCenter; // 主体控制器中心，用于分离粒子排除指向主体的凝聚力分量
            public float3 MainVelocity; // 主体控制器速度，用于计算相对速度
            public float MaxDeformDistXZ; // 水平形变上限（模拟坐标）
            public float MaxDeformDistY;  // 垂直形变上限（模拟坐标）

            public void Execute(int i)
            {
                Particle p = Ps[i];

                if (p.Type == ParticleType.Dormant || p.Type == ParticleType.FadingOut)
                {
                    PsNew[i] = p;
                    Velocity[i] = float3.zero;
                    return;
                }
                
                // 递减自由态倒计时并处理状态转换
                if (p.FreeFrames > 0)
                {
                    p.FreeFrames--;
                    if (p.FreeFrames == 0 && p.Type == ParticleType.Emitted)
                    {
                        // 发射粒子飞行结束后变成分离粒子，由 CCA 控制成为独立小史莱姆
                        p.Type = ParticleType.Separated;
                    }
                }

                int controllerIndex = 0;
                if (p.Type != ParticleType.MainBody)
                {
                    int blobId = p.BlobId;
                    if (blobId >= 0 && blobId < BlobIdToControllerSlot.Length)
                        controllerIndex = BlobIdToControllerSlot[blobId];
                    else
                        controllerIndex = -1;
                }

                bool windEligible = p.Type == ParticleType.Emitted || p.Type == ParticleType.Separated;
                bool hasWindZones = windEligible && WindZones.IsCreated && WindZones.Length > 0 && WindTargetLayerBit != 0;

                float windGroundDrag = 0f;
                float windAirDrag = 0f;
                float3 windPush = float3.zero;
                if (hasWindZones)
                {
                    float3 pos = p.Position;
                    for (int z = 0; z < WindZones.Length; z++)
                    {
                        var zone = WindZones[z];
                        if ((zone.AffectsLayerMask & WindTargetLayerBit) == 0)
                            continue;
                        float3 d = math.abs(pos - zone.CenterSim);
                        if (d.x > zone.ExtentsSim.x || d.y > zone.ExtentsSim.y || d.z > zone.ExtentsSim.z)
                            continue;

                        windGroundDrag += zone.GroundDrag;
                        windAirDrag += zone.AirDrag;
                        windPush += zone.PushSim;
                    }
                }

                // 【关键】自由飞行粒子：只受重力影响，不受速度衰减和控制器影响
                if (p.FreeFrames > 0)
                {
                    float3 freeVel = Velocity[i] + Gravity * DeltaTime;

                    if (hasWindZones && (windGroundDrag > 0f || windAirDrag > 0f || math.lengthsq(windPush) > 0f))
                    {
                        float groundY = Controllers.Length > 0 ? Controllers[0].GroundY : -1000000f;
                        if (controllerIndex >= 0 && controllerIndex < Controllers.Length)
                            groundY = Controllers[controllerIndex].GroundY;
                        bool grounded = p.Position.y <= (groundY + ParticleRadiusSim);
                        float drag = grounded ? windGroundDrag : windAirDrag;
                        if (drag > 0f)
                            freeVel *= 1f / (1f + drag * DeltaTime);
                        if (math.lengthsq(windPush) > 0f)
                            freeVel += windPush * DeltaTime;
                    }

                    p.Position += freeVel * PredictStep;
                    PsNew[i] = p;
                    Velocity[i] = freeVel;
                    return;
                }

                var velocity = Velocity[i] * VelocityDamping + Gravity * DeltaTime;

                if (hasWindZones && (windGroundDrag > 0f || windAirDrag > 0f || math.lengthsq(windPush) > 0f))
                {
                    float groundY = Controllers.Length > 0 ? Controllers[0].GroundY : -1000000f;
                    if (controllerIndex >= 0 && controllerIndex < Controllers.Length)
                        groundY = Controllers[controllerIndex].GroundY;
                    bool grounded = p.Position.y <= (groundY + ParticleRadiusSim);
                    float drag = grounded ? windGroundDrag : windAirDrag;
                    if (drag > 0f)
                        velocity *= 1f / (1f + drag * DeltaTime);
                    if (math.lengthsq(windPush) > 0f)
                        velocity += windPush * DeltaTime;
                }
                
                // 场景水珠使用 SourceControllers
                if (p.Type == ParticleType.SceneDroplet && p.SourceId >= 0)
                {
                    if (p.SourceId < SourceControllers.Length)
                    {
                        ParticleController cl = SourceControllers[p.SourceId];
                        if (cl.Concentration > 0)
                        {
                            float3 toCenter = cl.Center + new float3(0f, cl.Radius * DropletVerticalOffset, 0f) - p.Position;
                            float len = math.length(toCenter);
                            if (len > 0.1f)
                            {
                                velocity += cl.Concentration * DeltaTime * math.min(1, len) *
                                            math.normalizesafe(toCenter);
                            }
                        }
                    }
                }
                // 主体和分离粒子使用 Controllers
                else if (controllerIndex >= 0 && controllerIndex < Controllers.Length)
                {
                    ParticleController cl = Controllers[controllerIndex];
                    float verticalOffsetFactor = (p.Type == ParticleType.MainBody) ? VerticalOffset : 0f;
                    float3 toCenter = cl.Center + new float3(0, cl.Radius * verticalOffsetFactor, 0) - p.Position;
                    float len = math.length(toCenter);
                    bool isSeparated = (p.Type == ParticleType.Separated || p.Type == ParticleType.Emitted);
                    bool inFreeState = p.FreeFrames > 0; // 自由飞行状态，不受控制器影响
                    bool enableRecall = EnableRecall;

                    int blobId = p.BlobId;
                    bool canRecallThisController = !UseRecallEligibleBlobIds ||
                                                   (blobId > 0 && blobId < RecallEligibleBlobIds.Length && RecallEligibleBlobIds[blobId] != 0);

                    bool enableRecallThisController = enableRecall && canRecallThisController;

                    // 分离粒子召回：enableRecall=true 且在控制器半径外时触发
                    // 自由飞行状态的粒子不受召回影响
                    if (enableRecallThisController && isSeparated && !inFreeState && len >= cl.Radius)
                    {
                        // ControllerSlot=0 的分离粒子：计算指向主体的召回方向
                        if (controllerIndex == 0)
                        {
                            float3 dirToMain = math.normalizesafe(new float3(toCenter.x, 0f, toCenter.z));
                            // 召回速度：距离远时快，接近边缘时慢（防止冲过头）
                            // 使用固定的fadeDistance，不依赖主体大小
                            float distFromEdge = len - cl.Radius; // 距离边缘的距离
                            float maxRecallSpeed = 12f;  // 远距离时的速度
                            float minRecallSpeed = 2f;   // 接近边缘时的速度
                            float fadeDistance = 15f;    // 固定值：在15单位内逐渐减速
                            float speedFactor = math.saturate(distFromEdge / fadeDistance);
                            float recallSpeed = math.lerp(minRecallSpeed, maxRecallSpeed, speedFactor);
                            
                            float originalY = velocity.y;
                            velocity = dirToMain * recallSpeed;
                            velocity.y = originalY;
                        }
                        else
                        {
                            // 分离组控制器：直接应用控制器的召回速度。
                            // 注意：避障台阶上抬速度写在 cl.Velocity.y，如果这里强行保留原Y会导致“上台阶”完全无效。
                            float originalY = velocity.y;
                            velocity = cl.Velocity;
                            if (cl.Velocity.y > 0f)
                                velocity.y = math.max(originalY, cl.Velocity.y);
                            else if (cl.Velocity.y < 0f)
                                velocity.y = math.min(originalY, cl.Velocity.y);
                            else
                                velocity.y = originalY;

                            float3 attraction = cl.Concentration * DeltaTime * math.min(1, len) *
                                                math.normalizesafe(toCenter);
                            if (attraction.y > 0f)
                            {
                                float upDVMax = math.abs(Gravity.y) * DeltaTime * 0.5f; // 允许最多抵消50%重力
                                attraction.y = math.min(attraction.y, upDVMax);
                            }
                            velocity += attraction;
                        }
                    }
                    else if (len < cl.Radius)
                    {
                        // 自由飞行状态的粒子：完全跳过控制器的速度跟随和向心力
                        if (inFreeState)
                        {
                            // 不做任何处理，保持粒子原有速度
                        }
                        // 分离粒子对主体控制器(index=0)在非召回模式：只保留重力，不向主体凝聚
                        else if (isSeparated && controllerIndex == 0 && !enableRecallThisController)
                        {
                            // ControllerSlot=0 表示 CCA 还没分配分离组控制器
                            // 不做额外处理，保留重力效果（已在上面应用：velocity = Velocity[i] * VelocityDamping + Gravity * DeltaTime）
                            // 分离粒子靠自身重力下落，等待 CCA 分配分离组控制器后再凝聚
                        }
                        // 分离粒子对分离组控制器(index>0)在非召回模式：速度跟随 + 凝聚力（原版）
                        else if (isSeparated && controllerIndex > 0 && !enableRecallThisController)
                        {
                            // 速度跟随：让粒子跟随组的整体运动
                            float lerpFactor = math.lerp(1, len * 0.1f, cl.Concentration * 0.002f);
                            float originalY = velocity.y;
                            velocity = math.lerp(cl.Velocity, velocity, lerpFactor);
                            velocity.y = originalY; // 保留Y分量
                            
                            // 原版凝聚力
                            float3 attraction = cl.Concentration * DeltaTime * math.min(1, len) *
                                                math.normalizesafe(toCenter);
                            if (attraction.y > 0f)
                            {
                                float upDVMax = math.abs(Gravity.y) * DeltaTime * 0.5f; // 允许最多抵消50%重力
                                attraction.y = math.min(attraction.y, upDVMax);
                            }
                            velocity += attraction;
                        }
                        else
                        {
                            // 原版：速度跟随，分离粒子保留Y分量
                            float lerpFactor = math.lerp(1, len * 0.1f, cl.Concentration * 0.002f);
                            
                            // 分离粒子在召回时（控制器有速度）：更强跟随控制器
                            bool hasRecallVel = enableRecallThisController && math.lengthsq(cl.Velocity) > 0.1f;
                            if (isSeparated && hasRecallVel)
                            {
                                // 召回时使用更低的 lerpFactor，让粒子更跟随控制器
                                lerpFactor = math.min(lerpFactor, 0.3f);
                                float originalY = velocity.y;
                                velocity = math.lerp(cl.Velocity, velocity, lerpFactor);
                                if (cl.Velocity.y > 0f)
                                    velocity.y = math.max(originalY, cl.Velocity.y);
                                else if (cl.Velocity.y < 0f)
                                    velocity.y = math.min(originalY, cl.Velocity.y);
                                else
                                    velocity.y = originalY;
                            }
                            else if (isSeparated)
                            {
                                // 分离组控制器(index>0)：应用速度跟随保持凝聚
                                float originalY = velocity.y;
                                velocity = math.lerp(cl.Velocity, velocity, lerpFactor);
                                velocity.y = originalY;
                            }
                            else
                            {
                                velocity = math.lerp(cl.Velocity, velocity, lerpFactor);
                            }
                            
                            // 原版：向心力
                            // 召回模式：所有粒子都应用向心力
                            // 非召回模式：分离粒子对主体控制器(index=0)不应用，防止被吸回
                            if (!isSeparated || controllerIndex > 0 || enableRecallThisController)
                            {
                                float3 attraction = cl.Concentration * DeltaTime * math.min(1, len) *
                                                    math.normalizesafe(toCenter);
                                if (isSeparated && controllerIndex > 0 && attraction.y > 0f)
                                {
                                    float upDVMax = math.abs(Gravity.y) * DeltaTime * 0.5f;
                                    attraction.y = math.min(attraction.y, upDVMax);
                                }
                                velocity += attraction;
                            }
                        }
                    }
                }

                // 【P3 预测式软限制-椭球约束】预测粒子下一帧位置，若会超出椭球边界则提前消除外扩速度
                // XZ 方向用 MaxDeformDistXZ，Y 方向用 MaxDeformDistY
                // 【关键】使用相对速度，避免影响整体移动；使用预测位置而非当前位置进行判断
                bool deformLimitEnabled = MaxDeformDistXZ > 0f && MaxDeformDistY > 0f;
                if (deformLimitEnabled && p.Type == ParticleType.MainBody)
                {
                    float3 deformCenter = MainCenter;
                    float3 deformVelocity = MainVelocity;

                    // 预测下一帧位置（相对于预测的控制器中心）
                    float3 predictedMainCenter = deformCenter + deformVelocity * PredictStep;
                    float3 predictedPos = p.Position + velocity * PredictStep;
                    float3 d_pred = predictedPos - predictedMainCenter; // 预测位置相对于预测中心的偏移
                    
                    // 椭球归一化距离: r = sqrt((dx/a)^2 + (dy/b)^2 + (dz/a)^2)
                    float3 normalized_pred = new float3(
                        d_pred.x / MaxDeformDistXZ, 
                        d_pred.y / MaxDeformDistY, 
                        d_pred.z / MaxDeformDistXZ
                    );
                    float r_pred = math.length(normalized_pred);
                    
                    // 【预测式】如果预测位置会超出边界，提前消除外扩速度
                    if (r_pred > 1f)
                    {
                        // 椭球表面的梯度方向（基于预测位置，指向外部法线）
                        float3 gradient = new float3(
                            d_pred.x / (MaxDeformDistXZ * MaxDeformDistXZ),
                            d_pred.y / (MaxDeformDistY * MaxDeformDistY),
                            d_pred.z / (MaxDeformDistXZ * MaxDeformDistXZ)
                        );
                        float3 outwardDir = math.normalizesafe(gradient); // 指向椭球外的法线
                        
                        // 【关键】使用相对速度判断是否远离中心
                        // 只消除粒子相对于控制器的"扩张"速度，保留整体移动速度
                        float3 relativeVel = velocity - deformVelocity;
                        float outwardVel = math.dot(relativeVel, outwardDir); // 相对于控制器的远离速度
                        
                        if (outwardVel > 0)
                        {
                            // 消除相对远离椭球的分量，保留切线方向和整体移动
                            velocity -= outwardDir * outwardVel;
                        }
                    }
                }

                p.Position += velocity * PredictStep;
                PsNew[i] = p;
                Velocity[i] = velocity;
            }
        }
        /// <summary>
        /// 简化版 Lambda 计算 Job - 不检查 FreeFrames，用于水珠等简单场景
        /// </summary>
        [BurstCompile]
        public struct ComputeLambdaJob : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int, int2> Lut;
            [ReadOnly] public NativeArray<float3> PosPredict;
            [WriteOnly] public NativeArray<float> Lambda;
            public float TargetDensity;
            public float MinC;

            public void Execute(int i)
            {
                float3 pos = PosPredict[i];
                int3 coord = PBF_Utils.GetCoord(pos);
                float rho = 0.0f;
                float3 grad_i = float3.zero;
                float sigmaGrad = 0.0f;
                
                for (int dz = -1; dz <= 1; ++dz)
                for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + math.int3(dx, dy, dz));
                    if (!Lut.ContainsKey(key)) continue;
                    int2 range = Lut[key];
                    for (int j = range.x; j < range.y; j++)
                    {
                        if (i == j)
                            continue;

                        float3 dir = pos - PosPredict[j];
                        float r2 = math.lengthsq(dir);
                        if (r2 > PBF_Utils.h2 || r2 < PBF_Utils.NeighborR2Epsilon) continue;

                        float r = math.sqrt(r2);
                        rho += PBF_Utils.SmoothingKernelPoly6(r2) / TargetDensity;
                        float3 grad_j = PBF_Utils.DerivativeSpikyPow3(r) / TargetDensity * math.normalizesafe(dir);
                        sigmaGrad += math.lengthsq(grad_j);
                        grad_i += grad_j;
                    }
                }

                sigmaGrad += math.dot(grad_i, grad_i);
                float c = math.max(MinC, rho / TargetDensity - 1.0f);
                Lambda[i] = -c / (sigmaGrad + PBF_Utils.LambdaEpsilon);
            }
        }
        
        /// <summary>
        /// 带 FreeFrames 检查的 Lambda 计算 Job - 用于主体粒子，排除发射粒子与主体的交互
        /// </summary>
        [BurstCompile]
        public struct ComputeLambdaJobWithFreeFrames : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int, int2> Lut;
            [ReadOnly] public NativeArray<float3> PosPredict;
            [ReadOnly] public NativeArray<Particle> Particles; // 用于检查 FreeFrames
            [ReadOnly] public NativeArray<int2> Hashes;
            [WriteOnly] public NativeArray<float> Lambda;
            public float TargetDensity;

            public void Execute(int i)
            {
                float3 pos = PosPredict[i];
                int3 coord = PBF_Utils.GetCoord(pos);
                float rho = 0.0f;
                float3 grad_i = float3.zero;
                float sigmaGrad = 0.0f;
                
                // 当前粒子是否在自由飞行（发射中）
                int originalIdx = Hashes[i].y;
                Particle originalP = Particles[originalIdx];
                bool iAmFree = originalP.FreeFrames > 0;
                bool iAmFadingOut = originalP.Type == ParticleType.FadingOut;
                
                // 【关键】自由飞行粒子完全不参与 PBF 交互，让它们能自由飞出
                if (iAmFree || iAmFadingOut)
                {
                    Lambda[i] = 0;
                    return;
                }
                
                for (int dz = -1; dz <= 1; ++dz)
                for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + math.int3(dx, dy, dz));
                    if (!Lut.ContainsKey(key)) continue;
                    int2 range = Lut[key];
                    for (int j = range.x; j < range.y; j++)
                    {
                        if (i == j)
                            continue;
                        
                        // 跳过自由飞行粒子（它们不参与任何 PBF 交互）
                        int originalJ = Hashes[j].y;
                        if (Particles[originalJ].FreeFrames > 0 || Particles[originalJ].Type == ParticleType.FadingOut) continue;

                        float3 dir = pos - PosPredict[j];
                        float r2 = math.lengthsq(dir);
                        if (r2 > PBF_Utils.h2 || r2 < PBF_Utils.NeighborR2Epsilon) continue;

                        float r = math.sqrt(r2);
                        rho += PBF_Utils.SmoothingKernelPoly6(r2) / TargetDensity;
                        float3 grad_j = PBF_Utils.DerivativeSpikyPow3(r) / TargetDensity * math.normalizesafe(dir);
                        sigmaGrad += math.lengthsq(grad_j);
                        grad_i += grad_j;
                    }
                }

                sigmaGrad += math.dot(grad_i, grad_i);
                float c = math.max(PBF_Utils.MainBodyMinC, rho / TargetDensity - 1.0f);
                Lambda[i] = -c / (sigmaGrad + PBF_Utils.LambdaEpsilon);
            }
        }

        /// <summary>
        /// 简化版位置修正Job - 只输出位置，不需要 Controllers 等参数
        /// 供水珠等简单场景使用
        /// </summary>
        [BurstCompile]
        public struct TopDown : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int, int2> Lut;
            [ReadOnly] public NativeArray<float3> PosPredictIn;
            [WriteOnly] public NativeArray<float3> PosPredictOut;
            [ReadOnly] public NativeArray<float> Lambda;
            public float TargetDensity;
            
            private const float TensileDq = PBF_Utils.TensileDqFactor * PBF_Utils.h;
            private const float TensileK = PBF_Utils.TensileK;
            
            public void Execute(int i)
            {
                float3 pos = PosPredictIn[i];
                float3 dp = float3.zero;
                float W_dq = PBF_Utils.SmoothingKernelPoly6(TensileDq * TensileDq);
                float lambda_i = Lambda[i];
                
                int3 coord = PBF_Utils.GetCoord(pos);
                
                for (int dz = -1; dz <= 1; ++dz)
                for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + math.int3(dx, dy, dz));
                    if (!Lut.ContainsKey(key)) continue;
                    
                    int2 range = Lut[key];
                    for (int j = range.x; j < range.y; j++)
                    {
                        if (i == j) continue;
                        
                        float3 diff = pos - PosPredictIn[j];
                        float r2 = math.lengthsq(diff);
                        if (r2 >= PBF_Utils.h2 || r2 < PBF_Utils.NeighborR2Epsilon) continue;
                        
                        float r = math.sqrt(r2);
                        float3 grad = PBF_Utils.SpikyKernelPow3(r) * math.normalizesafe(diff);
                        
                        // 表面张力修正
                        float corr = PBF_Utils.SmoothingKernelPoly6(r2) / W_dq;
                        corr = -TensileK * corr * corr * corr * corr;
                        
                        dp += (lambda_i + Lambda[j] + corr) * grad;
                    }
                }
                
                PosPredictOut[i] = pos - dp / TargetDensity;
            }
        }

        /// <summary>
        /// 公共凝聚力Job - 适用于水珠等需要向目标点凝聚的场景
        /// 包含：凝聚力、速度阻尼、重力
        /// </summary>
        [BurstCompile]
        public struct ApplyCohesionWithPositionJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> TargetCenters;  // 每个粒子的目标中心
            [ReadOnly] public NativeArray<float3> Positions;      // 当前位置
            [NativeDisableParallelForRestriction]
            public NativeSlice<float3> Velocities;  // 使用 Slice 支持切片操作
            public float CohesionStrength;
            public float CohesionRadius;
            public float VelocityDamping;
            public float3 Gravity;
            public float DeltaTime;
            public float VerticalCohesionScale;  // 向下凝聚力缩放（0.3 = 30%）
            
            public void Execute(int i)
            {
                float3 pos = Positions[i];
                float3 targetCenter = TargetCenters[i];
                float3 vel = Velocities[i];
                
                // 1. 速度阻尼，让粒子趋于静止
                vel *= VelocityDamping;
                
                // 2. 重力
                vel += Gravity * DeltaTime;
                
                // 3. 凝聚力（如果有有效目标）
                if (math.lengthsq(targetCenter) > 0.001f)  // 有效目标
                {
                    float3 toTarget = targetCenter - pos;
                    float dist = math.length(toTarget);
                    
                    if (dist > 0.1f)
                    {
                        // 距离越远，凝聚力越强（但有上限）
                        float factor = math.saturate(dist / CohesionRadius);
                        float3 cohesion = CohesionStrength * DeltaTime * factor * math.normalizesafe(toTarget);
                        
                        // 减弱向下的凝聚力，保留向上的（对抗重力）
                        if (cohesion.y < 0)
                            cohesion.y *= VerticalCohesionScale;
                        
                        vel += cohesion;
                    }
                }
                
                Velocities[i] = vel;
            }
        }

        /// <summary>
        /// 带 FreeFrames 检查的位置修正 Job - 用于主体粒子，排除发射粒子与主体的交互
        /// </summary>
        [BurstCompile]
        public struct ComputeDeltaPosJob : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int, int2> Lut;
            [ReadOnly] public NativeArray<float3> PosPredict;
            [ReadOnly] public NativeArray<float> Lambda;
            [ReadOnly] public NativeArray<int2> Hashes;
            [ReadOnly] public NativeArray<ParticleController> Controllers;
            [ReadOnly] public NativeArray<int> BlobIdToControllerSlot;
            [ReadOnly] public NativeArray<Particle> PsOriginal; // 排序后的粒子数组
            [WriteOnly] public NativeArray<Particle> PsNew;
            [WriteOnly] public NativeArray<float3> ClampDelta; // 【P4】输出钳制位移量，用于速度回算修正
            public float TargetDensity;
            public float MaxDeformDistXZ; // 水平形变上限（模拟坐标）
            public float MaxDeformDistY;  // 垂直形变上限（模拟坐标）
            public float3 MainCenter; // 主体控制器中心
            private const float TensileDq = PBF_Utils.TensileDqFactor * PBF_Utils.h;
            private const float TensileK = PBF_Utils.TensileK;

            public void Execute(int i)
            {
                float3 position = PosPredict[i];
                float3 dp = float3.zero;
                float W_dp = PBF_Utils.SmoothingKernelPoly6(TensileDq * TensileDq);

                float lambda = Lambda[i];
                int3 coord = PBF_Utils.GetCoord(position);
                
                // 当前粒子是否在自由飞行（发射中）
                int originalIdx = Hashes[i].y;
                Particle originalP = PsOriginal[originalIdx];
                bool iAmFree = originalP.FreeFrames > 0;
                bool iAmFadingOut = originalP.Type == ParticleType.FadingOut;
                
                // 【关键】自由飞行粒子完全不参与 PBF 交互，直接输出原位置
                if (iAmFree || iAmFadingOut)
                {
                    PsNew[i] = new Particle
                    {
                        Position = position, // 不修正位置
                        Type = originalP.Type,
                        ControllerSlot = originalP.ControllerSlot,
                        BlobId = originalP.BlobId,
                        SourceId = originalP.SourceId,
                        ClusterId = originalP.ClusterId,
                        FreeFrames = originalP.FreeFrames,
                        FramesOutsideMain = originalP.FramesOutsideMain,
                    };
                    ClampDelta[i] = float3.zero; // 【P4】无钳制
                    return;
                }
                
                for (int dz = -1; dz <= 1; ++dz)
                for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + math.int3(dx, dy, dz));
                    if (!Lut.ContainsKey(key)) continue;
                    int2 range = Lut[key];
                    for (int j = range.x; j < range.y; j++)
                    {
                        if (i == j)
                            continue;
                        
                        // 跳过自由飞行粒子（它们不参与任何 PBF 交互）
                        int originalJ = Hashes[j].y;
                        if (PsOriginal[originalJ].FreeFrames > 0 || PsOriginal[originalJ].Type == ParticleType.FadingOut) continue;

                        float3 dir = position - PosPredict[j];
                        float r2 = math.dot(dir, dir);
                        if (r2 >= PBF_Utils.h2 || r2 < PBF_Utils.NeighborR2Epsilon) continue;

                        float r = math.sqrt(r2);
                        float3 w_spiky = PBF_Utils.SpikyKernelPow3(r) * math.normalizesafe(dir);
                        float corr = PBF_Utils.SmoothingKernelPoly6(r2) / W_dp;
                        float s_corr = -TensileK * corr * corr * corr * corr;
                        dp += (lambda + Lambda[j] + s_corr) * w_spiky;
                    }
                }

                dp /= TargetDensity;

                float3 newPos = position - dp;
                float3 clampDelta = float3.zero; // 【P4】记录钳制位移量
                
                // 【形变上限-椭球约束】主体粒子超出椭球边界时，将位置拉回边界
                if (originalP.Type == ParticleType.MainBody && MaxDeformDistXZ > 0 && MaxDeformDistY > 0)
                {
                    float3 d = newPos - MainCenter; // 从中心指向粒子
                    // 椭球归一化距离: r = sqrt((dx/a)^2 + (dy/b)^2 + (dz/a)^2)
                    float3 normalized = new float3(d.x / MaxDeformDistXZ, d.y / MaxDeformDistY, d.z / MaxDeformDistXZ);
                    float r = math.length(normalized);
                    
                    if (r > 1f)
                    {
                        // 将位置拉回到椭球边界: p = center + d / r
                        float3 clampedPos = MainCenter + d / r;
                        clampDelta = clampedPos - newPos; // 【P4】钳制造成的位移
                        newPos = clampedPos;
                    }
                }

                ClampDelta[i] = clampDelta; // 【P4】输出钳制位移量
                PsNew[i] = new Particle
                {
                    Position = newPos,
                    Type = originalP.Type,
                    ControllerSlot = originalP.ControllerSlot,
                    BlobId = originalP.BlobId,
                    SourceId = originalP.SourceId,
                    ClusterId = originalP.ClusterId,
                    FreeFrames = originalP.FreeFrames,
                    FramesOutsideMain = originalP.FramesOutsideMain,
                };
            }
        }

        /// <summary>
        /// 简化版位置修正 Job - 用于水珠等独立粒子系统，不检查 FreeFrames
        /// </summary>
        [BurstCompile]
        public struct ComputeDeltaPosJobSimple : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int, int2> Lut;
            [ReadOnly] public NativeArray<float3> PosPredictIn;
            [WriteOnly] public NativeArray<float3> PosPredictOut;
            [ReadOnly] public NativeArray<float> Lambda;
            public float TargetDensity;
            private const float TensileDq = PBF_Utils.TensileDqFactor * PBF_Utils.h;
            public float TensileK;

            public void Execute(int i)
            {
                float3 position = PosPredictIn[i];
                float3 dp = float3.zero;
                float W_dp = PBF_Utils.SmoothingKernelPoly6(TensileDq * TensileDq);

                float lambda = Lambda[i];
                int3 coord = PBF_Utils.GetCoord(position);

                for (int dz = -1; dz <= 1; ++dz)
                for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + math.int3(dx, dy, dz));
                    if (!Lut.ContainsKey(key)) continue;
                    int2 range = Lut[key];
                    for (int j = range.x; j < range.y; j++)
                    {
                        if (i == j) continue;

                        float3 dir = position - PosPredictIn[j];
                        float r2 = math.dot(dir, dir);
                        if (r2 >= PBF_Utils.h2 || r2 < PBF_Utils.NeighborR2Epsilon) continue;

                        float r = math.sqrt(r2);
                        float3 w_spiky = PBF_Utils.SpikyKernelPow3(r) * math.normalizesafe(dir);
                        float corr = PBF_Utils.SmoothingKernelPoly6(r2) / W_dp;
                        float s_corr = -TensileK * corr * corr * corr * corr;
                        dp += (lambda + Lambda[j] + s_corr) * w_spiky;
                    }
                }

                dp /= TargetDensity;
                PosPredictOut[i] = position - dp;
            }
        }

        [BurstCompile]
        public struct UpdateJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<MyBoxCollider> Colliders;
            [ReadOnly] public NativeList<ParticleController> Controllers; // 【新增】控制器数组，用于获取每个粒子对应的 GroundY
            [ReadOnly] public NativeArray<int> BlobIdToControllerSlot;
            public int ColliderCount;
            public int UseStaticSdf;
            public WorldSdfRuntime.Volume StaticSdf;
            public int DisableStaticColliderFallback;
            public float ParticleRadiusSim;
            public float StaticFriction;
            [ReadOnly] public NativeArray<float> TerrainHeights01;
            public int TerrainResX;
            public int TerrainResZ;
            public float3 TerrainOriginSim;
            public float3 TerrainSizeSim;
            [ReadOnly] public NativeArray<float3> PosOld;
            [ReadOnly] public NativeArray<float3> ClampDelta; // 【P4】ComputeDeltaPosJob 输出的钳制位移量
            public NativeArray<Particle> Ps;
            public NativeArray<float3> Velocity;
            public float DeltaTime;
            public float MaxVelocity;
            public float FallbackGroundY; // 后备地面高度（当控制器无效时使用）
            public float MaxDeformDistXZ; // 水平形变上限（模拟坐标）
            public float MaxDeformDistY;  // 垂直形变上限（模拟坐标）
            public float3 MainCenter; // 主体控制器中心（模拟坐标）
            public float3 MainVelocity; // 主体控制器速度，用于计算相对速度
            public bool EnableCollisionDeformLimit; // 是否在碰撞后应用形变上限
            public bool EnableP4VelocityConsistency; // 【P4开关】是否启用速度一致化（排除钳制位移）

            private bool TrySampleTerrain(float2 xzSim, out float heightSim, out float3 normal)
            {
                heightSim = 0f;
                normal = new float3(0, 1, 0);

                if (!TerrainHeights01.IsCreated || TerrainResX < 2 || TerrainResZ < 2)
                    return false;

                float sizeX = TerrainSizeSim.x;
                float sizeY = TerrainSizeSim.y;
                float sizeZ = TerrainSizeSim.z;

                if (sizeX <= 1e-6f || sizeZ <= 1e-6f)
                    return false;

                float2 rel = xzSim - TerrainOriginSim.xz;
                if (rel.x < 0f || rel.y < 0f || rel.x > sizeX || rel.y > sizeZ)
                    return false;

                float u = rel.x / sizeX;
                float v = rel.y / sizeZ;

                float fx = u * (TerrainResX - 1);
                float fz = v * (TerrainResZ - 1);

                int x0 = (int)math.floor(fx);
                int z0 = (int)math.floor(fz);
                x0 = math.clamp(x0, 0, TerrainResX - 2);
                z0 = math.clamp(z0, 0, TerrainResZ - 2);
                float tx = fx - x0;
                float tz = fz - z0;

                int idx00 = z0 * TerrainResX + x0;
                float h00 = TerrainHeights01[idx00];
                float h10 = TerrainHeights01[idx00 + 1];
                float h01 = TerrainHeights01[idx00 + TerrainResX];
                float h11 = TerrainHeights01[idx00 + TerrainResX + 1];

                float h0 = math.lerp(h00, h10, tx);
                float h1 = math.lerp(h01, h11, tx);
                float h = math.lerp(h0, h1, tz);
                heightSim = TerrainOriginSim.y + h * sizeY;

                int xi = math.clamp((int)math.round(fx), 1, TerrainResX - 2);
                int zi = math.clamp((int)math.round(fz), 1, TerrainResZ - 2);

                int idxC = zi * TerrainResX + xi;
                float hL = TerrainHeights01[idxC - 1];
                float hR = TerrainHeights01[idxC + 1];
                float hD = TerrainHeights01[idxC - TerrainResX];
                float hU = TerrainHeights01[idxC + TerrainResX];

                float dh01dx = (hR - hL) * 0.5f;
                float dh01dz = (hU - hD) * 0.5f;

                float cellX = sizeX / (TerrainResX - 1);
                float cellZ = sizeZ / (TerrainResZ - 1);

                float dhdx = (dh01dx * sizeY) / math.max(cellX, 1e-6f);
                float dhdz = (dh01dz * sizeY) / math.max(cellZ, 1e-6f);

                normal = math.normalizesafe(new float3(-dhdx, 1f, -dhdz), new float3(0, 1, 0));
                if (normal.y < 0.55f)
                    normal = new float3(0, 1, 0);

                return true;
            }

            public void Execute(int i)
            {
                Particle p = Ps[i];
                float3 posOld = PosOld[i];

                if (p.Type == ParticleType.Dormant || p.Type == ParticleType.FadingOut)
                {
                    Velocity[i] = float3.zero;
                    Ps[i] = p;
                    return;
                }
                
                // 所有粒子统一使用模拟坐标（内部坐标）
                float3 simPos = p.Position;
                float3 simPosBeforeCollision = simPos;

                bool hadSdfCollision = false;
                float3 sdfNormal = new float3(0, 1, 0);
                bool sdfInBounds = false;
                float sdfDistSim = float.MaxValue;
                bool sdfValid = false;
                bool sdfSkipStaticColliders = false;
                if (UseStaticSdf != 0 && StaticSdf.IsCreated)
                {
                    float voxel = math.max(StaticSdf.VoxelSizeSim, 1e-3f);
                    float contactSkin = math.min(voxel * 0.25f, ParticleRadiusSim * 0.25f);
                    float contactRadius = ParticleRadiusSim + contactSkin;
                    float contactSlop = math.min(voxel * 0.05f, ParticleRadiusSim * 0.1f);

                    float3 minSim = StaticSdf.AabbMinSim;
                    float3 maxSim = StaticSdf.AabbMaxSim;
                    float boundsMargin = contactRadius + voxel * 2f;
                    sdfInBounds = !(simPos.x < (minSim.x - boundsMargin) || simPos.y < (minSim.y - boundsMargin) || simPos.z < (minSim.z - boundsMargin) ||
                                   simPos.x > (maxSim.x + boundsMargin) || simPos.y > (maxSim.y + boundsMargin) || simPos.z > (maxSim.z + boundsMargin));
                    float d = StaticSdf.SampleDistanceDenseWithLocalAndIndices(simPos, out float3 sdfLocal, out int3 sdfI0, out int3 sdfI1, out float3 sdfF);
                    sdfDistSim = d;
                    sdfValid = d < (StaticSdf.MaxDistanceSim - 1e-5f);
                    float skipMargin = voxel * 0.5f;
                    sdfSkipStaticColliders = sdfInBounds && sdfValid && (d > (contactRadius + skipMargin));
                    if (sdfValid && d < contactRadius)
                    {
                        hadSdfCollision = true;
                        float maxStep = voxel * 1.25f;
                        for (int it = 0; it < 4 && d < contactRadius; it++)
                        {
                            float3 n = StaticSdf.SampleNormalForwardDenseLocalFromIndices(sdfLocal, sdfI0, sdfI1, sdfF, d);
                            float stepOut = contactRadius - d;
                            if (stepOut <= contactSlop)
                            {
                                sdfNormal = math.normalizesafe(n, new float3(0, 1, 0));
                                break;
                            }

                            stepOut = math.min(stepOut, maxStep);
                            simPos += n * stepOut;
                            sdfNormal = math.normalizesafe(n, new float3(0, 1, 0));

                            d = StaticSdf.SampleDistanceDenseWithLocalAndIndices(simPos, out sdfLocal, out sdfI0, out sdfI1, out sdfF);
                        }
                    }
                }
                
                // 碰撞检测（Colliders 也是模拟坐标，包含地面碰撞体）
                // 记录碰撞轴，用于后续速度衰减
                bool3 collisionAxes = false;
                bool hadObbCollision = false;
                float3 obbNormal = float3.zero;
                float obbFriction = 0f;
                float3 obbSurfaceVelocity = float3.zero;

                for (int c = 0; c < ColliderCount; c++)
                {
                    MyBoxCollider box = Colliders[c];
                    if (box.IsDynamic == 0 && UseStaticSdf != 0 && StaticSdf.IsCreated && ((DisableStaticColliderFallback != 0 && sdfInBounds && sdfValid) || sdfSkipStaticColliders || hadSdfCollision))
                        continue;

                     switch (box.Shape)
                     {
                         case ColliderShapes.Obb:
                         {
                             quaternion invRot = math.conjugate(box.Rotation);
                             float3 local = math.mul(invRot, (simPos - box.Center));
                             float3 vec = math.abs(local);
                             if (math.all(vec < box.Extent))
                             {
                                 float3 remain = box.Extent - vec;
                                 int axis = 0;
                                 if (remain.y < remain[axis]) axis = 1;
                                 if (remain.z < remain[axis]) axis = 2;

                                 float sign = math.sign(local[axis]);
                                 if (sign == 0f) sign = 1f;
                                 local[axis] = sign * box.Extent[axis];
                                 simPos = box.Center + math.mul(box.Rotation, local);

                                 float3 nLocal = float3.zero;
                                 nLocal[axis] = sign;
                                 float3 nWorld = math.mul(box.Rotation, nLocal);

                                 hadObbCollision = true;
                                 obbNormal = nWorld;
                                 obbFriction = math.max(obbFriction, box.Friction);
                                 obbSurfaceVelocity = box.Velocity;
                             }
                             break;
                         }
                         case ColliderShapes.Capsule:
                         {
                             ColliderShapeUtils.ComputeCapsule(in box, out float3 a, out float3 b, out float r);
                             float r2 = r * r;
                             float d2 = simPos.udSqrSegment(a, b);
                             if (d2 < r2)
                             {
                                 float3 closest = ColliderShapeUtils.ClosestPointOnSegment(simPos, a, b);
                                 float3 d = simPos - closest;

                                 float3 n;
                                 if (d2 < PBF_Utils.NeighborR2Epsilon)
                                 {
                                     float3 fallback = math.mul(box.Rotation, new float3(0, 1, 0));
                                     float fb2 = math.lengthsq(fallback);
                                     n = fb2 < PBF_Utils.NeighborR2Epsilon ? new float3(0, 1, 0) : fallback * math.rsqrt(fb2);
                                 }
                                 else
                                 {
                                    n = d * math.rsqrt(d2);
                                 }

                                 simPos = closest + n * r;
                                 hadObbCollision = true;
                                 obbNormal = n;
                                 obbFriction = math.max(obbFriction, box.Friction);
                                 obbSurfaceVelocity = box.Velocity;
                             }
                             break;
                         }
                         default:
                         {
                             float3 dir = simPos - box.Center;
                             float3 vec = math.abs(dir);
                             if (math.all(vec < box.Extent))
                             {
                                 float3 remain = box.Extent - vec;
                                 int axis = 0;
                                 if (remain.y < remain[axis]) axis = 1;
                                 if (remain.z < remain[axis]) axis = 2;
                                 simPos[axis] = box.Center[axis] + math.sign(dir[axis]) * box.Extent[axis];
                                 collisionAxes[axis] = true;
                             }
                             break;
                         }
                     }

                }

                bool terrainCovers = false;
                bool hadTerrainCollision = false;
                float3 terrainNormal = new float3(0, 1, 0);
                float terrainYSimFinal = 0f;
                float planeDistFinal = 0f;

                for (int it = 0; it < 3; it++)
                {
                    if (!TrySampleTerrain(simPos.xz, out terrainYSimFinal, out terrainNormal))
                        break;

                    if (!terrainCovers)
                        terrainCovers = true;

                    float3 surfacePoint = new float3(simPos.x, terrainYSimFinal, simPos.z);
                    float3 groundPoint = surfacePoint + terrainNormal * ParticleRadiusSim;
                    float pd = math.dot(terrainNormal, simPos - groundPoint);
                    planeDistFinal = pd;

                    if (pd >= 0f)
                        break;

                    simPos -= terrainNormal * pd;
                    collisionAxes.y = true;
                    hadTerrainCollision = true;
                }

                if (terrainCovers)
                {
                    float minY = terrainYSimFinal + ParticleRadiusSim;
                    if (simPos.y < minY)
                    {
                        simPos.y = minY;
                        collisionAxes.y = true;
                        hadTerrainCollision = true;
                    }
                }
                
                bool allowGroundClamp = PBF_Utils.AllowGroundClamp(UseStaticSdf, DisableStaticColliderFallback);
                if (!terrainCovers && allowGroundClamp && p.Type == ParticleType.MainBody)
                {
                    float groundY = FallbackGroundY;
                    float3 fallbackGroundPoint = new float3(simPos.x, groundY, simPos.z);
                    float3 groundNormal = new float3(0, 1, 0);
                    if (Controllers.Length > 0)
                    {
                        var ctrl0 = Controllers[0];
                        groundY = ctrl0.GroundY;
                        fallbackGroundPoint = ctrl0.GroundPoint;
                        groundNormal = ctrl0.GroundNormal;
                    }

                    if (PBF_Utils.ClampToGroundPlane(ref simPos, groundY, fallbackGroundPoint, groundNormal))
                        collisionAxes.y = true;
                }
                
                // 更新位置（模拟坐标）
                p.Position = simPos;
                
                // 【关键】自由飞行粒子完全跳过速度重算和碰撞衰减，保持原速度
                // 否则发射速度会被位置差重算或碰撞衰减削减掉
                if (p.FreeFrames > 0)
                {
                    bool hadCollision = hadSdfCollision || hadObbCollision || collisionAxes.x || collisionAxes.y || collisionAxes.z;
                    if (hadCollision)
                    {
                        p.FreeFrames = 0;
                        if (p.Type == ParticleType.Emitted)
                            p.Type = ParticleType.Separated;
                    }
                    else
                    {
                        // 保持原速度不变，不应用任何衰减
                        // 碰撞只修正位置（已在上面完成），不衰减速度
                        Velocity[i] = Velocity[i]; // 保持原速度
                        Ps[i] = p;
                        return;
                    }
                }
                
                // 【P4】速度重算：排除钳制位移，只用物理位移
                // simPos = posOld + 物理位移 + 钳制位移
                // 我们只要物理位移：(simPos - clampDelta) - posOld
                float3 clampDelta = EnableP4VelocityConsistency ? ClampDelta[i] : float3.zero;
                float3 collisionDelta = simPos - simPosBeforeCollision;
                float3 velCalc = (simPos - clampDelta - collisionDelta - posOld) / DeltaTime;
                
                // 【关键修复】碰撞时衰减碰撞轴方向的速度，防止落地炸开
                const float normalCollisionDamping = 0.1f;
                if (collisionAxes.x) velCalc.x *= normalCollisionDamping;
                if (collisionAxes.y) velCalc.y *= normalCollisionDamping;
                if (collisionAxes.z) velCalc.z *= normalCollisionDamping;

                if (hadSdfCollision)
                {
                    float vnInto = math.dot(velCalc, sdfNormal);
                    if (vnInto < 0f)
                        velCalc -= sdfNormal * vnInto;

                    float vn = math.dot(velCalc, sdfNormal);
                    float3 vN = sdfNormal * vn;
                    float3 vT = velCalc - vN;
                    vN *= normalCollisionDamping;
                    vT *= math.max(0f, 1f - StaticFriction);
                    velCalc = vN + vT;
                }

                if (hadTerrainCollision)
                {
                    float vnInto = math.dot(velCalc, terrainNormal);
                    if (vnInto < 0f)
                        velCalc -= terrainNormal * vnInto;

                    float vn = math.dot(velCalc, terrainNormal);
                    float3 vN = terrainNormal * vn;
                    float3 vT = velCalc - vN;
                    vN *= normalCollisionDamping;
                    vT *= math.max(0f, 1f - StaticFriction);
                    velCalc = vN + vT;
                }

                if (hadObbCollision)
                {
                    float3 vRel = velCalc - obbSurfaceVelocity;
                    float vnInto = math.dot(vRel, obbNormal);
                    if (vnInto < 0f)
                        vRel -= obbNormal * vnInto;

                    float vn = math.dot(vRel, obbNormal);
                    float3 vN = obbNormal * vn;
                    float3 vT = vRel - vN;
                    vN *= normalCollisionDamping;
                    vT *= math.max(0f, 1f - math.saturate(obbFriction));
                    velCalc = (vN + vT) + obbSurfaceVelocity;
                }
                
                // 【形变上限-椭球约束】碰撞后应用椭球形变上限，防止落地时过度扩展
                // 【关键】使用相对速度，避免影响整体移动
                if (EnableCollisionDeformLimit && p.Type == ParticleType.MainBody && MaxDeformDistXZ > 0 && MaxDeformDistY > 0)
                {
                    float3 d = simPos - MainCenter; // 从中心指向粒子
                    // 椭球归一化距离: r = sqrt((dx/a)^2 + (dy/b)^2 + (dz/a)^2)
                    float3 normalized = new float3(d.x / MaxDeformDistXZ, d.y / MaxDeformDistY, d.z / MaxDeformDistXZ);
                    float r = math.length(normalized);
                    
                    if (r > 1f)
                    {
                        // 椭球表面的梯度方向（指向外部法线）
                        float3 gradient = new float3(
                            d.x / (MaxDeformDistXZ * MaxDeformDistXZ),
                            d.y / (MaxDeformDistY * MaxDeformDistY),
                            d.z / (MaxDeformDistXZ * MaxDeformDistXZ)
                        );
                        float3 outwardDir = math.normalizesafe(gradient); // 指向椭球外的法线
                        
                        // 【关键修复】使用相对速度判断是否远离中心
                        float3 relativeVel = velCalc - MainVelocity;
                        float outwardVel = math.dot(relativeVel, outwardDir); // 相对于控制器的远离速度
                        
                        if (outwardVel > 0)
                        {
                            // 消除相对远离椭球的分量，保留整体移动
                            velCalc -= outwardDir * outwardVel;
                        }
                    }
                }
                
                // 【关键】自由飞行粒子不受速度限制，否则发射速度会被截断
                if (p.FreeFrames == 0)
                {
                    velCalc = math.min(MaxVelocity, math.length(velCalc)) * math.normalizesafe(velCalc);
                }
                Velocity[i] = velCalc;
                Ps[i] = p;
            }
        }

        /// <summary>
        /// 把排序后的粒子数据映射回原始索引
        /// 排序是为了空间哈希加速邻域查询，但 Control() 等逻辑需要原始索引
        /// </summary>
        [BurstCompile]
        public struct UnshuffleJob : IJob
        {
            [ReadOnly] public NativeArray<int2> Hashes;
            [ReadOnly] public NativeArray<Particle> PsUpdated;  // 从UpdateJob更新后的数据
            [ReadOnly] public NativeArray<float3> VelocitySorted;
            [WriteOnly] public NativeArray<Particle> PsOriginal;
            [WriteOnly] public NativeArray<float3> VelocityOriginal;

            public void Execute()
            {
                for (int i = 0; i < Hashes.Length; i++)
                {
                    int originalIndex = Hashes[i].y;
                    var updatedParticle = PsUpdated[i];  // 使用UpdateJob更新后的粒子
                    
                    // 保护SourceId：只有场景水珠才能有非负SourceId
                    if (updatedParticle.SourceId >= 0 && updatedParticle.Type != ParticleType.SceneDroplet)
                    {
                        updatedParticle.SourceId = -1;
                    }
                    
                    PsOriginal[originalIndex] = updatedParticle;
                    VelocityOriginal[originalIndex] = VelocitySorted[i];
                }
            }
        }

        [BurstCompile]
        public struct ApplyViscosityJob : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int, int2> Lut;
            [ReadOnly] public NativeArray<float3> PosPredict;
            [ReadOnly] public NativeArray<float3> VelocityR;
            [ReadOnly] public NativeArray<Particle> Particles; // 原始索引的粒子数组
            [ReadOnly] public NativeArray<int2> Hashes; // 用于获取原始索引
            [WriteOnly] public NativeArray<float3> VelocityW;
            public float ViscosityStrength;
            public float TargetDensity;
            public float DeltaTime;

            public void Execute(int i)
            {
                float3 vel = VelocityR[i];
                
                // 【关键修复】使用 Hashes 获取原始索引，因为 Particles 是原始索引数组
                int originalIdx = Hashes[i].y;

                if (Particles[originalIdx].Type == ParticleType.FadingOut)
                {
                    VelocityW[i] = float3.zero;
                    return;
                }

                // 【关键】自由飞行粒子不参与粘性计算，保持原速度
                if (Particles[originalIdx].FreeFrames > 0)
                {
                    VelocityW[i] = vel;
                    return;
                }
                
                float3 pos = PosPredict[i];
                int3 coord = PBF_Utils.GetCoord(pos);
                float3 viscosityForce = float3.zero;
                for (int dz = -1; dz <= 1; ++dz)
                for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + math.int3(dx, dy, dz));
                    if (!Lut.ContainsKey(key)) continue;
                    int2 range = Lut[key];
                    for (int j = range.x; j < range.y; j++)
                    {
                        if (i == j)
                            continue;
                        
                        // 跳过自由飞行粒子（使用原始索引）
                        int originalJ = Hashes[j].y;
                        if (Particles[originalJ].FreeFrames > 0 || Particles[originalJ].Type == ParticleType.FadingOut) continue;

                        float3 dir = pos - PosPredict[j];
                        float r2 = math.lengthsq(dir);
                        if (r2 > PBF_Utils.h2) continue;

                        viscosityForce += (VelocityR[j] - vel) * PBF_Utils.SmoothingKernelPoly6(r2);
                    }
                }

                VelocityW[i] = vel + viscosityForce / TargetDensity * ViscosityStrength * DeltaTime;
            }
        }
        
        /// <summary>
        /// 简化版粘性 Job - 用于水珠等独立粒子系统，不检查 FreeFrames
        /// </summary>
        [BurstCompile]
        public struct ApplyViscosityJobSimple : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int, int2> Lut;
            [ReadOnly] public NativeArray<float3> PosPredict;
            [ReadOnly] public NativeArray<float3> VelocityR;
            [WriteOnly] public NativeArray<float3> VelocityW;
            public float ViscosityStrength;
            public float TargetDensity;
            public float DeltaTime;

            public void Execute(int i)
            {
                float3 pos = PosPredict[i];
                int3 coord = PBF_Utils.GetCoord(pos);
                float3 viscosityForce = float3.zero;
                float3 vel = VelocityR[i];
                for (int dz = -1; dz <= 1; ++dz)
                for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + math.int3(dx, dy, dz));
                    if (!Lut.ContainsKey(key)) continue;
                    int2 range = Lut[key];
                    for (int j = range.x; j < range.y; j++)
                    {
                        if (i == j)
                            continue;

                        float3 dir = pos - PosPredict[j];
                        float r2 = math.lengthsq(dir);
                        if (r2 > PBF_Utils.h2) continue;

                        viscosityForce += (VelocityR[j] - vel) * PBF_Utils.SmoothingKernelPoly6(r2);
                    }
                }

                VelocityW[i] = vel + viscosityForce / TargetDensity * ViscosityStrength * DeltaTime;
            }
        }
    }
}
