namespace Lattice.Tests.Slices.Warm;

public sealed class Box<T>
{
    public T? Value { get; set; }

    public T? Get() => Value;
}

public sealed class Holder
{
    public Box<int> Valued() => new();

    public Box<string> Shared() => new();
}
