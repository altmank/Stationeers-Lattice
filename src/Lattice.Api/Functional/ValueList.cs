using System;
using System.Collections;
using System.Collections.Generic;

namespace Lattice.Api;

/// <summary>
/// An immutable list that compares by its elements, in order, so records holding one keep value equality.
/// Copies its input once; nobody else holds its storage. <c>foreach</c> over it allocates nothing.
/// </summary>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly T[] _items;

    private ValueList(T[] items) => _items = items;

    /// <summary>The empty list.</summary>
    public static ValueList<T> Empty { get; } = new(Array.Empty<T>());

    /// <summary>A list holding a copy of the items. Cost: linear in their number.</summary>
    public static ValueList<T> From(IEnumerable<T> items)
    {
        if (items is null)
            throw new ArgumentNullException(nameof(items));
        if (items is ICollection<T> collection)
        {
            if (collection.Count == 0)
                return Empty;
            var copy = new T[collection.Count];
            collection.CopyTo(copy, 0);
            return new ValueList<T>(copy);
        }

        var list = new List<T>(items);
        return list.Count == 0 ? Empty : new ValueList<T>(list.ToArray());
    }

    /// <summary>A list of one item.</summary>
    public static ValueList<T> Of(T item) => new(new[] { item });

    /// <summary>Takes ownership of an array the caller built and never touches again.</summary>
    internal static ValueList<T> Own(T[] items) => items.Length == 0 ? Empty : new ValueList<T>(items);

    /// <inheritdoc />
    public int Count => _items.Length;

    /// <inheritdoc />
    public T this[int index] => _items[index];

    /// <summary>An enumerator that allocates nothing.</summary>
    public Enumerator GetEnumerator() => new(_items);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <summary>Equal when both hold equal elements in the same order.</summary>
    public bool Equals(ValueList<T>? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || other._items.Length != _items.Length)
            return false;
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < _items.Length; i++)
            if (!comparer.Equals(_items[i], other._items[i]))
                return false;
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (var i = 0; i < _items.Length; i++)
            hash.Add(_items[i]);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => $"[{_items.Length} items]";

    /// <summary>Walks a <see cref="ValueList{T}"/> without allocating.</summary>
    public struct Enumerator
    {
        private readonly T[] _items;
        private int _index;

        internal Enumerator(T[] items)
        {
            _items = items;
            _index = -1;
        }

        /// <summary>The element at the current position.</summary>
        public readonly T Current => _items[_index];

        /// <summary>Moves to the next element; false past the end.</summary>
        public bool MoveNext() => ++_index < _items.Length;
    }
}

/// <summary>Builds value lists with the type inferred.</summary>
public static class ValueList
{
    /// <summary>A list holding a copy of the items.</summary>
    public static ValueList<T> From<T>(IEnumerable<T> items) => ValueList<T>.From(items);

    /// <summary>A list of one item.</summary>
    public static ValueList<T> Of<T>(T item) => ValueList<T>.Of(item);
}
