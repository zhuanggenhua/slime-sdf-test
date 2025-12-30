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

        private enum MeshSeedMode
        {
            Triangles = 0,
            Bounds = 1,
        }

        private struct BakeDistanceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> Keys;
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

            public int yzStride;
            public int dimZ;
            public float voxel;
            public Vector3 originWorld;
            public float storeBandWorld;
            public float storeBandSqr;
            public int startIndex;

            [WriteOnly] public NativeArray<float> OutBestAbs;
            [WriteOnly] public NativeArray<float> OutBestDWorld;
            [WriteOnly] public NativeArray<byte> OutFlags;

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
                int gi = startIndex + index;
                int key = Keys[gi];

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
                        Vector3 p = MultiplyPoint3x4(CandidateWorldToLocal[ci], pWorld) - CandidateLocalCenter[ci];
                        float d = SdBox(p, CandidateBoxHalfSize[ci]) * uniformScale;
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
                                Vector3 n = MeshTriNormalsW[nOffset + t];
                                float s = triDelta.x * n.x + triDelta.y * n.y + triDelta.z * n.z;
                                float signed = s < 0f ? -dist : dist;
                                bestAbs = dist;
                                bestDWorld = signed;
                                bestSignedKnown = true;
                                if (triDistSqr < 1e-12f)
                                    break;
                            }
                        }
                    }
                }

                OutBestAbs[gi] = bestAbs;
                OutBestDWorld[gi] = bestDWorld;
                OutFlags[gi] = (byte)((bestSignedKnown ? 1 : 0) | (anyBoundsPass ? 2 : 0));
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

        private void Bake()
        {
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
            if (estimatedMb > 128.0)
            {
                if (!EditorUtility.DisplayDialog("World SDF 烘焙", $"预计生成 {estimatedMb:F1} MB 的 SDF 资源（dims=({dimX},{dimY},{dimZ}) voxel={voxel}）。\n\n这可能会很慢并占用大量内存/磁盘。是否继续？", "继续", "取消"))
                {
                    return;
                }
            }

            string path = EditorUtility.SaveFilePanelInProject("Save World SDF", "WorldSdf", "bytes", "");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var colliders = FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
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
                Vector3 ls = c.transform.lossyScale;
                bool uniform = Mathf.Abs(ls.x - ls.y) < 1e-4f && Mathf.Abs(ls.x - ls.z) < 1e-4f;
                if (uniform)
                {
                    uniformScale = ls.x;
                    if (c is BoxCollider) kind = 1;
                    else if (c is SphereCollider) kind = 2;
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

            var candidateMeshDataIndex = new int[candidates.Count];
            var meshVertsW = new List<Vector3[]>(16);
            var meshTris = new List<int[]>(16);
            var meshTriNormalsW = new List<Vector3[]>(16);
            int AddMeshData(MeshCollider mc)
            {
                Mesh m = mc.sharedMesh;
                var verts = m.vertices;
                var tris = m.triangles;
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

            Vector3 min = bounds.min;
            Vector3 originWorld = min + Vector3.one * (voxel * 0.5f);

            float invScale = Revive.Slime.PBF_Utils.InvScale;
            float voxelSim = voxel * invScale;
            float maxDistanceSim = Mathf.Max(0.0001f, maxDistanceWorld) * invScale;
            Vector3 originSim = originWorld * invScale;
            var queryTriggers = includeTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
            float insideProbeRadius = voxel * 0.45f;
            float insideTestRadius = Mathf.Max(1e-4f, voxel * 0.05f);

            uint magic = Revive.Slime.WorldSdfRuntime.Magic;
            int version = Revive.Slime.WorldSdfRuntime.Version;
            string absoluteBytesPath = Path.Combine(Directory.GetCurrentDirectory(), path);
            bool cancelled = false;
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
                                candidateWorldToLocalNA[ci] = box.transform.worldToLocalMatrix;
                                candidateLocalCenterNA[ci] = box.center;
                                candidateBoxHalfSizeNA[ci] = box.size * 0.5f;
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
                                    Keys = keysNA,
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

                                    yzStride = yzStride,
                                    dimZ = dimZ,
                                    voxel = voxel,
                                    originWorld = originWorld,
                                    storeBandWorld = storeBandWorld,
                                    storeBandSqr = storeBandSqr,
                                    startIndex = start,

                                    OutBestAbs = outBestAbsNA,
                                    OutBestDWorld = outBestDWorldNA,
                                    OutFlags = outFlagsNA,
                                };

                                job.Schedule(count, 64).Complete();
                                if (cancelled) break;
                            }
                            ticksJobDistance = System.Diagnostics.Stopwatch.GetTimestamp() - tJob0;

                            if (!cancelled)
                            {
                                long tPatch0 = System.Diagnostics.Stopwatch.GetTimestamp();
                                for (int ki = 0; ki < keyCount; ki++)
                                {
                                    float progress = keyCount <= 1 ? 0f : (float)ki / (keyCount - 1);
                                    if (EditorUtility.DisplayCancelableProgressBar("World SDF 烘焙", $"补洞/写入 {ki + 1}/{keyCount}（updated={entryCount:N0}）", progress))
                                    {
                                        cancelled = true;
                                        break;
                                    }

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

                                    voxelsBoundsPass++;
                                    if (bestAbs > storeBandWorld)
                                        continue;

                                    float dWorld;
                                    if (bestSignedKnown)
                                    {
                                        dWorld = bestDWorld;
                                    }
                                    else
                                    {
                                        checkSphereCalls++;
                                        bool inside = Physics.CheckSphere(pWorld, insideTestRadius, staticLayers.value, queryTriggers);
                                        dWorld = inside ? -bestAbs : bestAbs;
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
                    Debug.Log($"[WorldSdfBaker] BakeProfile time={sw.Elapsed.TotalSeconds:F2}s voxels={voxelsTotal:N0} seedKeys={seedKeys.Count:N0} seedAddAttempts={seedAddAttempts:N0} seedAdded={seedAdded:N0} seedMeshColliders={seedMeshColliderCount:N0} seedMeshTris={seedTriCount:N0} jobDist={(ticksJobDistance * invFreq):F2}s patchDist={(ticksPatchDistance * invFreq):F2}s boundsPass={voxelsBoundsPass:N0} closestPointCalls={closestPointCalls:N0} closestPointErrors={closestPointErrors:N0} checkSphereCalls={checkSphereCalls:N0} updated={entryCount:N0} updatedNeg={insideNegCount:N0} updatedMin={updatedMin:F4} updatedMax={updatedMax:F4} allNeg={totalNeg:N0} allMin={allMin:F4} allMax={allMax:F4} candidates={candidates.Count}");
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
    }
}
