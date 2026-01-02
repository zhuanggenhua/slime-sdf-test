using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Revive.Slime.Editor
{
    public sealed class WorldSdfBakerWindow : EditorWindow
    {
        [SerializeField] private Vector3 boundsCenterWorld = Vector3.zero;
        [SerializeField] private Vector3 boundsSizeWorld = new Vector3(50f, 20f, 50f);
        [SerializeField] private float voxelSizeWorld = 0.1f;
        [SerializeField] private float maxDistanceWorld = 1.0f;
        [SerializeField] private LayerMask staticLayers = 0;
        [SerializeField] private bool includeTriggers;
        [SerializeField] private MeshSeedMode meshSeedMode = MeshSeedMode.Triangles;
        [SerializeField] private bool onlyBakeSelection;
        [SerializeField] private bool autoEnableUnreadableMeshReadWrite;
        [SerializeField] private string outputDirectoryAssetPath = "Assets/MMData/SDFData";

        private enum MeshSeedMode
        {
            Triangles = 0,
            Bounds = 1,
        }

        private struct BvhNode
        {
            public Vector3 BMin;
            public Vector3 BMax;
            public int Left;
            public int Right;
            public int Escape;
            public int TriStart;
            public int TriCount;
        }

        private struct BakeDistanceJob : IJobParallelFor
        {
            [ReadOnly] public NativeSlice<int> Keys;
            [ReadOnly] public NativeArray<byte> CandidateKinds;
            [ReadOnly] public NativeArray<Vector3> CandidateBoundsMin;
            [ReadOnly] public NativeArray<Vector3> CandidateBoundsMax;
            [ReadOnly] public NativeArray<Matrix4x4> CandidateWorldToLocal;
            [ReadOnly] public NativeArray<Vector3> CandidateLocalCenter;
            [ReadOnly] public NativeArray<Vector3> CandidateBoxHalfSize;
            [ReadOnly] public NativeArray<float> CandidateSphereRadius;
            [ReadOnly] public NativeArray<float> CandidateCapsuleRadius;
            [ReadOnly] public NativeArray<float> CandidateCapsuleHalfHeight;
            [ReadOnly] public NativeArray<int> CandidateCapsuleDirection;
            [ReadOnly] public NativeArray<float> CandidateUniformScale;

            [ReadOnly] public NativeArray<int> CandidateMeshIndex;
            [ReadOnly] public NativeArray<int> MeshTriOffset;
            [ReadOnly] public NativeArray<int> MeshTriCount;
            [ReadOnly] public NativeArray<int> MeshTriNormalOffset;
            [ReadOnly] public NativeArray<Vector3> MeshVertsW;
            [ReadOnly] public NativeArray<int> MeshTris;
            [ReadOnly] public NativeArray<Vector3> MeshTriNormalsW;

            [ReadOnly] public NativeArray<int> MeshBvhRoot;
            [ReadOnly] public NativeArray<BvhNode> MeshBvhNodes;
            [ReadOnly] public NativeArray<int> MeshBvhTriIndices;

            public int yzStride;
            public int dimZ;
            public float voxel;
            public Vector3 originWorld;
            public float storeBandWorld;
            public float storeBandSqr;

            [WriteOnly] public NativeSlice<float> OutBestAbs;
            [WriteOnly] public NativeSlice<float> OutBestDWorld;
            [WriteOnly] public NativeSlice<byte> OutFlags;

            private static float Abs(float x) => x < 0f ? -x : x;
            private static float Max(float a, float b) => a > b ? a : b;
            private static float Min(float a, float b) => a < b ? a : b;
            private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
            private static float Sqrt(float v) => (float)Math.Sqrt(v);

            private static Vector3 MultiplyPoint3x4(in Matrix4x4 m, in Vector3 v)
            {
                return new Vector3(
                    m.m00 * v.x + m.m01 * v.y + m.m02 * v.z + m.m03,
                    m.m10 * v.x + m.m11 * v.y + m.m12 * v.z + m.m13,
                    m.m20 * v.x + m.m21 * v.y + m.m22 * v.z + m.m23
                );
            }

            private static float SqrDistancePointAabb(in Vector3 p, in Vector3 bmin, in Vector3 bmax)
            {
                float cx = Clamp(p.x, bmin.x, bmax.x);
                float cy = Clamp(p.y, bmin.y, bmax.y);
                float cz = Clamp(p.z, bmin.z, bmax.z);
                float dx = p.x - cx;
                float dy = p.y - cy;
                float dz = p.z - cz;
                return dx * dx + dy * dy + dz * dz;
            }

            private static float SdBox(in Vector3 p, in Vector3 b)
            {
                Vector3 q = new Vector3(Abs(p.x), Abs(p.y), Abs(p.z)) - b;
                Vector3 qMax = new Vector3(Max(q.x, 0f), Max(q.y, 0f), Max(q.z, 0f));
                float outside = Sqrt(qMax.x * qMax.x + qMax.y * qMax.y + qMax.z * qMax.z);
                float inside = Min(Max(q.x, Max(q.y, q.z)), 0f);
                return outside + inside;
            }

            private static Vector3 ClosestPointOnTriangle(in Vector3 p, in Vector3 a, in Vector3 b, in Vector3 c)
            {
                Vector3 ab = b - a;
                Vector3 ac = c - a;
                Vector3 ap = p - a;

                float d1 = ab.x * ap.x + ab.y * ap.y + ab.z * ap.z;
                float d2 = ac.x * ap.x + ac.y * ap.y + ac.z * ap.z;
                if (d1 <= 0f && d2 <= 0f) return a;

                Vector3 bp = p - b;
                float d3 = ab.x * bp.x + ab.y * bp.y + ab.z * bp.z;
                float d4 = ac.x * bp.x + ac.y * bp.y + ac.z * bp.z;
                if (d3 >= 0f && d4 <= d3) return b;

                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0f && d1 >= 0f && d3 <= 0f)
                {
                    float v = d1 / (d1 - d3);
                    return a + v * ab;
                }

                Vector3 cp = p - c;
                float d5 = ab.x * cp.x + ab.y * cp.y + ab.z * cp.z;
                float d6 = ac.x * cp.x + ac.y * cp.y + ac.z * cp.z;
                if (d6 >= 0f && d5 <= d6) return c;

                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0f && d2 >= 0f && d6 <= 0f)
                {
                    float w = d2 / (d2 - d6);
                    return a + w * ac;
                }

                float va = d3 * d6 - d5 * d4;
                if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
                {
                    float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                    return b + w * (c - b);
                }

                float denom = 1f / (va + vb + vc);
                float v2 = vb * denom;
                float w2 = vc * denom;
                return a + ab * v2 + ac * w2;
            }

            public void Execute(int index)
            {
                int key = Keys[index];

                int x = key / yzStride;
                int rem = key - x * yzStride;
                int y = rem / dimZ;
                int z = rem - y * dimZ;

                Vector3 pWorld = originWorld + new Vector3(x * voxel, y * voxel, z * voxel);

                float bestAbs = storeBandWorld + 1f;
                float bestDWorld = 0f;
                bool bestSignedKnown = false;
                bool anyBoundsPass = false;

                for (int ci = 0; ci < CandidateKinds.Length; ci++)
                {
                    float boundsSqr = SqrDistancePointAabb(pWorld, CandidateBoundsMin[ci], CandidateBoundsMax[ci]);
                    if (boundsSqr > storeBandSqr)
                        continue;

                    anyBoundsPass = true;
                    float bestAbsSqr = bestAbs * bestAbs;
                    if (boundsSqr > bestAbsSqr)
                        continue;

                    byte kind = CandidateKinds[ci];
                    float uniformScale = CandidateUniformScale[ci];
                    if (kind == 1)
                    {
                        Vector3 p = MultiplyPoint3x4(CandidateWorldToLocal[ci], pWorld - CandidateLocalCenter[ci]);
                        float d = SdBox(p, CandidateBoxHalfSize[ci]);
                        float abs = Abs(d);
                        if (abs < bestAbs)
                        {
                            bestAbs = abs;
                            bestDWorld = d;
                            bestSignedKnown = true;
                        }
                        continue;
                    }
                    if (kind == 2)
                    {
                        Vector3 p = MultiplyPoint3x4(CandidateWorldToLocal[ci], pWorld) - CandidateLocalCenter[ci];
                        float d = (Sqrt(p.x * p.x + p.y * p.y + p.z * p.z) - CandidateSphereRadius[ci]) * uniformScale;
                        float abs = Abs(d);
                        if (abs < bestAbs)
                        {
                            bestAbs = abs;
                            bestDWorld = d;
                            bestSignedKnown = true;
                        }
                        continue;
                    }
                    if (kind == 3)
                    {
                        Vector3 p = MultiplyPoint3x4(CandidateWorldToLocal[ci], pWorld) - CandidateLocalCenter[ci];
                        int dir = CandidateCapsuleDirection[ci];
                        if (dir == 0)
                            p = new Vector3(p.y, p.x, p.z);
                        else if (dir == 2)
                            p = new Vector3(p.x, p.z, p.y);

                        float r = CandidateCapsuleRadius[ci];
                        float h = CandidateCapsuleHalfHeight[ci];
                        float yClamped = Clamp(p.y, -h, h);
                        float dxz = Sqrt(p.x * p.x + p.z * p.z);
                        float dd = Sqrt(dxz * dxz + (p.y - yClamped) * (p.y - yClamped)) - r;
                        float d = dd * uniformScale;
                        float abs = Abs(d);
                        if (abs < bestAbs)
                        {
                            bestAbs = abs;
                            bestDWorld = d;
                            bestSignedKnown = true;
                        }
                        continue;
                    }

                    int mi = CandidateMeshIndex[ci];
                    if (mi >= 0)
                    {
                        float bestSqr = bestAbs * bestAbs;
                        int triOffset = MeshTriOffset[mi];
                        int triCount = MeshTriCount[mi];
                        int nOffset = MeshTriNormalOffset[mi];
                        int root = (MeshBvhRoot.IsCreated && mi < MeshBvhRoot.Length) ? MeshBvhRoot[mi] : -1;
                        if (root >= 0 && MeshBvhNodes.IsCreated && MeshBvhTriIndices.IsCreated)
                        {
                            int nodeIdx = root;
                            while (nodeIdx >= 0)
                            {
                                BvhNode node = MeshBvhNodes[nodeIdx];
                                float aabbSqr = SqrDistancePointAabb(pWorld, node.BMin, node.BMax);
                                if (aabbSqr > bestSqr)
                                {
                                    nodeIdx = node.Escape;
                                    continue;
                                }

                                if (node.TriCount > 0)
                                {
                                    int start = node.TriStart;
                                    int end = start + node.TriCount;
                                    for (int it = start; it < end; it++)
                                    {
                                        int t = MeshBvhTriIndices[it];
                                        if ((uint)t >= (uint)triCount)
                                            continue;

                                        int ti = triOffset + t * 3;
                                        int i0 = MeshTris[ti];
                                        int i1 = MeshTris[ti + 1];
                                        int i2 = MeshTris[ti + 2];

                                        Vector3 a = MeshVertsW[i0];
                                        Vector3 b = MeshVertsW[i1];
                                        Vector3 c = MeshVertsW[i2];
                                        Vector3 triCp = ClosestPointOnTriangle(pWorld, a, b, c);
                                        Vector3 triDelta = pWorld - triCp;
                                        float triDistSqr = triDelta.x * triDelta.x + triDelta.y * triDelta.y + triDelta.z * triDelta.z;
                                        if (triDistSqr < bestSqr)
                                        {
                                            bestSqr = triDistSqr;
                                            float dist = Sqrt(triDistSqr);
                                            bestAbs = dist;
                                            bestDWorld = dist;
                                            bestSignedKnown = false;
                                            if (triDistSqr < 1e-12f)
                                                break;
                                        }
                                    }
                                    nodeIdx = node.Escape;
                                }
                                else
                                {
                                    nodeIdx = node.Left >= 0 ? node.Left : node.Escape;
                                }
                            }
                        }
                        else
                        {
                            for (int t = 0; t < triCount; t++)
                            {
                                int ti = triOffset + t * 3;
                                int i0 = MeshTris[ti];
                                int i1 = MeshTris[ti + 1];
                                int i2 = MeshTris[ti + 2];

                                Vector3 a = MeshVertsW[i0];
                                Vector3 b = MeshVertsW[i1];
                                Vector3 c = MeshVertsW[i2];
                                Vector3 triCp = ClosestPointOnTriangle(pWorld, a, b, c);
                                Vector3 triDelta = pWorld - triCp;
                                float triDistSqr = triDelta.x * triDelta.x + triDelta.y * triDelta.y + triDelta.z * triDelta.z;
                                if (triDistSqr < bestSqr)
                                {
                                    bestSqr = triDistSqr;
                                    float dist = Sqrt(triDistSqr);
                                    bestAbs = dist;
                                    bestDWorld = dist;
                                    bestSignedKnown = false;
                                    if (triDistSqr < 1e-12f)
                                        break;
                                }
                            }
                        }
                    }
                }

                OutBestAbs[index] = bestAbs;
                OutBestDWorld[index] = bestDWorld;
                OutFlags[index] = (byte)((bestSignedKnown ? 1 : 0) | (anyBoundsPass ? 2 : 0));
            }
        }

        [MenuItem("Slime/World/Bake SDF")]
        public static void Open()
        {
            var win = CreateWindow<WorldSdfBakerWindow>();
            win.titleContent = new GUIContent("世界 SDF 烘焙");
            win.Show();
            win.Focus();
        }

        private void OnGUI()
        {
            LoadSettingsFromEditorPrefs();

            EditorGUILayout.HelpBox(
                "说明：SDF 必须烘焙到一个有限的体积范围内（体积越大、体素越小，生成越慢、资源越大）。\n" +
                "一般做法是只烘焙当前关卡/可交互区域；后续如果做分块再扩展到多块。",
                MessageType.Info);

            boundsCenterWorld = EditorGUILayout.Vector3Field("烘焙区域中心(世界)", boundsCenterWorld);
            boundsSizeWorld = EditorGUILayout.Vector3Field("烘焙区域尺寸(世界)", boundsSizeWorld);
            voxelSizeWorld = EditorGUILayout.FloatField("体素大小(世界/米)", voxelSizeWorld);
            maxDistanceWorld = EditorGUILayout.FloatField("最大距离(世界/米)", maxDistanceWorld);
            using (new EditorGUILayout.HorizontalScope())
            {
                int concatenatedMask = InternalEditorUtility.LayerMaskToConcatenatedLayersMask(staticLayers);
                concatenatedMask = EditorGUILayout.MaskField("参与烘焙的层(静态)", concatenatedMask, InternalEditorUtility.layers);
                staticLayers = InternalEditorUtility.ConcatenatedLayersMaskToLayerMask(concatenatedMask);

                if (GUILayout.Button("无", GUILayout.Width(36f)))
                {
                    staticLayers.value = 0;
                }
            }

            includeTriggers = EditorGUILayout.Toggle("包含 Trigger", includeTriggers);
            meshSeedMode = (MeshSeedMode)EditorGUILayout.EnumPopup("Mesh Seed 模式", meshSeedMode);
            onlyBakeSelection = EditorGUILayout.Toggle("仅烘焙选中对象", onlyBakeSelection);
            autoEnableUnreadableMeshReadWrite = EditorGUILayout.Toggle("烘焙时临时开启Mesh可读写(仅不可读)", autoEnableUnreadableMeshReadWrite);

            if (onlyBakeSelection)
            {
                EditorGUILayout.HelpBox("Tip：在 Hierarchy 选中关卡根对象（或关卡父物体）再烘焙，会显著减少候选 Collider 数量，从而提高烘焙速度。", MessageType.Info);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                outputDirectoryAssetPath = EditorGUILayout.TextField("输出目录(Assets下)", outputDirectoryAssetPath);
                if (GUILayout.Button("选择", GUILayout.Width(48f)))
                {
                    string projectRoot = Directory.GetCurrentDirectory().Replace('\\', '/');
                    string assetsRoot = (projectRoot + "/Assets").Replace('\\', '/');
                    string currentAbs = string.IsNullOrEmpty(outputDirectoryAssetPath)
                        ? assetsRoot
                        : Path.Combine(projectRoot, outputDirectoryAssetPath).Replace('\\', '/');

                    string picked = EditorUtility.OpenFolderPanel("选择输出目录（必须在 Assets 下）", currentAbs, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        picked = picked.Replace('\\', '/');
                        if (!picked.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.LogError($"[WorldSdfBaker] 输出目录必须在 Assets 下：picked={picked} assetsRoot={assetsRoot}");
                        }
                        else
                        {
                            outputDirectoryAssetPath = "Assets" + picked.Substring(assetsRoot.Length);
                            outputDirectoryAssetPath = outputDirectoryAssetPath.Replace('\\', '/');
                        }
                    }
                }
                if (GUILayout.Button("定位", GUILayout.Width(48f)))
                {
                    if (!string.IsNullOrEmpty(outputDirectoryAssetPath))
                    {
                        var folder = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(outputDirectoryAssetPath);
                        if (folder != null)
                        {
                            EditorGUIUtility.PingObject(folder);
                            Selection.activeObject = folder;
                        }
                    }
                }
            }

            if (staticLayers.value == 0)
            {
                EditorGUILayout.HelpBox("当前静态层掩码为 0：将不会采样到任何碰撞体，烘焙出来的 SDF 将无效（运行时容易直接穿透）。", MessageType.Warning);
            }

            {
                var bounds = new Bounds(boundsCenterWorld, boundsSizeWorld);
                float voxel = Mathf.Max(0.0001f, voxelSizeWorld);
                int dimX = Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / voxel));
                int dimY = Mathf.Max(1, Mathf.CeilToInt(bounds.size.y / voxel));
                int dimZ = Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / voxel));
                long total = (long)dimX * dimY * dimZ;
                long bytes = Revive.Slime.WorldSdfRuntime.HeaderSizeBytes + total * sizeof(float);
                double mb = bytes / (1024.0 * 1024.0);
                EditorGUILayout.LabelField("预计体素维度", $"{dimX} x {dimY} x {dimZ}");
                EditorGUILayout.LabelField("预计体素数量", total.ToString("N0"));
                EditorGUILayout.LabelField("预计资源大小", $"{mb:F1} MB (float32)");

                if (mb > 64.0)
                {
                    EditorGUILayout.HelpBox("预计资源较大（>64MB）。请确认 bounds/voxel 是否合理；建议先用“从场景碰撞体自动计算范围”。", MessageType.Warning);
                }
            }

            using (new EditorGUI.DisabledScope(voxelSizeWorld <= 0.0001f))
            {
                if (GUILayout.Button("从场景碰撞体自动计算范围"))
                {
                    AutoFitBoundsFromScene();
                }
            }

            using (new EditorGUI.DisabledScope(voxelSizeWorld <= 0.0001f || Selection.transforms == null || Selection.transforms.Length == 0))
            {
                if (GUILayout.Button("从选中对象自动计算范围"))
                {
                    AutoFitBoundsFromSelection();
                }
            }

            using (new EditorGUI.DisabledScope(voxelSizeWorld <= 0.0001f))
            {
                if (GUILayout.Button("烘焙 SDF 资源"))
                {
                    Bake();
                }
            }

            SaveSettingsToEditorPrefs();
        }

        private const string PrefKey_BoundsCenter = "WorldSdfBaker_BoundsCenter";
        private const string PrefKey_BoundsSize = "WorldSdfBaker_BoundsSize";
        private const string PrefKey_VoxelSize = "WorldSdfBaker_VoxelSize";
        private const string PrefKey_MaxDistance = "WorldSdfBaker_MaxDistance";
        private const string PrefKey_StaticLayers = "WorldSdfBaker_StaticLayers";
        private const string PrefKey_IncludeTriggers = "WorldSdfBaker_IncludeTriggers";
        private const string PrefKey_MeshSeedMode = "WorldSdfBaker_MeshSeedMode";
        private const string PrefKey_OnlyBakeSelection = "WorldSdfBaker_OnlyBakeSelection";
        private const string PrefKey_AutoEnableUnreadableMeshReadWrite = "WorldSdfBaker_AutoEnableUnreadableMeshReadWrite";
        private const string PrefKey_OutputDirectoryAssetPath = "WorldSdfBaker_OutputDirectoryAssetPath";

        private void LoadSettingsFromEditorPrefs()
        {
            boundsCenterWorld = new Vector3(
                EditorPrefs.GetFloat(PrefKey_BoundsCenter + "_x", boundsCenterWorld.x),
                EditorPrefs.GetFloat(PrefKey_BoundsCenter + "_y", boundsCenterWorld.y),
                EditorPrefs.GetFloat(PrefKey_BoundsCenter + "_z", boundsCenterWorld.z)
            );
            boundsSizeWorld = new Vector3(
                EditorPrefs.GetFloat(PrefKey_BoundsSize + "_x", boundsSizeWorld.x),
                EditorPrefs.GetFloat(PrefKey_BoundsSize + "_y", boundsSizeWorld.y),
                EditorPrefs.GetFloat(PrefKey_BoundsSize + "_z", boundsSizeWorld.z)
            );
            voxelSizeWorld = EditorPrefs.GetFloat(PrefKey_VoxelSize, voxelSizeWorld);
            maxDistanceWorld = EditorPrefs.GetFloat(PrefKey_MaxDistance, maxDistanceWorld);
            staticLayers.value = EditorPrefs.GetInt(PrefKey_StaticLayers, staticLayers.value);
            includeTriggers = EditorPrefs.GetBool(PrefKey_IncludeTriggers, includeTriggers);
            meshSeedMode = (MeshSeedMode)EditorPrefs.GetInt(PrefKey_MeshSeedMode, (int)meshSeedMode);
            onlyBakeSelection = EditorPrefs.GetBool(PrefKey_OnlyBakeSelection, onlyBakeSelection);
            autoEnableUnreadableMeshReadWrite = EditorPrefs.GetBool(PrefKey_AutoEnableUnreadableMeshReadWrite, autoEnableUnreadableMeshReadWrite);
            outputDirectoryAssetPath = EditorPrefs.GetString(PrefKey_OutputDirectoryAssetPath, outputDirectoryAssetPath);
        }

        private void SaveSettingsToEditorPrefs()
        {
            EditorPrefs.SetFloat(PrefKey_BoundsCenter + "_x", boundsCenterWorld.x);
            EditorPrefs.SetFloat(PrefKey_BoundsCenter + "_y", boundsCenterWorld.y);
            EditorPrefs.SetFloat(PrefKey_BoundsCenter + "_z", boundsCenterWorld.z);
            EditorPrefs.SetFloat(PrefKey_BoundsSize + "_x", boundsSizeWorld.x);
            EditorPrefs.SetFloat(PrefKey_BoundsSize + "_y", boundsSizeWorld.y);
            EditorPrefs.SetFloat(PrefKey_BoundsSize + "_z", boundsSizeWorld.z);
            EditorPrefs.SetFloat(PrefKey_VoxelSize, voxelSizeWorld);
            EditorPrefs.SetFloat(PrefKey_MaxDistance, maxDistanceWorld);
            EditorPrefs.SetInt(PrefKey_StaticLayers, staticLayers.value);
            EditorPrefs.SetBool(PrefKey_IncludeTriggers, includeTriggers);
            EditorPrefs.SetInt(PrefKey_MeshSeedMode, (int)meshSeedMode);
            EditorPrefs.SetBool(PrefKey_OnlyBakeSelection, onlyBakeSelection);
            EditorPrefs.SetBool(PrefKey_AutoEnableUnreadableMeshReadWrite, autoEnableUnreadableMeshReadWrite);
            EditorPrefs.SetString(PrefKey_OutputDirectoryAssetPath, outputDirectoryAssetPath ?? string.Empty);
        }

        private void OnDestroy()
        {
            SaveSettingsToEditorPrefs();
        }

        private void AutoFitBoundsFromScene()
        {
            var colliders = FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            AutoFitBoundsFromColliders(colliders, "Scene");
        }

        private void AutoFitBoundsFromSelection()
        {
            var roots = Selection.transforms;
            if (roots == null || roots.Length == 0)
            {
                Debug.LogWarning("[WorldSdfBaker] 当前未选中任何对象，无法从选中对象自动计算范围。请在 Hierarchy 中选中一个父对象/根对象再试。 ");
                return;
            }

            var set = new HashSet<Collider>(256);
            for (int r = 0; r < roots.Length; r++)
            {
                var t = roots[r];
                if (t == null) continue;
                var cols = t.GetComponentsInChildren<Collider>(false);
                for (int i = 0; i < cols.Length; i++)
                {
                    var c = cols[i];
                    if (c != null)
                        set.Add(c);
                }
            }

            if (set.Count == 0)
            {
                Debug.LogWarning("[WorldSdfBaker] 选中对象及其子层级下未找到任何 Collider。请确认选中的是关卡父物体，并且其子物体上有 3D Collider。 ");
                return;
            }

            var list = new List<Collider>(set.Count);
            foreach (var c in set)
                list.Add(c);

            string scope = roots.Length == 1 ? $"Selection:{roots[0].name}" : $"Selection:{roots.Length}";
            AutoFitBoundsFromColliders(list.ToArray(), scope);
        }

        private void AutoFitBoundsFromColliders(Collider[] colliders, string scope)
        {
            bool hasAny = false;
            Bounds b = default;

            int total = 0;
            int passed = 0;
            int skippedByTrigger = 0;
            int skippedByLayer = 0;

            Collider minXCol = null;
            Collider maxXCol = null;
            Collider minYCol = null;
            Collider maxYCol = null;
            Collider minZCol = null;
            Collider maxZCol = null;
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            float minZ = float.PositiveInfinity;
            float maxZ = float.NegativeInfinity;

            Collider top0 = null;
            Collider top1 = null;
            Collider top2 = null;
            float top0v = -1f;
            float top1v = -1f;
            float top2v = -1f;

            for (int i = 0; i < colliders.Length; i++)
            {
                total++;
                var c = colliders[i];
                if (c == null) continue;
                if (c is TerrainCollider) continue;
                if (!includeTriggers && c.isTrigger)
                {
                    skippedByTrigger++;
                    continue;
                }

                int bit = 1 << c.gameObject.layer;
                if ((staticLayers.value & bit) == 0)
                {
                    skippedByLayer++;
                    continue;
                }

                passed++;

                var cb = c.bounds;
                Vector3 cbMin = cb.min;
                Vector3 cbMax = cb.max;
                if (cbMin.x < minX) { minX = cbMin.x; minXCol = c; }
                if (cbMax.x > maxX) { maxX = cbMax.x; maxXCol = c; }
                if (cbMin.y < minY) { minY = cbMin.y; minYCol = c; }
                if (cbMax.y > maxY) { maxY = cbMax.y; maxYCol = c; }
                if (cbMin.z < minZ) { minZ = cbMin.z; minZCol = c; }
                if (cbMax.z > maxZ) { maxZ = cbMax.z; maxZCol = c; }

                Vector3 s = cb.size;
                float vol = Mathf.Abs(s.x * s.y * s.z);
                if (vol > top0v)
                {
                    top2 = top1; top2v = top1v;
                    top1 = top0; top1v = top0v;
                    top0 = c; top0v = vol;
                }
                else if (vol > top1v)
                {
                    top2 = top1; top2v = top1v;
                    top1 = c; top1v = vol;
                }
                else if (vol > top2v)
                {
                    top2 = c; top2v = vol;
                }

                if (!hasAny)
                {
                    b = cb;
                    hasAny = true;
                }
                else
                {
                    b.Encapsulate(cb);
                }
            }

            if (!hasAny)
            {
                Debug.LogWarning($"[WorldSdfBaker] 自动计算范围失败：scope={scope}。在当前“静态层掩码/Trigger 过滤”条件下未找到任何 Collider。 ");
                return;
            }

            float voxel = Mathf.Max(0.0001f, voxelSizeWorld);
            float expand = voxel * 2f + Mathf.Max(0.0001f, maxDistanceWorld) * 2f;
            b.Expand(Vector3.one * expand);
            boundsCenterWorld = b.center;
            boundsSizeWorld = b.size;

            string LayerName(Collider c)
            {
                if (c == null) return "<null>";
                int layer = c.gameObject.layer;
                return $"{LayerMask.LayerToName(layer)}({layer})";
            }

            string ColName(Collider c)
            {
                if (c == null) return "<null>";
                return c.name;
            }

            Debug.Log(
                $"[WorldSdfBaker] AutoFit 完成：scope={scope} totalColliders={total} passed={passed} skippedTrigger={skippedByTrigger} skippedLayer={skippedByLayer} includeTriggers={includeTriggers} staticLayers=0x{staticLayers.value:X}\n" +
                $"  boundsCenter={boundsCenterWorld} boundsSize={boundsSizeWorld} (expand={expand:F4}=voxel*2+maxDist*2)\n" +
                $"  minX={minX:F3} by {ColName(minXCol)} layer={LayerName(minXCol)}\n" +
                $"  maxX={maxX:F3} by {ColName(maxXCol)} layer={LayerName(maxXCol)}\n" +
                $"  minY={minY:F3} by {ColName(minYCol)} layer={LayerName(minYCol)}\n" +
                $"  maxY={maxY:F3} by {ColName(maxYCol)} layer={LayerName(maxYCol)}\n" +
                $"  minZ={minZ:F3} by {ColName(minZCol)} layer={LayerName(minZCol)}\n" +
                $"  maxZ={maxZ:F3} by {ColName(maxZCol)} layer={LayerName(maxZCol)}\n" +
                $"  topVol0={ColName(top0)} layer={LayerName(top0)} vol={top0v:F3}\n" +
                $"  topVol1={ColName(top1)} layer={LayerName(top1)} vol={top1v:F3}\n" +
                $"  topVol2={ColName(top2)} layer={LayerName(top2)} vol={top2v:F3}");
            Repaint();
        }

        private static bool HasShear(Matrix4x4 l2w)
        {
            Vector3 x = new Vector3(l2w.m00, l2w.m10, l2w.m20);
            Vector3 y = new Vector3(l2w.m01, l2w.m11, l2w.m21);
            Vector3 z = new Vector3(l2w.m02, l2w.m12, l2w.m22);

            float lx = x.magnitude;
            float ly = y.magnitude;
            float lz = z.magnitude;
            if (lx < 1e-6f || ly < 1e-6f || lz < 1e-6f)
                return true;

            x /= lx;
            y /= ly;
            z /= lz;

            float dxy = Mathf.Abs(Vector3.Dot(x, y));
            float dxz = Mathf.Abs(Vector3.Dot(x, z));
            float dyz = Mathf.Abs(Vector3.Dot(y, z));
            return dxy > 1e-3f || dxz > 1e-3f || dyz > 1e-3f;
        }

        private void Bake()
        {
            Dictionary<string, bool> restoreReadableByAssetPath = null;
            var bounds = new Bounds(boundsCenterWorld, boundsSizeWorld);
            float voxel = Mathf.Max(0.0001f, voxelSizeWorld);

            int dimX = Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / voxel));
            int dimY = Mathf.Max(1, Mathf.CeilToInt(bounds.size.y / voxel));
            int dimZ = Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / voxel));

            long totalLong = (long)dimX * dimY * dimZ;
            if (totalLong <= 0 || totalLong > int.MaxValue)
            {
                Debug.LogError($"[WorldSdfBaker] 体素维度非法：dims=({dimX},{dimY},{dimZ})。请检查 bounds/voxelSize 设置是否合理。 ");
                return;
            }

            long estimatedBytes = Revive.Slime.WorldSdfRuntime.HeaderSizeBytes + totalLong * sizeof(float);
            double estimatedMb = estimatedBytes / (1024.0 * 1024.0);

            string path = null;
            if (!string.IsNullOrEmpty(outputDirectoryAssetPath) && outputDirectoryAssetPath.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                string projectRoot = Directory.GetCurrentDirectory();
                string absDir = Path.Combine(projectRoot, outputDirectoryAssetPath);
                if (!Directory.Exists(absDir))
                    Directory.CreateDirectory(absDir);

                string autoPath = Path.Combine(outputDirectoryAssetPath, "WorldSdf.bytes").Replace('\\', '/');
                path = autoPath;
            }
            else
            {
                path = EditorUtility.SaveFilePanelInProject("Save World SDF", "WorldSdf", "bytes", "");
                if (string.IsNullOrEmpty(path))
                    return;

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    outputDirectoryAssetPath = dir.Replace('\\', '/');
            }

            bool cancelled = false;

            try
            {

            Collider[] colliders;
            if (onlyBakeSelection)
            {
                var roots = Selection.transforms;
                if (roots == null || roots.Length == 0)
                {
                    Debug.LogWarning("[WorldSdfBaker] 已启用‘仅烘焙选中对象’，但当前 Selection 为空：已自动回退到全场景烘焙。Tip：选中关卡根对象会显著提高烘焙速度。 ");
                    colliders = FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                    goto FILTER_COLLIDERS;
                }

                var set = new HashSet<Collider>(256);
                for (int r = 0; r < roots.Length; r++)
                {
                    var t = roots[r];
                    if (t == null) continue;
                    var cols = t.GetComponentsInChildren<Collider>(false);
                    for (int i = 0; i < cols.Length; i++)
                    {
                        var c = cols[i];
                        if (c != null)
                            set.Add(c);
                    }
                }

                if (set.Count == 0)
                {
                    Debug.LogWarning("[WorldSdfBaker] 已启用‘仅烘焙选中对象’，但选中对象及其子层级下未找到任何 Collider：已自动回退到全场景烘焙。Tip：选中关卡根对象会显著提高烘焙速度。 ");
                    colliders = FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                    goto FILTER_COLLIDERS;
                }

                colliders = new Collider[set.Count];
                set.CopyTo(colliders);
            }
            else
            {
                colliders = FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            }

            FILTER_COLLIDERS:

            var candidates = new List<Collider>(colliders.Length);
            var candidateBounds = new List<Bounds>(colliders.Length);
            var candidateKinds = new List<byte>(colliders.Length);
            var candidateScales = new List<float>(colliders.Length);
            int skippedByTrigger = 0;
            int skippedByLayer = 0;
            int skippedByBounds = 0;

            for (int i = 0; i < colliders.Length; i++)
            {
                var c = colliders[i];
                if (c == null) continue;
                if (c is TerrainCollider) continue;

                if (!includeTriggers && c.isTrigger)
                {
                    skippedByTrigger++;
                    continue;
                }
                int bit = 1 << c.gameObject.layer;
                if ((staticLayers.value & bit) == 0)
                {
                    skippedByLayer++;
                    continue;
                }
                if (!bounds.Intersects(c.bounds))
                {
                    skippedByBounds++;
                    continue;
                }
                candidates.Add(c);
                candidateBounds.Add(c.bounds);

                byte kind = 0;
                float uniformScale = 1f;
                Transform tr = c.transform;
                Vector3 ls = tr.lossyScale;
                bool uniform = Mathf.Abs(ls.x - ls.y) < 1e-4f && Mathf.Abs(ls.x - ls.z) < 1e-4f;
                if (c is BoxCollider)
                {
                    if (!HasShear(tr.localToWorldMatrix))
                        kind = 1;
                }
                else if (uniform)
                {
                    uniformScale = Mathf.Abs(ls.x);
                    if (c is SphereCollider) kind = 2;
                    else if (c is CapsuleCollider) kind = 3;
                }
                candidateKinds.Add(kind);
                candidateScales.Add(uniformScale);
            }

            if (candidates.Count == 0)
            {
                Debug.LogError($"[WorldSdfBaker] 未找到任何候选碰撞体，已取消烘焙。统计：总Collider={colliders.Length}，通过过滤=0，因Trigger过滤={skippedByTrigger}，因层掩码过滤={skippedByLayer}，因不在烘焙范围内={skippedByBounds}。\n请检查：\n- 参与烘焙的层(静态) 是否包含碰撞体所在层\n- 是否需要勾选‘包含 Trigger’\n- boundsCenter/boundsSize 是否覆盖到关卡碰撞体\n- 目标是否是 3D Collider（2D Collider 不会被统计）\n");
                return;
            }

            if (autoEnableUnreadableMeshReadWrite)
            {
                var assetPaths = new List<string>(8);
                var set = new HashSet<string>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i] is not MeshCollider mc || mc.sharedMesh == null)
                        continue;

                    var m = mc.sharedMesh;
                    if (m.isReadable)
                        continue;

                    string assetPath = AssetDatabase.GetAssetPath(m);
                    if (string.IsNullOrEmpty(assetPath))
                        continue;

                    if (set.Add(assetPath))
                        assetPaths.Add(assetPath);
                }

                if (assetPaths.Count > 0)
                {
                    int changedCount = 0;
                    int skippedNonModelImporter = 0;
                    restoreReadableByAssetPath = new Dictionary<string, bool>(assetPaths.Count);

                    for (int ai = 0; ai < assetPaths.Count; ai++)
                    {
                        float progress = assetPaths.Count <= 1 ? 0f : (float)ai / (assetPaths.Count - 1);
                        if (EditorUtility.DisplayCancelableProgressBar("World SDF 烘焙", $"临时开启 Mesh 可读写 {ai + 1}/{assetPaths.Count}", progress))
                        {
                            cancelled = true;
                            break;
                        }

                        string assetPath = assetPaths[ai];
                        var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                        if (importer == null)
                        {
                            skippedNonModelImporter++;
                            continue;
                        }

                        bool wasReadable = importer.isReadable;
                        if (wasReadable)
                            continue;

                        restoreReadableByAssetPath[assetPath] = wasReadable;
                        importer.isReadable = true;
                        importer.SaveAndReimport();
                        changedCount++;
                    }

                    if (!cancelled)
                    {
                        Debug.Log($"[WorldSdfBaker] 临时开启 Mesh 可读写：targets={assetPaths.Count} changed={changedCount} skippedNonModelImporter={skippedNonModelImporter}");
                    }
                }
            }

            if (cancelled)
                return;

            var candidateMeshDataIndex = new int[candidates.Count];
            var meshVertsW = new List<Vector3[]>(16);
            var meshTris = new List<int[]>(16);
            var meshTriNormalsW = new List<Vector3[]>(16);
            int AddMeshData(MeshCollider mc)
            {
                Mesh m = mc.sharedMesh;
                if (m == null)
                    return -1;

                Vector3[] verts;
                int[] tris;
                try
                {
                    verts = m.vertices;
                    tris = m.triangles;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[WorldSdfBaker] Mesh 数据不可读，已回退到 patch：{mc.name} mesh={m.name} err={e.Message}");
                    return -1;
                }

                if (verts == null || verts.Length == 0 || tris == null || tris.Length < 3)
                {
                    Debug.LogWarning($"[WorldSdfBaker] Mesh 三角形为空，已回退到 patch：{mc.name} mesh={m.name} verts={(verts != null ? verts.Length : 0)} tris={(tris != null ? tris.Length : 0)} isReadable={m.isReadable}");
                    return -1;
                }

                var l2w = mc.transform.localToWorldMatrix;

                var vertsW = new Vector3[verts.Length];
                for (int vi = 0; vi < verts.Length; vi++)
                    vertsW[vi] = l2w.MultiplyPoint3x4(verts[vi]);

                int triCount = tris.Length / 3;
                var triNs = new Vector3[triCount];
                for (int ti = 0, t = 0; ti + 2 < tris.Length; ti += 3, t++)
                {
                    Vector3 a = vertsW[tris[ti]];
                    Vector3 b = vertsW[tris[ti + 1]];
                    Vector3 c = vertsW[tris[ti + 2]];
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    float len = n.magnitude;
                    triNs[t] = len > 1e-12f ? (n / len) : Vector3.up;
                }

                meshVertsW.Add(vertsW);
                meshTris.Add(tris);
                meshTriNormalsW.Add(triNs);
                return meshVertsW.Count - 1;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                int mdi = -1;
                if (candidates[i] is MeshCollider mc && mc.sharedMesh != null)
                    mdi = AddMeshData(mc);
                candidateMeshDataIndex[i] = mdi;
            }

            {
                int analyticCount = 0;
                int meshColliderCount = 0;
                int meshDataOkCount = 0;
                int patchCandidateCountPreview = 0;

                for (int i = 0; i < candidates.Count; i++)
                {
                    byte kind = candidateKinds[i];
                    if (kind == 1 || kind == 2 || kind == 3)
                    {
                        analyticCount++;
                        continue;
                    }

                    if (candidates[i] is MeshCollider)
                        meshColliderCount++;

                    int mdi = candidateMeshDataIndex[i];
                    if (mdi >= 0)
                    {
                        meshDataOkCount++;
                        continue;
                    }

                    patchCandidateCountPreview++;
                }

                Debug.Log($"[WorldSdfBaker] CandidatesSummary total={candidates.Count} analytic(kind123)={analyticCount} meshColliders={meshColliderCount} meshDataOk(mdi>=0)={meshDataOkCount} patchCandidates(kind0 && mdi<0)={patchCandidateCountPreview}");

                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i] is not MeshCollider mc || mc.sharedMesh == null)
                        continue;

                    int mdi = candidateMeshDataIndex[i];
                    if (mdi < 0)
                    {
                        Debug.Log($"[WorldSdfBaker] MeshColliderInfo name={mc.name} mesh={(mc.sharedMesh != null ? mc.sharedMesh.name : "null")} mdi={mdi} (will use patch path)");
                    }
                    else
                    {
                        int vertCount = meshVertsW[mdi] != null ? meshVertsW[mdi].Length : 0;
                        int triCount = meshTris[mdi] != null ? (meshTris[mdi].Length / 3) : 0;
                        Debug.Log($"[WorldSdfBaker] MeshColliderInfo name={mc.name} mesh={mc.sharedMesh.name} mdi={mdi} verts={vertCount} tris={triCount}");
                    }
                }
            }

            Vector3 min = bounds.min;
            Vector3 originWorld = min + Vector3.one * (voxel * 0.5f);

            float invScale = Revive.Slime.PBF_Utils.InvScale;
            float voxelSim = voxel * invScale;
            float maxDistanceSim = Mathf.Max(0.0001f, maxDistanceWorld) * invScale;
            Vector3 originSim = originWorld * invScale;
            var queryTriggers = includeTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
            float insideProbeRadius = voxel * 1.0f;
            float insideTestRadius = Mathf.Max(1e-4f, voxel * 0.05f);

            uint magic = Revive.Slime.WorldSdfRuntime.Magic;
            int version = Revive.Slime.WorldSdfRuntime.Version;
            string absoluteBytesPath = Path.Combine(Directory.GetCurrentDirectory(), path);
            try
            {
                EditorUtility.DisplayCancelableProgressBar("World SDF 烘焙", "准备中...", 0f);
                using (var fs = new FileStream(absoluteBytesPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var bw = new BinaryWriter(fs))
                {
                    float storeBandWorld = Mathf.Max(0.0001f, maxDistanceWorld);

                    bw.Write(magic);
                    bw.Write(version);
                    bw.Write(originSim.x);
                    bw.Write(originSim.y);
                    bw.Write(originSim.z);
                    bw.Write(dimX);
                    bw.Write(dimY);
                    bw.Write(dimZ);
                    bw.Write(voxelSim);

                    bw.Write(maxDistanceSim);

                    int yzStride = dimY * dimZ;
                    int total = dimX * dimY * dimZ;
                    var denseSim = new float[total];
                    for (int i = 0; i < total; i++)
                        denseSim[i] = maxDistanceSim;

                    float storeBandSqr = storeBandWorld * storeBandWorld;
                    int entryCount = 0;
                    int insideNegCount = 0;
                    float updatedMin = float.PositiveInfinity;
                    float updatedMax = float.NegativeInfinity;

                    long voxelsTotal = (long)dimX * dimY * dimZ;
                    long voxelsBoundsPass = 0;
                    long closestPointCalls = 0;
                    long closestPointErrors = 0;
                    long checkSphereCalls = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    long ticksJobDistance = 0;
                    long ticksPatchDistance = 0;

                    var seedKeys = new HashSet<int>(1024);
                    long seedAddAttempts = 0;
                    long seedAdded = 0;
                    long seedTriCount = 0;
                    int seedMeshColliderCount = 0;

                    void AddSeedAabb(Vector3 minW, Vector3 maxW)
                    {
                        int x0 = Mathf.FloorToInt((minW.x - originWorld.x) / voxel);
                        int y0 = Mathf.FloorToInt((minW.y - originWorld.y) / voxel);
                        int z0 = Mathf.FloorToInt((minW.z - originWorld.z) / voxel);
                        int x1 = Mathf.CeilToInt((maxW.x - originWorld.x) / voxel);
                        int y1 = Mathf.CeilToInt((maxW.y - originWorld.y) / voxel);
                        int z1 = Mathf.CeilToInt((maxW.z - originWorld.z) / voxel);

                        x0 = Mathf.Clamp(x0, 0, dimX - 1);
                        y0 = Mathf.Clamp(y0, 0, dimY - 1);
                        z0 = Mathf.Clamp(z0, 0, dimZ - 1);
                        x1 = Mathf.Clamp(x1, 0, dimX - 1);
                        y1 = Mathf.Clamp(y1, 0, dimY - 1);
                        z1 = Mathf.Clamp(z1, 0, dimZ - 1);

                        for (int x = x0; x <= x1; x++)
                        {
                            int xBase = x * yzStride;
                            for (int y = y0; y <= y1; y++)
                            {
                                int xyBase = xBase + y * dimZ;
                                for (int z = z0; z <= z1; z++)
                                {
                                    int key = xyBase + z;
                                    seedAddAttempts++;
                                    if (seedKeys.Add(key)) seedAdded++;
                                }
                            }
                        }
                    }

                    for (int ci = 0; ci < candidates.Count; ci++)
                    {
                        float progress = candidates.Count <= 1 ? 0f : (float)ci / (candidates.Count - 1);
                        if (EditorUtility.DisplayCancelableProgressBar("World SDF 烘焙", $"生成窄带seed {ci + 1}/{candidates.Count}（meshTri累计={seedTriCount:N0}）", progress))
                        {
                            cancelled = true;
                            break;
                        }

                        var col = candidates[ci];
                        if (col == null) continue;

                        if (col is MeshCollider mc && mc.sharedMesh != null)
                        {
                            seedMeshColliderCount++;
                            if (meshSeedMode == MeshSeedMode.Bounds)
                            {
                                Bounds b = mc.bounds;
                                Vector3 minW = b.min - Vector3.one * storeBandWorld;
                                Vector3 maxW = b.max + Vector3.one * storeBandWorld;
                                AddSeedAabb(minW, maxW);
                            }
                            else
                            {
                                int mdi = candidateMeshDataIndex[ci];
                                if (mdi < 0)
                                {
                                    Bounds b = mc.bounds;
                                    Vector3 minW = b.min - Vector3.one * storeBandWorld;
                                    Vector3 maxW = b.max + Vector3.one * storeBandWorld;
                                    AddSeedAabb(minW, maxW);
                                }
                                else
                                {
                                    var tris = meshTris[mdi];
                                    var vertsW = meshVertsW[mdi];
                                    float expand = storeBandWorld;

                                    for (int ti = 0; ti + 2 < tris.Length; ti += 3)
                                    {
                                        int i0 = tris[ti];
                                        int i1 = tris[ti + 1];
                                        int i2 = tris[ti + 2];

                                        Vector3 v0 = vertsW[i0];
                                        Vector3 v1 = vertsW[i1];
                                        Vector3 v2 = vertsW[i2];

                                        Vector3 triMin = Vector3.Min(v0, Vector3.Min(v1, v2));
                                        Vector3 triMax = Vector3.Max(v0, Vector3.Max(v1, v2));
                                        triMin -= Vector3.one * expand;
                                        triMax += Vector3.one * expand;

                                        AddSeedAabb(triMin, triMax);
                                        seedTriCount++;

                                        if ((seedTriCount & 1023) == 0)
                                        {
                                            if (EditorUtility.DisplayCancelableProgressBar("World SDF 烘焙", $"生成Mesh seed tri={seedTriCount:N0}（当前Collider {ci + 1}/{candidates.Count}）", progress))
                                            {
                                                cancelled = true;
                                                break;
                                            }
                                        }
                                    }

                                    if (cancelled) break;
                                }
                            }
                        }
                        else
                        {
                            Bounds b = candidateBounds[ci];
                            Vector3 minW = b.min - Vector3.one * storeBandWorld;
                            Vector3 maxW = b.max + Vector3.one * storeBandWorld;
                            AddSeedAabb(minW, maxW);
                        }
                    }

                    if (!cancelled)
                    {
                        var keys = new List<int>(seedKeys);

                        int keyCount = keys.Count;
                        var keysArray = keys.ToArray();

                        var keysNA = new NativeArray<int>(keysArray, Allocator.TempJob);
                        var outBestAbsNA = new NativeArray<float>(keyCount, Allocator.TempJob);
                        var outBestDWorldNA = new NativeArray<float>(keyCount, Allocator.TempJob);
                        var outFlagsNA = new NativeArray<byte>(keyCount, Allocator.TempJob);

                        int candidateCount = candidates.Count;
                        var candidateKindsNA = new NativeArray<byte>(candidateCount, Allocator.TempJob);
                        var candidateBoundsMinNA = new NativeArray<Vector3>(candidateCount, Allocator.TempJob);
                        var candidateBoundsMaxNA = new NativeArray<Vector3>(candidateCount, Allocator.TempJob);
                        var candidateWorldToLocalNA = new NativeArray<Matrix4x4>(candidateCount, Allocator.TempJob);
                        var candidateLocalCenterNA = new NativeArray<Vector3>(candidateCount, Allocator.TempJob);
                        var candidateBoxHalfSizeNA = new NativeArray<Vector3>(candidateCount, Allocator.TempJob);
                        var candidateSphereRadiusNA = new NativeArray<float>(candidateCount, Allocator.TempJob);
                        var candidateCapsuleRadiusNA = new NativeArray<float>(candidateCount, Allocator.TempJob);
                        var candidateCapsuleHalfHeightNA = new NativeArray<float>(candidateCount, Allocator.TempJob);
                        var candidateCapsuleDirectionNA = new NativeArray<int>(candidateCount, Allocator.TempJob);
                        var candidateUniformScaleNA = new NativeArray<float>(candidateCount, Allocator.TempJob);
                        var candidateMeshIndexNA = new NativeArray<int>(candidateCount, Allocator.TempJob);

                        int patchCandidateCount = 0;
                        for (int ci = 0; ci < candidateCount; ci++)
                        {
                            Bounds b = candidateBounds[ci];
                            candidateBoundsMinNA[ci] = b.min;
                            candidateBoundsMaxNA[ci] = b.max;

                            byte kind = candidateKinds[ci];
                            candidateKindsNA[ci] = kind;

                            float uniformScale = candidateScales[ci];
                            candidateUniformScaleNA[ci] = uniformScale;

                            int mdi = candidateMeshDataIndex[ci];
                            candidateMeshIndexNA[ci] = mdi;
                            candidateWorldToLocalNA[ci] = default;
                            candidateLocalCenterNA[ci] = default;
                            candidateBoxHalfSizeNA[ci] = default;
                            candidateSphereRadiusNA[ci] = 0f;
                            candidateCapsuleRadiusNA[ci] = 0f;
                            candidateCapsuleHalfHeightNA[ci] = 0f;
                            candidateCapsuleDirectionNA[ci] = 1;

                            if (kind == 1)
                            {
                                var box = (BoxCollider)candidates[ci];
                                var t = box.transform;
                                Vector3 ls = t.lossyScale;
                                Vector3 absScale = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                                candidateWorldToLocalNA[ci] = Matrix4x4.Rotate(Quaternion.Inverse(t.rotation));
                                candidateLocalCenterNA[ci] = t.TransformPoint(box.center);
                                candidateBoxHalfSizeNA[ci] = Vector3.Scale(box.size * 0.5f, absScale);
                            }
                            else if (kind == 2)
                            {
                                var sphere = (SphereCollider)candidates[ci];
                                candidateWorldToLocalNA[ci] = sphere.transform.worldToLocalMatrix;
                                candidateLocalCenterNA[ci] = sphere.center;
                                candidateSphereRadiusNA[ci] = sphere.radius;
                            }
                            else if (kind == 3)
                            {
                                var capsule = (CapsuleCollider)candidates[ci];
                                candidateWorldToLocalNA[ci] = capsule.transform.worldToLocalMatrix;
                                candidateLocalCenterNA[ci] = capsule.center;
                                candidateCapsuleRadiusNA[ci] = capsule.radius;
                                candidateCapsuleHalfHeightNA[ci] = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
                                candidateCapsuleDirectionNA[ci] = capsule.direction;
                            }
                            else if (mdi < 0)
                            {
                                patchCandidateCount++;
                            }
                        }

                        var patchCandidateIndices = new int[patchCandidateCount];
                        int patchWrite = 0;
                        for (int ci = 0; ci < candidateCount; ci++)
                        {
                            byte kind = candidateKinds[ci];
                            if (kind == 1 || kind == 2 || kind == 3)
                                continue;
                            if (candidateMeshDataIndex[ci] >= 0)
                                continue;
                            patchCandidateIndices[patchWrite++] = ci;
                        }

                        int meshCount = meshVertsW.Count;
                        int totalMeshVerts = 0;
                        int totalMeshTriIndices = 0;
                        int totalMeshTriCount = 0;
                        for (int mi = 0; mi < meshCount; mi++)
                        {
                            totalMeshVerts += meshVertsW[mi].Length;
                            totalMeshTriIndices += meshTris[mi].Length;
                            totalMeshTriCount += meshTriNormalsW[mi].Length;
                        }

                        var meshTriOffsetNA = new NativeArray<int>(meshCount, Allocator.TempJob);
                        var meshTriCountNA = new NativeArray<int>(meshCount, Allocator.TempJob);
                        var meshTriNormalOffsetNA = new NativeArray<int>(meshCount, Allocator.TempJob);
                        var meshVertsAllNA = new NativeArray<Vector3>(Mathf.Max(1, totalMeshVerts), Allocator.TempJob);
                        var meshTrisAllNA = new NativeArray<int>(Mathf.Max(1, totalMeshTriIndices), Allocator.TempJob);
                        var meshTriNormalsAllNA = new NativeArray<Vector3>(Mathf.Max(1, totalMeshTriCount), Allocator.TempJob);

                        var meshBvhRootNA = new NativeArray<int>(meshCount, Allocator.TempJob);
                        NativeArray<BvhNode> meshBvhNodesAllNA = default;
                        NativeArray<int> meshBvhTriIndicesAllNA = default;

                        try
                        {
                            int vBase = 0;
                            int tBase = 0;
                            int nBase = 0;
                            for (int mi = 0; mi < meshCount; mi++)
                            {
                                var vArr = meshVertsW[mi];
                                var tArr = meshTris[mi];
                                var nArr = meshTriNormalsW[mi];

                                meshTriOffsetNA[mi] = tBase;
                                int triCount = tArr.Length / 3;
                                meshTriCountNA[mi] = triCount;
                                meshTriNormalOffsetNA[mi] = nBase;

                                for (int vi = 0; vi < vArr.Length; vi++)
                                    meshVertsAllNA[vBase + vi] = vArr[vi];

                                for (int ti = 0; ti < tArr.Length; ti++)
                                    meshTrisAllNA[tBase + ti] = vBase + tArr[ti];

                                for (int ni = 0; ni < nArr.Length; ni++)
                                    meshTriNormalsAllNA[nBase + ni] = nArr[ni];

                                vBase += vArr.Length;
                                tBase += tArr.Length;
                                nBase += nArr.Length;
                            }

                            {
                                const int leafTriCount = 8;
                                var allNodes = new List<BvhNode>(Mathf.Max(1, totalMeshTriCount * 2));
                                var allTriIndices = new List<int>(Mathf.Max(1, totalMeshTriCount));

                                for (int mi = 0; mi < meshCount; mi++)
                                {
                                    int triCount = meshTris[mi].Length / 3;
                                    if (triCount <= 0)
                                    {
                                        meshBvhRootNA[mi] = -1;
                                        continue;
                                    }

                                    var vertsW = meshVertsW[mi];
                                    var tris = meshTris[mi];

                                    var triMins = new Vector3[triCount];
                                    var triMaxs = new Vector3[triCount];
                                    var triCenters = new Vector3[triCount];
                                    for (int t = 0; t < triCount; t++)
                                    {
                                        int ti = t * 3;
                                        Vector3 a = vertsW[tris[ti]];
                                        Vector3 b = vertsW[tris[ti + 1]];
                                        Vector3 c = vertsW[tris[ti + 2]];
                                        triMins[t] = Vector3.Min(a, Vector3.Min(b, c));
                                        triMaxs[t] = Vector3.Max(a, Vector3.Max(b, c));
                                        triCenters[t] = (a + b + c) * (1f / 3f);
                                    }

                                    var triIndices = new int[triCount];
                                    for (int t = 0; t < triCount; t++)
                                        triIndices[t] = t;

                                    var localNodes = new List<BvhNode>(triCount * 2);

                                    float GetAxis(in Vector3 v, int axis)
                                    {
                                        return axis == 0 ? v.x : (axis == 1 ? v.y : v.z);
                                    }

                                    void Swap(int a, int b)
                                    {
                                        int tmp = triIndices[a];
                                        triIndices[a] = triIndices[b];
                                        triIndices[b] = tmp;
                                    }

                                    int Partition(int start, int count, int axis, float split)
                                    {
                                        int i = start;
                                        int j = start + count - 1;
                                        while (i <= j)
                                        {
                                            while (i <= j && GetAxis(triCenters[triIndices[i]], axis) < split) i++;
                                            while (i <= j && GetAxis(triCenters[triIndices[j]], axis) >= split) j--;
                                            if (i < j)
                                            {
                                                Swap(i, j);
                                                i++;
                                                j--;
                                            }
                                        }
                                        return i;
                                    }

                                    int Build(int start, int count)
                                    {
                                        Vector3 bmin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                                        Vector3 bmax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                                        Vector3 cmin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                                        Vector3 cmax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

                                        for (int i = 0; i < count; i++)
                                        {
                                            int t = triIndices[start + i];
                                            bmin = Vector3.Min(bmin, triMins[t]);
                                            bmax = Vector3.Max(bmax, triMaxs[t]);
                                            Vector3 cc = triCenters[t];
                                            cmin = Vector3.Min(cmin, cc);
                                            cmax = Vector3.Max(cmax, cc);
                                        }

                                        int nodeIdx = localNodes.Count;
                                        localNodes.Add(new BvhNode
                                        {
                                            BMin = bmin,
                                            BMax = bmax,
                                            Left = -1,
                                            Right = -1,
                                            Escape = -1,
                                            TriStart = 0,
                                            TriCount = 0,
                                        });

                                        if (count <= leafTriCount)
                                        {
                                            localNodes[nodeIdx] = new BvhNode
                                            {
                                                BMin = bmin,
                                                BMax = bmax,
                                                Left = -1,
                                                Right = -1,
                                                Escape = -1,
                                                TriStart = start,
                                                TriCount = count,
                                            };
                                            return nodeIdx;
                                        }

                                        Vector3 extent = cmax - cmin;
                                        int axis = extent.x >= extent.y
                                            ? (extent.x >= extent.z ? 0 : 2)
                                            : (extent.y >= extent.z ? 1 : 2);

                                        float split = (GetAxis(cmin, axis) + GetAxis(cmax, axis)) * 0.5f;
                                        int mid = Partition(start, count, axis, split);
                                        if (mid <= start || mid >= start + count)
                                        {
                                            int end = start + count;
                                            System.Array.Sort(triIndices, start, count, Comparer<int>.Create((a, b) =>
                                                GetAxis(triCenters[a], axis).CompareTo(GetAxis(triCenters[b], axis))));
                                            mid = start + (count / 2);
                                            if (mid <= start) mid = start + 1;
                                            if (mid >= end) mid = end - 1;
                                        }

                                        int left = Build(start, mid - start);
                                        int right = Build(mid, start + count - mid);

                                        localNodes[nodeIdx] = new BvhNode
                                        {
                                            BMin = bmin,
                                            BMax = bmax,
                                            Left = left,
                                            Right = right,
                                            Escape = -1,
                                            TriStart = 0,
                                            TriCount = 0,
                                        };
                                        return nodeIdx;
                                    }

                                    int localRoot = Build(0, triCount);

                                    void AssignEscape(int node, int escape)
                                    {
                                        var n = localNodes[node];
                                        n.Escape = escape;
                                        localNodes[node] = n;
                                        if (n.TriCount > 0)
                                            return;

                                        int left = n.Left;
                                        int right = n.Right;
                                        if (left >= 0)
                                            AssignEscape(left, right >= 0 ? right : escape);
                                        if (right >= 0)
                                            AssignEscape(right, escape);
                                    }

                                    AssignEscape(localRoot, -1);

                                    int nodeOffset = allNodes.Count;
                                    int triOffset = allTriIndices.Count;
                                    for (int t = 0; t < triCount; t++)
                                        allTriIndices.Add(triIndices[t]);

                                    for (int ni = 0; ni < localNodes.Count; ni++)
                                    {
                                        var n = localNodes[ni];
                                        if (n.Left >= 0) n.Left += nodeOffset;
                                        if (n.Right >= 0) n.Right += nodeOffset;
                                        if (n.Escape >= 0) n.Escape += nodeOffset;
                                        if (n.TriCount > 0) n.TriStart += triOffset;
                                        allNodes.Add(n);
                                    }

                                    meshBvhRootNA[mi] = nodeOffset + localRoot;
                                }

                                if (allNodes.Count <= 0)
                                    allNodes.Add(default);
                                if (allTriIndices.Count <= 0)
                                    allTriIndices.Add(0);

                                meshBvhNodesAllNA = new NativeArray<BvhNode>(allNodes.Count, Allocator.TempJob);
                                for (int i = 0; i < allNodes.Count; i++)
                                    meshBvhNodesAllNA[i] = allNodes[i];
                                meshBvhTriIndicesAllNA = new NativeArray<int>(allTriIndices.Count, Allocator.TempJob);
                                for (int i = 0; i < allTriIndices.Count; i++)
                                    meshBvhTriIndicesAllNA[i] = allTriIndices[i];
                            }

                            long tJob0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            int chunkSize = 200_000;
                            for (int start = 0; start < keyCount; start += chunkSize)
                            {
                                int count = Mathf.Min(chunkSize, keyCount - start);
                                float progress = keyCount <= 1 ? 0f : (float)start / (keyCount - 1);
                                if (EditorUtility.DisplayCancelableProgressBar("World SDF 烘焙", $"Job计算距离 {start + 1}/{keyCount}", progress))
                                {
                                    cancelled = true;
                                    break;
                                }

                                var job = new BakeDistanceJob
                                {
                                    Keys = new NativeSlice<int>(keysNA, start, count),
                                    CandidateKinds = candidateKindsNA,
                                    CandidateBoundsMin = candidateBoundsMinNA,
                                    CandidateBoundsMax = candidateBoundsMaxNA,
                                    CandidateWorldToLocal = candidateWorldToLocalNA,
                                    CandidateLocalCenter = candidateLocalCenterNA,
                                    CandidateBoxHalfSize = candidateBoxHalfSizeNA,
                                    CandidateSphereRadius = candidateSphereRadiusNA,
                                    CandidateCapsuleRadius = candidateCapsuleRadiusNA,
                                    CandidateCapsuleHalfHeight = candidateCapsuleHalfHeightNA,
                                    CandidateCapsuleDirection = candidateCapsuleDirectionNA,
                                    CandidateUniformScale = candidateUniformScaleNA,

                                    CandidateMeshIndex = candidateMeshIndexNA,
                                    MeshTriOffset = meshTriOffsetNA,
                                    MeshTriCount = meshTriCountNA,
                                    MeshTriNormalOffset = meshTriNormalOffsetNA,
                                    MeshVertsW = meshVertsAllNA,
                                    MeshTris = meshTrisAllNA,
                                    MeshTriNormalsW = meshTriNormalsAllNA,

                                    MeshBvhRoot = meshBvhRootNA,
                                    MeshBvhNodes = meshBvhNodesAllNA,
                                    MeshBvhTriIndices = meshBvhTriIndicesAllNA,

                                    yzStride = yzStride,
                                    dimZ = dimZ,
                                    voxel = voxel,
                                    originWorld = originWorld,
                                    storeBandWorld = storeBandWorld,
                                    storeBandSqr = storeBandSqr,

                                    OutBestAbs = new NativeSlice<float>(outBestAbsNA, start, count),
                                    OutBestDWorld = new NativeSlice<float>(outBestDWorldNA, start, count),
                                    OutFlags = new NativeSlice<byte>(outFlagsNA, start, count),
                                };

                                job.Schedule(count, 64).Complete();
                                if (cancelled) break;
                            }
                            ticksJobDistance = System.Diagnostics.Stopwatch.GetTimestamp() - tJob0;

                            if (!cancelled)
                            {
                                long tPatch0 = System.Diagnostics.Stopwatch.GetTimestamp();
                                int boundsPassCount = 0;
                                for (int ki = 0; ki < keyCount; ki++)
                                {
                                    if ((outFlagsNA[ki] & 2) != 0)
                                        boundsPassCount++;
                                }

                                var boundsPassIndices = new int[boundsPassCount];
                                int boundsWrite = 0;
                                for (int ki = 0; ki < keyCount; ki++)
                                {
                                    if ((outFlagsNA[ki] & 2) != 0)
                                        boundsPassIndices[boundsWrite++] = ki;
                                }

                                voxelsBoundsPass = boundsPassCount;
                                int progressInterval = 2048;
                                for (int bi = 0; bi < boundsPassCount; bi++)
                                {
                                    if ((bi % progressInterval) == 0 || bi == boundsPassCount - 1)
                                    {
                                        float progress = boundsPassCount <= 1 ? 0f : (float)bi / (boundsPassCount - 1);
                                        if (EditorUtility.DisplayCancelableProgressBar("World SDF 烘焙", $"补洞/写入 {bi + 1}/{boundsPassCount}（updated={entryCount:N0}）", progress))
                                        {
                                            cancelled = true;
                                            break;
                                        }
                                    }

                                    int ki = boundsPassIndices[bi];
                                    int key = keysArray[ki];
                                    int x = key / yzStride;
                                    int rem = key - x * yzStride;
                                    int y = rem / dimZ;
                                    int z = rem - y * dimZ;

                                    Vector3 pWorld = originWorld + new Vector3(x * voxel, y * voxel, z * voxel);

                                    float bestAbs = outBestAbsNA[ki];
                                    float bestDWorld = outBestDWorldNA[ki];
                                    byte flags = outFlagsNA[ki];
                                    bool bestSignedKnown = (flags & 1) != 0;
                                    bool anyBoundsPass = (flags & 2) != 0;

                                    if (!anyBoundsPass)
                                        continue;

                                    float bestAbsSqr = bestAbs * bestAbs;
                                    for (int pci = 0; pci < patchCandidateIndices.Length; pci++)
                                    {
                                        int ci = patchCandidateIndices[pci];
                                        var col = candidates[ci];
                                        if (col == null) continue;

                                        float boundsSqr = candidateBounds[ci].SqrDistance(pWorld);
                                        if (boundsSqr > storeBandSqr)
                                            continue;
                                        if (boundsSqr > bestAbsSqr)
                                            continue;

                                        closestPointCalls++;
                                        Vector3 cp;
                                        try
                                        {
                                            cp = col.ClosestPoint(pWorld);
                                        }
                                        catch (Exception e)
                                        {
                                            closestPointErrors++;
                                            if (closestPointErrors <= 3)
                                                Debug.LogError($"[WorldSdfBaker] ClosestPoint 失败：{col.name} type={col.GetType().Name} err={e.Message}");
                                            continue;
                                        }
                                        Vector3 dd = pWorld - cp;
                                        float distSqr = dd.sqrMagnitude;
                                        if (distSqr < bestAbsSqr)
                                        {
                                            bestAbsSqr = distSqr;
                                            bestAbs = Mathf.Sqrt(distSqr);
                                            bestDWorld = bestAbs;
                                            bestSignedKnown = false;
                                            if (distSqr < 1e-12f)
                                            {
                                                bestAbs = 0f;
                                                bestDWorld = 0f;
                                                break;
                                            }
                                        }
                                    }

                                    if (bestAbs > storeBandWorld)
                                        continue;

                                    float dWorld;
                                    if (bestSignedKnown)
                                    {
                                        dWorld = bestDWorld;
                                    }
                                    else
                                    {
                                        if (bestAbs <= insideProbeRadius)
                                        {
                                            checkSphereCalls++;
                                            bool inside = Physics.CheckSphere(pWorld, insideTestRadius, staticLayers.value, queryTriggers);
                                            dWorld = inside ? -bestAbs : bestAbs;
                                        }
                                        else
                                        {
                                            dWorld = bestAbs;
                                        }
                                    }

                                    if (Mathf.Abs(dWorld) <= storeBandWorld)
                                    {
                                        float dSim = dWorld * invScale;
                                        if (dSim > maxDistanceSim) dSim = maxDistanceSim;
                                        else if (dSim < -maxDistanceSim) dSim = -maxDistanceSim;
                                        denseSim[key] = dSim;
                                        entryCount++;
                                        if (dSim < 0f) insideNegCount++;
                                        if (dSim < updatedMin) updatedMin = dSim;
                                        if (dSim > updatedMax) updatedMax = dSim;
                                    }
                                }

                                ticksPatchDistance = System.Diagnostics.Stopwatch.GetTimestamp() - tPatch0;
                            }
                        }
                        finally
                        {
                            keysNA.Dispose();
                            outBestAbsNA.Dispose();
                            outBestDWorldNA.Dispose();
                            outFlagsNA.Dispose();
                            candidateKindsNA.Dispose();
                            candidateBoundsMinNA.Dispose();
                            candidateBoundsMaxNA.Dispose();
                            candidateWorldToLocalNA.Dispose();
                            candidateLocalCenterNA.Dispose();
                            candidateBoxHalfSizeNA.Dispose();
                            candidateSphereRadiusNA.Dispose();
                            candidateCapsuleRadiusNA.Dispose();
                            candidateCapsuleHalfHeightNA.Dispose();
                            candidateCapsuleDirectionNA.Dispose();
                            candidateUniformScaleNA.Dispose();
                            candidateMeshIndexNA.Dispose();
                            meshTriOffsetNA.Dispose();
                            meshTriCountNA.Dispose();
                            meshTriNormalOffsetNA.Dispose();
                            meshVertsAllNA.Dispose();
                            meshTrisAllNA.Dispose();
                            meshTriNormalsAllNA.Dispose();

                            meshBvhRootNA.Dispose();
                            if (meshBvhNodesAllNA.IsCreated) meshBvhNodesAllNA.Dispose();
                            if (meshBvhTriIndicesAllNA.IsCreated) meshBvhTriIndicesAllNA.Dispose();
                        }
                    }

                    if (!cancelled)
                    {
                        var bytes = new byte[denseSim.Length * sizeof(float)];
                        Buffer.BlockCopy(denseSim, 0, bytes, 0, bytes.Length);
                        bw.Write(bytes);
                    }

                    bw.Flush();

                    sw.Stop();
                    int totalNeg = 0;
                    float allMin = float.PositiveInfinity;
                    float allMax = float.NegativeInfinity;
                    for (int i = 0; i < denseSim.Length; i++)
                    {
                        float v = denseSim[i];
                        if (v < 0f) totalNeg++;
                        if (v < allMin) allMin = v;
                        if (v > allMax) allMax = v;
                    }
                    double invFreq = 1.0 / System.Diagnostics.Stopwatch.Frequency;
                    Debug.Log($"[WorldSdfBaker] BakeProfile time={sw.Elapsed.TotalSeconds:F2}s voxels={voxelsTotal:N0} seedMode={meshSeedMode} seedKeys={seedKeys.Count:N0} seedAddAttempts={seedAddAttempts:N0} seedAdded={seedAdded:N0} seedMeshColliders={seedMeshColliderCount:N0} seedMeshTris={seedTriCount:N0} jobDist={(ticksJobDistance * invFreq):F2}s patchDist={(ticksPatchDistance * invFreq):F2}s boundsPass={voxelsBoundsPass:N0} closestPointCalls={closestPointCalls:N0} closestPointErrors={closestPointErrors:N0} checkSphereCalls={checkSphereCalls:N0} updated={entryCount:N0} updatedNeg={insideNegCount:N0} updatedMin={updatedMin:F4} updatedMax={updatedMax:F4} allNeg={totalNeg:N0} allMin={allMin:F4} allMax={allMax:F4} candidates={candidates.Count}");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (cancelled)
            {
                if (File.Exists(absoluteBytesPath))
                    File.Delete(absoluteBytesPath);
                AssetDatabase.Refresh();
                Debug.LogWarning("[WorldSdfBaker] 已取消烘焙，未生成资源文件。 ");
                return;
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[WorldSdfBaker] SDF 烘焙完成：{path} dims=({dimX},{dimY},{dimZ}) voxelWorld={voxel}");
            }
            finally
            {
                if (restoreReadableByAssetPath != null && restoreReadableByAssetPath.Count > 0)
                {
                    int i = 0;
                    int n = restoreReadableByAssetPath.Count;
                    foreach (var kv in restoreReadableByAssetPath)
                    {
                        float progress = n <= 1 ? 0f : (float)i / (n - 1);
                        EditorUtility.DisplayCancelableProgressBar("World SDF 烘焙", $"还原 Mesh 可读写 {i + 1}/{n}", progress);
                        var importer = AssetImporter.GetAtPath(kv.Key) as ModelImporter;
                        if (importer != null)
                        {
                            importer.isReadable = kv.Value;
                            importer.SaveAndReimport();
                        }
                        i++;
                    }
                }

                EditorUtility.ClearProgressBar();
            }
        }
    }
}
