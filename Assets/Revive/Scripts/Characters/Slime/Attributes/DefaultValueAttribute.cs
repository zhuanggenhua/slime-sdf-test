using System;
using UnityEngine;

namespace Revive.Slime
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
}
