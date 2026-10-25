namespace Lattice.Api;

/// <summary>The one value of "nothing to return": the success of a <see cref="Result{T, TError}"/> that carries no data.</summary>
public readonly record struct Unit
{
    /// <summary>The one value.</summary>
    public static Unit Value => default;

    /// <inheritdoc />
    public override string ToString() => "()";
}
