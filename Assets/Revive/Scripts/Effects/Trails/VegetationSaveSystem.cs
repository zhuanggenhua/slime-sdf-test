using UnityEngine;
using MoreMountains.Tools;
using System.Collections.Generic;
using System.Linq;

namespace Revive.Effects
{
    /// <summary>
    /// 可序列化的Vector3
    /// </summary>
    [System.Serializable]
    public struct SerializableVector3
    {
        public float x;
        public float y;
        public float z;
        
        public SerializableVector3(Vector3 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
        }
        
        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }
        
        public static implicit operator Vector3(SerializableVector3 sv)
        {
            return sv.ToVector3();
        }
        
        public static implicit operator SerializableVector3(Vector3 v)
        {
            return new SerializableVector3(v);
        }
    }
    
    /// <summary>
    /// 路径点数据（用于序列化）
    /// </summary>
    [System.Serializable]
    public class PathPointData
    {
        public SerializableVector3 Position;
        public SerializableVector3 Normal;
        public float Timestamp;
        public int VegetationTypeIndex;
        
        public PathPointData() { }
        
        public PathPointData(SpawnPathPoint point)
        {
            Position = point.Position;
            Normal = point.Normal;
            Timestamp = point.Timestamp;
            VegetationTypeIndex = point.VegetationTypeIndex;
        }
        
        public SpawnPathPoint ToSpawnPathPoint()
        {
            return new SpawnPathPoint(Position, Normal, VegetationTypeIndex)
            {
                Timestamp = Timestamp
            };
        }
    }
    
    /// <summary>
    /// 植被存档数据
    /// </summary>
    [System.Serializable]
    public class VegetationSaveData
    {
        public List<PathPointData> PathPoints = new List<PathPointData>();
        public float SaveTime;
        public string SceneName;
        
        public VegetationSaveData()
        {
            SaveTime = Time.time;
            SceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        }
    }
    
    /// <summary>
    /// 植被存档管理器（扩展VegetationGrowthTrail）
    /// </summary>
    public partial class VegetationGrowthTrail
    {
        /// <summary>
        /// 保存到文件
        /// </summary>
        public void SaveToFile()
        {
            SaveToFile(SaveFileName);
        }
        
        /// <summary>
        /// 保存到指定文件
        /// </summary>
        public void SaveToFile(string filename)
        {
            VegetationSaveData saveData = new VegetationSaveData();
            
            // 转换路径点数据
            saveData.PathPoints = _spawnPath.Select(p => new PathPointData(p)).ToList();
            
            try
            {
                MMSaveLoadManager.Save(saveData, filename);
                Debug.Log($"[VegetationGrowthTrail] 保存成功: {filename}, 路径点数量: {saveData.PathPoints.Count}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[VegetationGrowthTrail] 保存失败: {e.Message}");
            }
        }
        
        /// <summary>
        /// 从文件加载
        /// </summary>
        public void LoadFromFile()
        {
            LoadFromFile(SaveFileName);
        }
        
        /// <summary>
        /// 从指定文件加载
        /// </summary>
        public void LoadFromFile(string filename)
        {
            try
            {
                VegetationSaveData saveData = (VegetationSaveData)MMSaveLoadManager.Load(
                    typeof(VegetationSaveData),
                    filename
                );
                
                if (saveData == null)
                {
                    Debug.LogWarning($"[VegetationGrowthTrail] 未找到存档文件: {filename}");
                    return;
                }
                
                // 清除现有植被
                ClearAll();
                
                // 重建植被
                RegenerateVegetationFromPathPoints(saveData.PathPoints);
                
                Debug.Log($"[VegetationGrowthTrail] 加载成功: {filename}, 路径点数量: {saveData.PathPoints.Count}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[VegetationGrowthTrail] 加载失败: {e.Message}");
            }
        }
        
        /// <summary>
        /// 根据路径点重新生成植被
        /// </summary>
        private void RegenerateVegetationFromPathPoints(List<PathPointData> pathPoints)
        {
            foreach (var pointData in pathPoints)
            {
                SpawnPathPoint point = pointData.ToSpawnPathPoint();
                _spawnPath.Add(point);
                
                // 在这个点重新生成植被
                RegenerateVegetationAtPoint(point);
            }
        }
        
        /// <summary>
        /// 在指定点重新生成植被
        /// </summary>
        private void RegenerateVegetationAtPoint(SpawnPathPoint point)
        {
            if (point.VegetationTypeIndex < 0 || point.VegetationTypeIndex >= VegetationTypes.Count)
            {
                Debug.LogWarning($"[VegetationGrowthTrail] 无效的植被类型索引: {point.VegetationTypeIndex}");
                return;
            }
            
            VegetationTypeConfig config = VegetationTypes[point.VegetationTypeIndex];
            VegetationRenderer renderer = _renderers[point.VegetationTypeIndex];
            
            // 根据密度生成植被
            int spawnCount = Mathf.RoundToInt(Density * SpawnDistanceThreshold);
            
            for (int i = 0; i < spawnCount; i++)
            {
                // 在扩散半径内随机位置
                Vector2 randomCircle = UnityEngine.Random.insideUnitCircle * SpreadRadius;
                Vector3 spawnOffset = new Vector3(randomCircle.x, 0, randomCircle.y);
                Vector3 spawnPos = point.Position + spawnOffset;
                
                // 重新检测地面
                Vector3 groundPos, groundNormal;
                if (TryGetGroundInfo(spawnPos + Vector3.up * 2f, out groundPos, out groundNormal))
                {
                    spawnPos = groundPos;
                }
                else
                {
                    spawnPos.y = point.Position.y;
                    groundNormal = point.Normal;
                }
                
                // 创建植被实例（生成时间设为过去，使其立即完成生长）
                VegetationInstance instance = CreateVegetationInstance(
                    spawnPos,
                    groundNormal,
                    point.VegetationTypeIndex,
                    config
                );
                
                // 设置为已完成生长的时间戳
                instance.SpawnTime = Time.time - GrowthDuration;
                
                renderer.AddInstance(instance);
            }
        }
        
        /// <summary>
        /// 删除存档文件
        /// </summary>
        public void DeleteSaveFile()
        {
            DeleteSaveFile(SaveFileName);
        }
        
        /// <summary>
        /// 删除指定存档文件
        /// </summary>
        public void DeleteSaveFile(string filename)
        {
            try
            {
                MMSaveLoadManager.DeleteSave(filename);
                Debug.Log($"[VegetationGrowthTrail] 删除存档成功: {filename}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[VegetationGrowthTrail] 删除存档失败: {e.Message}");
            }
        }
        
        /// <summary>
        /// 检查存档文件是否存在
        /// </summary>
        public bool SaveFileExists()
        {
            return SaveFileExists(SaveFileName);
        }
        
        /// <summary>
        /// 检查指定存档文件是否存在
        /// </summary>
        public bool SaveFileExists(string filename)
        {
            // MMSaveLoadManager没有直接的Exists方法，尝试加载来检查
            try
            {
                object data = MMSaveLoadManager.Load(typeof(VegetationSaveData), filename);
                return data != null;
            }
            catch
            {
                return false;
            }
        }
    }
}

