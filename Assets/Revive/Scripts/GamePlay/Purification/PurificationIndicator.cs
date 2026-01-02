using UnityEngine;
using System;

namespace Revive.GamePlay.Purification
{
    /// <summary>
    /// 净化指示物数据类
    /// 记录单个净化事件的位置、贡献值等信息
    /// </summary>
    [System.Serializable]
    public class PurificationIndicator
    {
        /// <summary>
        /// 指示物名称（用于调试标识）
        /// </summary>
        public string Name;
        
        /// <summary>
        /// 世界坐标位置
        /// </summary>
        public Vector3 Position;
        
        /// <summary>
        /// 净化贡献值
        /// </summary>
        public float ContributionValue;
        
        /// <summary>
        /// 指示物类型（如：Idle逗留、Water浇水、Spore孢子等）
        /// </summary>
        public string IndicatorType;
        
        /// <summary>
        /// 创建时间戳
        /// </summary>
        public float Timestamp;
        
        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="name">指示物名称</param>
        /// <param name="position">世界坐标</param>
        /// <param name="contributionValue">贡献值</param>
        /// <param name="indicatorType">类型标识</param>
        public PurificationIndicator(string name, Vector3 position, float contributionValue, string indicatorType)
        {
            Name = name;
            Position = position;
            ContributionValue = contributionValue;
            IndicatorType = indicatorType;
            Timestamp = Time.time;
        }
        
        /// <summary>
        /// 获取指示物的年龄（秒）
        /// </summary>
        public float GetAge()
        {
            return Time.time - Timestamp;
        }
        
        /// <summary>
        /// 计算到目标位置的距离
        /// </summary>
        public float DistanceTo(Vector3 target)
        {
            return Vector3.Distance(Position, target);
        }
        
        /// <summary>
        /// 判断是否在指定半径范围内
        /// </summary>
        public bool IsInRange(Vector3 target, float radius)
        {
            return DistanceTo(target) <= radius;
        }
        
        public override string ToString()
        {
            return $"[{Name}] Type:{IndicatorType} Position:{Position} Value:{ContributionValue} Age:{GetAge():F1}s";
        }
    }
}

