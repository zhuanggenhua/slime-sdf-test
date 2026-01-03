using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 中文Header特性 - 用下划线样式替代加粗，解决Unity加粗字体中文缺字问题
    /// </summary>
    public class ChineseHeaderAttribute : Revive.ChineseHeaderAttribute
    {
        public ChineseHeaderAttribute(string header) : base(header)
        {
        }
    }
}
