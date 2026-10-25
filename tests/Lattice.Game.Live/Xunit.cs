using System;
using System.Collections;
using System.Collections.Generic;

namespace Xunit;

/// <summary>A test method with no data.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute
{
}

/// <summary>A test method run once per data row.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TheoryAttribute : Attribute
{
}

/// <summary>A theory's rows, from a public static member of the test class.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MemberDataAttribute(string memberName) : Attribute
{
    public string MemberName { get; } = memberName;
}

/// <summary>A theory's rows of one value.</summary>
public sealed class TheoryData<T> : IEnumerable<object?[]>
{
    private readonly List<object?[]> _rows = new();

    public void Add(T value) => _rows.Add(new object?[] { value });

    public IEnumerator<object?[]> GetEnumerator() => _rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A failed assertion.</summary>
public sealed class AssertFailed(string message) : Exception(message);

/// <summary>The assertions the test kit's port suites use, with xunit's meaning.</summary>
public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
            throw new AssertFailed(message ?? "Expected true.");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition)
            throw new AssertFailed(message ?? "Expected false.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertFailed($"Expected {expected}, got {actual}.");
    }

    public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        var want = new List<T>(expected);
        var got = new List<T>(actual);
        if (want.Count != got.Count)
            throw new AssertFailed($"Expected [{string.Join(", ", want)}], got [{string.Join(", ", got)}].");
        for (var i = 0; i < want.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(want[i], got[i]))
                throw new AssertFailed($"Expected [{string.Join(", ", want)}], got [{string.Join(", ", got)}].");
    }

    public static void NotEqual<T>(T unexpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new AssertFailed($"Expected anything but {unexpected}.");
    }

    public static void Contains<T>(T item, IEnumerable<T> items)
    {
        foreach (var each in items)
            if (EqualityComparer<T>.Default.Equals(each, item))
                return;
        throw new AssertFailed($"{item} is not in the collection.");
    }

    public static void Empty(IEnumerable items)
    {
        foreach (var unused in items)
            throw new AssertFailed("Expected an empty collection.");
    }

    public static void NotEmpty(IEnumerable items)
    {
        foreach (var unused in items)
            return;
        throw new AssertFailed("Expected a non-empty collection.");
    }
}
