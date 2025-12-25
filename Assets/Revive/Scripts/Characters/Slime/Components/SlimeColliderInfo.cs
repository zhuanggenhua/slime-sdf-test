using UnityEngine;

namespace Revive.Slime
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
            
            /// <summary>水体 - 史莱姆会扩散和流动</summary>
            Water = 2,
            
            /// <summary>粘性表面 - 史莱姆移动缓慢</summary>
            Sticky = 3,
            
            /// <summary>弹跳平台 - 史莱姆接触后向上弹跳</summary>
            JumpPad = 4
        }
        
        [Tooltip("碰撞体类型 - 决定史莱姆与此碰撞体的交互方式")]
        public ColliderType colliderType = ColliderType.Ground;
        
        [Tooltip("表面摩擦力 - 影响沿表面移动的速度衰减（仅Climbable有效）")]
        [Range(0f, 1f)]
        public float surfaceFriction = 0.3f;
    }
}
