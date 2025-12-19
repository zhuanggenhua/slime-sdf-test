#nullable disable
using System.Collections.Generic;

namespace Revive.Core.Pool
{
    public class DictionaryPool<TKey, TValue> : 
        CollectionPool<Dictionary<TKey, TValue>, KeyValuePair<TKey, TValue>>
    {
    }
}