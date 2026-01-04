using System;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace MoreMountains.Tools
{	

	[CustomPropertyDrawer(typeof(MMReadOnlyAttribute))]

	public class MMReadOnlyAttributeDrawer : PropertyDrawer
	{
		public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
		{
			if (TryGetChineseLabel(fieldInfo, out string chinese))
			{
				label.text = chinese;
				label.tooltip = chinese;
			}

			GUI.enabled = false; // Disable fields
			EditorGUI.PropertyField(position, property, label, true);
			GUI.enabled = true; // Enable fields
		}

		private static bool TryGetChineseLabel(FieldInfo info, out string text)
		{
			text = null;
			if (info == null)
				return false;

			object[] attributes;
			try
			{
				attributes = info.GetCustomAttributes(true);
			}
			catch
			{
				return false;
			}

			for (int i = 0; i < attributes.Length; i++)
			{
				object attr = attributes[i];
				if (attr == null)
					continue;

				Type t = attr.GetType();
				if (t == null)
					continue;
				if (!string.Equals(t.Name, "ChineseLabelAttribute", StringComparison.Ordinal))
					continue;

				PropertyInfo p = t.GetProperty("Label", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (p == null || p.PropertyType != typeof(string))
					continue;

				text = p.GetValue(attr) as string;
				return !string.IsNullOrEmpty(text);
			}

			return false;
		}

		// Necessary since some properties tend to collapse smaller than their content
		public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
		{
			return EditorGUI.GetPropertyHeight(property, label, true);
		}
	}
}