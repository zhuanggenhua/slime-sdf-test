using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

namespace Revive.Effects
{
    /// <summary>
    /// 植被GPU Instancing渲染器
    /// </summary>
    public class VegetationRenderer
    {
        private Mesh _mesh;
        private Material _material;
        private List<VegetationInstance> _instances = new List<VegetationInstance>();
        
        // 渲染数据
        private Matrix4x4[] _matrices;
        private Vector4[] _customData;
        private MaterialPropertyBlock _propertyBlock;
        
        // 渲染参数
        private int _maxInstancesPerBatch;
        private ShadowCastingMode _shadowCastingMode;
        private bool _receiveShadows;
        
        // Shader属性ID（性能优化）
        private static readonly int CustomDataID = Shader.PropertyToID("_CustomData");
        private static readonly int GrowthPhaseID = Shader.PropertyToID("_GrowthPhase");
        private static readonly int WindStrengthID = Shader.PropertyToID("_WindStrength");
        private static readonly int WindSpeedID = Shader.PropertyToID("_WindSpeed");
        
        public int InstanceCount => _instances.Count;
        
        /// <summary>
        /// 构造函数
        /// </summary>
        public VegetationRenderer(
            Mesh mesh,
            Material material,
            int maxInstancesPerBatch = 1023,
            bool castShadows = true,
            bool receiveShadows = true)
        {
            _mesh = mesh;
            _material = material;
            _maxInstancesPerBatch = Mathf.Min(maxInstancesPerBatch, 1023); // GPU硬限制
            _shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            _receiveShadows = receiveShadows;
            
            _propertyBlock = new MaterialPropertyBlock();
            
            // 验证材质支持GPU Instancing
            if (!material.enableInstancing)
            {
                Debug.LogWarning($"[VegetationRenderer] 材质 {material.name} 未启用GPU Instancing，性能可能受影响");
                material.enableInstancing = true;
            }
        }
        
        /// <summary>
        /// 添加实例
        /// </summary>
        public void AddInstance(VegetationInstance instance)
        {
            _instances.Add(instance);
            
            // 动态扩展数组
            if (_matrices == null || _instances.Count > _matrices.Length)
            {
                int newSize = Mathf.NextPowerOfTwo(_instances.Count);
                _matrices = new Matrix4x4[newSize];
                _customData = new Vector4[newSize];
            }
        }
        
        /// <summary>
        /// 更新并渲染所有实例
        /// </summary>
        public void UpdateAndRender(
            float currentTime,
            float growthDuration,
            AnimationCurve growthCurve,
            WindZone windZone)
        {
            if (_instances.Count == 0 || _mesh == null || _material == null)
            {
                return;
            }
            
            // 更新风场参数
            UpdateWindParameters(windZone);
            
            // 分批渲染
            int batchCount = Mathf.CeilToInt((float)_instances.Count / _maxInstancesPerBatch);
            
            for (int batch = 0; batch < batchCount; batch++)
            {
                int startIndex = batch * _maxInstancesPerBatch;
                int count = Mathf.Min(_maxInstancesPerBatch, _instances.Count - startIndex);
                
                // 更新渲染数据
                UpdateRenderData(startIndex, count, currentTime, growthDuration, growthCurve);
                
                // 渲染这一批
                RenderBatch(count);
            }
        }
        
        /// <summary>
        /// 更新渲染数据（矩阵和自定义数据）
        /// </summary>
        private void UpdateRenderData(
            int startIndex,
            int count,
            float currentTime,
            float growthDuration,
            AnimationCurve growthCurve)
        {
            for (int i = 0; i < count; i++)
            {
                VegetationInstance inst = _instances[startIndex + i];
                
                // 计算生长进度
                float growthPhase = inst.GetGrowthPhase(currentTime, growthDuration);
                float growthScale = growthCurve.Evaluate(growthPhase);
                
                // 构建变换矩阵
                _matrices[i] = inst.GetMatrix(growthScale);
                
                // 自定义数据：用于Shader的额外参数
                _customData[i] = new Vector4(
                    growthPhase,           // x: 生长阶段 (0-1)
                    inst.Position.x,       // y: 世界X坐标（用于风场噪声）
                    inst.Position.z,       // z: 世界Z坐标（用于风场噪声）
                    inst.SpawnTime         // w: 生成时间（用于动画偏移）
                );
            }
            
            // 设置MaterialPropertyBlock
            _propertyBlock.SetVectorArray(CustomDataID, _customData);
        }
        
        /// <summary>
        /// 更新风场参数
        /// </summary>
        private void UpdateWindParameters(WindZone windZone)
        {
            if (windZone != null)
            {
                _propertyBlock.SetFloat(WindStrengthID, windZone.windMain);
                _propertyBlock.SetFloat(WindSpeedID, windZone.windPulseFrequency);
            }
            else
            {
                _propertyBlock.SetFloat(WindStrengthID, 0f);
                _propertyBlock.SetFloat(WindSpeedID, 1f);
            }
        }
        
        /// <summary>
        /// 渲染一批实例
        /// </summary>
        private void RenderBatch(int count)
        {
            // 使用Graphics.RenderMeshInstanced进行GPU Instancing渲染
            RenderParams renderParams = new RenderParams(_material)
            {
                matProps = _propertyBlock,
                shadowCastingMode = _shadowCastingMode,
                receiveShadows = _receiveShadows,
                layer = 0 // 默认层
            };
            
            Graphics.RenderMeshInstanced(
                renderParams,
                _mesh,
                0, // submesh index
                _matrices,
                count
            );
        }
        
        /// <summary>
        /// 清除所有实例
        /// </summary>
        public void Clear()
        {
            _instances.Clear();
        }
        
        /// <summary>
        /// 移除指定索引的实例
        /// </summary>
        public void RemoveAt(int index)
        {
            if (index >= 0 && index < _instances.Count)
            {
                _instances.RemoveAt(index);
            }
        }
        
        /// <summary>
        /// 获取实例
        /// </summary>
        public VegetationInstance GetInstance(int index)
        {
            if (index >= 0 && index < _instances.Count)
            {
                return _instances[index];
            }
            return null;
        }
        
        /// <summary>
        /// 获取所有实例的边界（用于视锥剔除优化）
        /// </summary>
        public Bounds GetBounds()
        {
            if (_instances.Count == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }
            
            Bounds bounds = new Bounds(_instances[0].Position, Vector3.one);
            
            for (int i = 1; i < _instances.Count; i++)
            {
                bounds.Encapsulate(_instances[i].Position);
            }
            
            return bounds;
        }
    }
}

