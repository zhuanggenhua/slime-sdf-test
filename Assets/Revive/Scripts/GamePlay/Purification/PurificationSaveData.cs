using UnityEngine;
using System.Collections.Generic;

namespace Revive.GamePlay.Purification
{
    /// <summary>
    /// 序列化的指示物数据（用于存储）
    /// </summary>
    [System.Serializable]
    public class SerializedIndicator
    {
        public string Name;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float ContributionValue;
        public string IndicatorType;
        public float Timestamp;
        
        /// <summary>
        /// 从PurificationIndicator转换
        /// </summary>
        public SerializedIndicator(PurificationIndicator indicator)
        {
            Name = indicator.Name;
            PositionX = indicator.Position.x;
            PositionY = indicator.Position.y;
            PositionZ = indicator.Position.z;
            ContributionValue = indicator.ContributionValue;
            IndicatorType = indicator.IndicatorType;
            Timestamp = indicator.Timestamp;
        }
        
        /// <summary>
        /// 转换为PurificationIndicator
        /// </summary>
        public PurificationIndicator ToIndicator()
        {
            Vector3 position = new Vector3(PositionX, PositionY, PositionZ);
            return new PurificationIndicator(Name, position, ContributionValue, IndicatorType)
            {
                Timestamp = Timestamp
            };
        }
    }
    
    /// <summary>
    /// 净化系统存档数据
    /// </summary>
    [System.Serializable]
    public class PurificationSaveData
    {
        /// <summary>
        /// 存档时间
        /// </summary>
        public string SaveTime;
        
        /// <summary>
        /// 场景名称
        /// </summary>
        public string SceneName;
        
        /// <summary>
        /// 序列化的指示物列表
        /// </summary>
        public List<SerializedIndicator> Indicators = new List<SerializedIndicator>();
        
        /// <summary>
        /// 构造函数
        /// </summary>
        public PurificationSaveData()
        {
            SaveTime = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        }
        
        /// <summary>
        /// 从指示物列表创建存档数据
        /// </summary>
        public static PurificationSaveData FromIndicators(List<PurificationIndicator> indicators)
        {
            PurificationSaveData data = new PurificationSaveData();
            foreach (var indicator in indicators)
            {
                data.Indicators.Add(new SerializedIndicator(indicator));
            }
            return data;
        }
        
        /// <summary>
        /// 转换为指示物列表
        /// </summary>
        public void ToIndicators(List<PurificationIndicator> indicators)
        {
            indicators.Clear();
            foreach (var serialized in Indicators)
            {
                indicators.Add(serialized.ToIndicator());
            }
        }
    }
}

