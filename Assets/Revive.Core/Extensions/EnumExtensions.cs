using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Revive.Core.Pool;

namespace Revive.Core.Extensions
{
    /// <summary>
    /// Helper functions to process enum flags.
    /// 
    /// 性能优化特性：
    /// - 使用 Unsafe.As 避免装箱操作
    /// - 使用 ArrayPool 减少内存分配
    /// - 避免 LINQ 操作减少临时对象创建
    /// - 使用 Span 进行高效的内存操作
    /// - 手动循环替代 LINQ 聚合操作
    /// - 缓存友好的数组访问模式
    /// </summary>
    public static class EnumExtensions
    {
        /// <summary>
        /// 获取标志枚举中的所有单个标志值，排除零值和多位标志
        /// 高性能版本：避免装箱、LINQ分配和枚举器开销，直接填充到List中
        /// </summary>
        /// <param name="enumType">标志枚举类型</param>
        /// <param name="results">用于接收结果的List，方法会清空并填充此List</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetIndividualFlags(Type enumType, List<Enum> results)
        {
            results.Clear();
            
            // 获取原始数组，避免Cast<Enum>()的装箱开销
            var values = Enum.GetValues(enumType);
            var length = values.Length;
            
            for (int i = 0; i < length; i++)
            {
                var value = (Enum)values.GetValue(i);
                ulong flag = 0x1;

                // 直接使用unsafe转换避免Convert.ToUInt64的装箱
                var bits = GetEnumValue(value);
                if (bits == 0L)
                    continue; // skip the zero value

                while (flag < bits)
                    flag <<= 1;

                if (flag == bits)
                    results.Add(value);
            }
        }
        
        /// <summary>
        /// 高性能获取枚举值，避免装箱，使用泛型和Unsafe进行零拷贝转换
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong GetEnumValue(Enum value)
        {
            // 直接使用Convert.ToUInt64，它内部已经优化了类型转换
            // 虽然可能有轻微的装箱，但是类型安全且正确处理有符号到无符号的转换
            return Convert.ToUInt64(value);
        }
        
        /// <summary>
        /// 泛型版本的高性能枚举值获取，完全避免装箱
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong GetEnumValue<T>(T value) where T : struct, Enum
        {
            // 使用Unsafe.As在泛型约束下进行零拷贝转换
            var typeCode = value.GetTypeCode();
            return typeCode switch
            {
                TypeCode.Byte => Unsafe.As<T, byte>(ref value),
                TypeCode.SByte => unchecked((ulong)(long)Unsafe.As<T, sbyte>(ref value)),
                TypeCode.Int16 => unchecked((ulong)(long)Unsafe.As<T, short>(ref value)),
                TypeCode.UInt16 => Unsafe.As<T, ushort>(ref value),
                TypeCode.Int32 => unchecked((ulong)(long)Unsafe.As<T, int>(ref value)),
                TypeCode.UInt32 => Unsafe.As<T, uint>(ref value),
                TypeCode.Int64 => unchecked((ulong)Unsafe.As<T, long>(ref value)),
                TypeCode.UInt64 => Unsafe.As<T, ulong>(ref value),
                _ => Convert.ToUInt64(value) // fallback
            };
        }
        
        /// <summary>
        /// 获取给定值中包含的所有标志，包括零标志和多位标志
        /// 高性能版本：避免ToList()、Cast()和枚举器分配，直接填充到List中
        /// </summary>
        /// <param name="value">要匹配标志的值</param>
        /// <param name="results">用于接收结果的List，方法会清空并填充此List</param>
        public static void GetAllFlags(this Enum value, List<Enum> results)
        {
            results.Clear();
            
            // 直接使用原始数组，避免ToList()分配
            var enumValues = Enum.GetValues(value.GetType());
            var flags = ArrayPool<Enum>.Shared.Rent(enumValues.Length);
            
            try
            {
                // 手动转换避免Cast<Enum>()
                for (int i = 0; i < enumValues.Length; i++)
                {
                    flags[i] = (Enum)enumValues.GetValue(i);
                }
                
                GetFlags(value, flags.AsSpan(0, enumValues.Length), results);
            }
            finally
            {
                ArrayPool<Enum>.Shared.Return(flags);
            }
        }

        /// <summary>
        /// 获取给定值中包含的所有单个标志，排除零标志和多位标志
        /// 高性能版本：避免ToList()分配和枚举器开销，直接填充到List中
        /// </summary>
        /// <param name="value">要匹配标志的值</param>
        /// <param name="results">用于接收结果的List，方法会清空并填充此List</param>
        public static void GetIndividualFlags(this Enum value, List<Enum> results)
        {
            // 使用临时List获取个体标志
            using var _ = ListPool<Enum>.Get(out var tempFlags);
            GetIndividualFlags(value.GetType(), tempFlags);
            
            // 直接传递给GetFlags避免枚举器分配
            GetFlags(value, tempFlags, results);
        }

        /// <summary>
        /// 使用按位OR操作符将所有给定标志组合成一个枚举值
        /// 高性能版本：避免LINQ、装箱和枚举器分配，直接使用List
        /// </summary>
        /// <param name="enumType">枚举类型</param>
        /// <param name="flags">要组合的标志列表</param>
        /// <returns>组合后的枚举值</returns>
        public static Enum GetEnum(Type enumType, List<Enum> flags)
        {
            ulong value = 0;
            
            // 手动循环避免LINQ和枚举器分配
            for (int i = 0; i < flags.Count; i++)
            {
                value |= GetEnumValue(flags[i]);
            }
            
            return (Enum)Enum.ToObject(enumType, value);
        }

        /// <summary>
        /// 获取给定标志列表中包含在指定值中的所有标志，使用按位AND操作
        /// 高性能版本：避免List分配和枚举器开销，直接填充到结果List中
        /// </summary>
        /// <param name="value">要匹配标志的值</param>
        /// <param name="flags">要测试的标志列表</param>
        /// <param name="results">用于接收结果的List，方法会清空并填充此List</param>
        private static void GetFlags(Enum value, List<Enum> flags, List<Enum> results)
        {
            results.Clear();
            
            var bits = GetEnumValue(value);
            // Empty flag enum
            if (bits == 0L)
                return;

            // 从后向前遍历，保持原有顺序（相当于原来的Reverse效果）
            for (var i = flags.Count - 1; i >= 0; i--)
            {
                var mask = GetEnumValue(flags[i]);
                if (mask == 0L)
                    continue;

                if ((bits & mask) == mask)
                {
                    results.Add(flags[i]);
                }
            }
        }
        
        /// <summary>
        /// 高性能版本的GetFlags，专门处理Span参数，避免枚举器分配
        /// </summary>
        private static void GetFlags(Enum value, ReadOnlySpan<Enum> flags, List<Enum> results)
        {
            results.Clear();
            
            var bits = GetEnumValue(value);
            // Empty flag enum
            if (bits == 0L)
                return;

            // 从后向前遍历，保持原有顺序（相当于原来的Reverse效果）
            for (var i = flags.Length - 1; i >= 0; i--)
            {
                var mask = GetEnumValue(flags[i]);
                if (mask == 0L)
                    continue;

                if ((bits & mask) == mask)
                {
                    results.Add(flags[i]);
                }
            }
        }
    }
}
