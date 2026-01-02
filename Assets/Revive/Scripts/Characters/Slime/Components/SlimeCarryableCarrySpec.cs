using UnityEngine;

namespace Revive.Slime
{
    [DisallowMultipleComponent]
    public class SlimeCarryableCarrySpec : MonoBehaviour
    {
        [ChineseHeader("携带能力")]

        [ChineseLabel("最大下落速度倍率"), Tooltip("持有该物体时：最大下落速度 = 角色基础MaximumFallSpeed × 倍率。越小越像滑翔。")]
        [Range(0f, 1f), DefaultValue(1f)]
        public float MaximumFallSpeedMultiplier = 1f;

        [ChineseHeader("数值修正")]

        [ChineseLabel("移动速度倍率")]
        [Min(0f), DefaultValue(1f)]
        public float MoveSpeedMultiplier = 1f;

        [ChineseLabel("投掷距离倍率")]
        [Min(0f), DefaultValue(1f)]
        public float ThrowRangeMultiplier = 1f;

        [ChineseLabel("风场免疫")]
        [DefaultValue(false)]
        public bool WindFieldImmune = false;

        [ChineseLabel("形变扼制倍率")]
        [Min(0f), DefaultValue(1f)]
        public float DeformLimitMultiplier = 1f;
    }
}
