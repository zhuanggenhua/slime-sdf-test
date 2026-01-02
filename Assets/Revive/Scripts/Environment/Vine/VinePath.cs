using System.Collections.Generic;
using UnityEngine;

namespace Revive.Environment
{
    /// <summary>
    /// 路径参考帧（切线、法线、副法线）
    /// </summary>
    [System.Serializable]
    public struct PathFrame
    {
        public Vector3 Tangent;   // 切线方向（沿路径）
        public Vector3 Normal;    // 法线方向（侧向1）
        public Vector3 Binormal;  // 副法线方向（侧向2）
        
        public PathFrame(Vector3 tangent, Vector3 normal, Vector3 binormal)
        {
            Tangent = tangent;
            Normal = normal;
            Binormal = binormal;
        }
    }
    
    /// <summary>
    /// 藤蔓路径数据结构
    /// 存储路径点、切线、法线等信息
    /// </summary>
    [System.Serializable]
    public class VinePath
    {
        /// <summary>
        /// 路径点列表
        /// </summary>
        public List<Vector3> Points = new List<Vector3>();
        
        /// <summary>
        /// 预计算的参考帧（使用平行传输算法，避免扭曲）
        /// </summary>
        private List<PathFrame> _frames = null;
        
        /// <summary>
        /// 是否为分支路径
        /// </summary>
        public bool IsBranch = false;
        
        /// <summary>
        /// 分支起始粗细（继承自父路径分支点的粗细）
        /// </summary>
        public float BranchStartThickness = 1.0f;
        
        /// <summary>
        /// 分支起始进度（继承自父路径分支点的归一化位置0-1）
        /// 用于生长动画的连续性
        /// </summary>
        public float BranchStartProgress = 0f;
        
        /// <summary>
        /// 子分支列表
        /// </summary>
        public List<VinePath> Branches = new List<VinePath>();
        
        /// <summary>
        /// 添加路径点
        /// </summary>
        public void AddPoint(Vector3 point)
        {
            Points.Add(point);
        }
        
        /// <summary>
        /// 获取路径点数量
        /// </summary>
        public int Count => Points.Count;
        
        /// <summary>
        /// 计算指定索引处的切线方向
        /// </summary>
        public Vector3 GetTangent(int index)
        {
            if (Points.Count < 2)
                return Vector3.up;
            
            if (index == 0)
            {
                // 起点：使用前向差分
                return (Points[1] - Points[0]).normalized;
            }
            else if (index >= Points.Count - 1)
            {
                // 终点：使用后向差分
                return (Points[Points.Count - 1] - Points[Points.Count - 2]).normalized;
            }
            else
            {
                // 中间点：使用中心差分（更平滑）
                return ((Points[index + 1] - Points[index - 1]) * 0.5f).normalized;
            }
        }
        
        /// <summary>
        /// 计算并缓存所有点的参考帧（使用平行传输算法）
        /// 必须在生成网格前调用，避免顶点翻转问题
        /// </summary>
        public void ComputeParallelTransportFrames()
        {
            if (Points.Count < 2)
                return;
            
            _frames = new List<PathFrame>(Points.Count);
            
            // 1. 计算第一个帧（初始参考帧）
            Vector3 tangent0 = GetTangent(0);
            Vector3 initialNormal = ComputeInitialNormal(tangent0);
            Vector3 initialBinormal = Vector3.Cross(tangent0, initialNormal).normalized;
            
            _frames.Add(new PathFrame(tangent0, initialNormal, initialBinormal));
            
            // 2. 使用平行传输算法计算后续帧
            for (int i = 1; i < Points.Count; i++)
            {
                Vector3 tangent = GetTangent(i);
                PathFrame prevFrame = _frames[i - 1];
                
                // 计算旋转轴（前一切线到当前切线）
                Vector3 axis = Vector3.Cross(prevFrame.Tangent, tangent);
                
                Vector3 normal, binormal;
                
                if (axis.sqrMagnitude > 0.001f) // 有旋转
                {
                    axis.Normalize();
                    float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(prevFrame.Tangent, tangent), -1f, 1f));
                    
                    // 使用Rodrigues旋转公式旋转前一帧的法线和副法线
                    normal = RotateVector(prevFrame.Normal, axis, angle);
                    binormal = RotateVector(prevFrame.Binormal, axis, angle);
                }
                else // 切线方向几乎相同，直接继承
                {
                    normal = prevFrame.Normal;
                    binormal = prevFrame.Binormal;
                }
                
                // 正交化（确保垂直）
                normal = (normal - Vector3.Dot(normal, tangent) * tangent).normalized;
                binormal = Vector3.Cross(tangent, normal).normalized;
                
                _frames.Add(new PathFrame(tangent, normal, binormal));
            }
        }
        
        /// <summary>
        /// 计算初始法线方向
        /// </summary>
        private Vector3 ComputeInitialNormal(Vector3 tangent)
        {
            // 选择与切线最不平行的轴作为参考
            Vector3 reference = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(tangent, Vector3.up)) > 0.9f)
            {
                reference = Vector3.right;
            }
            
            return Vector3.Cross(tangent, reference).normalized;
        }
        
        /// <summary>
        /// Rodrigues旋转公式：绕任意轴旋转向量
        /// </summary>
        private Vector3 RotateVector(Vector3 v, Vector3 axis, float angle)
        {
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);
            
            return v * cos + Vector3.Cross(axis, v) * sin + axis * Vector3.Dot(axis, v) * (1f - cos);
        }
        
        /// <summary>
        /// 获取指定索引处的参考帧
        /// 如果未计算帧，会自动计算
        /// </summary>
        public PathFrame GetFrame(int index)
        {
            if (_frames == null || _frames.Count != Points.Count)
            {
                ComputeParallelTransportFrames();
            }
            
            if (_frames != null && index >= 0 && index < _frames.Count)
            {
                return _frames[index];
            }
            
            // 降级方案：使用旧方法
            return new PathFrame(GetTangent(index), GetNormalFallback(index), GetBinormalFallback(index));
        }
        
        /// <summary>
        /// 计算指定索引处的法线方向（使用预计算的帧）
        /// </summary>
        public Vector3 GetNormal(int index)
        {
            return GetFrame(index).Normal;
        }
        
        /// <summary>
        /// 计算指定索引处的副法线方向（使用预计算的帧）
        /// </summary>
        public Vector3 GetBinormal(int index)
        {
            return GetFrame(index).Binormal;
        }
        
        /// <summary>
        /// 降级方案：旧的法线计算方法
        /// </summary>
        private Vector3 GetNormalFallback(int index)
        {
            Vector3 tangent = GetTangent(index);
            Vector3 reference = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(tangent, reference)) > 0.9f)
            {
                reference = Vector3.right;
            }
            return Vector3.Cross(tangent, reference).normalized;
        }
        
        /// <summary>
        /// 降级方案：旧的副法线计算方法
        /// </summary>
        private Vector3 GetBinormalFallback(int index)
        {
            Vector3 tangent = GetTangent(index);
            Vector3 normal = GetNormalFallback(index);
            return Vector3.Cross(normal, tangent).normalized;
        }
        
        /// <summary>
        /// 获取路径总长度
        /// </summary>
        public float GetTotalLength()
        {
            float length = 0f;
            for (int i = 1; i < Points.Count; i++)
            {
                length += Vector3.Distance(Points[i - 1], Points[i]);
            }
            return length;
        }
        
        /// <summary>
        /// 获取指定索引处的归一化位置 (0-1)
        /// 对于分支，会继承父路径在分支点的进度
        /// </summary>
        public float GetNormalizedPosition(int index)
        {
            if (Points.Count <= 1)
                return IsBranch ? BranchStartProgress : 0f;
            
            float totalLength = GetTotalLength();
            if (totalLength <= 0f)
                return IsBranch ? BranchStartProgress : 0f;
            
            float currentLength = 0f;
            for (int i = 1; i <= index && i < Points.Count; i++)
            {
                currentLength += Vector3.Distance(Points[i - 1], Points[i]);
            }
            
            float localProgress = currentLength / totalLength;
            
            // 如果是分支，从父路径的进度开始，继续向上增长
            // 分支的进度范围是 [BranchStartProgress, 1.0]
            if (IsBranch)
            {
                float remainingRange = 1.0f - BranchStartProgress;
                return BranchStartProgress + localProgress * remainingRange;
            }
            
            return localProgress;
        }
        
        /// <summary>
        /// 添加分支
        /// </summary>
        /// <param name="branch">分支路径</param>
        /// <param name="startIndex">分支在主干上的起始索引</param>
        /// <param name="startThickness">分支起始粗细（可选，默认继承主干该点的粗细）</param>
        public void AddBranch(VinePath branch, int startIndex, float startThickness = -1f)
        {
            branch.IsBranch = true;
            
            // 计算并继承主干在分支点的归一化位置
            branch.BranchStartProgress = GetNormalizedPosition(startIndex);
            
            // 如果没有指定起始粗细，计算主干该点的粗细
            if (startThickness < 0f)
            {
                startThickness = Mathf.Lerp(1.0f, 0.1f, branch.BranchStartProgress) * 0.8f; // 比主干细20%
            }
            
            branch.BranchStartThickness = startThickness;
            Branches.Add(branch);
        }
        
        /// <summary>
        /// 获取所有路径（包括分支）的总点数
        /// </summary>
        public int GetTotalPointCount()
        {
            int count = Points.Count;
            foreach (var branch in Branches)
            {
                count += branch.GetTotalPointCount();
            }
            return count;
        }
        
        /// <summary>
        /// 清空路径
        /// </summary>
        public void Clear()
        {
            Points.Clear();
            Branches.Clear();
            IsBranch = false;
        }
        
        #region 样条插值
        
        /// <summary>
        /// 使用Catmull-Rom样条对路径进行平滑插值
        /// </summary>
        /// <param name="pointsPerSegment">每段之间插入的点数（默认4）</param>
        /// <returns>平滑后的新路径（注意：返回的路径需要重新计算参考帧）</returns>
        public VinePath SmoothWithSpline(int pointsPerSegment = 4)
        {
            if (Points.Count < 2)
                return this;
            
            VinePath smoothPath = new VinePath
            {
                IsBranch = this.IsBranch,
                BranchStartThickness = this.BranchStartThickness,
                BranchStartProgress = this.BranchStartProgress
            };
            
            // 为每对相邻点之间插值
            for (int i = 0; i < Points.Count - 1; i++)
            {
                Vector3 p0 = i > 0 ? Points[i - 1] : Points[i];
                Vector3 p1 = Points[i];
                Vector3 p2 = Points[i + 1];
                Vector3 p3 = i < Points.Count - 2 ? Points[i + 2] : Points[i + 1];
                
                // 在当前段内插值
                for (int j = 0; j < pointsPerSegment; j++)
                {
                    float t = (float)j / pointsPerSegment;
                    Vector3 interpolatedPoint = CatmullRomPoint(p0, p1, p2, p3, t);
                    smoothPath.AddPoint(interpolatedPoint);
                }
            }
            
            // 添加最后一个点
            smoothPath.AddPoint(Points[Points.Count - 1]);
            
            // 递归处理分支
            foreach (var branch in Branches)
            {
                VinePath smoothBranch = branch.SmoothWithSpline(pointsPerSegment);
                smoothPath.Branches.Add(smoothBranch);
            }
            
            return smoothPath;
        }
        
        /// <summary>
        /// Catmull-Rom样条插值（通过控制点）
        /// </summary>
        /// <param name="p0">前一个控制点</param>
        /// <param name="p1">当前段起点</param>
        /// <param name="p2">当前段终点</param>
        /// <param name="p3">后一个控制点</param>
        /// <param name="t">插值参数 (0-1)</param>
        /// <returns>插值后的点</returns>
        private Vector3 CatmullRomPoint(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            // Catmull-Rom样条公式
            float t2 = t * t;
            float t3 = t2 * t;
            
            Vector3 result =
                0.5f * ((2f * p1) +
                       (-p0 + p2) * t +
                       (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                       (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
            
            return result;
        }
        
        #endregion
    }
}

