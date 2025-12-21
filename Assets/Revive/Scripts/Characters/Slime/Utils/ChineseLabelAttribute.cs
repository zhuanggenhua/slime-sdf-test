using UnityEngine;

namespace Slime
{
    /// <summary>
    /// 中文标签特性 - 让字段在Inspector中显示中文名称
    /// </summary>
    public class ChineseLabelAttribute : PropertyAttribute
    {
        public string Label { get; private set; }
        
        public ChineseLabelAttribute(string label)
        {
            Label = label;
        }
    }
}
