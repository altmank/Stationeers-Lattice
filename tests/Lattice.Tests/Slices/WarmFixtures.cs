using System.Collections.Generic;

namespace Lattice.Tests.Slices.Warm;

public enum Colour
{
    Red,
    Green,
}

public sealed class Box<T>
{
    public T? Value { get; set; }

    public T? Get() => Value;
}

public sealed class Holder
{
    public Box<int> Valued() => new();

    public Box<string> Shared() => new();

    public Box<Colour> Enumerated() => new();

    public int? Optional() => null;

    public List<KeyValuePair<int, string>> Pairs() => [];
}
