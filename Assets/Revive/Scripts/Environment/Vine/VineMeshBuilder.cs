using System.Collections.Generic;
using UnityEngine;

namespace Revive.Environment
{
    /// <summary>
    /// 藤蔓网格构建器
    /// 根据路径生成Tube网格，并设置顶点色
    /// </summary>
    public class VineMeshBuilder
    {
        /// <summary>
        /// Tube圆形截面分段数
        /// </summary>
        public int TubeSegments = 8;
        
        /// <summary>
        /// 根部半径
        /// </summary>
        public float BaseRadius = 0.08f;
        
        /// <summary>
        /// 末端半径
        /// </summary>
        public float TipRadius = 0.02f;
        
        /// <summary>
        /// 构建网格
        /// </summary>
        /// <param name="paths">所有藤蔓路径（主干+分支）</param>
        /// <returns>生成的网格</returns>
        public Mesh BuildMesh(List<VinePath> paths)
        {
            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();
            List<Color> colors = new List<Color>();
            List<Vector3> normals = new List<Vector3>();
            
            // 为每条路径生成网格
            foreach (var path in paths)
            {
                // 预计算平行传输帧（避免顶点翻转）
                path.ComputeParallelTransportFrames();
                BuildPathMesh(path, vertices, triangles, colors, normals);
                
                // 递归处理分支
                foreach (var branch in path.Branches)
                {
                    // 为分支也计算帧
                    branch.ComputeParallelTransportFrames();
                    BuildPathMesh(branch, vertices, triangles, colors, normals);
                }
            }
            
            // 组装Mesh
            Mesh mesh = new Mesh();
            mesh.name = "ProceduralVine";
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetColors(colors);
            // mesh.SetNormals(normals);
            mesh.RecalculateNormals();
            
            // 重新计算边界
            mesh.RecalculateBounds();
            
            return mesh;
        }
        
        /// <summary>
        /// 为单条路径构建网格
        /// </summary>
        private void BuildPathMesh(VinePath path, List<Vector3> vertices, List<int> triangles, 
                                   List<Color> colors, List<Vector3> normals)
        {
            if (path.Count < 2)
                return;
            
            int startVertexIndex = vertices.Count;
            
            // 沿路径生成截面圈
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 point = path.Points[i];
                Vector3 tangent = path.GetTangent(i);
                Vector3 normal = path.GetNormal(i);
                Vector3 binormal = path.GetBinormal(i);
                
                // 计算该点的归一化位置和粗细
                float normalizedPos = path.GetNormalizedPosition(i);
                float radius = CalculateRadiusAtProgress(normalizedPos, path);
                
                // 生成圆形截面的顶点
                for (int j = 0; j < TubeSegments; j++)
                {
                    float angle = (float)j / TubeSegments * Mathf.PI * 2f;
                    float cos = Mathf.Cos(angle);
                    float sin = Mathf.Sin(angle);
                    
                    // 计算截面上的点
                    Vector3 offset = (normal * cos + binormal * sin) * radius;
                    Vector3 vertex = point + offset;
                    
                    vertices.Add(vertex);
                    
                    // 顶点色：x=路径位置(0-1), y=粗细
                    colors.Add(new Color(normalizedPos, radius, 0f, 1f));
                    
                    // 法线：从圆心指向顶点
                    normals.Add(offset.normalized);
                }
            }
            
            // 生成三角形索引
            for (int i = 0; i < path.Count - 1; i++)
            {
                int ringStart = startVertexIndex + i * TubeSegments;
                int nextRingStart = ringStart + TubeSegments;
                
                for (int j = 0; j < TubeSegments; j++)
                {
                    int current = ringStart + j;
                    int next = ringStart + (j + 1) % TubeSegments;
                    int currentNext = nextRingStart + j;
                    int nextNext = nextRingStart + (j + 1) % TubeSegments;
                    
                    // 两个三角形组成四边形
                    triangles.Add(current);
                    triangles.Add(next);
                    triangles.Add(currentNext);
                    
                    triangles.Add(next);
                    triangles.Add(nextNext);
                    triangles.Add(currentNext);
                }
            }
            
            // 封闭末端（可选）
            // AddCapTriangles(path, vertices, triangles, normals, colors, startVertexIndex);
        }
        
        /// <summary>
        /// 根据路径进度和粗细计算实际半径
        /// </summary>
        private float CalculateRadiusAtProgress(float progress, VinePath path)
        {
            if (path.IsBranch)
            {
                // 分支：基于继承的粗细缩放半径
                float branchBaseRadius = BaseRadius * path.BranchStartThickness;
                float branchTipRadius = TipRadius;
                return Mathf.Lerp(branchBaseRadius, branchTipRadius, progress);
            }
            else
            {
                // 主干：正常从BaseRadius到TipRadius
                return Mathf.Lerp(BaseRadius, TipRadius, progress);
            }
        }
        
        /// <summary>
        /// 封闭Tube末端（生成圆盘）
        /// </summary>
        private void AddCapTriangles(VinePath path, List<Vector3> vertices, List<int> triangles, 
                                     List<Vector3> normals, List<Color> colors, int startVertexIndex)
        {
            if (path.Count < 1)
                return;
            
            // 末端封盖
            int lastRingStart = startVertexIndex + (path.Count - 1) * TubeSegments;
            Vector3 center = path.Points[path.Count - 1];
            Vector3 tangent = path.GetTangent(path.Count - 1);
            
            // 添加中心点
            int centerIndex = vertices.Count;
            vertices.Add(center);
            normals.Add(tangent);
            colors.Add(new Color(1f, 0.1f, 0f, 1f)); // 末端
            
            // 生成三角形扇形
            for (int i = 0; i < TubeSegments; i++)
            {
                int current = lastRingStart + i;
                int next = lastRingStart + (i + 1) % TubeSegments;
                
                triangles.Add(centerIndex);
                triangles.Add(next);
                triangles.Add(current);
            }
        }
    }
}

