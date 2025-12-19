using System;
using System.Collections.Generic;

namespace Revive.Core.Pool
{
    public class ConcurrentCollectionPool<TCollection, TItem> where TCollection : class, ICollection<TItem>, new()
    {
        internal static readonly ObjectPool<TCollection> s_Pool = new((Func<TCollection>) (() => new TCollection()), actionOnRelease: (Action<TCollection>) (l => l.Clear()));

        private static object s_Lock = new object();
        public static TCollection Get()
        {
            lock (s_Lock)
            {
                return s_Pool.Get();
            }
        }

        public static PooledObject<TCollection> Get(out TCollection value)
        {
            lock (s_Lock)
            {
                return s_Pool.Get(out value);
            }
        }

        public static void Release(TCollection toRelease)
        {
            lock (s_Lock)
            {
                s_Pool.Release(toRelease);
            }
        }
    }
}