#nullable disable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Revive.Core.Pool
{
    public class ObjectPool<T> : IDisposable, IObjectPool<T> where T : class
    {
        internal readonly List<T> m_List;
        private readonly Func<T> m_CreateFunc;
        private readonly Action<T> m_ActionOnGet;
        private readonly Action<T> m_ActionOnRelease;
        private readonly Action<T> m_ActionOnDestroy;
        private readonly int m_MaxSize;
        internal bool m_CollectionCheck;
        private T m_FreshlyReleased;

        public int CountAll { get; private set; }

        public int CountActive => this.CountAll - this.CountInactive;

        public int CountInactive
        {
            get => this.m_List.Count + ((object)this.m_FreshlyReleased != null ? 1 : 0);
        }

        public ObjectPool(
            Func<T> createFunc,
            Action<T> actionOnGet = null,
            Action<T> actionOnRelease = null,
            Action<T> actionOnDestroy = null,
            bool collectionCheck = true,
            int defaultCapacity = 10,
            int maxSize = 10000)
        {
            if (createFunc == null)
                throw new ArgumentNullException(nameof(createFunc));
            if (maxSize <= 0)
                throw new ArgumentException("Max Size must be greater than 0", nameof(maxSize));
            this.m_List = new List<T>(defaultCapacity);
            this.m_CreateFunc = createFunc;
            this.m_MaxSize = maxSize;
            this.m_ActionOnGet = actionOnGet;
            this.m_ActionOnRelease = actionOnRelease;
            this.m_ActionOnDestroy = actionOnDestroy;
            this.m_CollectionCheck = collectionCheck;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get()
        {
            T obj;
            if ((object)this.m_FreshlyReleased != null)
            {
                obj = this.m_FreshlyReleased;
                this.m_FreshlyReleased = default(T);
            }
            else if (this.m_List.Count == 0)
            {
                obj = this.m_CreateFunc();
                ++this.CountAll;
            }
            else
            {
                int index = this.m_List.Count - 1;
                obj = this.m_List[index];
                this.m_List.RemoveAt(index);
            }

            Action<T> actionOnGet = this.m_ActionOnGet;
            if (actionOnGet != null)
                actionOnGet(obj);
            return obj;
        }

        public PooledObject<T> Get(out T v)
        {
            return new PooledObject<T>(v = this.Get(), (IObjectPool<T>)this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release(T element)
        {
            if (this.m_CollectionCheck && (this.m_List.Count > 0 || (object)this.m_FreshlyReleased != null))
            {
                if ((object)element == (object)this.m_FreshlyReleased)
                    throw new InvalidOperationException(
                        "Trying to release an object that has already been released to the pool.");
                for (int index = 0; index < this.m_List.Count; ++index)
                {
                    if ((object)element == (object)this.m_List[index])
                        throw new InvalidOperationException(
                            "Trying to release an object that has already been released to the pool.");
                }
            }

            Action<T> actionOnRelease = this.m_ActionOnRelease;
            if (actionOnRelease != null)
                actionOnRelease(element);
            if ((object)this.m_FreshlyReleased == null)
                this.m_FreshlyReleased = element;
            else if (this.CountInactive < this.m_MaxSize)
            {
                this.m_List.Add(element);
            }
            else
            {
                --this.CountAll;
                Action<T> actionOnDestroy = this.m_ActionOnDestroy;
                if (actionOnDestroy != null)
                    actionOnDestroy(element);
            }
        }

        public void Clear()
        {
            if (this.m_ActionOnDestroy != null)
            {
                foreach (T obj in this.m_List)
                    this.m_ActionOnDestroy(obj);
                if ((object)this.m_FreshlyReleased != null)
                    this.m_ActionOnDestroy(this.m_FreshlyReleased);
            }

            this.m_FreshlyReleased = default(T);
            this.m_List.Clear();
            this.CountAll = 0;
        }

        public void Dispose() => this.Clear();

        internal bool HasElement(T element)
        {
            return (object)this.m_FreshlyReleased == (object)element || this.m_List.Contains(element);
        }
    }
}