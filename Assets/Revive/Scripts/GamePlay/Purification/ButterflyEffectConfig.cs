using UnityEngine;

namespace Revive.GamePlay.Purification
{
    /// <summary>
    /// 蝴蝶特效配置资源
    /// 可在多个花朵之间共享配置
    /// </summary>
    [CreateAssetMenu(fileName = "ButterflyEffectConfig", menuName = "Revive/Purification/Butterfly Effect Config")]
    public class ButterflyEffectConfig : ScriptableObject
    {
        [Header("蝴蝶预制体")]
        [Tooltip("蝴蝶预制体数组（随机选择一个生成）")]
        public GameObject[] ButterflyPrefabs;
        
        [Header("生成设置")]
        [Tooltip("开花时生成蝴蝶的概率（0-1）")]
        [Range(0f, 1f)]
        public float SpawnChance = 0.3f;
        
        [Tooltip("蝴蝶生成位置偏移（相对于花朵）")]
        public Vector3 SpawnOffset = new Vector3(0f, 1f, 0f);
        
        [Tooltip("蝴蝶生成位置随机半径")]
        public float SpawnRandomRadius = 0.5f;
        
        [Header("生命周期")]
        [Tooltip("蝴蝶自动销毁时间（秒，0表示不自动销毁）")]
        public float Lifetime = 0f;
        
        [Tooltip("花凋谢时是否移除蝴蝶")]
        public bool RemoveOnWither = true;
        
        /// <summary>
        /// 获取随机蝴蝶预制体
        /// </summary>
        public GameObject GetRandomPrefab()
        {
            if (ButterflyPrefabs == null || ButterflyPrefabs.Length == 0)
                return null;
            
            return ButterflyPrefabs[Random.Range(0, ButterflyPrefabs.Length)];
        }
        
        /// <summary>
        /// 检查配置是否有效
        /// </summary>
        public bool IsValid()
        {
            return ButterflyPrefabs != null && ButterflyPrefabs.Length > 0;
        }
        
        /// <summary>
        /// 计算生成位置（基于花朵位置）
        /// </summary>
        public Vector3 CalculateSpawnPosition(Vector3 flowerPosition)
        {
            Vector3 position = flowerPosition + SpawnOffset;
            
            if (SpawnRandomRadius > 0f)
            {
                Vector2 randomCircle = Random.insideUnitCircle * SpawnRandomRadius;
                position += new Vector3(randomCircle.x, 0f, randomCircle.y);
            }
            
            return position;
        }
    }
}

