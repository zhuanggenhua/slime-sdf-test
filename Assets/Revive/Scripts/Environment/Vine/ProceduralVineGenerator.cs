using System.Collections.Generic;
using UnityEngine;

namespace Revive.Environment
{
    /// <summary>
    /// 程序化藤蔓网格生成器
    /// 根据BoxCollider范围生成藤蔓路径和网格
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [AddComponentMenu("Revive/Environment/Procedural Vine Generator")]
    public class ProceduralVineGenerator : MonoBehaviour
    {
        [Header("Base点生成")]
        [Tooltip("Base点间隔（米）")]
        public float BasePointSpacing = 0.5f;
        
        [Tooltip("Base点随机偏移范围")]
        public float BasePointRandomOffset = 0.5f;
        
        [Header("底部藤蔓")]
        [Tooltip("每个Base点的底部段数")]
        [Range(2, 8)]
        public int BottomSegmentCount = 4;
        
        [Tooltip("底部段长度范围（最小,最大）")]
        public Vector2 BottomSegmentLength = new Vector2(0.3f, 0.6f);
        
        [Tooltip("底部XZ方向随机性 (0-1)")]
        [Range(0f, 1f)]
        public float BottomRandomness = 0.8f;
        
        [Tooltip("底部Y向偏移范围（避免堆叠）")]
        public Vector2 BottomYOffset = new Vector2(-0.5f, 0.8f);
        
        [Header("向上生长")]
        [Tooltip("向上段数")]
        [Range(5, 100)]
        public int UpwardSegmentCount = 12;
        
        [Tooltip("向上段长度范围（最小,最大）")]
        public Vector2 UpwardSegmentLength = new Vector2(0.4f, 0.8f);
        
        [Tooltip("向上方向随机性（方向偏移角度）")]
        [Range(0f, 90f)]
        public float UpwardDirectionVariation = 60f;
        
        [Tooltip("向上趋势强度（0=完全随机，1=严格向上）")]
        [Range(0f, 1f)]
        public float UpwardBias = 0.4f;
        
        [Header("分支设置")]
        [Tooltip("启用分支")]
        public bool EnableBranches = true;
        
        [Tooltip("分支概率 (0-1)")]
        [Range(0f, 1f)]
        public float BranchProbability = 0.3f;
        
        [Tooltip("分支长度比例")]
        [Range(0.3f, 0.9f)]
        public float BranchLengthRatio = 0.6f;
        
        [Tooltip("分支起始位置范围（沿主干的0-1位置）")]
        public Vector2 BranchStartRange = new Vector2(0.3f, 0.7f);
        
        [Header("Tube网格")]
        [Tooltip("圆形截面分段数")]
        [Range(4, 16)]
        public int TubeSegments = 6;
        
        [Tooltip("根部半径（米）")]
        public float BaseRadius = 0.08f;
        
        [Tooltip("末端半径（米）")]
        public float TipRadius = 0.02f;
        
        [Header("样条平滑")]
        [Tooltip("启用Catmull-Rom样条平滑")]
        public bool EnableSplineSmoothing = true;
        
        [Tooltip("每段之间插入的点数（越多越平滑）")]
        [Range(2, 8)]
        public int SplinePointsPerSegment = 4;
        
        [Header("边缘处理")]
        [Tooltip("启用边缘排斥力（避免藤蔓贴边）")]
        public bool EnableBoundaryRepulsion = true;
        
        [Tooltip("边缘检测距离（距离边界多远开始排斥）")]
        [Range(0.1f, 2f)]
        public float BoundaryDetectionDistance = 0.8f;
        
        [Tooltip("边缘排斥强度（0-1）")]
        [Range(0f, 1f)]
        public float BoundaryRepulsionStrength = 0.5f;
        
        [Header("引用")]
        [Tooltip("网格过滤器（自动获取）")]
        public MeshFilter MeshFilter;
        
        [Tooltip("碰撞体")]
        public BoxCollider BoxCollider;
        
        [Header("随机种子")]
        [Tooltip("使用固定随机种子（0=随机）")]
        public int RandomSeed = 0;
        
        // 生成的路径
        private List<VinePath> _generatedPaths = new List<VinePath>();
        
        private void Awake()
        {
            if (MeshFilter == null)
                MeshFilter = GetComponent<MeshFilter>();
            
            if (BoxCollider == null)
                BoxCollider = GetComponent<BoxCollider>();
        }
        
        /// <summary>
        /// 生成藤蔓网格（运行时和Editor都可调用）
        /// </summary>
        public Mesh GenerateVineMesh()
        {
            if (BoxCollider == null)
            {
                Debug.LogError("[ProceduralVineGenerator] BoxCollider未设置！", this);
                return null;
            }
            
            // 设置随机种子
            if (RandomSeed != 0)
            {
                Random.InitState(RandomSeed);
            }
            
            // 1. 生成路径
            _generatedPaths = GeneratePaths();
            
            // 2. 构建网格
            VineMeshBuilder builder = new VineMeshBuilder
            {
                TubeSegments = TubeSegments,
                BaseRadius = BaseRadius,
                TipRadius = TipRadius
            };
            
            Mesh mesh = builder.BuildMesh(_generatedPaths);
            
            // 3. 应用到MeshFilter
            if (MeshFilter != null)
            {
                MeshFilter.sharedMesh = mesh;
            }
            
            Debug.Log($"[ProceduralVineGenerator] 生成完成！路径数: {_generatedPaths.Count}, 顶点数: {mesh.vertexCount}");
            
            return mesh;
        }
        
        /// <summary>
        /// 生成所有藤蔓路径
        /// </summary>
        private List<VinePath> GeneratePaths()
        {
            List<VinePath> paths = new List<VinePath>();
            Bounds bounds = GetLocalBounds();
            
            // 1. 生成Base点
            List<Vector3> basePoints = GenerateBasePoints(bounds);
            
            // 2. 为每个Base点生成主干路径
            foreach (var basePoint in basePoints)
            {
                VinePath mainPath = GenerateMainPath(basePoint, bounds);
                if (mainPath != null && mainPath.Count > 0)
                {
                    // 应用样条平滑
                    if (EnableSplineSmoothing)
                    {
                        mainPath = mainPath.SmoothWithSpline(SplinePointsPerSegment);
                    }
                    
                    paths.Add(mainPath);
                    
                    // 3. 可选：生成分支
                    if (EnableBranches && Random.value < BranchProbability)
                    {
                        GenerateBranches(mainPath, bounds);
                    }
                }
            }
            
            return paths;
        }
        
        /// <summary>
        /// 生成Base点
        /// </summary>
        private List<Vector3> GenerateBasePoints(Bounds bounds)
        {
            List<Vector3> points = new List<Vector3>();
            
            // 在底部XZ平面上按网格生成
            float xMin = bounds.min.x;
            float xMax = bounds.max.x;
            float zMin = bounds.min.z;
            float zMax = bounds.max.z;
            float yBase = bounds.min.y;
            
            for (float x = xMin; x <= xMax; x += BasePointSpacing)
            {
                for (float z = zMin; z <= zMax; z += BasePointSpacing)
                {
                    // 添加随机偏移
                    Vector3 point = new Vector3(
                        x + Random.Range(-BasePointRandomOffset, BasePointRandomOffset),
                        yBase,
                        z + Random.Range(-BasePointRandomOffset, BasePointRandomOffset)
                    );
                    
                    // 约束在范围内
                    point = ClampToBox(point, bounds);
                    points.Add(point);
                }
            }
            
            return points;
        }
        
        /// <summary>
        /// 生成主干路径（底部+向上）
        /// </summary>
        private VinePath GenerateMainPath(Vector3 startPoint, Bounds bounds)
        {
            VinePath path = new VinePath();
            Vector3 currentPoint = startPoint;
            path.AddPoint(currentPoint);
            
            // 1. 生成底部段（XZ平面随机移动）
            Vector3 bottomDirection = new Vector3(
                Random.Range(-1f, 1f),
                0f,
                Random.Range(-1f, 1f)
            ).normalized;
            
            for (int i = 0; i < BottomSegmentCount; i++)
            {
                // 添加随机扰动（包括Y向偏移，避免底部堆叠）
                Vector3 randomOffset = new Vector3(
                    Random.Range(-BottomRandomness, BottomRandomness),
                    Random.Range(BottomYOffset.x, BottomYOffset.y), // Y向偏移
                    Random.Range(-BottomRandomness, BottomRandomness)
                );
                
                Vector3 direction = (bottomDirection + randomOffset).normalized;
                float segmentLength = Random.Range(BottomSegmentLength.x, BottomSegmentLength.y);
                
                currentPoint += direction * segmentLength;
                currentPoint = ClampToBox(currentPoint, bounds);
                path.AddPoint(currentPoint);
            }
            
            // 2. 生成向上段（渐进式方向变化）
            Vector3 currentDirection = Vector3.up; // 初始向上
            
            for (int i = 0; i < UpwardSegmentCount; i++)
            {
                // 计算边界排斥力
                Vector3 boundaryRepulsion = CalculateBoundaryRepulsion(currentPoint, bounds);
                
                // 计算新方向：基于当前方向 + 随机变化 + 向上偏向 + 边界排斥
                Vector3 randomVariation = Random.onUnitSphere;
                
                // 混合当前方向、随机方向和向上方向
                Vector3 targetDirection = Vector3.Lerp(
                    Vector3.Lerp(currentDirection, randomVariation, UpwardDirectionVariation / 90f),
                    Vector3.up,
                    UpwardBias
                ).normalized;
                
                // 应用边界排斥力
                if (boundaryRepulsion != Vector3.zero)
                {
                    targetDirection = (targetDirection + boundaryRepulsion).normalized;
                }
                
                // 平滑过渡到新方向（避免突变）
                currentDirection = Vector3.Slerp(currentDirection, targetDirection, 0.7f).normalized;
                
                float segmentLength = Random.Range(UpwardSegmentLength.x, UpwardSegmentLength.y);
                
                currentPoint += currentDirection * segmentLength;
                currentPoint = ClampToBox(currentPoint, bounds);
                path.AddPoint(currentPoint);
                
                // 如果已到达顶部，提前结束
                if (currentPoint.y >= bounds.max.y - 0.1f)
                {
                    break;
                }
            }
            
            return path;
        }
        
        /// <summary>
        /// 为主干生成分支
        /// </summary>
        private void GenerateBranches(VinePath mainPath, Bounds bounds)
        {
            int branchCount = Random.Range(1, 3); // 1-2个分支
            
            for (int i = 0; i < branchCount; i++)
            {
                // 在主干的中部位置随机选择分支起点
                int branchStartIndex = Mathf.RoundToInt(
                    Random.Range(
                        mainPath.Count * BranchStartRange.x,
                        mainPath.Count * BranchStartRange.y
                    )
                );
                
                if (branchStartIndex >= mainPath.Count)
                    continue;
                
                Vector3 branchStart = mainPath.Points[branchStartIndex];
                VinePath branch = GenerateBranchPath(branchStart, mainPath.GetTangent(branchStartIndex), bounds);
                
                if (branch != null && branch.Count > 0)
                {
                    // 应用样条平滑到分支
                    if (EnableSplineSmoothing)
                    {
                        branch = branch.SmoothWithSpline(SplinePointsPerSegment);
                    }
                    
                    mainPath.AddBranch(branch, branchStartIndex);
                }
            }
        }
        
        /// <summary>
        /// 生成分支路径（使用渐进式方向变化）
        /// </summary>
        private VinePath GenerateBranchPath(Vector3 startPoint, Vector3 parentTangent, Bounds bounds)
        {
            VinePath branch = new VinePath();
            Vector3 currentPoint = startPoint;
            branch.AddPoint(currentPoint);
            
            // 分支初始方向：偏离主干的随机方向，但倾向向上
            Vector3 perpendicular = Vector3.Cross(parentTangent, Random.onUnitSphere).normalized;
            Vector3 initialDirection = Vector3.Lerp(perpendicular, Vector3.up, 0.5f).normalized;
            Vector3 currentDirection = initialDirection;
            
            int branchSegments = Mathf.RoundToInt(UpwardSegmentCount * BranchLengthRatio);
            
            for (int i = 0; i < branchSegments; i++)
            {
                // 分支也使用渐进式方向变化，逐渐向上
                float progressRatio = (float)i / branchSegments;
                
                // 计算边界排斥力
                Vector3 boundaryRepulsion = CalculateBoundaryRepulsion(currentPoint, bounds);
                
                // 随着生长，逐渐增加向上的趋势
                float currentUpwardBias = Mathf.Lerp(0.3f, UpwardBias, progressRatio);
                
                Vector3 randomVariation = Random.onUnitSphere;
                
                // 混合当前方向、随机方向和向上方向
                Vector3 targetDirection = Vector3.Lerp(
                    Vector3.Lerp(currentDirection, randomVariation, UpwardDirectionVariation / 90f),
                    Vector3.up,
                    currentUpwardBias
                ).normalized;
                
                // 应用边界排斥力
                if (boundaryRepulsion != Vector3.zero)
                {
                    targetDirection = (targetDirection + boundaryRepulsion).normalized;
                }
                
                // 平滑过渡到新方向
                currentDirection = Vector3.Slerp(currentDirection, targetDirection, 0.7f).normalized;
                
                float segmentLength = Random.Range(UpwardSegmentLength.x, UpwardSegmentLength.y);
                
                currentPoint += currentDirection * segmentLength;
                currentPoint = ClampToBox(currentPoint, bounds);
                branch.AddPoint(currentPoint);
                
                // 如果到达边界，提前结束
                if (currentPoint.y >= bounds.max.y - 0.1f)
                {
                    break;
                }
            }
            
            return branch;
        }
        
        /// <summary>
        /// 计算边界排斥力（势场）
        /// 当点接近边界时，返回指向内部的排斥向量
        /// </summary>
        private Vector3 CalculateBoundaryRepulsion(Vector3 point, Bounds bounds)
        {
            if (!EnableBoundaryRepulsion)
                return Vector3.zero;
            
            Vector3 repulsion = Vector3.zero;
            
            // 检测到各个边界面的距离
            float distToMinX = point.x - bounds.min.x;
            float distToMaxX = bounds.max.x - point.x;
            float distToMinY = point.y - bounds.min.y;
            float distToMaxY = bounds.max.y - point.y;
            float distToMinZ = point.z - bounds.min.z;
            float distToMaxZ = bounds.max.z - point.z;
            
            // X轴排斥
            if (distToMinX < BoundaryDetectionDistance)
            {
                float strength = 1f - (distToMinX / BoundaryDetectionDistance);
                repulsion.x += strength * BoundaryRepulsionStrength;
            }
            if (distToMaxX < BoundaryDetectionDistance)
            {
                float strength = 1f - (distToMaxX / BoundaryDetectionDistance);
                repulsion.x -= strength * BoundaryRepulsionStrength;
            }
            
            // Y轴排斥（只排斥底部，不排斥顶部让藤蔓可以到顶）
            if (distToMinY < BoundaryDetectionDistance)
            {
                float strength = 1f - (distToMinY / BoundaryDetectionDistance);
                repulsion.y += strength * BoundaryRepulsionStrength;
            }
            
            // Z轴排斥
            if (distToMinZ < BoundaryDetectionDistance)
            {
                float strength = 1f - (distToMinZ / BoundaryDetectionDistance);
                repulsion.z += strength * BoundaryRepulsionStrength;
            }
            if (distToMaxZ < BoundaryDetectionDistance)
            {
                float strength = 1f - (distToMaxZ / BoundaryDetectionDistance);
                repulsion.z -= strength * BoundaryRepulsionStrength;
            }
            
            return repulsion;
        }
        
        /// <summary>
        /// 约束点到BoxCollider范围内
        /// </summary>
        private Vector3 ClampToBox(Vector3 point, Bounds bounds)
        {
            return new Vector3(
                Mathf.Clamp(point.x, bounds.min.x, bounds.max.x),
                Mathf.Clamp(point.y, bounds.min.y, bounds.max.y),
                Mathf.Clamp(point.z, bounds.min.z, bounds.max.z)
            );
        }
        
        /// <summary>
        /// 获取世界空间的Bounds
        /// </summary>
        private Bounds GetLocalBounds()
        {
            Bounds localBounds = new Bounds(BoxCollider.center, BoxCollider.size);
            return localBounds;
        }
        
#if UNITY_EDITOR
        /// <summary>
        /// 绘制BoxCollider范围
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (BoxCollider == null)
                return;
            
            Gizmos.color = new Color(0.3f, 1f, 0.3f, 0.3f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(BoxCollider.center, BoxCollider.size);
            Gizmos.matrix = Matrix4x4.identity;
            
            // 绘制Base点位置预览
            if (_generatedPaths != null && _generatedPaths.Count > 0)
            {
                Gizmos.color = Color.red;
                foreach (var path in _generatedPaths)
                {
                    if (path.Count > 0)
                    {
                        Gizmos.DrawSphere(path.Points[0], 0.05f);
                    }
                }
            }
        }
#endif
    }
}

