using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 粒子状态管理器 - 集中处理所有粒子状态转换逻辑
    /// </summary>
    public static class ParticleStateManager
    {
        /// <summary>
        /// 验证粒子状态的合法性
        /// </summary>
        public static bool ValidateParticle(ref Particle p, int particleIndex = -1)
        {
            bool isValid = true;
            
            // 规则1：只有场景水珠才能有非负SourceId
            if (p.SourceId >= 0 && p.Type != ParticleType.SceneDroplet)
            {
                p.SourceId = -1; // 自动修正
                isValid = false;
            }
            
            // 规则2：场景水珠必须有有效的SourceId
            if (p.Type == ParticleType.SceneDroplet && p.SourceId < 0)
            {
                // 无法自动修正，需要知道正确的SourceId
                isValid = false;
            }
            
            // 规则3：主体粒子的ControllerId必须是0
            if (p.Type == ParticleType.MainBody && p.ControllerId != 0)
            {
                p.ControllerId = 0; // 自动修正
                isValid = false;
            }
            
            // 规则4：休眠粒子不应有任何关联
            if (p.Type == ParticleType.Dormant)
            {
                if (p.SourceId != -1 || p.ControllerId != 0 || p.FreeFrames != 0 || p.StableId != 0)
                {
                    p.SourceId = -1;
                    p.ControllerId = 0;
                    p.StableId = 0;
                    p.FreeFrames = 0;
                    isValid = false;
                }
            }
            
            // 规则5：只有Emitted和Separated粒子才能有FreeFrames
            if (p.Type != ParticleType.Emitted && p.Type != ParticleType.Separated && p.FreeFrames > 0)
            {
                p.FreeFrames = 0; // 自动修正
                isValid = false;
            }
            
            return isValid;
        }
        
        /// <summary>
        /// 将粒子转换为主体粒子
        /// </summary>
        public static void ConvertToMainBody(ref Particle p, float3 mainCenter)
        {
            // 分离粒子、发射粒子、场景水珠都可以合并回主体
            if (p.Type != ParticleType.Separated && p.Type != ParticleType.Emitted && p.Type != ParticleType.SceneDroplet) 
            {
                return;
            }
            
            // 粒子 Position 已经是模拟坐标（内部坐标），状态转换时无需坐标转换
            p.Type = ParticleType.MainBody;
            p.ControllerId = 0;
            p.StableId = 0;
            p.FreeFrames = 0;
            p.SourceId = -1; // 确保清除SourceId
        }
        
        /// <summary>
        /// 将粒子转换为分离粒子
        /// </summary>
        /// <param name="freeFrames">自由飞行帧数（期间不受召回影响），默认0，发射时主动传参</param>
        public static void ConvertToSeparated(ref Particle p, float3 mainCenter, int controllerId = 0, int freeFrames = 0)
        {
            // 场景水珠不能转换为分离粒子
            if (p.Type == ParticleType.SceneDroplet)
            {
                // 状态转换错误检查日志已清理
                return;
            }
            
            if (p.Type == ParticleType.MainBody)
            {
                // 粒子 Position 已经是模拟坐标（内部坐标），状态转换时无需坐标转换
                p.Type = ParticleType.Separated;
                p.ControllerId = controllerId;
                p.StableId = 0;
                p.FreeFrames = freeFrames; // 给分离粒子自由飞行时间
                p.SourceId = -1; // 确保清除SourceId
                // Debug.Log($"[ConvertToSeparated] 设置 FreeFrames={freeFrames}, Type={p.Type}, ControllerId={controllerId}");
            }
            else if (p.Type == ParticleType.Emitted && p.FreeFrames == 0)
            {
                // 发射粒子自由飞行结束，变为普通分离粒子
                p.Type = ParticleType.Separated;
                p.ControllerId = controllerId;
                p.StableId = 0;
                p.SourceId = -1;
            }
        }
        
        /// <summary>
        /// 发射粒子
        /// </summary>
        public static void EmitParticle(ref Particle p, float3 mainCenter, float3 velocity, int freeFrames = 120)
        {
            // 只能从主体发射
            if (p.Type != ParticleType.MainBody)
            {
                return;
            }
            
            // 粒子 Position 已经是模拟坐标（内部坐标），状态转换时无需坐标转换
            p.Type = ParticleType.Emitted;
            p.ControllerId = 0; // 发射粒子暂不属于任何控制器
            p.StableId = 0;
            p.FreeFrames = freeFrames;
            p.SourceId = -1; // 发射粒子不是场景水珠
        }
        
        /// <summary>
        /// 初始化场景水珠
        /// </summary>
        public static void InitAsSceneDroplet(ref Particle p, float3 worldPos, int sourceId)
        {
            p.Position = worldPos; // 注意：传入的 worldPos 应该已经是模拟坐标（内部坐标）
            p.Type = ParticleType.SceneDroplet;
            p.ControllerId = 0;
            p.StableId = 0;
            p.SourceId = sourceId;
            p.FreeFrames = 0;
            p.ClusterId = 0;
        }
        
        /// <summary>
        /// 休眠粒子
        /// </summary>
        public static void SetDormant(ref Particle p)
        {
            p.Position = new float3(0, -1000, 0);
            p.Type = ParticleType.Dormant;
            p.ControllerId = 0;
            p.StableId = 0;
            p.SourceId = -1; // 清除所有源关联
            p.FreeFrames = 0;
            p.ClusterId = 0;
        }
        
        /// <summary>
        /// 判断粒子是否可以被合并到主体
        /// </summary>
        public static bool CanMergeToMain(Particle p)
        {
            // 只有普通分离粒子可以合并
            return p.Type == ParticleType.Separated;
        }
        
        /// <summary>
        /// 判断粒子是否是场景水珠
        /// </summary>
        public static bool IsSceneDroplet(Particle p)
        {
            return p.Type == ParticleType.SceneDroplet;
        }
        
        /// <summary>
        /// 判断粒子是否处于自由飞行状态
        /// </summary>
        public static bool IsFreeFlying(Particle p)
        {
            return p.Type == ParticleType.Emitted && p.FreeFrames > 0;
        }
        
        /// <summary>
        /// 判断粒子是否活跃（参与物理模拟）
        /// </summary>
        public static bool IsActive(Particle p)
        {
            return p.Type != ParticleType.Dormant && p.Type != ParticleType.FadingOut;
        }
        
        /// <summary>
        /// 交换两个粒子时保护关键属性
        /// </summary>
        public static void SwapParticles(ref NativeArray<Particle> particles, int i, int j)
        {
            var pi = particles[i];
            var pj = particles[j];
            
            // 交换粒子数据
            particles[i] = pj;
            particles[j] = pi;
            
            // 验证交换后的状态
            var temp = particles[i];
            ValidateParticle(ref temp, i);
            particles[i] = temp;
            
            temp = particles[j];
            ValidateParticle(ref temp, j);
            particles[j] = temp;
        }
        
        /// <summary>
        /// 复制粒子时保护SourceId
        /// </summary>
        public static void CopyParticle(ref Particle dest, Particle src, bool protectSourceId = true)
        {
            dest = src;
            
            if (protectSourceId)
            {
                // 如果目标粒子不是场景水珠但有非负SourceId，清除它
                if (dest.SourceId >= 0 && dest.Type != ParticleType.SceneDroplet)
                {
                    dest.SourceId = -1;
                }
            }
        }
        
        /// <summary>
        /// 获取粒子类型（用于调试）
        /// </summary>
        public static string GetParticleTypeString(Particle p)
        {
            switch (p.Type)
            {
                case ParticleType.Dormant: return "休眠";
                case ParticleType.FadingOut: return "淡出";
                case ParticleType.MainBody: return "主体";
                case ParticleType.SceneDroplet: return $"场景水珠(源{p.SourceId})";
                case ParticleType.Emitted: return $"发射中(剩{p.FreeFrames}帧)";
                case ParticleType.Separated: 
                    return p.ControllerId > 0 ? $"分离组{p.ControllerId}" : "分离";
                default: return "未知";
            }
        }
    }
}
