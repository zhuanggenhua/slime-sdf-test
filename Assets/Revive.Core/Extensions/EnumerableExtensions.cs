using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Revive.Core.Extensions
{
    public static class EnumerableExtensions
    {
        /// <summary>
        /// Tells whether a sequence is null or empty.
        /// </summary>
        /// <param name="source">The source sequence.</param>
        /// <returns>Returns true if the sequence is null or empty, false if it is not null and contains at least one element.</returns>
        [Pure]
        public static bool IsNullOrEmpty(this IEnumerable source)
        {
            if (source == null)
                return true;

            var enumerator = source.GetEnumerator();
            if (enumerator == null)
                throw new ArgumentException("Invalid 'source' IEnumerable.");

            return enumerator.MoveNext() == false;
        }

        /// <summary>
        /// Executes an action for each (casted) item of the given enumerable.
        /// </summary>
        /// <typeparam name="T">Type of the item value in the enumerable.</typeparam>
        /// <param name="source">Input enumerable to work on.</param>
        /// <param name="action">Action performed for each item in the enumerable.</param>
        /// <remarks>This extension method do not yield. It acts just like a foreach statement, and performs a cast to a typed enumerable in the middle.</remarks>
        public static void ForEach<T>(this IEnumerable source, Action<T> action)
        {
            source.Cast<T>().ForEach(action);
        }

        /// <summary>
        /// Executes an action for each item of the given enumerable.
        /// </summary>
        /// <typeparam name="T">Type of the item value in the enumerable.</typeparam>
        /// <param name="source">Input enumerable to work on.</param>
        /// <param name="action">Action performed for each item in the enumerable.</param>
        /// <remarks>This extension method do not yield. It acts just like a foreach statement.</remarks>
        public static void ForEach<T>(this IEnumerable<T> source, Action<T> action)
        {
            foreach (var item in source)
            {
                action(item);
            }
        }

        /// <summary>
        /// An <see cref="IEnumerable{T}"/> extension method that searches for the first match and returns its index.
        /// </summary>
        /// <typeparam name="T">Generic type parameter.</typeparam>
        /// <param name="source">Input enumerable to work on.</param>
        /// <param name="predicate">The predicate.</param>
        /// <returns>The index of the first element matching.</returns>
        [Pure]
        public static int IndexOf<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            var index = 0;
            foreach (var item in source)
            {
                if (predicate(item))
                    return index;
                index++;
            }
            return -1;
        }

        /// <summary>
        /// An <see cref="IEnumerable{T}"/> extension method that searches for the last match and returns its index.
        /// </summary>
        /// <typeparam name="T">Generic type parameter.</typeparam>
        /// <param name="source">Input enumerable to work on.</param>
        /// <param name="predicate">The predicate.</param>
        /// <returns>The index of the last element matching.</returns>
        [Pure]
        public static int LastIndexOf<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            var list = source as IList<T>;
            if (list != null)
            {
                // Faster search for lists.
                for (var i = list.Count - 1; i >= 0; --i)
                {
                    if (predicate(list[i]))
                        return i;
                }
                return -1;
            }
            var index = 0;
            var lastIndex = -1;
            foreach (var item in source)
            {
                if (predicate(item))
                    lastIndex = index;
                index++;
            }
            return lastIndex;
        }

        /// <summary>
        /// Filters out null items from the enumerable.
        /// </summary>
        /// <typeparam name="T">Generic type parameter.</typeparam>
        /// <param name="source">Input enumerable to work on.</param>
        /// <returns>An enumeration of all items in <paramref name="source"/> that are not <c>null</c>.</returns>
        [Pure]
        public static IEnumerable<T> NotNull<T>(this IEnumerable<T> source) where T : class
        {
            return source.Where(x => x != null);
        }

        /// <summary>
        /// Filters out null items from the enumerable.
        /// </summary>
        /// <typeparam name="T">Generic type parameter.</typeparam>
        /// <param name="source">Input enumerable to work on.</param>
        /// <returns>An enumeration of all items in <paramref name="source"/> that are not <c>null</c>.</returns>
        [Pure]
        public static IEnumerable<T> NotNull<T>(this IEnumerable<T?> source) where T : struct
        {
            return source.Where(item => item.HasValue).Select(item => item.Value);
        }

        /// <summary>
        /// Enumerates the linked list nodes.
        /// </summary>
        /// <param name="list">The linked list.</param>
        /// <returns>An enumeration of the linked list nodes.</returns>
        [Pure]
        internal static IEnumerable<LinkedListNode<T>> EnumerateNodes<T>(this LinkedList<T> list)
        {
            var node = list.First;
            while (node != null)
            {
                yield return node;
                node = node.Next;
            }
        }

        /// <summary>
        /// Calculates a combined hash code for items of the enumerbale.
        /// </summary>
        /// <typeparam name="T">Generic type parameter.</typeparam>
        /// <param name="source">Input enumerable to work on.</param>
        /// <returns>A combined hash code or 0 if the source is empty.</returns>
        [Pure]
        public static int ToHashCode<T>(this IEnumerable<T> source) where T : class
        {
            if (source.IsNullOrEmpty()) return 0;

            unchecked
            {
                return source.Aggregate(17, (hash, item) => hash * 23 + item.GetHashCode()); 
            }
        }
        
        public static IEnumerable<TSource> IterBfs<TSource>(
            this TSource self,
            Func<TSource, IEnumerable<TSource>> neighbors)
        {
            var queue = new Queue<TSource>();
            var visited = new HashSet<TSource>();
            queue.Enqueue(self);
            visited.Add(self);
            while (queue.Count > 0)
            {
                var elem = queue.Dequeue();
                foreach (var neighbor in neighbors(elem).Where(neighbor => !visited.Contains(neighbor)))
                {
                    queue.Enqueue(neighbor);
                    visited.Add(neighbor);
                }
                yield return elem;
            }
        }
        
        public static void IterBfs<TSource>(
            this TSource self,
            Func<TSource, IEnumerable<TSource>> neighbors,
            Action<TSource> action)
        {
            var queue = new Queue<TSource>();
            var visited = new HashSet<TSource>();
            queue.Enqueue(self);
            visited.Add(self);
            while (queue.Count > 0)
            {
                var elem = queue.Dequeue();
                foreach (var neighbor in neighbors(elem).Where(neighbor => !visited.Contains(neighbor)))
                {
                    queue.Enqueue(neighbor);
                    visited.Add(neighbor);
                }
                action(elem);
            }
        }
    
        public static IEnumerable<TSource> IterDfs<TSource>(
            this TSource self,
            Func<TSource, IEnumerable<TSource>> neighbors)
        {
            var stack = new Stack<TSource>();
            var visited = new HashSet<TSource>();
            stack.Push(self);
            visited.Add(self);
            while (stack.Count > 0)
            {
                var elem = stack.Pop();
                foreach (var neighbor in neighbors(elem).Where(neighbor => !visited.Contains(neighbor)))
                {
                    stack.Push(neighbor);
                    visited.Add(neighbor);
                }
                yield return elem;
            }
        }
        
        public static void IterDfs<TSource>(
            this TSource self,
            Func<TSource, IEnumerable<TSource>> neighbors,
            Action<TSource> action)
        {
            var stack = new Stack<TSource>();
            var visited = new HashSet<TSource>();
            stack.Push(self);
            visited.Add(self);
            while (stack.Count > 0)
            {
                var elem = stack.Pop();
                foreach (var neighbor in neighbors(elem).Where(neighbor => !visited.Contains(neighbor)))
                {
                    stack.Push(neighbor);
                    visited.Add(neighbor);
                }
                action(elem);
            }
        }
        
        public static IEnumerable<TSource> IterBfs<TSource>(
            this TSource self,
            Func<TSource, IEnumerable<TSource>> neighbors,
            HashSet<TSource> visited)
        {
            var queue = new Queue<TSource>();
            visited ??= new HashSet<TSource>();
            // if (visited.Contains(self)) yield break;
            queue.Enqueue(self);
            visited.Add(self);
            while (queue.Count > 0)
            {
                var elem = queue.Dequeue();
                foreach (var neighbor in neighbors(elem).Where(neighbor => !visited.Contains(neighbor)))
                {
                    queue.Enqueue(neighbor);
                    visited.Add(neighbor);
                }
                yield return elem;
            }
        }
    
        public static IEnumerable<TSource> IterDfs<TSource>(
            this TSource self,
            Func<TSource, IEnumerable<TSource>> neighbors,
            HashSet<TSource> visited)
        {
            var stack = new Stack<TSource>();
            visited ??= new HashSet<TSource>();
            // if (visited.Contains(self)) yield break;
            stack.Push(self);
            visited.Add(self);
            while (stack.Count > 0)
            {
                var elem = stack.Pop();
                foreach (var neighbor in neighbors(elem).Where(neighbor => !visited.Contains(neighbor)))
                {
                    stack.Push(neighbor);
                    visited.Add(neighbor);
                }
                yield return elem;
            }
        }
        
        /// <summary>Determines whether two sequences are equal. Comparing the elements is done using the default equality comparer for their type.
        /// <para>Allows either parameter to be <c>null</c>.</para>
        /// <para>A thin wrapper around <see cref="Enumerable.SequenceEqual{TSource}(IEnumerable{TSource}, IEnumerable{TSource})"/>.</para></summary>
        /// <typeparam name="T">The type of the elements of the input sequences.</typeparam>
        /// <param name="first">An enumerable to compare to <paramref name="second"/>.</param>
        /// <param name="second">An enumerable to compare to <paramref name="first"/>.</param>
        /// <returns><c>true</c> if one of the following is true.
        /// <list type="bullet">
        /// <item><paramref name="first"/> and <paramref name="second"/> are the same object.</item>
        /// <item>Neither enumerable is <c>null</c> and they have the same length and each of the elements in the enumerables compare equal pairwise.</item>
        /// </list>
        /// <para><c>false</c> otherwise.</para></returns>
        public static bool SequenceEqualAllowNull<T>(this IEnumerable<T> first, IEnumerable<T> second)
            => SequenceEqualAllowNull(first, second, null);

        /// <summary>Determines whether two sequences are equal. Comparing the elements is done using the specified equality comparer.
        /// <para>Allows <paramref name="first"/> and/or <paramref name="second"/> to be <c>null</c>.</para>
        /// <para>A thin wrapper around <see cref="Enumerable.SequenceEqual{TSource}(IEnumerable{TSource}, IEnumerable{TSource}, IEqualityComparer{TSource}?)"/>.</para></summary>
        /// <typeparam name="T">The type of the elements of the input sequences.</typeparam>
        /// <param name="first">An enumerable to compare to <paramref name="second"/>.</param>
        /// <param name="second">An enumerable to compare to <paramref name="first"/>.</param>
        /// <param name="comparer">The equality comparer.</param>
        /// <returns><c>true</c> if one of the following is true.
        /// <list type="bullet">
        /// <item><paramref name="first"/> and <paramref name="second"/> are the same object.</item>
        /// <item>Neither enumerable is <c>null</c> and they have the same length and each of the elements in the enumerables compare equal pairwise.</item>
        /// </list>
        /// <para><c>false</c> otherwise.</para></returns>
        public static bool SequenceEqualAllowNull<T>(this IEnumerable<T> first, IEnumerable<T> second, IEqualityComparer<T> comparer)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first is null || second is null) return false;
            // if (first is List<T> llist && second is List<T> rlist)
            // {
            //     var lhs = CollectionsMarshal.AsSpan(llist);
            //     var rhs = CollectionsMarshal.AsSpan(rlist);
            //     return lhs.SequenceEqual(rhs);
            // }
            return Enumerable.SequenceEqual(first, second, comparer);
        }
        
#if !NET6_0_OR_GREATER
        public static TSource MinBy<TSource, TKey>(this IEnumerable<TSource> source,
            Func<TSource, TKey> selector)
        {
            return source.MinBy(selector, null);
        }
        
        public static TSource MinBy<TSource, TKey>(this IEnumerable<TSource> source,
            Func<TSource, TKey> selector, IComparer<TKey> comparer)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            comparer ??= Comparer<TKey>.Default;

            using var sourceIterator = source.GetEnumerator();
            if (!sourceIterator.MoveNext())
            {
                throw new InvalidOperationException("Sequence contains no elements");
            }
            var min = sourceIterator.Current;
            var minKey = selector(min);
            while (sourceIterator.MoveNext())
            {
                var candidate = sourceIterator.Current;
                var candidateProjected = selector(candidate);
                if (comparer.Compare(candidateProjected, minKey) < 0)
                {
                    min = candidate;
                    minKey = candidateProjected;
                }
            }
            return min;
        }
        
        public static TSource MaxBy<TSource, TKey>(this IEnumerable<TSource> source,
            Func<TSource, TKey> selector)
        {
            return source.MaxBy(selector, null);
        }

        public static TSource MaxBy<TSource, TKey>(this IEnumerable<TSource> source,
            Func<TSource, TKey> selector, IComparer<TKey> comparer)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            comparer ??= Comparer<TKey>.Default;

            using var sourceIterator = source.GetEnumerator();
            if (!sourceIterator.MoveNext())
            {
                throw new InvalidOperationException("Sequence contains no elements");
            }
            var max = sourceIterator.Current;
            var maxKey = selector(max);
            while (sourceIterator.MoveNext())
            {
                var candidate = sourceIterator.Current;
                var candidateProjected = selector(candidate);
                if (comparer.Compare(candidateProjected, maxKey) > 0)
                {
                    max = candidate;
                    maxKey = candidateProjected;
                }
            }
            return max;
        }
#endif
    }
}
