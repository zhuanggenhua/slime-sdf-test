using UnityEngine;
using System;
using System.Collections.Generic;

namespace Revive.Effects
{
    /// <summary>
    /// 植被类型配置
    /// </summary>
    [System.Serializable]
    public class VegetationTypeConfig
    {
        [Tooltip("植被预制件（用于提取Mesh和Material）")]
        public GameObject Prefab;
        
        [Tooltip("网格")]
        public Mesh Mesh;
        
        [Tooltip("材质（必须支持GPU Instancing）")]
        public Material Material;
        
        [Tooltip("生成概率")]
        [Range(0f, 1f)]
        public float SpawnProbability = 0.5f;
        
        [Tooltip("缩放范围")]
        public Vector2 ScaleRange = new Vector2(0.8f, 1.2f);
        
        /// <summary>
        /// 从预制件自动提取Mesh和Material
        /// </summary>
        public void ExtractFromPrefab()
        {
            if (Prefab == null) return;
            
            MeshFilter meshFilter = Prefab.GetComponentInChildren<MeshFilter>();
            if (meshFilter != null)
            {
                Mesh = meshFilter.sharedMesh;
            }
            
            MeshRenderer meshRenderer = Prefab.GetComponentInChildren<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.sharedMaterial != null)
            {
                Material = meshRenderer.sharedMaterial;
            }
        }
        
        /// <summary>
        /// 验证配置是否有效
        /// </summary>
        public bool IsValid()
        {
            return Mesh != null && Material != null;
        }
    }
    
    /// <summary>
    /// 路径生成点数据（用于存档）
    /// </summary>
    [System.Serializable]
    public class SpawnPathPoint
    {
        public Vector3 Position;
        public Vector3 Normal;
        public float Timestamp;
        public int VegetationTypeIndex;
        
        public SpawnPathPoint(Vector3 position, Vector3 normal, int typeIndex)
        {
            Position = position;
            Normal = normal;
            Timestamp = Time.time;
            VegetationTypeIndex = typeIndex;
        }
    }
    
    /// <summary>
    /// 植被实例数据
    /// </summary>
    public class VegetationInstance
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public float SpawnTime;
        public int TypeIndex;
        
        /// <summary>
        /// 获取生长阶段 (0-1)
        /// </summary>
        public float GetGrowthPhase(float currentTime, float growthDuration)
        {
            float age = currentTime - SpawnTime;
            return Mathf.Clamp01(age / growthDuration);
        }
        
        /// <summary>
        /// 获取世界变换矩阵（包含生长动画）
        /// </summary>
        public Matrix4x4 GetMatrix(float growthScale)
        {
            Vector3 finalScale = Scale * growthScale;
            return Matrix4x4.TRS(Position, Rotation, finalScale);
        }
    }
    
    /// <summary>
    /// 植被生长尾迹效果
    /// </summary>
    public partial class VegetationGrowthTrail : TrailEffectBase
    {
        [Header("Vegetation Settings")]
        [Tooltip("植被类型配置列表")]
        public List<VegetationTypeConfig> VegetationTypes = new List<VegetationTypeConfig>();
        
        [Tooltip("密度（每米生成数量）")]
        [Range(1f, 20f)]
        public float Density = 5f;
        
        [Tooltip("扩散半径")]
        public float SpreadRadius = 1.5f;
        
        [Header("Growth Animation")]
        [Tooltip("生长时长")]
        public float GrowthDuration = 2f;
        
        [Tooltip("生长曲线")]
        public AnimationCurve GrowthCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        
        [Header("Wind")]
        [Tooltip("启用风场交互")]
        public bool EnableWindInteraction = true;
        
        [Tooltip("风区（可选，为空则查找场景中的WindZone）")]
        public WindZone WindZone;
        
        [Header("Rendering")]
        [Tooltip("每批最大实例数（GPU限制）")]
        public int MaxInstancesPerBatch = 1023;
        
        [Tooltip("是否投射阴影")]
        public bool CastShadows = true;
        
        [Tooltip("是否接收阴影")]
        public bool ReceiveShadows = true;
        
        [Header("Save System")]
        [Tooltip("存档文件名")]
        public string SaveFileName = "VegetationTrail";
        
        private List<VegetationRenderer> _renderers = new List<VegetationRenderer>();
        private List<SpawnPathPoint> _spawnPath = new List<SpawnPathPoint>();
        
        public override int ActiveEffectCount
        {
            get
            {
                int total = 0;
                foreach (var renderer in _renderers)
                {
                    total += renderer.InstanceCount;
                }
                return total;
            }
        }
        
        public int TotalInstanceCount => ActiveEffectCount;
        public int PathPointCount => _spawnPath.Count;
        
        protected override void Awake()
        {
            base.Awake();
            
            // 自动提取预制件的Mesh和Material
            foreach (var config in VegetationTypes)
            {
                if (config.Mesh == null || config.Material == null)
                {
                    config.ExtractFromPrefab();
                }
            }
            
            // 验证配置
            ValidateConfigurations();
            
            // 初始化渲染器
            InitializeRenderers();
            
            // 查找WindZone
            if (EnableWindInteraction && WindZone == null)
            {
                WindZone = FindObjectOfType<WindZone>();
            }
        }
        
        protected override void SpawnEffect(Vector3 position, Vector3 normal)
        {
            // 根据密度计算生成数量
            int spawnCount = Mathf.RoundToInt(Density * SpawnDistanceThreshold);
            
            for (int i = 0; i < spawnCount; i++)
            {
                // 选择植被类型
                int typeIndex = SelectRandomVegetationType();
                if (typeIndex < 0) continue;
                
                VegetationTypeConfig config = VegetationTypes[typeIndex];
                VegetationRenderer renderer = _renderers[typeIndex];
                
                // 在扩散半径内随机位置
                Vector2 randomCircle = UnityEngine.Random.insideUnitCircle * SpreadRadius;
                Vector3 spawnOffset = new Vector3(randomCircle.x, 0, randomCircle.y);
                Vector3 spawnPos = position + spawnOffset;
                
                // 重新检测地面（确保植被在正确的高度）
                Vector3 groundPos, groundNormal;
                if (TryGetGroundInfo(spawnPos + Vector3.up * 2f, out groundPos, out groundNormal))
                {
                    spawnPos = groundPos;
                }
                else
                {
                    spawnPos.y = position.y;
                    groundNormal = normal;
                }
                
                // 创建植被实例
                VegetationInstance instance = CreateVegetationInstance(
                    spawnPos,
                    groundNormal,
                    typeIndex,
                    config
                );
                
                // 添加到渲染器
                renderer.AddInstance(instance);
                
                // 记录路径点（用于存档）
                if (i == 0) // 只记录第一个点代表这个位置
                {
                    _spawnPath.Add(new SpawnPathPoint(position, normal, typeIndex));
                }
            }
        }
        
        protected override void UpdateEffects()
        {
            float currentTime = Time.time;
            
            // 更新所有渲染器
            foreach (var renderer in _renderers)
            {
                renderer.UpdateAndRender(currentTime, GrowthDuration, GrowthCurve, WindZone);
            }
        }
        
        /// <summary>
        /// 验证所有配置
        /// </summary>
        private void ValidateConfigurations()
        {
            for (int i = VegetationTypes.Count - 1; i >= 0; i--)
            {
                if (!VegetationTypes[i].IsValid())
                {
                    Debug.LogWarning($"[VegetationGrowthTrail] 植被配置 {i} 无效，已移除");
                    VegetationTypes.RemoveAt(i);
                }
            }
            
            if (VegetationTypes.Count == 0)
            {
                Debug.LogError("[VegetationGrowthTrail] 没有有效的植被配置！");
            }
        }
        
        /// <summary>
        /// 初始化渲染器（每个植被类型一个）
        /// </summary>
        private void InitializeRenderers()
        {
            _renderers.Clear();
            
            foreach (var config in VegetationTypes)
            {
                VegetationRenderer renderer = new VegetationRenderer(
                    config.Mesh,
                    config.Material,
                    MaxInstancesPerBatch,
                    CastShadows,
                    ReceiveShadows
                );
                _renderers.Add(renderer);
            }
        }
        
        /// <summary>
        /// 根据概率选择植被类型
        /// </summary>
        private int SelectRandomVegetationType()
        {
            if (VegetationTypes.Count == 0) return -1;
            
            float totalProbability = 0f;
            foreach (var config in VegetationTypes)
            {
                totalProbability += config.SpawnProbability;
            }
            
            if (totalProbability <= 0f) return 0;
            
            float randomValue = UnityEngine.Random.Range(0f, totalProbability);
            float accumulated = 0f;
            
            for (int i = 0; i < VegetationTypes.Count; i++)
            {
                accumulated += VegetationTypes[i].SpawnProbability;
                if (randomValue <= accumulated)
                {
                    return i;
                }
            }
            
            return VegetationTypes.Count - 1;
        }
        
        /// <summary>
        /// 创建植被实例
        /// </summary>
        private VegetationInstance CreateVegetationInstance(
            Vector3 position,
            Vector3 normal,
            int typeIndex,
            VegetationTypeConfig config)
        {
            // 随机旋转
            float randomRotation = UnityEngine.Random.Range(0f, 360f);
            Quaternion rotation = Quaternion.LookRotation(normal) * Quaternion.Euler(0, randomRotation, 0);
            
            // 随机缩放
            float randomScale = UnityEngine.Random.Range(config.ScaleRange.x, config.ScaleRange.y);
            Vector3 scale = Vector3.one * randomScale;
            
            return new VegetationInstance
            {
                Position = position,
                Rotation = rotation,
                Scale = scale,
                SpawnTime = Time.time,
                TypeIndex = typeIndex
            };
        }
        
        protected override void DrawDebugGizmos()
        {
            if (_spawnPath == null || _spawnPath.Count == 0) return;
            
            // 绘制路径线
            Gizmos.color = Color.yellow;
            for (int i = 0; i < _spawnPath.Count - 1; i++)
            {
                Gizmos.DrawLine(_spawnPath[i].Position, _spawnPath[i + 1].Position);
            }
            
            // 绘制路径点
            Gizmos.color = Color.green;
            foreach (var point in _spawnPath)
            {
                Gizmos.DrawWireSphere(point.Position, 0.2f);
                Gizmos.DrawRay(point.Position, point.Normal * 0.5f);
            }
            
            #if UNITY_EDITOR
            // 显示统计信息
            if (_spawnPath.Count > 0)
            {
                Vector3 labelPos = _spawnPath[_spawnPath.Count - 1].Position + Vector3.up * 2f;
                UnityEditor.Handles.Label(labelPos, 
                    $"Vegetation: {TotalInstanceCount}\nPath Points: {PathPointCount}");
            }
            #endif
        }
        
        /// <summary>
        /// 清除所有植被
        /// </summary>
        public void ClearAll()
        {
            foreach (var renderer in _renderers)
            {
                renderer.Clear();
            }
            _spawnPath.Clear();
        }
        
        private void OnDestroy()
        {
            ClearAll();
        }
    }
}

