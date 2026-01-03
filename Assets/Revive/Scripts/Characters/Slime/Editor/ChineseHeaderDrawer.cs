using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using Revive;

namespace Revive.Slime.Editor
{
    /// <summary>
    /// ChineseHeader特性的自定义绘制器 - 用下划线样式显示中文Header
    /// </summary>
    [CustomPropertyDrawer(typeof(Revive.ChineseHeaderAttribute), true)]
    public class ChineseHeaderDrawer : DecoratorDrawer
    {
        private static GUIStyle _headerStyle;
        
        private static GUIStyle HeaderStyle
        {
            get
            {
                if (_headerStyle == null)
                {
                    _headerStyle = new GUIStyle(EditorStyles.label)
                    {
                        fontStyle = FontStyle.Normal,
                        fontSize = 12,
                        alignment = TextAnchor.LowerLeft,
                        padding = new RectOffset(0, 0, 0, 2)
                    };
                }
                return _headerStyle;
            }
        }

        public override VisualElement CreatePropertyGUI()
        {
            var headerAttr = attribute as Revive.ChineseHeaderAttribute;
            if (headerAttr == null) return null;

            string displayText = headerAttr.Header;
            if (!displayText.StartsWith("【"))
                displayText = "【" + displayText;
            if (!displayText.EndsWith("】"))
                displayText = displayText + "】";

            var container = new VisualElement();
            container.style.marginTop = 8;
            container.style.marginBottom = 4;

            var label = new Label(displayText);
            label.style.unityFontStyleAndWeight = FontStyle.Normal;
            label.style.fontSize = 12;
            label.style.unityTextAlign = TextAnchor.LowerLeft;
            label.style.paddingBottom = 2;
            container.Add(label);

            var line = new VisualElement();
            line.style.height = 1;
            line.style.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            container.Add(line);

            return container;
        }
        
        public override void OnGUI(Rect position)
        {
            var headerAttr = attribute as Revive.ChineseHeaderAttribute;
            if (headerAttr == null) return;
            
            // 上方留白
            position.y += 8;
            position.height = 16;
            
            // 绘制文字（自动添加【】）
            string displayText = headerAttr.Header;
            if (!displayText.StartsWith("【"))
                displayText = "【" + displayText;
            if (!displayText.EndsWith("】"))
                displayText = displayText + "】";
            EditorGUI.LabelField(position, displayText, HeaderStyle);
            
            // 绘制下划线
            var lineRect = new Rect(position.x, position.y + 16, position.width, 1);
            EditorGUI.DrawRect(lineRect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        }
        
        public override float GetHeight()
        {
            // 上方间距 + 文字高度 + 下划线 + 下方间距
            return 8 + 16 + 1 + 4;
        }
    }
}
