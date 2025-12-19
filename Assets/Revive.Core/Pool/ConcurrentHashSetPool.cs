using System;
using System.Collections.Generic;

namespace Revive.Core.Pool
{
    public class ConcurrentHashSetPool<T> : ConcurrentCollectionPool<HashSet<T>, T>
    {
        
    }
    
    public class ConcurrentDictionaryPool<TKey, TValue> : ConcurrentCollectionPool<Dictionary<TKey, TValue>, KeyValuePair<TKey, TValue>>
    {
        
    }

    public class ConcurrentObjectPool<T> where T : class, new()
    {
        private static readonly ObjectPool<T> Pool = new((Func<T>) (() => new T()), actionOnRelease: (Action<T>) (l => { }));
        
        private static readonly object s_Lock = new object();
        
        public static T Get()
        {
            lock (s_Lock)
            {
                return Pool.Get();
            }
        }

        public static PooledObject<T> Get(out T value)
        {
            lock (s_Lock)
            {
                return Pool.Get(out value);
            }
        }

        public static void Release(T toRelease)
        {
            lock (s_Lock)
            {
                Pool.Release(toRelease);
            }
        }
    }
}