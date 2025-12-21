using UnityEngine;

namespace Slime
{
    /// <summary>
    /// 史莱姆碰撞体信息 - 标记碰撞体类型，影响史莱姆的碰撞行为
    /// </summary>
    public class SlimeColliderInfo : MonoBehaviour
    {
        /// <summary>
        /// 碰撞体类型
        /// </summary>
        public enum ColliderType
        {
            /// <summary>普通地面/障碍物 - 史莱姆被推开</summary>
            Ground = 0,
            
            /// <summary>可攀爬表面 - 史莱姆可以沿表面移动（如藤蔓、墙壁）</summary>
            Climbable = 1,
            
            /// <summary>弹性表面 - 史莱姆会被弹开（预留）</summary>
            Bouncy = 2,
            
            /// <summary>粘性表面 - 史莱姆会粘附（预留）</summary>
            Sticky = 3,
        }
        
        [Tooltip("碰撞体类型 - 决定史莱姆与此碰撞体的交互方式")]
        public ColliderType colliderType = ColliderType.Ground;
        
        [Tooltip("表面摩擦力 - 影响沿表面移动的速度衰减（仅Climbable有效）")]
        [Range(0f, 1f)]
        public float surfaceFriction = 0.3f;
        
        [Tooltip("弹力系数 - 碰撞后的反弹力度（仅Bouncy有效）")]
        [Range(0f, 2f)]
        public float bounciness = 1f;
    }
}
