using System;
using System.IO;
using System.IO.Compression;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Revive.Slime
{
    public struct WorldSdfRuntime : IDisposable
    {
        public const uint Magic = 0x46445357;
        public const int Version = 3;
        public const int HeaderSizeBytes = 40;

        private const int PayloadKind_DeflateDenseFloat32 = 1;
        private const int PayloadKind_BrickSparseFloat32 = 2;

        private static void LogLoadError(TextAsset bytesAsset, string reason, int bytesLen)
        {
            string name = bytesAsset != null ? bytesAsset.name : "<null>";
            Debug.LogError($"[WorldSdf] LoadFromBytes failed: {reason} asset={name} bytes={bytesLen}");
        }

        public struct Volume
        {
            public enum StorageKind : byte
            {
                None = 0,
                Dense = 1,
                BrickSparse = 2,
            }

            public StorageKind Storage;
            public float3 OriginSim;
            public int3 Dims;
            public float VoxelSizeSim;
            public float InvVoxelSizeSim;
            public float3 AabbMinSim;
            public float3 AabbMaxSim;
            public float MaxDistanceSim;
            [ReadOnly] public NativeArray<float> DenseDistancesSim;

            public int BrickSize;
            public int3 BrickDims;
            public int BrickStrideY;
            public int BrickStrideX;
            [ReadOnly] public NativeArray<int> BrickOffsets;
            [ReadOnly] public NativeArray<float> BrickDistances;

            public bool IsCreated
            {
                get
                {
                    if (Dims.x <= 0 || Dims.y <= 0 || Dims.z <= 0 || VoxelSizeSim <= 0f) return false;
                    if (Storage == StorageKind.Dense) return DenseDistancesSim.IsCreated;
                    if (Storage == StorageKind.BrickSparse) return BrickOffsets.IsCreated && BrickDistances.IsCreated && BrickSize > 0;
                    return false;
                }
            }

            public float SampleDistance(float3 pSim)
            {
                if (!IsCreated) return float.MaxValue;

                if (Storage == StorageKind.Dense) return SampleDistanceDense(pSim);
                if (Storage == StorageKind.BrickSparse) return SampleDistanceBrick(pSim);
                return float.MaxValue;
            }

            public float SampleDistanceDenseWithLocal(float3 pSim, out float3 local)
            {
                local = default;
                if (!IsCreated) return float.MaxValue;
                if (Storage != StorageKind.Dense) return float.MaxValue;

                if (pSim.x < AabbMinSim.x || pSim.y < AabbMinSim.y || pSim.z < AabbMinSim.z ||
                    pSim.x > AabbMaxSim.x || pSim.y > AabbMaxSim.y || pSim.z > AabbMaxSim.z)
                    return MaxDistanceSim;

                float inv = InvVoxelSizeSim > 0f ? InvVoxelSizeSim : (1f / VoxelSizeSim);
                local = (pSim - OriginSim) * inv;
                return SampleDistanceDenseLocal(local);
            }

            public float SampleDistanceDenseWithLocalAndIndices(float3 pSim, out float3 local, out int3 i0, out int3 i1, out float3 f)
            {
                local = default;
                i0 = default;
                i1 = default;
                f = default;
                if (!IsCreated) return float.MaxValue;
                if (Storage != StorageKind.Dense) return float.MaxValue;

                if (pSim.x < AabbMinSim.x || pSim.y < AabbMinSim.y || pSim.z < AabbMinSim.z ||
                    pSim.x > AabbMaxSim.x || pSim.y > AabbMaxSim.y || pSim.z > AabbMaxSim.z)
                    return MaxDistanceSim;

                float inv = InvVoxelSizeSim > 0f ? InvVoxelSizeSim : (1f / VoxelSizeSim);
                local = (pSim - OriginSim) * inv;

                float maxX = Dims.x - 1;
                float maxY = Dims.y - 1;
                float maxZ = Dims.z - 1;
                if (local.x < 0f || local.y < 0f || local.z < 0f || local.x > maxX || local.y > maxY || local.z > maxZ)
                    return MaxDistanceSim;

                // local 已经保证 >=0，这里 (int) 截断与 floor 等价
                int ix0Raw = (int)local.x;
                int iy0Raw = (int)local.y;
                int iz0Raw = (int)local.z;
                f = new float3(local.x - ix0Raw, local.y - iy0Raw, local.z - iz0Raw);

                int dxm1 = Dims.x - 1;
                int dym1 = Dims.y - 1;
                int dzm1 = Dims.z - 1;

                int ix0 = math.max(0, math.min(ix0Raw, dxm1));
                int iy0 = math.max(0, math.min(iy0Raw, dym1));
                int iz0 = math.max(0, math.min(iz0Raw, dzm1));

                int ix1 = math.max(0, math.min(ix0Raw + 1, dxm1));
                int iy1 = math.max(0, math.min(iy0Raw + 1, dym1));
                int iz1 = math.max(0, math.min(iz0Raw + 1, dzm1));

                i0 = new int3(ix0, iy0, iz0);
                i1 = new int3(ix1, iy1, iz1);

                int strideY = Dims.z;
                int strideX = Dims.y * Dims.z;
                unsafe
                {
                    float* ptr = (float*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(DenseDistancesSim);
                    return SampleDistanceDenseLocalFromIndicesUnsafe(ptr, i0, i1, f, strideX, strideY);
                }
            }

            public float3 SampleNormalForward(float3 pSim, float dAtP)
            {
                if (!IsCreated) return new float3(0, 1, 0);

                if (Storage == StorageKind.Dense) return SampleNormalForwardDense(pSim, dAtP);
                if (Storage == StorageKind.BrickSparse) return SampleNormalForwardBrick(pSim, dAtP);
                return new float3(0, 1, 0);
            }

            private float3 SampleNormalForwardDense(float3 pSim, float dAtP)
            {
                if (Storage != StorageKind.Dense) return new float3(0, 1, 0);

                // cheap sim-space bounds check
                if (pSim.x < AabbMinSim.x || pSim.y < AabbMinSim.y || pSim.z < AabbMinSim.z ||
                    pSim.x > AabbMaxSim.x || pSim.y > AabbMaxSim.y || pSim.z > AabbMaxSim.z)
                    return new float3(0, 1, 0);

                float inv = InvVoxelSizeSim > 0f ? InvVoxelSizeSim : (1f / VoxelSizeSim);
                float3 local0 = (pSim - OriginSim) * inv;
                float d0 = dAtP == float.MaxValue ? SampleDistanceDenseLocal(local0) : dAtP;

                float sx = SampleDistanceDenseLocal(local0 + new float3(1f, 0f, 0f));
                float sy = SampleDistanceDenseLocal(local0 + new float3(0f, 1f, 0f));
                float sz = SampleDistanceDenseLocal(local0 + new float3(0f, 0f, 1f));
                float dx = sx - d0;
                float dy = sy - d0;
                float dz = sz - d0;

                float3 n = new float3(dx, dy, dz);
                float lenSq = math.lengthsq(n);
                if (lenSq < 1e-10f) return new float3(0, 1, 0);
                return n * math.rsqrt(lenSq);
            }

            private float3 SampleNormalForwardBrick(float3 pSim, float dAtP)
            {
                if (Storage != StorageKind.BrickSparse) return new float3(0, 1, 0);

                if (pSim.x < AabbMinSim.x || pSim.y < AabbMinSim.y || pSim.z < AabbMinSim.z ||
                    pSim.x > AabbMaxSim.x || pSim.y > AabbMaxSim.y || pSim.z > AabbMaxSim.z)
                    return new float3(0, 1, 0);

                float inv = InvVoxelSizeSim > 0f ? InvVoxelSizeSim : (1f / VoxelSizeSim);
                float3 local0 = (pSim - OriginSim) * inv;
                float d0 = dAtP == float.MaxValue ? SampleDistanceBrickLocal(local0) : dAtP;

                float sx = SampleDistanceBrickLocal(local0 + new float3(1f, 0f, 0f));
                float sy = SampleDistanceBrickLocal(local0 + new float3(0f, 1f, 0f));
                float sz = SampleDistanceBrickLocal(local0 + new float3(0f, 0f, 1f));

                float3 n = new float3(sx - d0, sy - d0, sz - d0);
                float lenSq = math.lengthsq(n);
                if (lenSq < 1e-10f) return new float3(0, 1, 0);
                return n * math.rsqrt(lenSq);
            }

            public float3 SampleNormalForwardDenseLocal(float3 local0, float dAtLocal0)
            {
                if (!IsCreated) return new float3(0, 1, 0);
                if (Storage != StorageKind.Dense) return new float3(0, 1, 0);

                float maxX = Dims.x - 1;
                float maxY = Dims.y - 1;
                float maxZ = Dims.z - 1;
                if (local0.x < 0f || local0.y < 0f || local0.z < 0f || local0.x > maxX || local0.y > maxY || local0.z > maxZ)
                    return new float3(0, 1, 0);

                int ix0Raw = (int)local0.x;
                int iy0Raw = (int)local0.y;
                int iz0Raw = (int)local0.z;
                float3 f = new float3(local0.x - ix0Raw, local0.y - iy0Raw, local0.z - iz0Raw);

                int dxm1 = Dims.x - 1;
                int dym1 = Dims.y - 1;
                int dzm1 = Dims.z - 1;

                int ix0 = math.max(0, math.min(ix0Raw, dxm1));
                int iy0 = math.max(0, math.min(iy0Raw, dym1));
                int iz0 = math.max(0, math.min(iz0Raw, dzm1));

                int ix1 = math.max(0, math.min(ix0Raw + 1, dxm1));
                int iy1 = math.max(0, math.min(iy0Raw + 1, dym1));
                int iz1 = math.max(0, math.min(iz0Raw + 1, dzm1));

                int3 i0 = new int3(ix0, iy0, iz0);
                int3 i1 = new int3(ix1, iy1, iz1);

                int strideY = Dims.z;
                int strideX = Dims.y * Dims.z;

                float d0;
                float sx;
                float sy;
                float sz;
                unsafe
                {
                    float* ptr = (float*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(DenseDistancesSim);
                    d0 = dAtLocal0 == float.MaxValue ? SampleDistanceDenseLocalFromIndicesUnsafe(ptr, i0, i1, f, strideX, strideY) : dAtLocal0;

                    sx = (local0.x + 1f > maxX)
                        ? MaxDistanceSim
                        : SampleDistanceDenseLocalFromIndicesUnsafe(
                            ptr,
                            new int3(math.min(i0.x + 1, Dims.x - 1), i0.y, i0.z),
                            new int3(math.min(i1.x + 1, Dims.x - 1), i1.y, i1.z),
                            f,
                            strideX,
                            strideY);
                    sy = (local0.y + 1f > maxY)
                        ? MaxDistanceSim
                        : SampleDistanceDenseLocalFromIndicesUnsafe(
                            ptr,
                            new int3(i0.x, math.min(i0.y + 1, Dims.y - 1), i0.z),
                            new int3(i1.x, math.min(i1.y + 1, Dims.y - 1), i1.z),
                            f,
                            strideX,
                            strideY);
                    sz = (local0.z + 1f > maxZ)
                        ? MaxDistanceSim
                        : SampleDistanceDenseLocalFromIndicesUnsafe(
                            ptr,
                            new int3(i0.x, i0.y, math.min(i0.z + 1, Dims.z - 1)),
                            new int3(i1.x, i1.y, math.min(i1.z + 1, Dims.z - 1)),
                            f,
                            strideX,
                            strideY);
                }

                float3 n = new float3(sx - d0, sy - d0, sz - d0);
                float lenSq = math.lengthsq(n);
                if (lenSq < 1e-10f) return new float3(0, 1, 0);
                return n * math.rsqrt(lenSq);
            }

            public float3 SampleNormalForwardDenseLocalFromIndices(float3 local0, int3 i0, int3 i1, float3 f, float dAtLocal0)
            {
                if (!IsCreated) return new float3(0, 1, 0);
                if (Storage != StorageKind.Dense) return new float3(0, 1, 0);

                float3 maxLocal = new float3(Dims.x - 1, Dims.y - 1, Dims.z - 1);

                int strideY = Dims.z;
                int strideX = Dims.y * Dims.z;

                float d0;
                float sx;
                float sy;
                float sz;
                unsafe
                {
                    float* ptr = (float*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(DenseDistancesSim);
                    d0 = dAtLocal0 == float.MaxValue ? SampleDistanceDenseLocalFromIndicesUnsafe(ptr, i0, i1, f, strideX, strideY) : dAtLocal0;

                    sx = (local0.x + 1f > maxLocal.x)
                        ? MaxDistanceSim
                        : SampleDistanceDenseLocalFromIndicesUnsafe(
                            ptr,
                            new int3(math.min(i0.x + 1, Dims.x - 1), i0.y, i0.z),
                            new int3(math.min(i1.x + 1, Dims.x - 1), i1.y, i1.z),
                            f,
                            strideX,
                            strideY);
                    sy = (local0.y + 1f > maxLocal.y)
                        ? MaxDistanceSim
                        : SampleDistanceDenseLocalFromIndicesUnsafe(
                            ptr,
                            new int3(i0.x, math.min(i0.y + 1, Dims.y - 1), i0.z),
                            new int3(i1.x, math.min(i1.y + 1, Dims.y - 1), i1.z),
                            f,
                            strideX,
                            strideY);
                    sz = (local0.z + 1f > maxLocal.z)
                        ? MaxDistanceSim
                        : SampleDistanceDenseLocalFromIndicesUnsafe(
                            ptr,
                            new int3(i0.x, i0.y, math.min(i0.z + 1, Dims.z - 1)),
                            new int3(i1.x, i1.y, math.min(i1.z + 1, Dims.z - 1)),
                            f,
                            strideX,
                            strideY);
                }

                float3 n = new float3(sx - d0, sy - d0, sz - d0);
                float lenSq = math.lengthsq(n);
                if (lenSq < 1e-10f) return new float3(0, 1, 0);
                return n * math.rsqrt(lenSq);
            }

            private float SampleDistanceDense(float3 pSim)
            {
                // cheap sim-space bounds check
                if (pSim.x < AabbMinSim.x || pSim.y < AabbMinSim.y || pSim.z < AabbMinSim.z ||
                    pSim.x > AabbMaxSim.x || pSim.y > AabbMaxSim.y || pSim.z > AabbMaxSim.z)
                    return MaxDistanceSim;

                float inv = InvVoxelSizeSim > 0f ? InvVoxelSizeSim : (1f / VoxelSizeSim);
                float3 local = (pSim - OriginSim) * inv;
                return SampleDistanceDenseLocal(local);
            }

            private float SampleDistanceBrick(float3 pSim)
            {
                if (pSim.x < AabbMinSim.x || pSim.y < AabbMinSim.y || pSim.z < AabbMinSim.z ||
                    pSim.x > AabbMaxSim.x || pSim.y > AabbMaxSim.y || pSim.z > AabbMaxSim.z)
                    return MaxDistanceSim;

                float inv = InvVoxelSizeSim > 0f ? InvVoxelSizeSim : (1f / VoxelSizeSim);
                float3 local = (pSim - OriginSim) * inv;
                return SampleDistanceBrickLocal(local);
            }

            private float SampleDistanceBrickLocal(float3 local)
            {
                float maxX = Dims.x - 1;
                float maxY = Dims.y - 1;
                float maxZ = Dims.z - 1;
                if (local.x < 0f || local.y < 0f || local.z < 0f || local.x > maxX || local.y > maxY || local.z > maxZ)
                    return MaxDistanceSim;

                int ix0Raw = (int)local.x;
                int iy0Raw = (int)local.y;
                int iz0Raw = (int)local.z;
                float3 f = new float3(local.x - ix0Raw, local.y - iy0Raw, local.z - iz0Raw);

                int dxm1 = Dims.x - 1;
                int dym1 = Dims.y - 1;
                int dzm1 = Dims.z - 1;

                int ix0 = math.max(0, math.min(ix0Raw, dxm1));
                int iy0 = math.max(0, math.min(iy0Raw, dym1));
                int iz0 = math.max(0, math.min(iz0Raw, dzm1));

                int ix1 = math.max(0, math.min(ix0Raw + 1, dxm1));
                int iy1 = math.max(0, math.min(iy0Raw + 1, dym1));
                int iz1 = math.max(0, math.min(iz0Raw + 1, dzm1));

                int3 i0 = new int3(ix0, iy0, iz0);
                int3 i1 = new int3(ix1, iy1, iz1);
                return SampleDistanceBrickLocalFromIndices(i0, i1, f);
            }

            private float SampleDistanceBrickLocalFromIndices(int3 i0, int3 i1, float3 f)
            {
                float c000 = AtBrick(i0.x, i0.y, i0.z);
                float c100 = AtBrick(i1.x, i0.y, i0.z);
                float c010 = AtBrick(i0.x, i1.y, i0.z);
                float c110 = AtBrick(i1.x, i1.y, i0.z);
                float c001 = AtBrick(i0.x, i0.y, i1.z);
                float c101 = AtBrick(i1.x, i0.y, i1.z);
                float c011 = AtBrick(i0.x, i1.y, i1.z);
                float c111 = AtBrick(i1.x, i1.y, i1.z);

                float cx00 = math.lerp(c000, c100, f.x);
                float cx10 = math.lerp(c010, c110, f.x);
                float cx01 = math.lerp(c001, c101, f.x);
                float cx11 = math.lerp(c011, c111, f.x);

                float cxy0 = math.lerp(cx00, cx10, f.y);
                float cxy1 = math.lerp(cx01, cx11, f.y);

                return math.lerp(cxy0, cxy1, f.z);
            }

            private float AtBrick(int x, int y, int z)
            {
                int bs = BrickSize;
                if (bs <= 0) return MaxDistanceSim;

                int bx = x / bs;
                int by = y / bs;
                int bz = z / bs;

                int blockIndex = bx * BrickStrideX + by * BrickStrideY + bz;
                if ((uint)blockIndex >= (uint)BrickOffsets.Length) return MaxDistanceSim;

                int off = BrickOffsets[blockIndex];
                if (off < 0) return MaxDistanceSim;

                int lx = x - bx * bs;
                int ly = y - by * bs;
                int lz = z - bz * bs;

                int blockVoxels = bs * bs * bs;
                int idx = off * blockVoxels + lx * (bs * bs) + ly * bs + lz;
                if ((uint)idx >= (uint)BrickDistances.Length) return MaxDistanceSim;
                return BrickDistances[idx];
            }

            private float SampleDistanceDenseLocal(float3 local)
            {
                float maxX = Dims.x - 1;
                float maxY = Dims.y - 1;
                float maxZ = Dims.z - 1;
                if (local.x < 0f || local.y < 0f || local.z < 0f || local.x > maxX || local.y > maxY || local.z > maxZ)
                    return MaxDistanceSim;

                int ix0Raw = (int)local.x;
                int iy0Raw = (int)local.y;
                int iz0Raw = (int)local.z;
                float3 f = new float3(local.x - ix0Raw, local.y - iy0Raw, local.z - iz0Raw);

                int dxm1 = Dims.x - 1;
                int dym1 = Dims.y - 1;
                int dzm1 = Dims.z - 1;

                int ix0 = math.max(0, math.min(ix0Raw, dxm1));
                int iy0 = math.max(0, math.min(iy0Raw, dym1));
                int iz0 = math.max(0, math.min(iz0Raw, dzm1));

                int ix1 = math.max(0, math.min(ix0Raw + 1, dxm1));
                int iy1 = math.max(0, math.min(iy0Raw + 1, dym1));
                int iz1 = math.max(0, math.min(iz0Raw + 1, dzm1));

                int3 i0 = new int3(ix0, iy0, iz0);
                int3 i1 = new int3(ix1, iy1, iz1);

                int strideY = Dims.z;
                int strideX = Dims.y * Dims.z;

                unsafe
                {
                    float* ptr = (float*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(DenseDistancesSim);
                    return SampleDistanceDenseLocalFromIndicesUnsafe(ptr, i0, i1, f, strideX, strideY);
                }
            }

            private float SampleDistanceDenseLocalFromIndices(int3 i0, int3 i1, float3 f, int strideX, int strideY)
            {
                unsafe
                {
                    float* ptr = (float*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(DenseDistancesSim);
                    return SampleDistanceDenseLocalFromIndicesUnsafe(ptr, i0, i1, f, strideX, strideY);
                }
            }

            private unsafe float SampleDistanceDenseLocalFromIndicesUnsafe(float* ptr, int3 i0, int3 i1, float3 f, int strideX, int strideY)
            {
                int base000 = i0.x * strideX + i0.y * strideY + i0.z;
                int dx = (i1.x - i0.x) * strideX;
                int dy = (i1.y - i0.y) * strideY;
                int dz = (i1.z - i0.z);

                float c000 = ptr[base000];
                float c100 = ptr[base000 + dx];
                float c010 = ptr[base000 + dy];
                float c110 = ptr[base000 + dx + dy];
                float c001 = ptr[base000 + dz];
                float c101 = ptr[base000 + dx + dz];
                float c011 = ptr[base000 + dy + dz];
                float c111 = ptr[base000 + dx + dy + dz];

                float cx00 = math.lerp(c000, c100, f.x);
                float cx10 = math.lerp(c010, c110, f.x);
                float cx01 = math.lerp(c001, c101, f.x);
                float cx11 = math.lerp(c011, c111, f.x);

                float cxy0 = math.lerp(cx00, cx10, f.y);
                float cxy1 = math.lerp(cx01, cx11, f.y);

                return math.lerp(cxy0, cxy1, f.z);
            }

            private float AtDense(int x, int y, int z, int strideX, int strideY)
            {
                int idx = x * strideX + y * strideY + z;
                return DenseDistancesSim[idx];
            }
        }

        private NativeArray<float> _distances;
        private NativeArray<int> _brickOffsets;
        private NativeArray<float> _brickDistances;
        private Volume _volume;

        public bool IsCreated => _volume.IsCreated;
        public Volume Data => _volume;

        public void LoadFromBytes(TextAsset bytesAsset)
        {
            Dispose();

            if (bytesAsset == null) return;
            byte[] bytes = bytesAsset.bytes;
            if (bytes == null || bytes.Length < HeaderSizeBytes)
            {
                LogLoadError(bytesAsset, "bytes null or too small", bytes != null ? bytes.Length : 0);
                return;
            }

            uint magic = BitConverter.ToUInt32(bytes, 0);
            if (magic != Magic)
            {
                LogLoadError(bytesAsset, $"magic mismatch (0x{magic:X8})", bytes.Length);
                return;
            }

            int version = BitConverter.ToInt32(bytes, 4);

            float ox1 = BitConverter.ToSingle(bytes, 8);
            float oy1 = BitConverter.ToSingle(bytes, 12);
            float oz1 = BitConverter.ToSingle(bytes, 16);
            int dimX1 = BitConverter.ToInt32(bytes, 20);
            int dimY1 = BitConverter.ToInt32(bytes, 24);
            int dimZ1 = BitConverter.ToInt32(bytes, 28);
            float voxelSizeSim1 = BitConverter.ToSingle(bytes, 32);
            float maxDistanceSim1 = BitConverter.ToSingle(bytes, 36);

            if (dimX1 <= 0 || dimY1 <= 0 || dimZ1 <= 0 || voxelSizeSim1 <= 0f)
            {
                LogLoadError(bytesAsset, $"invalid dims/voxel dims=({dimX1},{dimY1},{dimZ1}) voxelSim={voxelSizeSim1}", bytes.Length);
                return;
            }
            if (maxDistanceSim1 <= 0f)
            {
                LogLoadError(bytesAsset, $"invalid maxDistanceSim={maxDistanceSim1}", bytes.Length);
                return;
            }
            int total1 = dimX1 * dimY1 * dimZ1;
            if (total1 <= 0)
            {
                LogLoadError(bytesAsset, $"invalid total voxels={total1}", bytes.Length);
                return;
            }

            if (version == 1)
            {
                int expectedBytes1 = HeaderSizeBytes + total1 * sizeof(float);
                if (bytes.Length != expectedBytes1)
                {
                    LogLoadError(bytesAsset, $"v1 length mismatch expected={expectedBytes1} actual={bytes.Length}", bytes.Length);
                    return;
                }

                var tmp = new float[total1];
                Buffer.BlockCopy(bytes, HeaderSizeBytes, tmp, 0, total1 * sizeof(float));

                _distances = new NativeArray<float>(total1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _distances.CopyFrom(tmp);

                // Job 里传递 Volume 时，所有 NativeArray 字段都必须是有效容器（即使不使用）。
                _brickOffsets = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _brickOffsets[0] = -1;
                _brickDistances = new NativeArray<float>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _brickDistances[0] = maxDistanceSim1;
            }
            else if (version == 2)
            {
                int payloadHeaderOffset = HeaderSizeBytes;
                int payloadHeaderSize = sizeof(int) * 3;
                if (bytes.Length < payloadHeaderOffset + payloadHeaderSize)
                {
                    LogLoadError(bytesAsset, $"v2 payload header too small", bytes.Length);
                    return;
                }

                int payloadKind = BitConverter.ToInt32(bytes, payloadHeaderOffset + 0);
                int uncompressedBytes = BitConverter.ToInt32(bytes, payloadHeaderOffset + 4);
                int compressedBytes = BitConverter.ToInt32(bytes, payloadHeaderOffset + 8);

                if (payloadKind != PayloadKind_DeflateDenseFloat32)
                {
                    LogLoadError(bytesAsset, $"v2 payloadKind unsupported kind={payloadKind}", bytes.Length);
                    return;
                }

                int expectedUncompressedBytes = total1 * sizeof(float);
                if (uncompressedBytes != expectedUncompressedBytes)
                {
                    LogLoadError(bytesAsset, $"v2 uncompressedBytes mismatch expected={expectedUncompressedBytes} actual={uncompressedBytes}", bytes.Length);
                    return;
                }
                if (compressedBytes <= 0)
                {
                    LogLoadError(bytesAsset, $"v2 invalid compressedBytes={compressedBytes}", bytes.Length);
                    return;
                }

                int payloadOffset = payloadHeaderOffset + payloadHeaderSize;
                if (bytes.Length != payloadOffset + compressedBytes)
                {
                    LogLoadError(bytesAsset, $"v2 length mismatch expected={payloadOffset + compressedBytes} actual={bytes.Length}", bytes.Length);
                    return;
                }

                byte[] raw;
                try
                {
                    raw = new byte[uncompressedBytes];
                    using (var src = new MemoryStream(bytes, payloadOffset, compressedBytes))
                    using (var ds = new DeflateStream(src, CompressionMode.Decompress))
                    {
                        int readTotal = 0;
                        while (readTotal < raw.Length)
                        {
                            int n = ds.Read(raw, readTotal, raw.Length - readTotal);
                            if (n <= 0) break;
                            readTotal += n;
                        }
                        if (readTotal != raw.Length)
                        {
                            LogLoadError(bytesAsset, $"v2 deflate read incomplete read={readTotal} expected={raw.Length}", bytes.Length);
                            return;
                        }
                    }
                }
                catch
                {
                    LogLoadError(bytesAsset, "v2 deflate exception", bytes.Length);
                    return;
                }

                var tmp = new float[total1];
                Buffer.BlockCopy(raw, 0, tmp, 0, uncompressedBytes);

                _distances = new NativeArray<float>(total1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _distances.CopyFrom(tmp);

                // Job 里传递 Volume 时，所有 NativeArray 字段都必须是有效容器（即使不使用）。
                _brickOffsets = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _brickOffsets[0] = -1;
                _brickDistances = new NativeArray<float>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _brickDistances[0] = maxDistanceSim1;
            }
            else if (version == 3)
            {
                int payloadHeaderOffset = HeaderSizeBytes;
                int payloadHeaderSize = sizeof(int) * 9;
                if (bytes.Length < payloadHeaderOffset + payloadHeaderSize)
                {
                    LogLoadError(bytesAsset, $"v3 payload header too small", bytes.Length);
                    return;
                }

                int payloadKind = BitConverter.ToInt32(bytes, payloadHeaderOffset + 0);
                int brickSize = BitConverter.ToInt32(bytes, payloadHeaderOffset + 4);
                int bdx = BitConverter.ToInt32(bytes, payloadHeaderOffset + 8);
                int bdy = BitConverter.ToInt32(bytes, payloadHeaderOffset + 12);
                int bdz = BitConverter.ToInt32(bytes, payloadHeaderOffset + 16);
                int offsetsCount = BitConverter.ToInt32(bytes, payloadHeaderOffset + 20);
                int blocksCount = BitConverter.ToInt32(bytes, payloadHeaderOffset + 24);
                int uncompressedBytes = BitConverter.ToInt32(bytes, payloadHeaderOffset + 28);
                int compressedBytes = BitConverter.ToInt32(bytes, payloadHeaderOffset + 32);

                if (payloadKind != PayloadKind_BrickSparseFloat32)
                {
                    LogLoadError(bytesAsset, $"v3 payloadKind unsupported kind={payloadKind}", bytes.Length);
                    return;
                }
                if (brickSize <= 0)
                {
                    LogLoadError(bytesAsset, $"v3 invalid brickSize={brickSize}", bytes.Length);
                    return;
                }
                if (bdx <= 0 || bdy <= 0 || bdz <= 0)
                {
                    LogLoadError(bytesAsset, $"v3 invalid brickDims=({bdx},{bdy},{bdz})", bytes.Length);
                    return;
                }

                int expectedOffsetsCount = bdx * bdy * bdz;
                if (offsetsCount != expectedOffsetsCount)
                {
                    LogLoadError(bytesAsset, $"v3 offsetsCount mismatch expected={expectedOffsetsCount} actual={offsetsCount}", bytes.Length);
                    return;
                }

                if (blocksCount < 0)
                {
                    LogLoadError(bytesAsset, $"v3 invalid blocksCount={blocksCount}", bytes.Length);
                    return;
                }

                long expectedUncompressedBytesLong = (long)blocksCount * brickSize * brickSize * brickSize * sizeof(float);
                if (expectedUncompressedBytesLong < 0 || expectedUncompressedBytesLong > int.MaxValue)
                {
                    LogLoadError(bytesAsset, $"v3 uncompressedBytes overflow blocks={blocksCount} brickSize={brickSize}", bytes.Length);
                    return;
                }
                int expectedUncompressedBytes = (int)expectedUncompressedBytesLong;
                if (uncompressedBytes != expectedUncompressedBytes)
                {
                    LogLoadError(bytesAsset, $"v3 uncompressedBytes mismatch expected={expectedUncompressedBytes} actual={uncompressedBytes}", bytes.Length);
                    return;
                }
                if (compressedBytes < 0)
                {
                    LogLoadError(bytesAsset, $"v3 invalid compressedBytes={compressedBytes}", bytes.Length);
                    return;
                }

                if (uncompressedBytes == 0 && compressedBytes != 0)
                {
                    LogLoadError(bytesAsset, $"v3 invalid empty payload compressedBytes={compressedBytes}", bytes.Length);
                    return;
                }

                int offsetsBytes = offsetsCount * sizeof(int);
                int payloadOffset = payloadHeaderOffset + payloadHeaderSize;
                if (bytes.Length < payloadOffset + offsetsBytes)
                {
                    LogLoadError(bytesAsset, $"v3 offsets data too small", bytes.Length);
                    return;
                }

                int compressedOffset = payloadOffset + offsetsBytes;
                if (bytes.Length != compressedOffset + compressedBytes)
                {
                    LogLoadError(bytesAsset, $"v3 length mismatch expected={compressedOffset + compressedBytes} actual={bytes.Length}", bytes.Length);
                    return;
                }

                var offsetsManaged = new int[offsetsCount];
                Buffer.BlockCopy(bytes, payloadOffset, offsetsManaged, 0, offsetsBytes);

                for (int i = 0; i < offsetsManaged.Length; i++)
                {
                    int o = offsetsManaged[i];
                    if (o < -1 || o >= blocksCount)
                    {
                        LogLoadError(bytesAsset, $"v3 invalid block offset at {i} value={o} blocks={blocksCount}", bytes.Length);
                        return;
                    }
                }

                byte[] raw = null;
                if (uncompressedBytes > 0)
                {
                    try
                    {
                        raw = new byte[uncompressedBytes];
                        using (var src = new MemoryStream(bytes, compressedOffset, compressedBytes))
                        using (var ds = new DeflateStream(src, CompressionMode.Decompress))
                        {
                            int readTotal = 0;
                            while (readTotal < raw.Length)
                            {
                                int n = ds.Read(raw, readTotal, raw.Length - readTotal);
                                if (n <= 0) break;
                                readTotal += n;
                            }
                            if (readTotal != raw.Length)
                            {
                                LogLoadError(bytesAsset, $"v3 deflate read incomplete read={readTotal} expected={raw.Length}", bytes.Length);
                                return;
                            }
                        }
                    }
                    catch
                    {
                        LogLoadError(bytesAsset, "v3 deflate exception", bytes.Length);
                        return;
                    }
                }

                int blockVoxels = brickSize * brickSize * brickSize;
                int blockFloatCount = blocksCount * blockVoxels;
                var tmp = new float[blockFloatCount];
                if (uncompressedBytes > 0)
                    Buffer.BlockCopy(raw, 0, tmp, 0, uncompressedBytes);

                _brickOffsets = new NativeArray<int>(offsetsCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _brickOffsets.CopyFrom(offsetsManaged);
                _brickDistances = new NativeArray<float>(blockFloatCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _brickDistances.CopyFrom(tmp);

                // Job 里传递 Volume 时，所有 NativeArray 字段都必须是有效容器（即使不使用）。
                _distances = new NativeArray<float>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _distances[0] = maxDistanceSim1;

                _volume = new Volume
                {
                    Storage = Volume.StorageKind.BrickSparse,
                    OriginSim = new float3(ox1, oy1, oz1),
                    Dims = new int3(dimX1, dimY1, dimZ1),
                    VoxelSizeSim = voxelSizeSim1,
                    InvVoxelSizeSim = 1f / voxelSizeSim1,
                    AabbMinSim = new float3(ox1, oy1, oz1),
                    AabbMaxSim = new float3(ox1, oy1, oz1) + new float3(dimX1 - 1, dimY1 - 1, dimZ1 - 1) * voxelSizeSim1,
                    MaxDistanceSim = maxDistanceSim1,
                    DenseDistancesSim = _distances,
                    BrickSize = brickSize,
                    BrickDims = new int3(bdx, bdy, bdz),
                    BrickStrideY = bdz,
                    BrickStrideX = bdy * bdz,
                    BrickOffsets = _brickOffsets,
                    BrickDistances = _brickDistances,
                };

                return;
            }
            else
            {
                LogLoadError(bytesAsset, $"unsupported version={version}", bytes.Length);
                return;
            }

            _volume = new Volume
            {
                Storage = Volume.StorageKind.Dense,
                OriginSim = new float3(ox1, oy1, oz1),
                Dims = new int3(dimX1, dimY1, dimZ1),
                VoxelSizeSim = voxelSizeSim1,
                InvVoxelSizeSim = 1f / voxelSizeSim1,
                AabbMinSim = new float3(ox1, oy1, oz1),
                AabbMaxSim = new float3(ox1, oy1, oz1) + new float3(dimX1 - 1, dimY1 - 1, dimZ1 - 1) * voxelSizeSim1,
                MaxDistanceSim = maxDistanceSim1,
                DenseDistancesSim = _distances,
                BrickSize = 0,
                BrickDims = default,
                BrickStrideY = 0,
                BrickStrideX = 0,
                BrickOffsets = _brickOffsets,
                BrickDistances = _brickDistances,
            };
        }

        public void Dispose()
        {
            if (_distances.IsCreated) _distances.Dispose();
            _distances = default;
            if (_brickOffsets.IsCreated) _brickOffsets.Dispose();
            _brickOffsets = default;
            if (_brickDistances.IsCreated) _brickDistances.Dispose();
            _brickDistances = default;
            _volume = default;
        }
    }
}
