using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆粒子渲染器 - 封装 GPU Buffer 管理和绘制调用
    /// </summary>
    public class SlimeParticleRenderer : System.IDisposable
    {
        private ComputeBuffer _particleBuffer;
        private ComputeBuffer _covarianceBuffer;
        
        private readonly Material _particleMat;
        private readonly Mesh _particleMesh;
        private readonly int _maxParticles;
        
        private Bounds _bounds;
        
        // Particle 结构体大小：float3 Position + int Type + int ControllerSlot + int SourceId + int ClusterId + int FreeFrames = 32 bytes
        private const int PARTICLE_STRIDE = 32;
        private const int COVARIANCE_STRIDE = 64; // float4x4 = 16 floats
        
        public Bounds Bounds => _bounds;
        
        public SlimeParticleRenderer(Material particleMat, Mesh particleMesh, int maxParticles)
        {
            _particleMat = particleMat;
            _particleMesh = particleMesh;
            _maxParticles = maxParticles;
            
            _particleBuffer = new ComputeBuffer(maxParticles, PARTICLE_STRIDE);
            _covarianceBuffer = new ComputeBuffer(maxParticles, COVARIANCE_STRIDE);
            
            _particleMat.SetBuffer("_ParticleBuffer", _particleBuffer);
            _particleMat.SetBuffer("_CovarianceBuffer", _covarianceBuffer);
            
            _bounds = new Bounds(Vector3.zero, Vector3.one * 100f);
        }
        
        /// <summary>
        /// 上传粒子数据到 GPU
        /// </summary>
        public void UploadParticles(NativeArray<Particle> particles, int count)
        {
            if (count <= 0) return;
            _particleBuffer.SetData(particles, 0, 0, count);
            _particleMat.SetInt("_ParticleCount", count);
        }
        
        /// <summary>
        /// 上传协方差矩阵（用于各向异性渲染）
        /// </summary>
        public void UploadCovariance(NativeArray<float4x4> covariance, int count)
        {
            if (count <= 0) return;
            _covarianceBuffer.SetData(covariance, 0, 0, count);
        }
        
        /// <summary>
        /// 设置渲染边界（用于视锥剔除）
        /// </summary>
        public void SetBounds(float3 minPos, float3 maxPos)
        {
            _bounds = new Bounds
            {
                min = (Vector3)minPos,
                max = (Vector3)maxPos
            };
        }
        
        /// <summary>
        /// 设置无限边界（禁用剔除）
        /// </summary>
        public void SetInfiniteBounds()
        {
            _bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
        }
        
        /// <summary>
        /// 绘制粒子
        /// </summary>
        public void Draw(int instanceCount, bool anisotropic = false)
        {
            if (instanceCount <= 0) return;
            _particleMat.SetInt("_Aniso", anisotropic ? 1 : 0);
            Graphics.DrawMeshInstancedProcedural(_particleMesh, 0, _particleMat, _bounds, instanceCount);
        }
        
        /// <summary>
        /// 绘制粒子（使用自定义边界）
        /// </summary>
        public void Draw(int instanceCount, Bounds bounds, bool anisotropic = false)
        {
            if (instanceCount <= 0) return;
            _particleMat.SetInt("_Aniso", anisotropic ? 1 : 0);
            Graphics.DrawMeshInstancedProcedural(_particleMesh, 0, _particleMat, bounds, instanceCount);
        }
        
        public void Dispose()
        {
            if (_particleBuffer != null)
            {
                _particleBuffer.Release();
                _particleBuffer = null;
            }
            if (_covarianceBuffer != null)
            {
                _covarianceBuffer.Release();
                _covarianceBuffer = null;
            }
        }
    }
}
