using System;
using System.Collections;
using System.Collections.Generic;

namespace Lattice.Api;

/// <summary>
/// An immutable set that compares by its elements, in any order, so records holding one keep value equality.
/// Copies its input once; duplicates collapse.
/// </summary>
public sealed class ValueSet<T> : IReadOnlyCollection<T>, IEquatable<ValueSet<T>> where T : notnull
{
    private readonly HashSet<T> _items;

    private ValueSet(HashSet<T> items) => _items = items;

    /// <summary>The empty set.</summary>
    public static ValueSet<T> Empty { get; } = new(new HashSet<T>());

    /// <summary>A set holding a copy of the items. Cost: linear in their number.</summary>
    public static ValueSet<T> From(IEnumerable<T> items)
    {
        if (items is null)
            throw new ArgumentNullException(nameof(items));
        var set = new HashSet<T>(items);
        return set.Count == 0 ? Empty : new ValueSet<T>(set);
    }

    /// <summary>A set of one item.</summary>
    public static ValueSet<T> Of(T item) => new(new HashSet<T> { item });

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <summary>Whether the set holds the item. Cost: constant.</summary>
    public bool Contains(T item) => _items.Contains(item);

    /// <summary>An enumerator that allocates nothing.</summary>
    public Enumerator GetEnumerator() => new(_items.GetEnumerator());

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <summary>Equal when both hold the same elements.</summary>
    public bool Equals(ValueSet<T>? other) =>
        ReferenceEquals(this, other) || (other is not null && other.Count == Count && _items.SetEquals(other._items));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueSet<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var item in _items)
            hash ^= item.GetHashCode();
        return hash;
    }

    /// <inheritdoc />
    public override string ToString() => $"{{{_items.Count} items}}";

    /// <summary>Walks a <see cref="ValueSet{T}"/> without allocating.</summary>
    public struct Enumerator
    {
        private HashSet<T>.Enumerator _inner;

        internal Enumerator(HashSet<T>.Enumerator inner) => _inner = inner;

        /// <summary>The element at the current position.</summary>
        public T Current => _inner.Current;

        /// <summary>Moves to the next element; false past the end.</summary>
        public bool MoveNext() => _inner.MoveNext();
    }
}

/// <summary>Builds value sets with the type inferred.</summary>
public static class ValueSet
{
    /// <summary>A set holding a copy of the items.</summary>
    public static ValueSet<T> From<T>(IEnumerable<T> items) where T : notnull => ValueSet<T>.From(items);

    /// <summary>A set of one item.</summary>
    public static ValueSet<T> Of<T>(T item) where T : notnull => ValueSet<T>.Of(item);
}
