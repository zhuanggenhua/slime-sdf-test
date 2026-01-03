using System;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 默认值特性 - 用于标记字段的默认值，支持反射重置
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class DefaultValueAttribute : Revive.DefaultValueAttribute
    {
        public DefaultValueAttribute(object value) : base(value)
        {
        }

        public DefaultValueAttribute(float r, float g, float b, float a = 1f) : base(r, g, b, a)
        {
        }
    }
}
