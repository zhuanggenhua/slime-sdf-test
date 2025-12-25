using MoreMountains.TopDownEngine;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆移动能力 - 继承 CharacterMovement，自动绑定 Slime_PBF
    /// 替换 CharacterMovement 使用
    /// </summary>
    [AddComponentMenu("TopDown Engine/Character/Abilities/Slime Movement")]
    public class SlimeMovementAbility : CharacterMovement
    {
        [Header("Slime Bindings")]
        [Tooltip("Slime_PBF 组件，用于自动绑定 trans")]
        public Slime_PBF SlimePBF;

        protected override void Initialization()
        {
            base.Initialization();

            if (SlimePBF == null)
            {
                SlimePBF = GetComponentInChildren<Slime_PBF>();
            }

            if (SlimePBF == null)
            {
                Debug.LogWarning("[SlimeMovementAbility] SlimePBF is null and no Slime_PBF component was found on the same GameObject.", this);
                return;
            }

            if (SlimePBF.trans == null)
            {
                SlimePBF.trans = transform;
            }
        }
    }
}
