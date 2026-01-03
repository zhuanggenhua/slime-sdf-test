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
        /// 辐射范围（米），指示物影响的半径范围
        /// </summary>
        public float RadiationRadius;
        
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
        /// <param name="radiationRadius">辐射范围（默认8米）</param>
        public PurificationIndicator(string name, Vector3 position, float contributionValue, string indicatorType, float radiationRadius = 8f)
        {
            Name = name;
            Position = position;
            ContributionValue = contributionValue;
            IndicatorType = indicatorType;
            RadiationRadius = radiationRadius;
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
        
        /// <summary>
        /// 计算与查询球体的相交权重（线性衰减）
        /// 用于模拟两个球体的相交影响程度
        /// </summary>
        /// <param name="queryPosition">查询位置（球心）</param>
        /// <param name="queryRadius">查询半径</param>
        /// <returns>权重 0-1，1表示完全重叠，0表示不相交</returns>
        public float CalculateIntersectionWeight(Vector3 queryPosition, float queryRadius)
        {
            float distance = Vector3.Distance(Position, queryPosition);
            float radiusSum = RadiationRadius + queryRadius;
            
            // 完全不相交：两球心距离 >= 两半径之和
            if (distance >= radiusSum)
                return 0f;
            
            // 完全重叠：一个球心在另一个球内
            // 当距离 <= |r1 - r2| 时，小球完全在大球内
            float radiusDiff = Mathf.Abs(RadiationRadius - queryRadius);
            if (distance <= radiusDiff)
                return 1f;
            
            // 部分相交：线性衰减
            // 距离越近权重越大：weight = 1 - (distance / radiusSum)
            // 当 distance = 0 时，weight = 1（完全重叠）
            // 当 distance = radiusSum 时，weight = 0（刚好接触）
            return 1f - (distance / radiusSum);
        }
        
        public override string ToString()
        {
            return $"[{Name}] Type:{IndicatorType} Position:{Position} Value:{ContributionValue} Radius:{RadiationRadius}m Age:{GetAge():F1}s";
        }
    }
}

