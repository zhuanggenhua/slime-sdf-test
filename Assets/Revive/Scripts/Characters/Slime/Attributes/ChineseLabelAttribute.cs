using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 中文标签特性 - 让字段在Inspector中显示中文名称
    /// </summary>
    public class ChineseLabelAttribute : Revive.ChineseLabelAttribute
    {
        public ChineseLabelAttribute(string label) : base(label)
        {
        }
    }
}
