using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class MoveFlowerObjectsDownPerChild : EditorWindow
{
    [MenuItem("Tools/每个 Flower 子模型单独向下移动半个高度 %&F")] // 快捷键 Ctrl+Shift+F
    static void MoveFlowerChildrenDown()
    {
        GameObject[] selectedRoots = Selection.gameObjects;

        if (selectedRoots.Length == 0)
        {
            Debug.LogWarning("没有选中任何 GameObject！");
            return;
        }

        List<Transform> affectedTransforms = new List<Transform>();
        int processedCount = 0;

        foreach (GameObject root in selectedRoots)
        {
            // 获取所有 MeshRenderer（包括 inactive）
            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);

            foreach (MeshRenderer mr in renderers)
            {
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                // 判断名称是否包含 "flower"（不区分大小写）
                if (!mr.gameObject.name.ToLower().Contains("flower")) continue;

                // 计算该物体自身世界空间 Bounds
                Bounds bounds = mr.bounds;
                float halfHeight = bounds.extents.y; // 从中心到底部的距离

                Vector3 moveOffset = Vector3.down * halfHeight;

                // 记录用于 Undo
                affectedTransforms.Add(mr.transform);

                // 移动该子物体自身的位置
                mr.transform.position += moveOffset;

                Debug.Log($"[Moved Flower] {mr.gameObject.name} (路径: {GetHierarchyPath(mr.transform)}): " +
                          $"Bounds 高度 {bounds.size.y:F3}, 向下移动 {halfHeight:F3}");

                processedCount++;
            }
        }

        if (affectedTransforms.Count > 0)
        {
            // 注册 Undo
            Undo.RecordObjects(affectedTransforms.ToArray(), "Move Flower Children Down Individually");
            Debug.Log($"完成！共处理了 {processedCount} 个名称包含 'flower' 的花模型。");
        }
        else
        {
            Debug.Log("未找到任何名称包含 'flower' 且带有 Mesh 的子物体。");
        }
    }

    // 获取 Hierarchy 完整路径，用于日志方便排查
    private static string GetHierarchyPath(Transform transform)
    {
        string path = transform.name;
        Transform parent = transform.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }
}