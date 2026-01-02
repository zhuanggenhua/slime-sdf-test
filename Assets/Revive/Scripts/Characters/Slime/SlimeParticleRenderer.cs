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

        private NativeArray<ParticleRenderData> _particleUploadScratch;
        
        private readonly Material _particleMat;
        private readonly bool _ownsParticleMat;
        private readonly Mesh _particleMesh;
        private readonly int _maxParticles;
        
        private Bounds _bounds;
        
        // Particle 结构体大小：float4 Position + 6x int = 40 bytes（包含显式 Padding，确保与 GPU stride 一致）
        private const int PARTICLE_STRIDE = 40;
        private const int COVARIANCE_STRIDE = 64; // float4x4 = 16 floats

        private struct ParticleRenderData
        {
            public float4 x;
            public int Type;
            public int ControllerSlot;
            public int SourceId;
            public int ClusterId;
            public int FreeFrames;
            public int Padding0;
        }
        
        public Bounds Bounds => _bounds;
        
        public SlimeParticleRenderer(Material particleMat, Mesh particleMesh, int maxParticles)
        {
            _particleMat = particleMat != null ? new Material(particleMat) : null;
            _ownsParticleMat = _particleMat != null;
            _particleMesh = particleMesh;
            _maxParticles = maxParticles;
            
            _particleBuffer = new ComputeBuffer(maxParticles, PARTICLE_STRIDE);
            _covarianceBuffer = new ComputeBuffer(maxParticles, COVARIANCE_STRIDE);

            _particleUploadScratch = new NativeArray<ParticleRenderData>(maxParticles, Allocator.Persistent);
            
            if (_particleMat != null)
            {
                _particleMat.SetBuffer("_ParticleBuffer", _particleBuffer);
                _particleMat.SetBuffer("_CovarianceBuffer", _covarianceBuffer);
            }
            
            _bounds = new Bounds(Vector3.zero, Vector3.one * 100f);
        }

        public void SetSimToWorldScale(float simToWorldScale)
        {
            if (_particleMat == null)
                return;
            if (_particleMat.HasProperty("_SimToWorldScale"))
                _particleMat.SetFloat("_SimToWorldScale", simToWorldScale);
        }
        
        /// <summary>
        /// 上传粒子数据到 GPU
        /// </summary>
        public void UploadParticles(NativeArray<Particle> particles, int count)
        {
            if (count <= 0) return;

            if (count > _maxParticles)
                count = _maxParticles;

            for (int i = 0; i < count; i++)
            {
                var p = particles[i];
                _particleUploadScratch[i] = new ParticleRenderData
                {
                    x = new float4(p.Position, 0f),
                    Type = (int)p.Type,
                    ControllerSlot = p.ControllerSlot,
                    SourceId = p.SourceId,
                    ClusterId = p.ClusterId,
                    FreeFrames = p.FreeFrames,
                    Padding0 = 0
                };
            }

            _particleBuffer.SetData(_particleUploadScratch, 0, 0, count);
            if (_particleMat != null)
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
            if (_particleMat == null || _particleMesh == null)
                return;
            _particleMat.SetInt("_Aniso", anisotropic ? 1 : 0);
            Graphics.DrawMeshInstancedProcedural(_particleMesh, 0, _particleMat, _bounds, instanceCount);
        }
        
        /// <summary>
        /// 绘制粒子（使用自定义边界）
        /// </summary>
        public void Draw(int instanceCount, Bounds bounds, bool anisotropic = false)
        {
            if (instanceCount <= 0) return;
            if (_particleMat == null || _particleMesh == null)
                return;
            _particleMat.SetInt("_Aniso", anisotropic ? 1 : 0);
            Graphics.DrawMeshInstancedProcedural(_particleMesh, 0, _particleMat, bounds, instanceCount);
        }
        
        public void Dispose()
        {
            if (_particleUploadScratch.IsCreated)
            {
                _particleUploadScratch.Dispose();
            }

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

            if (_ownsParticleMat && _particleMat != null)
            {
                Object.Destroy(_particleMat);
            }
        }
    }
}
