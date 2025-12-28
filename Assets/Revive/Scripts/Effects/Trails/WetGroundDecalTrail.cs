using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;

namespace Revive.Effects
{
    /// <summary>
    /// 湿润地面Decal配置
    /// </summary>
    [System.Serializable]
    public class DecalMaterialConfig
    {
        [Tooltip("Decal材质")]
        public Material DecalMaterial;
        
        [Tooltip("基础颜色")]
        public Color BaseColor = Color.white;
        
        [Tooltip("生成概率")]
        [Range(0f, 1f)]
        public float SpawnProbability = 1f;
    }
    
    /// <summary>
    /// Decal实例数据
    /// </summary>
    public class DecalInstance
    {
        public GameObject GameObject;
        public DecalProjector Projector;
        public Material MaterialInstance;
        public float SpawnTime;
        public float Lifetime;
        public float FadeInDuration;
        public float FadeOutDuration;
        
        /// <summary>
        /// 获取当前生命周期阶段（0=淡入, 1=持续, 2=淡出）
        /// </summary>
        public int GetLifecyclePhase(float currentTime)
        {
            float age = currentTime - SpawnTime;
            if (age < FadeInDuration) return 0; // Fade-in
            if (age > Lifetime - FadeOutDuration) return 2; // Fade-out
            return 1; // Sustain
        }
        
        /// <summary>
        /// 获取当前Alpha值
        /// </summary>
        public float GetAlpha(float currentTime)
        {
            float age = currentTime - SpawnTime;
            
            if (age < FadeInDuration)
            {
                return age / FadeInDuration;
            }
            else if (age > Lifetime - FadeOutDuration)
            {
                float remainingTime = Lifetime - age;
                return Mathf.Max(0, remainingTime / FadeOutDuration);
            }
            
            return 1f;
        }
        
        /// <summary>
        /// 是否应该被移除
        /// </summary>
        public bool ShouldRemove(float currentTime)
        {
            return (currentTime - SpawnTime) >= Lifetime;
        }
    }
    
    /// <summary>
    /// 湿润地面Decal尾迹效果
    /// </summary>
    public class WetGroundDecalTrail : TrailEffectBase
    {
        [Header("Decal Materials")]
        [Tooltip("Decal材质配置列表")]
        public List<DecalMaterialConfig> DecalMaterials = new List<DecalMaterialConfig>();
        
        [Header("Decal Settings")]
        [Tooltip("Decal尺寸 (宽, 高, 深度)")]
        public Vector3 DecalSize = new Vector3(1f, 1f, 0.5f);
        
        [Tooltip("淡入时长")]
        public float FadeInDuration = 0.3f;
        
        [Tooltip("存活时间")]
        public float Lifetime = 10f;
        
        [Tooltip("淡出时长")]
        public float FadeOutDuration = 1f;
        
        [Header("Randomization")]
        [Tooltip("尺寸随机性")]
        [Range(0f, 1f)]
        public float SizeRandomness = 0.2f;
        
        [Tooltip("旋转随机性（度）")]
        [Range(0f, 360f)]
        public float RotationRandomness = 180f;
        
        [Tooltip("位置偏移半径")]
        public float PositionOffsetRadius = 0.2f;
        
        [Header("Performance")]
        [Tooltip("最大Decal数量")]
        public int MaxDecalCount = 100;
        
        private Queue<DecalInstance> _activeDecals = new Queue<DecalInstance>();
        private GameObject _decalContainer;
        
        public override int ActiveEffectCount => _activeDecals.Count;
        
        public int ActiveDecalCount => _activeDecals.Count;
        
        protected override void Awake()
        {
            base.Awake();
            
            // 创建Decal容器（保持在世界坐标系，不跟随角色）
            _decalContainer = new GameObject("DecalContainer_" + gameObject.name);
            // 不设置父对象，保持在场景根级别，这样Decal就不会跟随角色移动
            _decalContainer.transform.position = Vector3.zero;
        }
        
        protected override void SpawnEffect(Vector3 position, Vector3 normal)
        {
            if (DecalMaterials.Count == 0)
            {
                Debug.LogWarning("[WetGroundDecalTrail] 没有配置Decal材质");
                return;
            }
            
            // 选择一个材质配置
            DecalMaterialConfig config = SelectRandomMaterial();
            if (config == null || config.DecalMaterial == null)
            {
                return;
            }
            
            // 应用随机偏移
            Vector3 randomOffset = Random.insideUnitCircle * PositionOffsetRadius;
            Vector3 spawnPosition = position + new Vector3(randomOffset.x, 0, randomOffset.y);
            
            // 创建Decal GameObject
            GameObject decalObj = new GameObject($"Decal_{_activeDecals.Count}");
            decalObj.transform.SetParent(_decalContainer.transform);
            decalObj.transform.position = spawnPosition;
            
            // 设置朝向（朝下，带随机旋转）
            float randomRotation = Random.Range(-RotationRandomness, RotationRandomness);
            Quaternion rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0, 0, randomRotation);
            decalObj.transform.rotation = rotation;
            
            // 添加DecalProjector组件
            DecalProjector projector = decalObj.AddComponent<DecalProjector>();
            projector.pivot = Vector3.zero;
            
            // 创建材质实例
            Material materialInstance = new Material(config.DecalMaterial);
            // materialInstance.SetColor("_BaseColor", config.BaseColor);
            projector.material = materialInstance;
            
            // 设置Decal尺寸（带随机性）
            float sizeMultiplier = 1f + Random.Range(-SizeRandomness, SizeRandomness);
            projector.size = DecalSize * sizeMultiplier;
            
            // 创建Decal实例数据
            DecalInstance instance = new DecalInstance
            {
                GameObject = decalObj,
                Projector = projector,
                MaterialInstance = materialInstance,
                SpawnTime = Time.time,
                Lifetime = Lifetime,
                FadeInDuration = FadeInDuration,
                FadeOutDuration = FadeOutDuration
            };
            
            // 初始化Alpha为0
            UpdateDecalAlpha(instance, 0f);
            
            // 添加到队列
            _activeDecals.Enqueue(instance);
            
            // 检查数量限制
            while (_activeDecals.Count > MaxDecalCount)
            {
                RemoveOldestDecal();
            }
        }
        
        protected override void UpdateEffects()
        {
            float currentTime = Time.time;
            
            // 更新所有Decal的动画
            foreach (var decal in _activeDecals)
            {
                float alpha = decal.GetAlpha(currentTime);
                UpdateDecalAlpha(decal, alpha);
            }
            
            // 移除过期的Decal
            while (_activeDecals.Count > 0 && _activeDecals.Peek().ShouldRemove(currentTime))
            {
                RemoveOldestDecal();
            }
        }
        
        /// <summary>
        /// 根据概率选择一个材质配置
        /// </summary>
        private DecalMaterialConfig SelectRandomMaterial()
        {
            // 计算总概率
            float totalProbability = 0f;
            foreach (var config in DecalMaterials)
            {
                totalProbability += config.SpawnProbability;
            }
            
            if (totalProbability <= 0f)
            {
                return DecalMaterials[0];
            }
            
            // 随机选择
            float randomValue = Random.Range(0f, totalProbability);
            float accumulated = 0f;
            
            foreach (var config in DecalMaterials)
            {
                accumulated += config.SpawnProbability;
                if (randomValue <= accumulated)
                {
                    return config;
                }
            }
            
            return DecalMaterials[DecalMaterials.Count - 1];
        }
        
        /// <summary>
        /// 更新Decal的Alpha值
        /// </summary>
        private void UpdateDecalAlpha(DecalInstance decal, float alpha)
        {
            if (decal.MaterialInstance != null)
            {
                // 尝试设置常见的透明度属性
                if (decal.MaterialInstance.HasProperty("_Alpha"))
                {
                    decal.MaterialInstance.SetFloat("_Alpha", alpha);
                }
                
                // 也更新颜色的alpha通道
                if (decal.MaterialInstance.HasProperty("_BaseColor"))
                {
                    Color color = decal.MaterialInstance.GetColor("_BaseColor");
                    color.a = alpha;
                    decal.MaterialInstance.SetColor("_BaseColor", color);
                }
                
                // Decal Projector的fadeFactor
                if (decal.Projector != null)
                {
                    decal.Projector.fadeFactor = alpha;
                }
            }
        }
        
        /// <summary>
        /// 移除最老的Decal
        /// </summary>
        private void RemoveOldestDecal()
        {
            if (_activeDecals.Count > 0)
            {
                DecalInstance oldest = _activeDecals.Dequeue();
                if (oldest.GameObject != null)
                {
                    Destroy(oldest.GameObject);
                }
                if (oldest.MaterialInstance != null)
                {
                    Destroy(oldest.MaterialInstance);
                }
            }
        }
        
        protected override void DrawDebugGizmos()
        {
            if (_activeDecals == null) return;
            
            float currentTime = Time.time;
            
            foreach (var decal in _activeDecals)
            {
                if (decal.GameObject == null) continue;
                
                // 根据生命周期阶段设置颜色
                float lifetimePercent = (currentTime - decal.SpawnTime) / decal.Lifetime;
                Gizmos.color = Color.Lerp(Color.green, Color.red, lifetimePercent);
                
                // 绘制Decal边界框
                Vector3 size = decal.Projector != null ? decal.Projector.size : DecalSize;
                Gizmos.matrix = decal.GameObject.transform.localToWorldMatrix;
                Gizmos.DrawWireCube(Vector3.zero, size);
                
                #if UNITY_EDITOR
                // 显示剩余时间
                float remainingTime = decal.Lifetime - (currentTime - decal.SpawnTime);
                UnityEditor.Handles.Label(
                    decal.GameObject.transform.position,
                    $"{remainingTime:F1}s\nα:{decal.GetAlpha(currentTime):F2}"
                );
                #endif
            }
        }
        
        private void OnDestroy()
        {
            // 清理所有Decal
            while (_activeDecals.Count > 0)
            {
                RemoveOldestDecal();
            }
            
            if (_decalContainer != null)
            {
                Destroy(_decalContainer);
            }
        }
    }
}

