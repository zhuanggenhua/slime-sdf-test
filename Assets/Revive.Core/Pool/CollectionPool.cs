#nullable disable
using System;
using System.Collections.Generic;

namespace Revive.Core.Pool
{
    public class CollectionPool<TCollection, TItem> where TCollection : class, ICollection<TItem>, new()
    {
        internal static readonly ObjectPool<TCollection> s_Pool = new ObjectPool<TCollection>((Func<TCollection>) (() => new TCollection()), actionOnRelease: (Action<TCollection>) (l => l.Clear()));

        public static TCollection Get() => CollectionPool<TCollection, TItem>.s_Pool.Get();

        public static PooledObject<TCollection> Get(out TCollection value)
        {
            return CollectionPool<TCollection, TItem>.s_Pool.Get(out value);
        }

        public static void Release(TCollection toRelease)
        {
            CollectionPool<TCollection, TItem>.s_Pool.Release(toRelease);
        }
    }
}