using UnityEngine;
using UnityEditor;

namespace Revive.Effects.Editor
{
    /// <summary>
    /// TrailEffectBase的自定义Inspector
    /// </summary>
    [CustomEditor(typeof(TrailEffectBase), true)]
    public class TrailEffectDebugger : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            
            TrailEffectBase trail = (TrailEffectBase)target;
            
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("仅在运行时显示调试信息", MessageType.Info);
                return;
            }
            
            if (trail.ShowDebugInfo)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("调试信息", EditorStyles.boldLabel);
                
                EditorGUI.BeginDisabledGroup(true);
                
                // 显示通用信息
                EditorGUILayout.IntField("激活效果数量", trail.ActiveEffectCount);
                EditorGUILayout.FloatField("上次生成时间", Time.time - trail.LastSpawnTime);
                
                // WetGroundDecalTrail特定信息
                if (trail is WetGroundDecalTrail decalTrail)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Decal信息", EditorStyles.boldLabel);
                    EditorGUILayout.IntField("激活Decal数量", decalTrail.ActiveDecalCount);
                }
                
                // VegetationGrowthTrail特定信息
                else if (trail is VegetationGrowthTrail vegTrail)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("植被信息", EditorStyles.boldLabel);
                    EditorGUILayout.IntField("总植被数量", vegTrail.TotalInstanceCount);
                    EditorGUILayout.IntField("路径点数量", vegTrail.PathPointCount);
                    
                    EditorGUI.EndDisabledGroup();
                    
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("存档操作", EditorStyles.boldLabel);
                    
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("保存植被"))
                    {
                        vegTrail.SaveToFile();
                        EditorUtility.DisplayDialog("保存成功", "植被数据已保存", "确定");
                    }
                    if (GUILayout.Button("加载植被"))
                    {
                        if (EditorUtility.DisplayDialog("加载确认", 
                            "这将清除当前所有植被并加载存档。是否继续？", "是", "否"))
                        {
                            vegTrail.LoadFromFile();
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                    
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("清除所有植被"))
                    {
                        if (EditorUtility.DisplayDialog("清除确认", 
                            "这将删除所有植被。是否继续？", "是", "否"))
                        {
                            vegTrail.ClearAll();
                        }
                    }
                    if (GUILayout.Button("删除存档"))
                    {
                        if (EditorUtility.DisplayDialog("删除确认", 
                            "这将删除存档文件。是否继续？", "是", "否"))
                        {
                            vegTrail.DeleteSaveFile();
                            EditorUtility.DisplayDialog("删除成功", "存档文件已删除", "确定");
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                    
                    EditorGUI.BeginDisabledGroup(true);
                }
                
                EditorGUI.EndDisabledGroup();
                
                // 强制重绘
                if (Application.isPlaying)
                {
                    Repaint();
                }
            }
        }
    }
    
    /// <summary>
    /// TrailEffectManager的自定义Inspector
    /// </summary>
    [CustomEditor(typeof(TrailEffectManager))]
    public class TrailEffectManagerDebugger : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            
            TrailEffectManager manager = (TrailEffectManager)target;
            
            if (!Application.isPlaying)
            {
                return;
            }
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("控制面板", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("启用所有尾迹"))
            {
                manager.EnableAllTrails();
            }
            if (GUILayout.Button("禁用所有尾迹"))
            {
                manager.DisableAllTrails();
            }
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("尾迹列表", EditorStyles.boldLabel);
            
            EditorGUI.BeginDisabledGroup(true);
            foreach (var trail in manager.TrailEffects)
            {
                if (trail != null)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.ObjectField(trail.GetType().Name, trail, typeof(TrailEffectBase), true);
                    EditorGUILayout.Toggle("启用", trail.enabled);
                    EditorGUILayout.IntField("效果数", trail.ActiveEffectCount);
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUI.EndDisabledGroup();
            
            Repaint();
        }
    }
}

