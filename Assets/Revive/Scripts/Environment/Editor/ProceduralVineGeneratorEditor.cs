using UnityEngine;
using UnityEditor;
using System.IO;

namespace Revive.Environment.Editor
{
    /// <summary>
    /// ProceduralVineGenerator的自定义Inspector
    /// 提供生成、保存网格等功能按钮
    /// </summary>
    [CustomEditor(typeof(ProceduralVineGenerator))]
    public class ProceduralVineGeneratorEditor : UnityEditor.Editor
    {
        private ProceduralVineGenerator _generator;
        
        private void OnEnable()
        {
            _generator = (ProceduralVineGenerator)target;
        }
        
        public override void OnInspectorGUI()
        {
            // 绘制默认Inspector
            DrawDefaultInspector();
            
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("藤蔓生成工具", EditorStyles.boldLabel);
            
            // 生成按钮
            if (GUILayout.Button("生成藤蔓网格", GUILayout.Height(30)))
            {
                GenerateVineMesh();
            }
            
            EditorGUILayout.Space(5);
            
            // 保存网格按钮
            GUI.enabled = _generator.MeshFilter != null && _generator.MeshFilter.sharedMesh != null;
            if (GUILayout.Button("保存网格为Asset", GUILayout.Height(25)))
            {
                SaveMeshAsset();
            }
            GUI.enabled = true;
            
            EditorGUILayout.Space(5);
            
            // 加载已保存网格按钮
            if (GUILayout.Button("加载已保存的网格", GUILayout.Height(25)))
            {
                LoadMeshAsset();
            }
            
            EditorGUILayout.Space(5);
            
            // 清除网格按钮
            GUI.enabled = _generator.MeshFilter != null && _generator.MeshFilter.sharedMesh != null;
            if (GUILayout.Button("清除网格"))
            {
                ClearMesh();
            }
            GUI.enabled = true;
            
            EditorGUILayout.Space(10);
            
            // 显示统计信息
            if (_generator.MeshFilter != null && _generator.MeshFilter.sharedMesh != null)
            {
                Mesh mesh = _generator.MeshFilter.sharedMesh;
                
                // 检查网格是否为Asset
                string assetPath = AssetDatabase.GetAssetPath(mesh);
                bool isAsset = !string.IsNullOrEmpty(assetPath);
                
                string meshInfo = $"网格统计:\n" +
                                 $"名称: {mesh.name}\n" +
                                 $"类型: {(isAsset ? "已保存Asset ✓" : "临时生成")}\n" +
                                 $"顶点数: {mesh.vertexCount}\n" +
                                 $"三角形数: {mesh.triangles.Length / 3}";
                
                if (isAsset)
                {
                    meshInfo += $"\n路径: {assetPath}";
                }
                
                EditorGUILayout.HelpBox(meshInfo, isAsset ? MessageType.Info : MessageType.Warning);
                
                // 如果是临时生成的网格，提示保存
                if (!isAsset)
                {
                    EditorGUILayout.HelpBox(
                        "⚠ 当前网格为临时生成，退出Play模式或关闭Unity后会丢失！\n建议点击\"保存网格为Asset\"永久保存。",
                        MessageType.Warning
                    );
                }
            }
            
            EditorGUILayout.Space(5);
            
            // 使用说明
            EditorGUILayout.HelpBox(
                "使用步骤:\n" +
                "1. 调整BoxCollider定义藤蔓生长范围\n" +
                "2. 配置生成参数\n" +
                "3. 点击\"生成藤蔓网格\"预览\n" +
                "4. 满意后点击\"保存网格为Asset\"永久保存\n" +
                "   (保存后MeshFilter会自动引用Asset)\n\n" +
                "其他功能:\n" +
                "• 加载已保存的网格: 从文件加载之前保存的网格\n" +
                "• 清除网格: 移除当前网格引用",
                MessageType.Info
            );
        }
        
        /// <summary>
        /// 生成藤蔓网格
        /// </summary>
        private void GenerateVineMesh()
        {
            Undo.RecordObject(_generator, "Generate Vine Mesh");
            
            Mesh mesh = _generator.GenerateVineMesh();
            
            if (mesh != null)
            {
                EditorUtility.SetDirty(_generator);
                Debug.Log($"[ProceduralVineGenerator] 藤蔓网格生成成功！顶点数: {mesh.vertexCount}");
            }
            else
            {
                Debug.LogError("[ProceduralVineGenerator] 藤蔓网格生成失败！");
            }
        }
        
        /// <summary>
        /// 保存网格为Asset
        /// </summary>
        private void SaveMeshAsset()
        {
            if (_generator.MeshFilter == null || _generator.MeshFilter.sharedMesh == null)
            {
                EditorUtility.DisplayDialog("错误", "没有可保存的网格！请先生成网格。", "确定");
                return;
            }
            
            // 打开保存对话框
            string defaultPath = "Assets/Revive/Arts/Model/ProceduralVines";
            if (!Directory.Exists(defaultPath))
            {
                Directory.CreateDirectory(defaultPath);
            }
            
            string defaultName = $"VineMesh_{_generator.gameObject.name}_{System.DateTime.Now:yyyyMMdd_HHmmss}";
            string path = EditorUtility.SaveFilePanelInProject(
                "保存藤蔓网格",
                defaultName,
                "asset",
                "选择保存位置",
                defaultPath
            );
            
            if (string.IsNullOrEmpty(path))
                return;
            
            // 创建网格的副本
            Mesh meshCopy = Object.Instantiate(_generator.MeshFilter.sharedMesh);
            meshCopy.name = Path.GetFileNameWithoutExtension(path);
            
            // 保存为Asset
            AssetDatabase.CreateAsset(meshCopy, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            // 重新加载保存的Asset（确保是Asset引用而不是实例）
            Mesh savedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            
            // 设置MeshFilter引用到保存的Asset
            Undo.RecordObject(_generator.MeshFilter, "Set Mesh to Saved Asset");
            _generator.MeshFilter.sharedMesh = savedMesh;
            EditorUtility.SetDirty(_generator.MeshFilter);
            
            // 如果有MeshCollider，也更新它的mesh
            MeshCollider meshCollider = _generator.GetComponent<MeshCollider>();
            if (meshCollider != null)
            {
                Undo.RecordObject(meshCollider, "Update MeshCollider");
                meshCollider.sharedMesh = savedMesh;
                EditorUtility.SetDirty(meshCollider);
                Debug.Log($"[ProceduralVineGenerator] 已同步更新MeshCollider");
            }
            
            // 选中保存的Asset
            EditorGUIUtility.PingObject(savedMesh);
            
            Debug.Log($"[ProceduralVineGenerator] 网格已保存到: {path}\n已将MeshFilter.sharedMesh设置为保存的Asset");
            EditorUtility.DisplayDialog("成功", $"网格已保存到:\n{path}\n\nMeshFilter已自动引用保存的Asset", "确定");
        }
        
        /// <summary>
        /// 加载已保存的网格Asset
        /// </summary>
        private void LoadMeshAsset()
        {
            if (_generator.MeshFilter == null)
            {
                EditorUtility.DisplayDialog("错误", "未找到MeshFilter组件！", "确定");
                return;
            }
            
            // 打开文件选择对话框
            string defaultPath = "Assets/Revive/Arts/Model/ProceduralVines";
            string path = EditorUtility.OpenFilePanel(
                "选择藤蔓网格Asset",
                defaultPath,
                "asset"
            );
            
            if (string.IsNullOrEmpty(path))
                return;
            
            // 转换为相对路径
            if (path.StartsWith(Application.dataPath))
            {
                path = "Assets" + path.Substring(Application.dataPath.Length);
            }
            
            // 加载网格Asset
            Mesh loadedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            
            if (loadedMesh == null)
            {
                EditorUtility.DisplayDialog("错误", "无法加载网格Asset！\n请确保选择的是有效的Mesh文件。", "确定");
                return;
            }
            
            // 设置到MeshFilter
            Undo.RecordObject(_generator.MeshFilter, "Load Mesh Asset");
            _generator.MeshFilter.sharedMesh = loadedMesh;
            EditorUtility.SetDirty(_generator.MeshFilter);
            
            // 如果有MeshCollider，也更新它
            MeshCollider meshCollider = _generator.GetComponent<MeshCollider>();
            if (meshCollider != null)
            {
                Undo.RecordObject(meshCollider, "Update MeshCollider");
                meshCollider.sharedMesh = loadedMesh;
                EditorUtility.SetDirty(meshCollider);
            }
            
            Debug.Log($"[ProceduralVineGenerator] 已加载网格: {path}");
            EditorUtility.DisplayDialog("成功", $"已加载网格:\n{loadedMesh.name}\n\n顶点数: {loadedMesh.vertexCount}\n三角形数: {loadedMesh.triangles.Length / 3}", "确定");
        }
        
        /// <summary>
        /// 清除网格
        /// </summary>
        private void ClearMesh()
        {
            if (_generator.MeshFilter == null)
                return;
            
            Undo.RecordObject(_generator.MeshFilter, "Clear Vine Mesh");
            
            _generator.MeshFilter.sharedMesh = null;
            
            // 如果有MeshCollider，也清除它
            MeshCollider meshCollider = _generator.GetComponent<MeshCollider>();
            if (meshCollider != null)
            {
                Undo.RecordObject(meshCollider, "Clear MeshCollider");
                meshCollider.sharedMesh = null;
                EditorUtility.SetDirty(meshCollider);
            }
            
            EditorUtility.SetDirty(_generator.MeshFilter);
            Debug.Log("[ProceduralVineGenerator] 网格已清除");
        }
    }
}

