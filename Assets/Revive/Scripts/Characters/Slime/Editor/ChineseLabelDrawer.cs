using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using Revive;

namespace Revive.Slime.Editor
{
    /// <summary>
    /// ChineseLabel特性的自定义绘制器 - 在Inspector中显示中文标签
    /// </summary>
    [CustomPropertyDrawer(typeof(ChineseLabelAttribute), true)]
    public class ChineseLabelDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var chineseLabel = attribute as ChineseLabelAttribute;
            string displayText = chineseLabel != null ? chineseLabel.Label : property.displayName;
            return new PropertyField(property, displayText);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var chineseLabel = attribute as ChineseLabelAttribute;
            if (chineseLabel != null)
            {
                label.text = chineseLabel.Label;
            }
            EditorGUI.PropertyField(position, property, label, true);
        }
        
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUI.GetPropertyHeight(property, label, true);
        }
    }
}
