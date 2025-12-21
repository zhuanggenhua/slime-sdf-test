using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Slime
{
    public struct Particle 
    {
        public float3 Position;
        public int ID;           // CCA临时ID，仅用于当前帧控制器匹配
        public int BodyState;    // 持久状态：0=主体, 1=分离, 2=休眠
        public int SourceId;     // 水滴源ID：-1=主体/无源, >=0=场景水滴源索引
    }
    
    public struct ParticleController
    {
        public float3 Center;
        public float Radius;
        public float3 Velocity;
        public float Concentration;
    }
    
    /// <summary>
    /// 碰撞体类型常量
    /// </summary>
    public static class ColliderTypes
    {
        public const int Ground = 0;     // 普通地面/障碍物
        public const int Climbable = 1;  // 可攀爬表面
        public const int Bouncy = 2;     // 弹性表面
        public const int Sticky = 3;     // 粘性表面
    }
    
    public struct MyBoxCollider
    {
        public float3 Center;
        public float3 Extent;
        public int Type;       // 碰撞体类型（见 ColliderTypes）
        public float Friction; // 表面摩擦力（0-1）
    }

    public static class PBF_Utils
    {
        public const int Width = 16;
        public const int Num = Width * Width * Width / 2;
        public const int BubblesCount = 2048;
        public const int GridSize = 4 * 4 * 4;
        public const int GridNum = 768;

        public const float h = 1.0f;
        public const float h2 = h * h;
        public const float CellSize = 0.5f * h;
        public const float Scale = 0.1f;
        public const float InvScale = 10f;
    
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
            [ReadOnly] public NativeList<ParticleController> Controllers;
            [ReadOnly] public NativeArray<ParticleController> SourceControllers;
            [ReadOnly] public NativeArray<Particle> Ps;
            [WriteOnly] public NativeArray<Particle> PsNew;
            public NativeArray<float3> Velocity;
            public float3 Gravity;
            public float DeltaTime;
            public float PredictStep;
            public float VelocityDamping;
            public float VerticalOffset;

            public void Execute(int i)
            {
                Particle p = Ps[i];

                var velocity = Velocity[i] * VelocityDamping + Gravity * DeltaTime;
                
                // 根据 BodyState 选择控制器：
                // - BodyState=0（主体）：始终使用 Controllers[0]
                // - BodyState=1（分离）：使用分离组 ID 对应的控制器（ID>=1）
                //   但如果 ID=0 说明刚发射还没被分配，跳过控制器让它自由飞行
                int controllerIndex = (p.BodyState == 0) ? 0 : p.ID;
                
                // 分离粒子（BodyState=1）不使用主体控制器（ID=0 -> Controllers[0]）
                // 只有 ID>=1 时才使用对应的分离组控制器
                if (p.BodyState == 1 && controllerIndex == 0)
                {
                    // 分离粒子 ID=0（刚发射或CCA分配的0），不受主体控制器影响，自由飞行
                }
                else if (p.BodyState == 1 && p.SourceId >= 0)
                {
                    // 场景水珠使用 SourceControllers（紧凑索引，不再用 100+SourceId）
                    if (p.SourceId < SourceControllers.Length)
                    {
                        ParticleController cl = SourceControllers[p.SourceId];
                        if (cl.Concentration > 0) // 有效控制器
                        {
                            float3 toCenter = cl.Center - p.Position;
                            float len = math.length(toCenter);
                            
                            // 场景水珠只有聚集力，没有速度跟随
                            if (len > 0.1f)
                            {
                                velocity += cl.Concentration * DeltaTime * math.min(1, len) *
                                            math.normalizesafe(toCenter);
                            }
                        }
                    }
                }
                else if (controllerIndex >= 0 && controllerIndex < Controllers.Length)
                {
                    ParticleController cl = Controllers[controllerIndex];
                    float verticalOffsetFactor = (p.BodyState == 0) ? VerticalOffset : 0f;
                    float3 toCenter = cl.Center + new float3(0, cl.Radius * verticalOffsetFactor, 0) - p.Position;
                    float len = math.length(toCenter);

                    if (len < cl.Radius)
                    {
                        // 方案B：速度跟随只影响水平分量(xz)，不影响竖直分量(y)
                        // 这样分离团既能被召回（水平朝主体飞），又能正常下落（重力不被抵消）
                        float lerpFactor = math.lerp(1, len * 0.1f, cl.Concentration * 0.002f);
                        if (p.BodyState == 1)
                        {
                            float originalY = velocity.y;
                            velocity = math.lerp(cl.Velocity, velocity, lerpFactor);
                            velocity.y = originalY; // 恢复竖直分量，让重力正常作用
                        }
                        else
                        {
                            velocity = math.lerp(cl.Velocity, velocity, lerpFactor);
                        }
                        
                        // 向心力只作用于主体粒子，分离粒子不需要向心力（它们通过 PBF 约束保持凝聚）
                        if (p.BodyState == 0)
                        {
                            float3 attraction = cl.Concentration * DeltaTime * math.min(1, len) *
                                                math.normalizesafe(toCenter);
                            velocity += attraction;
                        }
                    }
                }

                p.Position += velocity * PredictStep;
                PsNew[i] = p;
                Velocity[i] = velocity;
            }
        }

        [BurstCompile]
        public struct ComputeLambdaJob : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int, int2> Lut;
            [ReadOnly] public NativeArray<float3> PosPredict;
            [WriteOnly] public NativeArray<float> Lambda;
            public float TargetDensity;

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
                        if (r2 > PBF_Utils.h2) continue;

                        float r = math.sqrt(r2);
                        rho += PBF_Utils.SmoothingKernelPoly6(r2) / TargetDensity;
                        float3 grad_j = PBF_Utils.DerivativeSpikyPow3(r) / TargetDensity * math.normalize(dir);
                        sigmaGrad += math.lengthsq(grad_j);
                        grad_i += grad_j;
                    }
                }

                sigmaGrad += math.dot(grad_i, grad_i);
                float c = math.max(-0.2f, rho / TargetDensity - 1.0f);
                Lambda[i] = -c / (sigmaGrad + 1e-5f);
            }
        }

        [BurstCompile]
        public struct ComputeDeltaPosJob : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int, int2> Lut;
            [ReadOnly] public NativeArray<float3> PosPredict;
            [ReadOnly] public NativeArray<float> Lambda;
            [ReadOnly] public NativeArray<int2> Hashes;
            [ReadOnly] public NativeArray<Particle> PsOriginal;
            [WriteOnly] public NativeArray<Particle> PsNew;
            public float TargetDensity;
            private const float TensileDq = 0.25f * PBF_Utils.h;
            private const float TensileK = 0.1f;

            public void Execute(int i)
            {
                float3 position = PosPredict[i];
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
                        if (i == j)
                            continue;

                        float3 dir = position - PosPredict[j];
                        float r2 = math.dot(dir, dir);
                        if (r2 >= PBF_Utils.h2) continue;

                        float r = math.sqrt(r2);
                        float3 w_spiky = PBF_Utils.SpikyKernelPow3(r) * math.normalize(dir);
                        float corr = PBF_Utils.SmoothingKernelPoly6(r2) / W_dp;
                        float s_corr = -TensileK * corr * corr * corr * corr;
                        dp += (lambda + Lambda[j] + s_corr) * w_spiky;
                    }
                }

                dp /= TargetDensity;

                int originalIndex = Hashes[i].y;
                Particle originalP = PsOriginal[originalIndex];

                PsNew[i] = new Particle
                {
                    Position = position - dp,
                    ID = originalP.ID,
                    BodyState = originalP.BodyState,
                    SourceId = originalP.SourceId,
                };
            }
        }

        [BurstCompile]
        public struct UpdateJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<MyBoxCollider> Colliders;
            public int ColliderCount; // 实际有效碰撞体数量
            [ReadOnly] public NativeArray<float3> PosOld;
            [WriteOnly] public NativeArray<float3> Velocity;
            public NativeArray<Particle> Ps;
            public float MaxVelocity;
            public float DeltaTime;
            public float MinGroundY; // 动态地面高度（基于控制器位置）

            public void Execute(int i)
            {
                Particle p = Ps[i];
                float3 posOld = PosOld[i];
                
                // 动态地面限制：防止粒子掉到控制器以下太远
                p.Position.y = math.max(MinGroundY, p.Position.y);
                
                // 只遍历有效的碰撞体（近场缓存）
                for (int c = 0; c < ColliderCount; c++)
                {
                    MyBoxCollider box = Colliders[c];
                    float3 dir = p.Position - box.Center;
                    float3 vec = math.abs(dir);
                    
                    if (math.all(vec < box.Extent))
                    {
                        float3 remain = box.Extent - vec;
                        int axis = 0;
                        if (remain.y < remain[axis]) axis = 1;
                        if (remain.z < remain[axis]) axis = 2;
                        
                        // 推出碰撞体
                        p.Position[axis] = box.Center[axis] + math.sign(dir[axis]) * box.Extent[axis];
                    }
                }
                
                float3 vel = (p.Position - posOld) / DeltaTime;
                Velocity[i] = math.min(MaxVelocity, math.length(vel)) * math.normalizesafe(vel);
                Ps[i] = p;
            }
        }

        [BurstCompile]
        public struct ApplyViscosityJob : IJobParallelFor
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
