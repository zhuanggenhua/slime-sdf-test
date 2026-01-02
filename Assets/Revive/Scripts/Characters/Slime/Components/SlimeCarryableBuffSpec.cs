using UnityEngine;

namespace Revive.Slime
{
    [DisallowMultipleComponent]
    public class SlimeCarryableBuffSpec : MonoBehaviour
    {
        [System.Serializable]
        public struct BuffModifiers
        {
            [ChineseHeader("数值修正")]
            [ChineseLabel("移动速度倍率")]
            [Min(0f), DefaultValue(1f)]
            public float MoveSpeedMultiplier;

            [ChineseLabel("跳跃冲量倍率")]
            [Min(0f), DefaultValue(1f)]
            public float JumpImpulseMultiplier;

            [ChineseLabel("额外跳跃次数")]
            [Min(0), DefaultValue(0)]
            public int ExtraJumps;

            [ChineseLabel("投掷距离倍率")]
            [Min(0f), DefaultValue(1f)]
            public float ThrowRangeMultiplier;

            [ChineseLabel("风场免疫")]
            [DefaultValue(false)]
            public bool WindFieldImmune;

            [ChineseLabel("形变扼制倍率")]
            [Min(0f), DefaultValue(1f)]
            public float DeformLimitMultiplier;

            [ChineseLabel("最大下落速度倍率"), Tooltip("最大下落速度 = 角色基础MaximumFallSpeed × 倍率。越小越像滑翔。")]
            [Min(0f), DefaultValue(1f)]
            public float MaximumFallSpeedMultiplier;

            public static BuffModifiers Default =>
                new BuffModifiers
                {
                    MoveSpeedMultiplier = 1f,
                    JumpImpulseMultiplier = 1f,
                    ExtraJumps = 0,
                    ThrowRangeMultiplier = 1f,
                    WindFieldImmune = false,
                    DeformLimitMultiplier = 1f,
                    MaximumFallSpeedMultiplier = 1f
                };
        }

        [ChineseHeader("携带 Buff")]
        [ChineseLabel("启用")]
        [DefaultValue(true)]
        public bool EnableCarry = true;

        [ChineseLabel("携带时效果")]
        public BuffModifiers Carry = BuffModifiers.Default;

        [ChineseHeader("消耗 Buff")]
        [ChineseLabel("启用")]
        [DefaultValue(false)]
        public bool EnableConsume = false;

        [ChineseLabel("持续时间(秒)")]
        [Min(0.01f), DefaultValue(10f)]
        public float DurationSeconds = 10f;

        [ChineseLabel("覆盖主体颜色")]
        [DefaultValue(true)]
        public bool OverrideTint = true;

        [ChineseLabel("主体颜色")]
        [DefaultValue(1f, 1f, 1f, 1f)]
        public Color Tint = Color.white;

        [ChineseLabel("消耗时效果")]
        public BuffModifiers Consume = BuffModifiers.Default;

        [ChineseHeader("消耗表现")]
        [ChineseLabel("溶解时长(秒)")]
        [Min(0f), DefaultValue(0.35f)]
        public float ConsumeDissolveSeconds = 0.35f;

        [ChineseLabel("溶解材质(可选)")]
        public Material ConsumeDissolveMaterial;

        [ChineseLabel("气泡爆发数量")]
        [Min(0), DefaultValue(60)]
        public int ConsumeBubbleBurstCount = 60;

        [ChineseLabel("气泡寿命(秒)")]
        [Min(0f), DefaultValue(1f)]
        public float ConsumeBubbleLifetimeSeconds = 1f;

        [ChineseLabel("气泡半径倍率")]
        [Min(0f), DefaultValue(1.2f)]
        public float ConsumeBubbleRadiusMultiplier = 1.2f;

        [ChineseLabel("气泡上升速度(世界)")]
        [Min(0f), DefaultValue(0.6f)]
        public float ConsumeBubbleUpSpeedWorld = 0.6f;

        [ChineseLabel("气泡强化持续(秒)")]
        [Min(0f), DefaultValue(0f)]
        public float ConsumeBubbleBoostSeconds = 0f;

        [ChineseLabel("气泡强化倍率")]
        [Min(0f), DefaultValue(2f)]
        public float ConsumeBubbleBoostMultiplier = 2f;

        [ChineseLabel("气泡尺寸强化倍率")]
        [Min(0f), DefaultValue(1.2f)]
        public float ConsumeBubbleBoostSizeMultiplier = 1.2f;
    }
}
