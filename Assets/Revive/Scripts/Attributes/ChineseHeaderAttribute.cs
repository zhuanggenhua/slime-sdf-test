using UnityEngine;

namespace Revive
{
    /// <summary>
    /// 中文Header特性 - 用下划线样式替代加粗，解决Unity加粗字体中文缺字问题
    /// </summary>
    public class ChineseHeaderAttribute : PropertyAttribute
    {
        public string Header { get; private set; }
        
        public ChineseHeaderAttribute(string header)
        {
            Header = header;
        }
    }
}
