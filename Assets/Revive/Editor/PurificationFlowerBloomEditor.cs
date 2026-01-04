using UnityEditor;
using UnityEngine;

namespace Revive.GamePlay.Purification
{
    /// <summary>
    /// PurificationFlowerBloom 的自定义 Inspector
    /// 提供配置源提示和便捷操作
    /// </summary>
    [CustomEditor(typeof(PurificationFlowerBloom))]
    public class PurificationFlowerBloomEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            PurificationFlowerBloom flower = (PurificationFlowerBloom)target;
            
            DrawDefaultInspector();
            
            // 显示蝴蝶配置状态
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("蝴蝶配置状态", EditorStyles.boldLabel);
            
            bool hasConfig = flower.ButterflyConfig != null;
            bool useLocalOverride = flower.UseLocalOverride;
            bool hasLocalPrefabs = flower.ButterflyPrefabs != null && flower.ButterflyPrefabs.Length > 0;
            
            if (useLocalOverride)
            {
                if (hasLocalPrefabs)
                {
                    EditorGUILayout.HelpBox($"✓ 使用本地覆盖配置：{flower.ButterflyPrefabs.Length} 个预制体，概率 {flower.ButterflySpawnChance * 100:F0}%", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox("⚠ 使用本地覆盖配置，但未设置预制体！蝴蝶不会生成。", MessageType.Warning);
                }
            }
            else if (hasConfig)
            {
                if (flower.ButterflyConfig.IsValid())
                {
                    EditorGUILayout.HelpBox($"✓ 使用配置资源: {flower.ButterflyConfig.name}\n" +
                        $"预制体数量: {flower.ButterflyConfig.ButterflyPrefabs.Length}, " +
                        $"生成概率: {flower.ButterflyConfig.SpawnChance * 100:F0}%", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox($"⚠ 配置资源 {flower.ButterflyConfig.name} 无效（未设置预制体）！蝴蝶不会生成。", MessageType.Warning);
                }
            }
            else
            {
                if (hasLocalPrefabs)
                {
                    EditorGUILayout.HelpBox($"✓ 使用本地配置：{flower.ButterflyPrefabs.Length} 个预制体，概率 {flower.ButterflySpawnChance * 100:F0}%", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox("⚠ 未配置蝴蝶特效！请设置 ButterflyConfig 或配置本地预制体。", MessageType.Warning);
                }
            }
            
            // 快速创建配置按钮
            EditorGUILayout.Space();
            if (!hasConfig && hasLocalPrefabs)
            {
                if (GUILayout.Button("从本地配置创建 ScriptableObject 资源"))
                {
                    CreateConfigFromLocal(flower);
                }
            }
            
            // 运行时测试按钮
            if (Application.isPlaying)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("运行时测试", EditorStyles.boldLabel);
                
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("强制绽放"))
                {
                    flower.ForceBloomed();
                }
                if (GUILayout.Button("强制凋谢"))
                {
                    flower.ForceWithered();
                }
                EditorGUILayout.EndHorizontal();
            }
        }
        
        private void CreateConfigFromLocal(PurificationFlowerBloom flower)
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "创建蝴蝶特效配置",
                $"ButterflyConfig_{flower.gameObject.name}",
                "asset",
                "选择保存位置",
                "Assets/Revive/Arts/Configs/Purification"
            );
            
            if (string.IsNullOrEmpty(path))
                return;
            
            // 创建配置资源
            ButterflyEffectConfig config = ScriptableObject.CreateInstance<ButterflyEffectConfig>();
            config.ButterflyPrefabs = flower.ButterflyPrefabs;
            config.SpawnChance = flower.ButterflySpawnChance;
            config.SpawnOffset = flower.ButterflySpawnOffset;
            config.SpawnRandomRadius = flower.ButterflySpawnRandomRadius;
            config.Lifetime = flower.ButterflyLifetime;
            config.RemoveOnWither = flower.RemoveButterflyOnWither;
            
            AssetDatabase.CreateAsset(config, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            // 自动分配给花朵
            Undo.RecordObject(flower, "Assign Butterfly Config");
            flower.ButterflyConfig = config;
            EditorUtility.SetDirty(flower);
            
            // 选中新创建的资源
            EditorGUIUtility.PingObject(config);
            Selection.activeObject = config;
            
            Debug.Log($"[PurificationFlowerBloom] 成功创建配置资源: {path}");
            EditorUtility.DisplayDialog("成功", $"配置资源已创建并分配：\n{path}", "确定");
        }
    }
}

