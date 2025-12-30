using UnityEngine;

namespace Revive.Environment.Watering
{
    /// <summary>
    /// PBF 浇水系统的“输入事件”。
    /// Amount 是上层玩法关心的稳定量（而不是逐粒子回调），用于驱动 charge/wetness 等。
    /// </summary>
    public struct WaterInput
    {
        /// <summary>本次输入的“水量”。通常等于 ParticleCount * WaterPerParticle。</summary>
        public float Amount;

        /// <summary>本次被消耗的粒子数量（用于调试或实现不同权重）。</summary>
        public int ParticleCount;

        /// <summary>接收体体积的世界坐标中心（用于特效/音效/方向性反馈）。</summary>
        public Vector3 PositionWorld;
    }

    /// <summary>
    /// 可被 PBF “浇水”影响的玩法目标。
    /// 例如树长大、机关充能、泥土湿润度提升等。
    /// </summary>
    public interface IPbfWaterTarget
    {
        void ReceiveWater(WaterInput input);
    }
}
