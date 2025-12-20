using MoreMountains.Tools;
using MoreMountains.TopDownEngine;

namespace Revive
{
    public class SlimeCharacter : Character
    {
        // 通过添加新的 MMStateMachine 来扩展逻辑
        public MMStateMachine<SlimeStates> SlimeStateMachine;
    
        protected override void Awake()
        {
            base.Awake();
            // 初始化自定义状态机
            SlimeStateMachine = new MMStateMachine<SlimeStates>(gameObject, true);
        }
    }
}
