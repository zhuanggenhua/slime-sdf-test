using UnityEngine;
using MoreMountains.TopDownEngine;

namespace Revive.Environment
{
    /// <summary>
    /// 弹跳平台顶部检测区域
    /// </summary>
    public class JumpPadTrigger : MonoBehaviour
    {
        public MushroomJumpPad3D jumpPad;
        
        private void OnTriggerEnter(Collider other)
        {
            TryTriggerJump(other);
        }

        private void OnTriggerExit(Collider other)
        {
            if (jumpPad == null)
            {
                return;
            }
            jumpPad.OnPlayerExitTrigger(other);
        }
        
        private void OnTriggerStay(Collider other)
        {
            // 需要OnTriggerStay来检测落下时的弹跳
            TryTriggerJump(other);
        }
        
        private void TryTriggerJump(Collider other)
        {
            if (jumpPad == null)
            {
                Debug.LogWarning($"[JumpPadTrigger] jumpPad 引用为空!");
                return;
            }
            
            TopDownController3D controller = other.GetComponent<TopDownController3D>();
            if (controller == null)
            {
                controller = other.GetComponentInParent<TopDownController3D>();
            }
            
            if (controller != null)
            {
                jumpPad.OnPlayerEnterTrigger(other);
            }
        }
    }
}
