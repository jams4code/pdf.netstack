#if !NET8_0_OR_GREATER
// Language and library features the compiler expects on .NET Standard 2.0. Internal, so they never
// collide with the real types in consumers that target newer frameworks.
#pragma warning disable CA1815, CA1066, CA2231, CA1716, CA1720, CA1062, CA1305, CA1307, CA1002

using System.Text;

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;

    internal static class RuntimeHelpers
    {
        public static T[] GetSubArray<T>(T[] array, Range range)
        {
            (int offset, int length) = range.GetOffsetAndLength(array.Length);
            var result = new T[length];
            Array.Copy(array, offset, result, 0, length);
            return result;
        }
    }
}

namespace System
{
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }

        public static Index Start => new(0);

        public static Index End => new(0, fromEnd: true);

        public int Value => _value < 0 ? ~_value : _value;

        public bool IsFromEnd => _value < 0;

        public static Index FromStart(int value) => new(value);

        public static Index FromEnd(int value) => new(value, fromEnd: true);

        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;

        public bool Equals(Index other) => _value == other._value;

        public override bool Equals(object? obj) => obj is Index i && Equals(i);

        public override int GetHashCode() => _value;

        public static implicit operator Index(int value) => FromStart(value);
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public Index Start { get; }

        public Index End { get; }

        public static Range All => new(Index.Start, Index.End);

        public static Range StartAt(Index start) => new(start, Index.End);

        public static Range EndAt(Index end) => new(Index.Start, end);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end) throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);

        public override bool Equals(object? obj) => obj is Range r && Equals(r);

        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
    }
}

namespace System.Collections.Generic
{
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object?>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object? obj) => obj is null ? 0 : obj.GetHashCode(); // PDF object types never override GetHashCode.
    }
}

namespace PdfNetStack
{
    internal static class PolyfillExtensions
    {
        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key, TValue value)
            where TKey : notnull
        {
            if (d.ContainsKey(key)) return false;
            d.Add(key, value);
            return true;
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key)
            where TKey : notnull => d.TryGetValue(key, out TValue? value) ? value : default;

        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }

        public static string GetString(this Encoding encoding, ReadOnlySpan<byte> bytes) => encoding.GetString(bytes.ToArray());

        public static int GetBytes(this Encoding encoding, string text, Span<byte> destination)
        {
            byte[] bytes = encoding.GetBytes(text);
            bytes.CopyTo(destination);
            return bytes.Length;
        }

        public static void Write(this Stream stream, ReadOnlySpan<byte> data)
        {
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                data.CopyTo(buffer);
                stream.Write(buffer, 0, data.Length);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static HashSet<T> ToHashSet<T>(this IEnumerable<T> items) => new(items);
    }
}
#endif
