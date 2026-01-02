using UnityEngine;

namespace Revive.GamePlay.Purification
{
    /// <summary>
    /// 净化度监听者接口
    /// 实现此接口的对象可以监听净化度变化
    /// </summary>
    public interface IPurificationListener
    {
        /// <summary>
        /// 当净化度发生变化时调用
        /// </summary>
        /// <param name="purificationLevel">当前净化度 (0-1)</param>
        /// <param name="position">监听者位置</param>
        void OnPurificationChanged(float purificationLevel, Vector3 position);
        
        /// <summary>
        /// 获取监听者当前位置
        /// </summary>
        /// <returns>世界坐标位置</returns>
        Vector3 GetListenerPosition();
        
        /// <summary>
        /// 获取监听者名称（用于调试）
        /// </summary>
        /// <returns>监听者名称</returns>
        string GetListenerName();
    }
}

