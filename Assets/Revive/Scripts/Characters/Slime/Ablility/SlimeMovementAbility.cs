using MoreMountains.TopDownEngine;
using Slime;
using UnityEngine;

namespace Revive
{
    /// <summary>
    /// 史莱姆移动能力 - 继承 CharacterMovement，自动绑定 Slime_PBF
    /// 替换 CharacterMovement 使用
    /// </summary>
    [AddComponentMenu("TopDown Engine/Character/Abilities/Slime Movement")]
    public class SlimeMovementAbility : CharacterMovement
    {
        [Header("Slime Bindings")]
        [Tooltip("Slime_PBF 组件，用于自动绑定 trans 和 velocityController")]
        public Slime_PBF SlimePBF;

        protected TopDownController3D _controller3D;

        protected override void Initialization()
        {
            base.Initialization();
            
            _controller3D = _controller as TopDownController3D;
            
            if (SlimePBF != null)
            {
                if (SlimePBF.trans == null)
                {
                    SlimePBF.trans = transform;
                }
                if (SlimePBF.velocityController == null && _controller3D != null)
                {
                    SlimePBF.velocityController = _controller3D;
                }
            }
        }
    }
}
