using System;
using System.Reflection;
using UnityEngine;

namespace Slime
{
    /// <summary>
    /// 默认值特性 - 用于标记字段的默认值，支持反射重置
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class DefaultValueAttribute : Attribute
    {
        public object Value { get; private set; }
        
        public DefaultValueAttribute(object value)
        {
            Value = value;
        }
        
        // 支持 Color 的构造函数
        public DefaultValueAttribute(float r, float g, float b, float a = 1f)
        {
            Value = new Color(r, g, b, a);
        }
    }
    
    /// <summary>
    /// 配置重置工具 - 使用反射将所有带有 DefaultValue 特性的字段重置为默认值
    /// </summary>
    public static class ConfigResetHelper
    {
        /// <summary>
        /// 将目标对象的所有带有 DefaultValue 特性的字段重置为默认值
        /// </summary>
        /// <param name="target">目标对象</param>
        /// <returns>重置的字段数量</returns>
        public static int ResetToDefaults(object target)
        {
            if (target == null) return 0;
            
            int count = 0;
            Type type = target.GetType();
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            
            foreach (var field in fields)
            {
                var attr = field.GetCustomAttribute<DefaultValueAttribute>();
                if (attr == null) continue;
                
                try
                {
                    object value = ConvertValue(attr.Value, field.FieldType);
                    field.SetValue(target, value);
                    count++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ConfigReset] 无法重置字段 {field.Name}: {e.Message}");
                }
            }
            
            return count;
        }
        
        /// <summary>
        /// 获取目标对象所有带有 DefaultValue 特性的字段信息
        /// </summary>
        public static string GetDefaultsInfo(object target)
        {
            if (target == null) return string.Empty;
            
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Type type = target.GetType();
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            
            sb.AppendLine($"=== {type.Name} 默认值列表 ===");
            
            foreach (var field in fields)
            {
                var attr = field.GetCustomAttribute<DefaultValueAttribute>();
                if (attr == null) continue;
                
                object currentValue = field.GetValue(target);
                sb.AppendLine($"  {field.Name}: 当前={currentValue}, 默认={attr.Value}");
            }
            
            return sb.ToString();
        }
        
        /// <summary>
        /// 类型转换辅助方法
        /// </summary>
        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;
            
            // 处理 Unity 特殊类型
            if (targetType == typeof(Color) && value is Color) return value;
            if (targetType == typeof(Vector2) && value is Vector2) return value;
            if (targetType == typeof(Vector3) && value is Vector3) return value;
            if (targetType == typeof(Vector4) && value is Vector4) return value;
            
            // 数值类型转换
            if (targetType == typeof(float) && value is double d) return (float)d;
            if (targetType == typeof(float) && value is int i) return (float)i;
            if (targetType == typeof(int) && value is float f) return (int)f;
            if (targetType == typeof(int) && value is double d2) return (int)d2;
            if (targetType == typeof(bool) && value is bool) return value;
            
            // 通用转换
            return Convert.ChangeType(value, targetType);
        }
    }
}
