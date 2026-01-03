using UnityEditor;
using UnityEngine;

namespace Revive.GamePlay.Purification
{
    /// <summary>
    /// 蝴蝶特效配置的自定义 Inspector
    /// 提供预设配置快速设置功能
    /// </summary>
    [CustomEditor(typeof(ButterflyEffectConfig))]
    public class ButterflyEffectConfigEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            ButterflyEffectConfig config = (ButterflyEffectConfig)target;
            
            // 绘制标题
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("蝴蝶特效配置", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("此配置可在多个花朵之间共享，避免重复配置。", MessageType.Info);
            EditorGUILayout.Space();
            
            // 绘制预设配置按钮
            EditorGUILayout.LabelField("快速预设", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("浪漫场景 (高概率)"))
            {
                Undo.RecordObject(config, "Apply Romantic Preset");
                config.SpawnChance = 0.8f;
                config.SpawnOffset = new Vector3(0f, 1f, 0f);
                config.SpawnRandomRadius = 0.8f;
                config.Lifetime = 30f;
                config.RemoveOnWither = false;
                EditorUtility.SetDirty(config);
            }
            
            if (GUILayout.Button("标准场景 (平衡)"))
            {
                Undo.RecordObject(config, "Apply Standard Preset");
                config.SpawnChance = 0.3f;
                config.SpawnOffset = new Vector3(0f, 1f, 0f);
                config.SpawnRandomRadius = 0.5f;
                config.Lifetime = 0f;
                config.RemoveOnWither = true;
                EditorUtility.SetDirty(config);
            }
            
            if (GUILayout.Button("稀有场景 (低概率)"))
            {
                Undo.RecordObject(config, "Apply Rare Preset");
                config.SpawnChance = 0.1f;
                config.SpawnOffset = new Vector3(0f, 1.2f, 0f);
                config.SpawnRandomRadius = 0.3f;
                config.Lifetime = 15f;
                config.RemoveOnWither = true;
                EditorUtility.SetDirty(config);
            }
            
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
            
            // 绘制默认属性
            DrawDefaultInspector();
            
            // 绘制状态信息
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("配置状态", EditorStyles.boldLabel);
            
            if (config.ButterflyPrefabs == null || config.ButterflyPrefabs.Length == 0)
            {
                EditorGUILayout.HelpBox("⚠ 未配置蝴蝶预制体！请添加至少一个预制体。", MessageType.Warning);
            }
            else
            {
                int validCount = 0;
                foreach (var prefab in config.ButterflyPrefabs)
                {
                    if (prefab != null) validCount++;
                }
                
                if (validCount == 0)
                {
                    EditorGUILayout.HelpBox("⚠ 所有预制体引用都是null！请分配有效的预制体。", MessageType.Warning);
                }
                else if (validCount < config.ButterflyPrefabs.Length)
                {
                    EditorGUILayout.HelpBox($"⚠ {config.ButterflyPrefabs.Length - validCount} 个预制体引用为null。", MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox($"✓ 配置有效：{validCount} 个蝴蝶预制体，生成概率 {config.SpawnChance * 100:F0}%", MessageType.Info);
                }
            }
        }
    }
}

