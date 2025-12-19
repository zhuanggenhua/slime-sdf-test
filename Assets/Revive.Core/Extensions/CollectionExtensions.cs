using System;
using System.Collections.Generic;

namespace Revive.Core.Extensions
{
    /// <summary>
    /// An extension class for various types of collection.
    /// </summary>
    public static class CollectionExtensions
    {
        /// <summary>
        /// Remove an item by swapping it with the last item and removing it from the last position. This function prevents to shift values from the list on removal but does not maintain order.
        /// </summary>
        /// <param name="list">The list.</param>
        /// <param name="item">The item to remove.</param>
        public static void SwapRemove<T>(this IList<T> list, T item)
        {
            var index = list.IndexOf(item);
            if (index < 0)
                return;

            list.SwapRemoveAt(index);
        }

        /// <summary>
        /// Remove an item by swapping it with the last item and removing it from the last position. This function prevents to shift values from the list on removal but does not maintain order.
        /// </summary>
        /// <param name="list">The list.</param>
        /// <param name="index">Index of the item to remove.</param>
        public static void SwapRemoveAt<T>(this IList<T> list, int index)
        {
            if (index < 0 || index >= list.Count) throw new ArgumentOutOfRangeException(nameof(index));

            if (index < list.Count - 1)
            {
                list[index] = list[list.Count - 1];
            }

            list.RemoveAt(list.Count - 1);
        }

        /// <summary>
        /// Gets the item from a list at a specified index. If index is out of the list, returns null.
        /// </summary>
        /// <typeparam name="T">Type of the item in the list</typeparam>
        /// <param name="list">The list.</param>
        /// <param name="index">The index.</param>
        /// <returns>The item from a list at a specified index. If index is out of the list, returns null..</returns>
        public static T GetItemOrNull<T>(this IList<T> list, int index) where T : class
        {
            if (index >= 0 && index < list.Count)
            {
                return list[index];
            }
            return null;
        }

        /// <summary>
        /// Determines the index of a specific item in the <see cref="IReadOnlyList{T}"/>.
        /// </summary>
        /// <param name="list">The list.</param>
        /// <param name="item">The object to locate in the <see cref="IReadOnlyList{T}"/>.</param>
        /// <returns>The index of item if found in the list; otherwise, -1.</returns>
        public static int IndexOf<T>(this IReadOnlyList<T> list, T item)
        {
            var comparer = EqualityComparer<T>.Default;
            for (var i = 0; i < list.Count; i++)
            {
                if (comparer.Equals(list[i], item)) return i;
            }
            return -1;
        }
        
        /// <summary>
        /// Deeply compares of two <see cref="IList{T}"/>.
        /// </summary>
        /// <typeparam name="T">Type of the object to compare</typeparam>
        /// <param name="a1">The list1 to compare</param>
        /// <param name="a2">The list2 to compare</param>
        /// <param name="comparer">The comparer to use (or default to the default EqualityComparer for T)</param>
        /// <returns><c>true</c> if the list are equal</returns>
        public static bool ArraysEqual<T>(IList<T> a1, IList<T> a2, IEqualityComparer<T> comparer = null)
        {
            // This is not really an extension method, maybe it should go somewhere else.
            if (ReferenceEquals(a1, a2))
                return true;

            if (a1 == null || a2 == null)
                return false;

            if (a1.Count != a2.Count)
                return false;

            if (comparer == null)
                comparer = EqualityComparer<T>.Default;
            for (var i = 0; i < a1.Count; i++)
            {
                if (!comparer.Equals(a1[i], a2[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Compares of two <see cref="IList{T}"/> using operator !=.
        /// </summary>
        /// <typeparam name="T">Type of the object to compare</typeparam>
        /// <param name="a1">The list1 to compare</param>
        /// <param name="a2">The list2 to compare</param>
        /// <returns><c>true</c> if the list are equal</returns>
        public static bool ArraysReferenceEqual<T>(IList<T> a1, IList<T> a2) where T : class
        {
            if (ReferenceEquals(a1, a2))
                return true;

            if (a1 == null || a2 == null)
                return false;

            if (a1.Count != a2.Count)
                return false;

            for (var i = 0; i < a1.Count; i++)
            {
                if (a1[i] != a2[i])
                    return false;
            }

            return true;
        }
        
        /// <summary>
        /// Remove and return the item in the end of the list.
        /// </summary>
        /// <typeparam name="T">Type of the item.</typeparam>
        /// <param name="list">List.</param>
        /// <returns>Last item.</returns>
        public static T Pop<T>(this IList<T> list)
        {
            var n = list.Count - 1;
            var result = list[n];
            list.RemoveAt(n);

            return result;
        }

        /// <summary>
        /// Computes the hash of a collection using hash of each elements.
        /// </summary>
        /// <typeparam name="T">Type of the object to calculate the hash</typeparam>
        /// <param name="data">The list to generates the hash</param>
        /// <param name="comparer">The comparer to use (or use the default comparer otherwise)</param>
        /// <returns>The hashcode of the collection.</returns>
        public static int ComputeHash<T>(this ICollection<T> data, IEqualityComparer<T> comparer = null)
        {
            unchecked
            {
                if (data == null)
                    return 0;

                if (comparer == null)
                    comparer = EqualityComparer<T>.Default;

                var hash = 17 + data.Count;
                var result = hash;
                foreach (var unknown in data)
                    result = result * 31 + comparer.GetHashCode(unknown);
                return result;
            }
        }

        /// <summary>
        /// Computes the hash of the array.
        /// </summary>
        /// <typeparam name="T">Type of the object to calculate the hash</typeparam>
        /// <param name="data">The array to generates the hash</param>
        /// <param name="comparer">The comparer to use (or use the default comparer otherwise)</param>
        /// <returns>The hashcode of the array.</returns>
        public static int ComputeHash<T>(this T[] data, IEqualityComparer<T> comparer = null)
        {
            unchecked
            {
                if (data == null)
                    return 0;

                if (comparer == null)
                    comparer = EqualityComparer<T>.Default;

                var hash = 17 + data.Length;
                var result = hash;
                foreach (var unknown in data)
                    result = result * 31 + comparer.GetHashCode(unknown);
                return result;
            }
        }

        /// <summary>
        /// Extracts a sub-array from an array.
        /// </summary>
        /// <typeparam name="T">Type of the array element</typeparam>
        /// <param name="data">The array to slice</param>
        /// <param name="index">The start of the index to get the data from.</param>
        /// <param name="length">The length of elements to slice</param>
        /// <returns>A slice of the array.</returns>
        public static T[] SubArray<T>(this T[] data, int index, int length)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var result = new T[length];
            Array.Copy(data, index, result, 0, length);
            return result;
        }

        /// <summary>
        /// Concats two arrays.
        /// </summary>
        /// <typeparam name="T">Type of the array element</typeparam>
        /// <param name="array1">The array1 to concat</param>
        /// <param name="array2">The array2 to concat</param>
        /// <returns>The concat of the array.</returns>
        public static T[] Concat<T>(this T[] array1, T[] array2)
        {
            if (array1 == null) throw new ArgumentNullException(nameof(array1));
            if (array2 == null) throw new ArgumentNullException(nameof(array2));
            var result = new T[array1.Length + array2.Length];

            array1.CopyTo(result, 0);
            array2.CopyTo(result, array1.Length);

            return result;
        }
    }
}
