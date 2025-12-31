using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Revive.Editor
{
    /// <summary>
    /// 编辑器工具：将MeshCollider转换为BoxCollider
    /// </summary>
    public class MeshColliderToBoxColliderConverter : UnityEditor.Editor
    {
        [MenuItem("Tools/Revive/Convert MeshCollider to BoxCollider", false, 1)]
        private static void ConvertMeshColliderToBoxCollider()
        {
            if (Selection.gameObjects.Length == 0)
            {
                EditorUtility.DisplayDialog("提示", "请先选择一个或多个GameObject", "确定");
                return;
            }

            int totalConverted = 0;
            
            // 记录操作以支持撤销
            Undo.SetCurrentGroupName("Convert MeshCollider to BoxCollider");
            int undoGroup = Undo.GetCurrentGroup();

            foreach (GameObject selectedObject in Selection.gameObjects)
            {
                totalConverted += ConvertGameObjectColliders(selectedObject);
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (totalConverted > 0)
            {
                EditorUtility.DisplayDialog("转换完成", 
                    $"成功转换了 {totalConverted} 个MeshCollider为BoxCollider", "确定");
            }
            else
            {
                EditorUtility.DisplayDialog("提示", 
                    "所选GameObject中未找到MeshCollider", "确定");
            }
        }

        [MenuItem("Tools/Revive/Convert MeshCollider to BoxCollider", true)]
        private static bool ValidateConvertMeshColliderToBoxCollider()
        {
            // 只有在选中GameObject时才启用菜单项
            return Selection.gameObjects.Length > 0;
        }

        /// <summary>
        /// 转换单个GameObject及其所有子物体的MeshCollider
        /// </summary>
        private static int ConvertGameObjectColliders(GameObject gameObject)
        {
            int convertedCount = 0;

            // 获取该GameObject及其所有子物体的MeshCollider
            MeshCollider[] meshColliders = gameObject.GetComponentsInChildren<MeshCollider>(true);

            foreach (MeshCollider meshCollider in meshColliders)
            {
                if (meshCollider == null)
                    continue;

                GameObject obj = meshCollider.gameObject;

                // 记录碰撞器的属性
                bool isTrigger = meshCollider.isTrigger;
                PhysicsMaterial material = meshCollider.sharedMaterial;
                Mesh mesh = meshCollider.sharedMesh;

                // 计算bounds
                Bounds bounds;
                if (mesh != null)
                {
                    bounds = mesh.bounds;
                }
                else
                {
                    // 如果没有mesh，使用默认大小
                    bounds = new Bounds(Vector3.zero, Vector3.one);
                }

                // 记录删除操作
                Undo.DestroyObjectImmediate(meshCollider);

                // 添加BoxCollider
                BoxCollider boxCollider = Undo.AddComponent<BoxCollider>(obj);

                // 应用属性
                Undo.RecordObject(boxCollider, "Set BoxCollider Properties");
                boxCollider.center = bounds.center;
                boxCollider.size = bounds.size;
                boxCollider.isTrigger = isTrigger;
                boxCollider.sharedMaterial = material;

                convertedCount++;

                Debug.Log($"已转换: {obj.name} - MeshCollider -> BoxCollider (Center: {bounds.center}, Size: {bounds.size})");
            }

            return convertedCount;
        }

        // 添加右键菜单支持
        [MenuItem("GameObject/Revive/Convert MeshCollider to BoxCollider", false, 10)]
        private static void ConvertMeshColliderToBoxColliderContextMenu()
        {
            ConvertMeshColliderToBoxCollider();
        }

        [MenuItem("GameObject/Revive/Convert MeshCollider to BoxCollider", true)]
        private static bool ValidateConvertMeshColliderToBoxColliderContextMenu()
        {
            return Selection.gameObjects.Length > 0;
        }
    }
}

