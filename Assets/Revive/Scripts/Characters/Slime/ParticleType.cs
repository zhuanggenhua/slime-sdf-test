using Unity.Mathematics;

namespace Revive.Slime
{
    /// <summary>
    /// 粒子类型枚举 - 明确定义每种粒子的身份
    /// </summary>
    public enum ParticleType : byte
    {
        /// <summary>
        /// 主体粒子 - 属于史莱姆主体
        /// </summary>
        MainBody = 0,
        
        /// <summary>
        /// 分离粒子 - 从主体分离但仍属于玩家
        /// </summary>
        Separated = 1,
        
        /// <summary>
        /// 发射粒子 - 刚发射出去的自由飞行粒子
        /// </summary>
        Emitted = 2,
        
        /// <summary>
        /// 场景水珠 - 预置在场景中的独立水珠
        /// </summary>
        SceneDroplet = 3,
        
        /// <summary>
        /// 休眠粒子 - 不参与模拟的备用粒子
        /// </summary>
        Dormant = 4,
        FadingOut = 5
    }

    // ImprovedParticle 已删除，统一使用 Particle（定义在 Jobs_Simulation_PBF.cs）
    // 状态转换方法统一使用 ParticleStateManager
}
