using System;
using System.Collections;
using System.Collections.Generic;

namespace Lattice.Api;

/// <summary>
/// An immutable dictionary that compares by its entries, in any order, so records holding one keep value equality.
/// Copies its input once.
/// </summary>
public sealed class ValueMap<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>, IEquatable<ValueMap<TKey, TValue>>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _entries;

    private ValueMap(Dictionary<TKey, TValue> entries) => _entries = entries;

    /// <summary>The empty map.</summary>
    public static ValueMap<TKey, TValue> Empty { get; } = new(new Dictionary<TKey, TValue>());

    /// <summary>A map holding a copy of the entries. Cost: linear in their number.</summary>
    public static ValueMap<TKey, TValue> From(IReadOnlyDictionary<TKey, TValue> entries)
    {
        if (entries is null)
            throw new ArgumentNullException(nameof(entries));
        if (entries.Count == 0)
            return Empty;
        var copy = new Dictionary<TKey, TValue>(entries.Count);
        foreach (var entry in entries)
            copy.Add(entry.Key, entry.Value);
        return new ValueMap<TKey, TValue>(copy);
    }

    /// <summary>A map of one entry.</summary>
    public static ValueMap<TKey, TValue> Of(TKey key, TValue value) =>
        new(new Dictionary<TKey, TValue>(1) { [key] = value });

    /// <summary>A copy with the key set to the value. Cost: linear in the entries.</summary>
    public ValueMap<TKey, TValue> With(TKey key, TValue value)
    {
        var copy = new Dictionary<TKey, TValue>(_entries) { [key] = value };
        return new ValueMap<TKey, TValue>(copy);
    }

    /// <summary>A copy without the key. Cost: linear in the entries.</summary>
    public ValueMap<TKey, TValue> Without(TKey key)
    {
        if (!_entries.ContainsKey(key))
            return this;
        var copy = new Dictionary<TKey, TValue>(_entries);
        copy.Remove(key);
        return copy.Count == 0 ? Empty : new ValueMap<TKey, TValue>(copy);
    }

    /// <inheritdoc />
    public int Count => _entries.Count;

    /// <inheritdoc />
    public TValue this[TKey key] => _entries[key];

    /// <inheritdoc />
    public IEnumerable<TKey> Keys => _entries.Keys;

    /// <inheritdoc />
    public IEnumerable<TValue> Values => _entries.Values;

    /// <inheritdoc />
    public bool ContainsKey(TKey key) => _entries.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(TKey key, out TValue value) => _entries.TryGetValue(key, out value!);

    /// <summary>An enumerator that allocates nothing.</summary>
    public Enumerator GetEnumerator() => new(_entries.GetEnumerator());

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() =>
        _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();

    /// <summary>Equal when both hold the same keys with equal values.</summary>
    public bool Equals(ValueMap<TKey, TValue>? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || other.Count != Count)
            return false;
        var comparer = EqualityComparer<TValue>.Default;
        foreach (var entry in _entries)
            if (!other._entries.TryGetValue(entry.Key, out var value) || !comparer.Equals(entry.Value, value))
                return false;
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueMap<TKey, TValue> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var entry in _entries)
            hash ^= HashCode.Combine(entry.Key, entry.Value);
        return hash;
    }

    /// <inheritdoc />
    public override string ToString() => $"{{{_entries.Count} entries}}";

    /// <summary>Walks a <see cref="ValueMap{TKey, TValue}"/> without allocating.</summary>
    public struct Enumerator
    {
        private Dictionary<TKey, TValue>.Enumerator _inner;

        internal Enumerator(Dictionary<TKey, TValue>.Enumerator inner) => _inner = inner;

        /// <summary>The entry at the current position.</summary>
        public KeyValuePair<TKey, TValue> Current => _inner.Current;

        /// <summary>Moves to the next entry; false past the end.</summary>
        public bool MoveNext() => _inner.MoveNext();
    }
}

/// <summary>Builds value maps with the types inferred.</summary>
public static class ValueMap
{
    /// <summary>A map holding a copy of the entries.</summary>
    public static ValueMap<TKey, TValue> From<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> entries) where TKey : notnull =>
        ValueMap<TKey, TValue>.From(entries);

    /// <summary>A map of one entry.</summary>
    public static ValueMap<TKey, TValue> Of<TKey, TValue>(TKey key, TValue value) where TKey : notnull =>
        ValueMap<TKey, TValue>.Of(key, value);
}
