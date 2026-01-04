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

            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Row;
            container.style.alignItems = Align.FlexStart;

            var leftLabel = new Label(displayText);
            leftLabel.tooltip = displayText;
            leftLabel.style.whiteSpace = WhiteSpace.NoWrap;
            leftLabel.style.textOverflow = TextOverflow.Ellipsis;
            leftLabel.style.overflow = Overflow.Hidden;
            leftLabel.style.flexShrink = 0;
            leftLabel.style.minWidth = Mathf.Max(140f, EditorGUIUtility.labelWidth);
            leftLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            leftLabel.style.marginRight = 4;
            container.Add(leftLabel);

            var field = new PropertyField(property, string.Empty);
            field.style.flexGrow = 1;
            field.style.minWidth = 0;
            container.Add(field);

            field.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                void HideInternalLabel()
                {
                    var internalLabel = field.Q<TextElement>(className: "unity-property-field__label");
                    if (internalLabel != null)
                    {
                        internalLabel.style.display = DisplayStyle.None;
                        return;
                    }
                    field.schedule.Execute(HideInternalLabel).ExecuteLater(0);
                }

                field.schedule.Execute(HideInternalLabel).ExecuteLater(0);
            });

            return container;
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
