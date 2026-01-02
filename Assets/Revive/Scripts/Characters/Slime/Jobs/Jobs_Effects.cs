using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Revive.Slime
{
    public class Effects
    {
        public struct Component
        {
            public int ID;
            public float3 Center;
            public float3 BoundsMin;
            public float3 BoundsMax;
            public int CellCount;

            public Component(int id)
            {
                ID = id;
                Center = float3.zero;
                BoundsMin = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
                BoundsMax = new float3(float.MinValue, float.MinValue, float.MinValue);
                CellCount = 0;
            }
        }

        public struct Bubble
        {
            public float3 Pos;
            public float Radius;
            public float3 Vel;
            public float LifeTime;
        }

        [BurstCompile]
        public struct ConnectComponentBlockJob : IJob
        {
            [ReadOnly] public NativeArray<int3> Keys;
            [ReadOnly] public NativeHashMap<int3, int> GridLut;
            [ReadOnly] public NativeArray<float> Grid;
            [WriteOnly] public NativeArray<int> GridID;
            public NativeList<Component> Components;
            public float Threshold;
            
            public void Execute()
            {
                var visited = new NativeArray<bool>(Grid.Length, Allocator.Temp);
                var stack = new NativeList<int3>(256, Allocator.Temp);
                int2 off = new int2(1, 0);

                for (int i = 0; i < Keys.Length; i++)
                {
                    int3 key = Keys[i];
                    for (int j = 0; j < 64; j++)
                    {
                        var coord = key * 4 + GetLocalCoord(j);
                        stack.Add(coord);

                        float3 posSum = float3.zero;
                        int id = Components.Length;
                        Component c = new Component(id);

                        while (stack.Length > 0)
                        {
                            int3 cur = stack[0];
                            stack.RemoveAtSwapBack(0);
                            if (!IsValid(cur, visited, out int index))
                                continue;
                            GridID[index] = id;
                            c.CellCount++;
                            float3 cellCenter = (float3)cur + 0.5f;
                            posSum += cellCenter;
                            c.BoundsMin = math.min(c.BoundsMin, cellCenter);
                            c.BoundsMax = math.max(c.BoundsMax, cellCenter);

                            stack.Add(cur + off.xyy);
                            stack.Add(cur - off.xyy);
                            stack.Add(cur + off.yxy);
                            stack.Add(cur - off.yxy);
                            stack.Add(cur + off.yyx);
                            stack.Add(cur - off.yyx);
                        }

                        if (c.CellCount > 0)
                        {
                            c.Center = (c.BoundsMin + c.BoundsMax) * 0.5f;
                            Components.Add(c);
                        }
                    }
                }

                stack.Dispose();
                visited.Dispose();
            }

            private bool IsValid(int3 coord, NativeArray<bool> visited, out int index)
            {
                int3 key = coord >> 2;
                index = 0;
                if (!GridLut.ContainsKey(key)) return false;
                int offset = GridLut[key];
                int localIndex = GetLocalIndex(coord & 3);
                index = localIndex + offset;
                return IsIdxValid(index, visited);
            }

            private bool IsIdxValid(int idx, NativeArray<bool> visited)
            {
                if (visited[idx]) return false;
                visited[idx] = true;
                if (Grid[idx] < Threshold) return false;
                return true;
            }

            private int3 GetLocalCoord(int i)
            {
                return new int3(i & 3, (i >> 2) & 3, (i >> 4) & 3);
            }

            private int GetLocalIndex(int3 coord)
            {
                return coord.x + 4 * (coord.y + 4 * coord.z);
            }
        }

        [BurstCompile]
        public struct ParticleIDJob : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int3, int> GridLut;
            [ReadOnly] public NativeArray<int> GridID;
            [ReadOnly] public NativeList<ParticleController> Controllers;
            [ReadOnly] public NativeArray<ParticleController> SourceControllers;
            public NativeArray<Particle> Particles;
            public float3 MinPos;

            public void Execute(int index)
            {
                Particle p = Particles[index];

                if (p.Type == ParticleType.Dormant || p.Type == ParticleType.FadingOut)
                {
                    p.ClusterId = 0;
                    Particles[index] = p;
                    return;
                }
                
                // 场景水珠跳过CCA，ClusterId 保持为 0
                if (p.Type == ParticleType.SceneDroplet || p.SourceId >= 0)
                {
                    p.ClusterId = 0;
                    Particles[index] = p;
                    return;
                }
                
                // 自由飞行的粒子跳过CCA，保持原有 ClusterId
                // 这样发射的粒子在 FreeFrames 期间不会被重新分组到主体
                if (p.FreeFrames > 0)
                {
                    return;
                }
                
                // 主体粒子和分离粒子进行CCA分配
                // 这样可以通过 ClusterId 判断粒子是否与主体连通
                // ClusterId = CCA组件ID + 1（0保留给无效/未分配）
                float3 worldPos = Simulation_PBF.GetWorldPosition(p, Controllers, SourceControllers);
                int3 coord = (int3)math.floor((worldPos - MinPos) / PBF_Utils.CellSize);
                int3 key = coord >> 2;
                if (GridLut.ContainsKey(key))
                {
                    int offset = GridLut[key];
                    int rawId = GridID[offset + GetLocalIndex(coord & 3)];
                    p.ClusterId = rawId >= 0 ? (rawId + 1) : 0;
                }
                else
                {
                    p.ClusterId = 0;
                }
                Particles[index] = p;
            }
            
            private int GetLocalIndex(int3 coord)
            {
                return coord.x + 4 * (coord.y + 4 * coord.z);
            }
        }
        
        public struct ConnectComponentArrayJob : IJob
        {
            [ReadOnly] public NativeArray<int3> Keys;
            [ReadOnly] public NativeHashMap<int3, int> GridLut;
            [ReadOnly] public NativeArray<float> Grid;
            public NativeList<Component> Components;
            public float Threshold;
            
            public void Execute()
            {
                var visited = new NativeArray<bool>(Grid.Length, Allocator.Temp);
                var stack = new NativeList<int3>(256, Allocator.Temp);
                int2 off = new int2(1, 0);

                for (int i = 0; i < Keys.Length; i++)
                {
                    int3 key = Keys[i];
                    for (int j = 0; j < 64; j++)
                    {
                        var coord = key * 4 + GetLocalCoord(j);
                        stack.Add(coord);

                        float3 posSum = float3.zero;
                        Component c = new Component(Components.Length);

                        while (stack.Length > 0)
                        {
                            int3 cur = stack[0];
                            stack.RemoveAtSwapBack(0);
                            if (!IsValid(cur, visited))
                                continue;
                            c.CellCount++;
                            float3 cellCenter = (float3)cur + 0.5f;
                            posSum += cellCenter;
                            c.BoundsMin = math.min(c.BoundsMin, cellCenter);
                            c.BoundsMax = math.max(c.BoundsMax, cellCenter);

                            stack.Add(cur + off.xyy);
                            stack.Add(cur - off.xyy);
                            stack.Add(cur + off.yxy);
                            stack.Add(cur - off.yxy);
                            stack.Add(cur + off.yyx);
                            stack.Add(cur - off.yyx);
                        }

                        if (c.CellCount > 0)
                        {
                            c.Center = posSum / c.CellCount;
                            Components.Add(c);
                        }
                    }
                }

                stack.Dispose();
                visited.Dispose();
            }

            private bool IsValid(int3 coord, NativeArray<bool> visited)
            {
                int3 key = coord >> 2;
                if (!GridLut.ContainsKey(key)) return false;
                int offset = GridLut[key];
                int localIndex = GetLocalIndex(coord & 3);
                return IsIdxValid(offset + localIndex, visited);
            }

            private bool IsIdxValid(int idx, NativeArray<bool> visited)
            {
                if (visited[idx]) return false;
                visited[idx] = true;
                if (Grid[idx] < Threshold) return false;
                return true;
            }

            private int3 GetLocalCoord(int i)
            {
                return new int3(i & 3, (i >> 2) & 3, (i >> 4) & 3);
            }

            private int GetLocalIndex(int3 coord)
            {
                return coord.x + 4 * (coord.y + 4 * coord.z);
            }
        }
        
        
        [BurstCompile]
        public struct RayInsectJob : IJob
        {
            [ReadOnly] public NativeHashMap<int3, int> GridLut;
            [ReadOnly] public NativeArray<float> Grid;
            public NativeArray<float3> Result;
            public float Threshold;
            public float3 Pos;
            public float3 Dir;
            public float3 MinPos;
            public float MaxRadius; // 最大搜索距离（控制器半径）
            
            public void Execute()
            {
                float3 cur = Pos;
                float step = PBF_Utils.CellSize * 0.5f; // 0.25，更细的步长
                float maxDist = math.max(MaxRadius * 2f, 15f); // 至少搜索15单位或2倍半径
                int maxSteps = (int)(maxDist / step) + 1;
                
                float oldValue = Threshold + 1f; // 初始假设在表面内（高密度）
                float3 oldPos = cur;
                bool foundSurface = false;
                
                for (int i = 0; i < maxSteps; i++)
                {
                    int3 coord = (int3)math.floor((cur - MinPos) / PBF_Utils.CellSize);
                    int3 key = coord >> 2;
                    
                    if (GridLut.ContainsKey(key))
                    {
                        int offset = GridLut[key];
                        float curValue = Grid[offset + GetLocalIndex(coord & 3)];
                        
                        // 检测穿越等值面：从高密度（>=Threshold）到低密度（<Threshold）
                        if (curValue < Threshold && oldValue >= Threshold)
                        {
                            // 线性插值找精确交点，clamp防止外插
                            float denom = oldValue - curValue;
                            float t = math.clamp((oldValue - Threshold) / denom, 0f, 1f);
                            cur = math.lerp(oldPos, cur, t);
                            foundSurface = true;
                            break;
                        }
                        oldValue = curValue;
                    }
                    else
                    {
                        // 离开网格区域，如果之前在表面内则当前位置就是边界
                        if (oldValue >= Threshold)
                        {
                            foundSurface = true;
                        }
                        break;
                    }
                    
                    oldPos = cur;
                    cur += Dir * step;
                }
                
                // 如果没找到表面，返回 NaN 让调用方处理
                Result[0] = foundSurface ? cur : new float3(float.NaN, float.NaN, float.NaN);
            }

            private int GetLocalIndex(int3 coord)
            {
                return coord.x + 4 * (coord.y + 4 * coord.z);
            }
        }

        [BurstCompile]
        public struct GenerateBubblesJobs : IJob
        {
            [ReadOnly] public NativeArray<int4> Keys;
            [ReadOnly] public NativeHashMap<int3, int> GridLut;
            [ReadOnly] public NativeArray<float> Grid;

            [WriteOnly] public NativeArray<Bubble> BubblesBuffer;
            public NativeList<int> BubblesStack;
            public float Threshold;
            public float Speed;
            public int BlockCount;
            public float3 MinPos;
            public uint Seed;
            
            public void Execute()
            {
                var rnd = new Random(Seed);
                for (int i = 0; i < BlockCount; i++)
                {
                    int3 key = Keys[i].xyz;
                    int offset = GridLut[key];
                    for (int j = 0; j < 64; j++)
                    {
                        float data = Grid[offset + j];
                        if (data < Threshold) continue;

                        if (BubblesStack.Length < 1) 
                            return;
                        
                        var coord = key * 4 + GetLocalCoord(j);

                        if (coord.y > 2 || rnd.NextFloat() > Speed) continue;
                        
                        int id = BubblesStack[0];
                        BubblesStack.RemoveAtSwapBack(0);
                        float radius = (rnd.NextFloat() * 0.7f + 0.3f) * PBF_Utils.CellSize;
                        Bubble bubble = new Bubble()
                        {
                            Pos = MinPos + PBF_Utils.CellSize * (coord + new float3(rnd.NextFloat(), rnd.NextFloat() - 1, rnd.NextFloat())),
                            Radius = radius,
                            Vel = new float3(0, radius * 2, 0),
                            LifeTime = 1,
                        };
                        
                        BubblesBuffer[id] = bubble;
                    }
                }
            }
            private int3 GetLocalCoord(int i)
            {
                return new int3(i & 3, (i >> 2) & 3, (i >> 4) & 3);
            }
        }

        [BurstCompile]
        public struct BubblesViscosityJob : IJobParallelFor
        {
            [ReadOnly] public NativeHashMap<int, int2> Lut;
            [ReadOnly] public NativeArray<Particle> Particles;
            [ReadOnly] public NativeArray<float3> VelocityR;
            [ReadOnly] public NativeList<ParticleController> Controllers;
            public NativeArray<Bubble> BubblesBuffer;
            public float ViscosityStrength;

            public void Execute(int i)
            {
                Bubble bubble = BubblesBuffer[i];
                if (bubble.LifeTime < 0) 
                    return;
                
                float3 vel = bubble.Vel;
                float3 pos = bubble.Pos;

                // for (int j = 0; j < Controllers.Length; j++)
                // {
                //     ParticleController cl = Controllers[j];
                //     float3 toCenter = cl.Center + new float3(0, cl.Radius * 0.05f, 0) - pos;
                //     float len = math.length(toCenter);
                //     if (len < cl.Radius)
                //     {
                //         vel += cl.Velocity * (cl.Concentration * 0.1f * PBF_Utils.DeltaTime);
                //         // vel += 0.5f * cl.Concentration * PBF_Utils.DeltaTime * math.min(1, len * 0.1f) * math.normalizesafe(toCenter);
                //     }
                // }
                
                int3 coord = PBF_Utils.GetCoord(pos);
                float3 viscosityVel = float3.zero;
                float rho = 0;
                for (int dz = -1; dz <= 1; ++dz)
                for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int key = PBF_Utils.GetKey(coord + math.int3(dx, dy, dz));
                    if (!Lut.ContainsKey(key)) continue;
                    int2 range = Lut[key];
                    for (int j = range.x; j < range.y; j++)
                    {
                        float3 dir = pos - Particles[j].Position;
                        float r2 = math.lengthsq(dir);
                        if (r2 > PBF_Utils.h2) continue;

                        float weight =  PBF_Utils.SmoothingKernelPoly6(r2);
                        viscosityVel += VelocityR[j] * weight;
                        rho += weight;
                    }
                }

                viscosityVel = rho > 1e-5f ? viscosityVel / rho : float3.zero;

                float y = math.min(4, pos.y) * 0.25f;
                bubble.Vel = math.lerp(vel, viscosityVel, new float3(y, ViscosityStrength, y));
                
                BubblesBuffer[i] = bubble;
            }
        }

        [BurstCompile]
        public struct UpdateBubblesJob : IJob
        {
            [ReadOnly] public NativeHashMap<int3, int> GridLut;
            [ReadOnly] public NativeArray<float> Grid;

            public NativeArray<Bubble> BubblesBuffer;
            public NativeList<int> BubblesStack;
            
            public float3 MinPos;
            public float Threshold;
            public float DeltaTime;
            
            public void Execute()
            {
                for (int i = 0; i < BubblesBuffer.Length; i++)
                {
                    Bubble b = BubblesBuffer[i];
                    if (b.LifeTime < 0)
                        continue;
                    b.Vel.y += b.Radius * b.Radius * DeltaTime * 10;
                    b.Pos += b.Vel * DeltaTime * 2;
                    b.LifeTime += DeltaTime;
                    float scale = 1.002f;
                    b.Radius *= scale;

                    int3 coord = (int3)math.floor((b.Pos - MinPos) / PBF_Utils.CellSize);
                    int3 key = coord >> 2;
                    if (!GridLut.ContainsKey(key))
                    {
                        b.LifeTime = -1;
                        b.Radius = 0;
                        BubblesStack.Add(i);
                    }
                    else
                    {
                        int offset = GridLut[key];
                        float data = Grid[offset + GetLocalIndex(coord & 3)];
                        if (data < Threshold)
                        {
                            b.LifeTime = -1;
                            b.Radius = 0;
                            BubblesStack.Add(i);
                        }
                    }

                    BubblesBuffer[i] = b;
                }
            }
            
            private int GetLocalIndex(int3 coord)
            {
                return coord.x + 4 * (coord.y + 4 * coord.z);
            }
        }
        
        /// <summary>
        /// 网格投票汇总结构
        /// </summary>
        public struct VoteResult
        {
            public int NewCompId;
            public int WinningStableId;
            public int WinningCount;
            public int TotalCount;
            public float Ratio;
        }
        
        /// <summary>
        /// 将稳定ID写回网格的Job
        /// </summary>
        [BurstCompile]
        public struct WriteStableIdToGridJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> GridID;              // 本帧CCA组件ID
            [ReadOnly] public NativeArray<int> CompToStableId;      // 组件ID -> 稳定ID映射
            public NativeArray<int> GridStableId;                   // 输出：网格稳定ID
            
            public void Execute(int index)
            {
                int compId = GridID[index];
                if (compId >= 0 && compId < CompToStableId.Length)
                {
                    GridStableId[index] = CompToStableId[compId];
                }
                else
                {
                    GridStableId[index] = 0; // 无效区域归属主体
                }
            }
        }
    }
}
