using Unity.Mathematics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using UnityEngine;
using System.Diagnostics;

namespace Revive.Slime
{
    /// <summary>
    /// PBF流体物理系统 - 完整封装缓冲区管理和流体算法
    /// 主体史莱姆和水珠都可以使用，各自保留特殊逻辑
    /// </summary>
    public class PBFSystem : System.IDisposable
    {
        // ========== 配置 ==========
        public struct Config
        {
            public int maxParticles;
            public float targetDensity;
            public int solverIterations;
            public float damping;
            
            public static Config MainBody => new Config
            {
                maxParticles = 8192,
                targetDensity = 1.5f,
                solverIterations = 3,
                damping = 0.99f
            };
            
            public static Config Droplet => new Config
            {
                maxParticles = 8192,
                targetDensity = 1.5f,  // 与主体一致
                solverIterations = 3,  // 与主体一致
                damping = 0.99f  // 与主体一致
            };
        }
        
        // ========== 缓冲区（完全封装，不暴露给外部） ==========
        private NativeHashMap<int, int2> _lut;          // 邻域查询表
        private NativeArray<int2> _hashes;              // 哈希值
        private NativeArray<float3> _posPredict;        // 预测位置
        private NativeArray<float3> _posPredictTemp;    // 双缓冲
        private NativeArray<float3> _posOld;            // 原始位置（用于速度计算）
        private NativeArray<float> _lambda;             // 密度约束
        private NativeArray<Particle> _particlesTemp;   // 临时粒子缓冲
        private NativeArray<float3> _velocityTemp;      // 速度临时缓冲（用于粘性输入）
        private NativeArray<float3> _velocityTemp2;     // 速度临时缓冲（用于粘性输出）
        
        private readonly Config _config;
        private bool _disposed;
        
        public Config Configuration => _config;
        
        // ========== 暴露必要的缓冲区供主体史莱姆特殊逻辑使用 ==========
        public NativeHashMap<int, int2> Lut => _lut;
        public NativeArray<int2> Hashes => _hashes;
        public NativeArray<float3> PosPredict => _posPredict;
        public NativeArray<float3> PosPredictTemp => _posPredictTemp;
        public NativeArray<float> Lambda => _lambda;
        
        // ========== 构造与销毁 ==========
        public PBFSystem(Config config)
        {
            _config = config;
            
            // 初始化所有缓冲区
            _lut = new NativeHashMap<int, int2>(config.maxParticles, Allocator.Persistent);
            _hashes = new NativeArray<int2>(config.maxParticles, Allocator.Persistent);
            _posPredict = new NativeArray<float3>(config.maxParticles, Allocator.Persistent);
            _posPredictTemp = new NativeArray<float3>(config.maxParticles, Allocator.Persistent);
            _posOld = new NativeArray<float3>(config.maxParticles, Allocator.Persistent);
            _lambda = new NativeArray<float>(config.maxParticles, Allocator.Persistent);
            _particlesTemp = new NativeArray<Particle>(config.maxParticles, Allocator.Persistent);
            _velocityTemp = new NativeArray<float3>(config.maxParticles, Allocator.Persistent);
            _velocityTemp2 = new NativeArray<float3>(config.maxParticles, Allocator.Persistent);
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            
            if (_lut.IsCreated) _lut.Dispose();
            if (_hashes.IsCreated) _hashes.Dispose();
            if (_posPredict.IsCreated) _posPredict.Dispose();
            if (_posPredictTemp.IsCreated) _posPredictTemp.Dispose();
            if (_posOld.IsCreated) _posOld.Dispose();
            if (_lambda.IsCreated) _lambda.Dispose();
            if (_particlesTemp.IsCreated) _particlesTemp.Dispose();
            if (_velocityTemp.IsCreated) _velocityTemp.Dispose();
            if (_velocityTemp2.IsCreated) _velocityTemp2.Dispose();
            
            _disposed = true;
        }
        
        // ========== 核心API：简单的单步模拟 ==========
        /// <summary>
        /// 执行一步PBF模拟（简化API，适用于水珠）
        /// </summary>
        public void SimulateStep(
            NativeSlice<Particle> particles, 
            NativeSlice<float3> velocities,
            int count,
            float deltaTime,
            float3 gravity,
            bool enableViscosity,
            float viscosityStrength)
        {
            SimulateStep(particles, velocities, count, deltaTime, gravity, enableViscosity, viscosityStrength, -1);
        }

        public void SimulateStep(
            NativeSlice<Particle> particles,
            NativeSlice<float3> velocities,
            int count,
            float deltaTime,
            float3 gravity,
            bool enableViscosity,
            float viscosityStrength,
            int solverIterationsOverride)
        {
            SimulateStep(particles, velocities, count, deltaTime, gravity, enableViscosity, viscosityStrength, solverIterationsOverride, float.NaN, float.NaN);
        }

        public void SimulateStep(
            NativeSlice<Particle> particles,
            NativeSlice<float3> velocities,
            int count,
            float deltaTime,
            float3 gravity,
            bool enableViscosity,
            float viscosityStrength,
            int solverIterationsOverride,
            float minCOverride,
            float tensileKOverride)
        {
            if (count <= 0) return;
            
            // 1. 应用重力和预测位置（写入 _posPredictTemp 作为临时存储）
            ApplyGravityAndPredict(particles, velocities, count, deltaTime, gravity);
            
            // 2. 构建邻域（基于预测位置，存储在 _posPredictTemp）
            BuildNeighborhoodFromPositions(_posPredictTemp, count);
            
            // 3. Shuffle：按排序顺序重排位置到 _posPredict
            ShufflePositionsInternal(count);
            
            // 4. PBF求解
            int solverIterations = solverIterationsOverride > 0 ? solverIterationsOverride : _config.solverIterations;
            float minC = float.IsNaN(minCOverride) ? -0.2f : minCOverride;
            float tensileK = float.IsNaN(tensileKOverride) ? 0.1f : tensileKOverride;
            SolveDensityConstraints(count, solverIterations, minC, tensileK);
            
            // 5. Unshuffle：恢复到原始索引顺序
            UnshufflePositionsInternal(count);
            
            // 6. 更新位置
            ApplyPositionCorrections(particles, velocities, count, deltaTime);
            
            // 7. 粘性（可选）：让速度趋向邻居平均速度，维持形状
            if (enableViscosity && viscosityStrength > 0)
            {
                ApplyViscosity(velocities, count, viscosityStrength, deltaTime);
            }
        }
        
        public struct StepTimings
        {
            public long TicksGravityPredict;
            public long TicksNeighborhoodHash;
            public long TicksNeighborhoodSort;
            public long TicksNeighborhoodBuildLut;
            public long TicksShufflePositions;
            public long TicksSolveLambda;
            public long TicksSolveDelta;
            public long TicksUnshufflePositions;
            public long TicksApplyPositionCorrections;
            public long TicksViscosityShuffleVel;
            public long TicksViscosityJob;
            public long TicksViscosityUnshuffleVel;
        }
        
        public void SimulateStepProfiled(
            NativeSlice<Particle> particles,
            NativeSlice<float3> velocities,
            int count,
            float deltaTime,
            float3 gravity,
            bool enableViscosity,
            float viscosityStrength,
            int solverIterationsOverride,
            float minCOverride,
            float tensileKOverride,
            ref StepTimings timings)
        {
            if (count <= 0) return;
            
            long t0 = Stopwatch.GetTimestamp();
            ApplyGravityAndPredict(particles, velocities, count, deltaTime, gravity);
            timings.TicksGravityPredict += Stopwatch.GetTimestamp() - t0;
            
            _lut.Clear();
            t0 = Stopwatch.GetTimestamp();
            new HashJobFromPositions
            {
                Positions = _posPredictTemp,
                Hashes = _hashes
            }.Schedule(count, 64).Complete();
            timings.TicksNeighborhoodHash += Stopwatch.GetTimestamp() - t0;
            
            t0 = Stopwatch.GetTimestamp();
            var activeHashes = _hashes.GetSubArray(0, count);
            activeHashes.SortJob(new PBF_Utils.Int2Comparer()).Schedule().Complete();
            timings.TicksNeighborhoodSort += Stopwatch.GetTimestamp() - t0;
            
            t0 = Stopwatch.GetTimestamp();
            new Simulation_PBF.BuildLutJob
            {
                Hashes = activeHashes,
                Lut = _lut
            }.Schedule().Complete();
            timings.TicksNeighborhoodBuildLut += Stopwatch.GetTimestamp() - t0;
            
            t0 = Stopwatch.GetTimestamp();
            ShufflePositionsInternal(count);
            timings.TicksShufflePositions += Stopwatch.GetTimestamp() - t0;
            
            int solverIterations = solverIterationsOverride > 0 ? solverIterationsOverride : _config.solverIterations;
            float minC = float.IsNaN(minCOverride) ? -0.2f : minCOverride;
            float tensileK = float.IsNaN(tensileKOverride) ? 0.1f : tensileKOverride;
            for (int iter = 0; iter < solverIterations; iter++)
            {
                NativeArray<float3> posIn = (iter % 2 == 0) ? _posPredict : _posPredictTemp;
                NativeArray<float3> posOut = (iter % 2 == 0) ? _posPredictTemp : _posPredict;
                
                t0 = Stopwatch.GetTimestamp();
                new Simulation_PBF.ComputeLambdaJob
                {
                    Lut = _lut,
                    PosPredict = posIn,
                    Lambda = _lambda,
                    TargetDensity = _config.targetDensity,
                    MinC = minC,
                }.Schedule(count, 64).Complete();
                timings.TicksSolveLambda += Stopwatch.GetTimestamp() - t0;
                
                t0 = Stopwatch.GetTimestamp();
                new Simulation_PBF.ComputeDeltaPosJobSimple
                {
                    Lut = _lut,
                    PosPredictIn = posIn,
                    PosPredictOut = posOut,
                    Lambda = _lambda,
                    TargetDensity = _config.targetDensity,
                    TensileK = tensileK,
                }.Schedule(count, 64).Complete();
                timings.TicksSolveDelta += Stopwatch.GetTimestamp() - t0;
            }
            
            t0 = Stopwatch.GetTimestamp();
            UnshufflePositionsInternal(count);
            timings.TicksUnshufflePositions += Stopwatch.GetTimestamp() - t0;
            
            t0 = Stopwatch.GetTimestamp();
            ApplyPositionCorrections(particles, velocities, count, deltaTime);
            timings.TicksApplyPositionCorrections += Stopwatch.GetTimestamp() - t0;
            
            if (enableViscosity && viscosityStrength > 0)
            {
                t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < count; i++)
                {
                    int origIdx = _hashes[i].y;
                    _velocityTemp[i] = velocities[origIdx];
                }
                timings.TicksViscosityShuffleVel += Stopwatch.GetTimestamp() - t0;
                
                t0 = Stopwatch.GetTimestamp();
                new Simulation_PBF.ApplyViscosityJobSimple
                {
                    Lut = _lut,
                    PosPredict = _posPredict,
                    VelocityR = _velocityTemp,
                    VelocityW = _velocityTemp2,
                    ViscosityStrength = viscosityStrength,
                    TargetDensity = _config.targetDensity,
                    DeltaTime = deltaTime
                }.Schedule(count, 64).Complete();
                timings.TicksViscosityJob += Stopwatch.GetTimestamp() - t0;
                
                t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < count; i++)
                {
                    int origIdx = _hashes[i].y;
                    velocities[origIdx] = _velocityTemp2[i];
                }
                timings.TicksViscosityUnshuffleVel += Stopwatch.GetTimestamp() - t0;
            }
        }
        
        // ========== 高级API：分步控制（适用于主体史莱姆） ==========
        /// <summary>
        /// 步骤1：准备粒子数据（拷贝到临时缓冲）
        /// </summary>
        public void PrepareParticles(NativeSlice<Particle> particles, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _particlesTemp[i] = particles[i];
            }
        }
        
        /// <summary>
        /// 步骤2：构建邻域查询表
        /// </summary>
        public void BuildNeighborhood(NativeSlice<Particle> particles, int count)
        {
            _lut.Clear();
            
            // 计算哈希
            new HashJob
            {
                Particles = particles,
                Hashes = _hashes
            }.Schedule(count, 64).Complete();
            
            // 排序
            var activeHashes = _hashes.GetSubArray(0, count);
            activeHashes.SortJob(new PBF_Utils.Int2Comparer()).Schedule().Complete();
            
            // 构建查询表（复用主体的 Job）
            new Simulation_PBF.BuildLutJob
            {
                Hashes = activeHashes,
                Lut = _lut
            }.Schedule().Complete();
        }
        
        /// <summary>
        /// 基于位置数组构建邻域（用于预测位置）
        /// </summary>
        private void BuildNeighborhoodFromPositions(NativeArray<float3> positions, int count)
        {
            _lut.Clear();
            
            // 计算哈希
            new HashJobFromPositions
            {
                Positions = positions,
                Hashes = _hashes
            }.Schedule(count, 64).Complete();
            
            // 排序
            var activeHashes = _hashes.GetSubArray(0, count);
            activeHashes.SortJob(new PBF_Utils.Int2Comparer()).Schedule().Complete();
            
            // 构建查询表（复用主体的 Job）
            new Simulation_PBF.BuildLutJob
            {
                Hashes = activeHashes,
                Lut = _lut
            }.Schedule().Complete();
        }
        
        /// <summary>
        /// 步骤3：Shuffle到排序空间
        /// </summary>
        public void ShufflePositions(NativeSlice<float3> worldPositions, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int origIdx = _hashes[i].y;
                _posPredict[i] = worldPositions[origIdx];
            }
        }
        
        /// <summary>
        /// 步骤4：求解密度约束
        /// </summary>
        public void SolveDensityConstraints(int count)
        {
            SolveDensityConstraints(count, _config.solverIterations, -0.2f, 0.1f);
        }

        private void SolveDensityConstraints(int count, int solverIterations, float minC, float tensileK)
        {
            for (int iter = 0; iter < solverIterations; iter++)
            {
                if (iter % 2 == 0)
                {
                    SolveDensityIteration(_posPredict, _posPredictTemp, count, minC, tensileK);
                }
                else
                {
                    SolveDensityIteration(_posPredictTemp, _posPredict, count, minC, tensileK);
                }
            }
        }
        
        /// <summary>
        /// 步骤5：Unshuffle回原始空间
        /// </summary>
        public void UnshufflePositions(NativeSlice<float3> worldPositions, int count)
        {
            bool oddIterations = (_config.solverIterations % 2 == 1);
            var finalPositions = oddIterations ? _posPredictTemp : _posPredict;
            
            for (int i = 0; i < count; i++)
            {
                int origIdx = _hashes[i].y;
                worldPositions[origIdx] = finalPositions[i];
            }
        }
        
        // ========== 内部方法 ==========
        private void ApplyGravityAndPredict(
            NativeSlice<Particle> particles, 
            NativeSlice<float3> velocities,
            int count,
            float deltaTime,
            float3 gravity)
        {
            for (int i = 0; i < count; i++)
            {
                var p = particles[i];
                var vel = velocities[i] + gravity * deltaTime;
                // 注意：damping 不在这里应用，而是在 ApplyPositionCorrections 中应用
                // 与主体流程一致：预测位置使用未衰减的速度
                
                // 保存原始位置到 _posOld（用于最终速度计算）
                _posOld[i] = p.Position;
                
                // 预测位置（写入临时缓冲，稍后 shuffle）
                _posPredictTemp[i] = p.Position + vel * deltaTime;
            }
        }
        
        private void ShufflePositionsInternal(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int origIdx = _hashes[i].y;
                _posPredict[i] = _posPredictTemp[origIdx];
            }
        }
        
        private void UnshufflePositionsInternal(int count)
        {
            bool oddIterations = (_config.solverIterations % 2 == 1);
            var finalPositions = oddIterations ? _posPredictTemp : _posPredict;
            
            // 修复：当 finalPositions == _posPredictTemp 时，直接写入会导致读写冲突
            // 使用 _posPredict 作为临时缓冲（此时它不再被使用）
            if (oddIterations)
            {
                // 先拷贝到 _posPredict 作为临时存储
                for (int i = 0; i < count; i++)
                {
                    _posPredict[i] = _posPredictTemp[i];
                }
                // 然后从 _posPredict 读取，写入 _posPredictTemp
                for (int i = 0; i < count; i++)
                {
                    int origIdx = _hashes[i].y;
                    _posPredictTemp[origIdx] = _posPredict[i];
                }
            }
            else
            {
                // finalPositions == _posPredict，写入 _posPredictTemp 无冲突
                for (int i = 0; i < count; i++)
                {
                    int origIdx = _hashes[i].y;
                    _posPredictTemp[origIdx] = finalPositions[i];
                }
            }
        }

        private void SolveDensityIteration(
            NativeArray<float3> posIn, 
            NativeArray<float3> posOut,
            int count,
            float minC,
            float tensileK)
        {
            // 计算Lambda（复用主体的 Job）
            new Simulation_PBF.ComputeLambdaJob
            {
                Lut = _lut,
                PosPredict = posIn,
                Lambda = _lambda,
                TargetDensity = _config.targetDensity,
                MinC = minC,
            }.Schedule(count, 64).Complete();
            
            // 计算位置修正（复用主体的简化版 Job）
            new Simulation_PBF.ComputeDeltaPosJobSimple
            {
                Lut = _lut,
                PosPredictIn = posIn,
                PosPredictOut = posOut,
                Lambda = _lambda,
                TargetDensity = _config.targetDensity,
                TensileK = tensileK,
        }.Schedule(count, 64).Complete();
        }
        
        private void ApplyPositionCorrections(
            NativeSlice<Particle> particles,
            NativeSlice<float3> velocities, 
            int count,
            float deltaTime)
        {
            // UnshufflePositionsInternal 已将最终位置写入 _posPredictTemp（原始索引顺序）
            // _posOld 保存着原始位置（在 ApplyGravityAndPredict 中设置）
            
            for (int i = 0; i < count; i++)
            {
                var p = particles[i];
                float3 newPos = _posPredictTemp[i];
                float3 oldPos = _posOld[i];  // 原始位置
                
                // 速度计算：与主体一致，使用 (newPos - oldPos) / dt
                // 注意：不乘 damping，与主体 UpdateJob 一致
                float3 vel = (newPos - oldPos) / deltaTime;
                
                // 速度限制（防止爆炸）
                float maxVel = 50f;  // 内部坐标
                float velMag = math.length(vel);
                if (velMag > maxVel)
                    vel = vel * (maxVel / velMag);
                    
                velocities[i] = vel;
                
                // 更新位置
                p.Position = newPos;
                
                // 递减 FreeFrames（保护期倒计时）
                if (p.FreeFrames > 0)
                    p.FreeFrames--;
                    
                particles[i] = p;
            }
        }
        
        /// <summary>
        /// 粘性：让速度趋向邻居平均速度，维持形状
        /// </summary>
        private void ApplyViscosity(NativeSlice<float3> velocities, int count, float viscosityStrength, float deltaTime)
        {
            // 水珠独立使用 PBFSystem，此时 _lut 和 _posPredict 是 PBF 求解后的排序顺序
            // 需要将速度也 shuffle 到排序顺序，计算粘性后再 unshuffle
            
            // 1. 将速度 shuffle 到排序顺序（与 _posPredict 对应）
            for (int i = 0; i < count; i++)
            {
                int origIdx = _hashes[i].y;
                _velocityTemp[i] = velocities[origIdx];
            }
            
            // 2. 计算粘性（此时 _lut、_posPredict、_velocityTemp 索引一致）
            // 水珠使用简化版，不需要检查 FreeFrames
            new Simulation_PBF.ApplyViscosityJobSimple
            {
                Lut = _lut,
                PosPredict = _posPredict,
                VelocityR = _velocityTemp,
                VelocityW = _velocityTemp2,
                ViscosityStrength = viscosityStrength,
                TargetDensity = _config.targetDensity,
                DeltaTime = deltaTime
            }.Schedule(count, 64).Complete();
            
            // 3. 将结果 unshuffle 回原始索引顺序
            for (int i = 0; i < count; i++)
            {
                int origIdx = _hashes[i].y;
                velocities[origIdx] = _velocityTemp2[i];
            }
        }
        
        // ========== Jobs（复用现有实现） ==========
        [BurstCompile]
        struct HashJob : IJobParallelFor
        {
            [ReadOnly] public NativeSlice<Particle> Particles;
            [WriteOnly] public NativeArray<int2> Hashes;
            
            public void Execute(int i)
            {
                float3 pos = Particles[i].Position;
                int hash = PBF_Utils.GetKey(PBF_Utils.GetCoord(pos));
                Hashes[i] = new int2(hash, i);
            }
        }
        
        [BurstCompile]
        struct HashJobFromPositions : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [WriteOnly] public NativeArray<int2> Hashes;
            
            public void Execute(int i)
            {
                float3 pos = Positions[i];
                int hash = PBF_Utils.GetKey(PBF_Utils.GetCoord(pos));
                Hashes[i] = new int2(hash, i);
            }
        }
        
        // BuildLutJob 复用 Simulation_PBF.BuildLutJob
        // ComputeLambdaJob 复用 Simulation_PBF.ComputeLambdaJob
        // ComputeDeltaPosJobSimple 复用 Simulation_PBF.ComputeDeltaPosJobSimple
    }
}
