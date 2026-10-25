using System;

namespace Lattice.Api;

/// <summary>A value that may be absent. <c>default</c> is None.</summary>
public readonly record struct Option<T> where T : notnull
{
    private readonly T? _value;
    private readonly bool _isSome;

    private Option(T value)
    {
        _value = value;
        _isSome = true;
    }

    /// <summary>The absent value.</summary>
    public static Option<T> None => default;

    /// <summary>A present value; a null (which nullable annotations already flag) gives <see cref="None"/>.</summary>
    public static Option<T> Some(T value) => value is null ? None : new(value);

    /// <summary>Whether a value is present.</summary>
    public bool IsSome => _isSome;

    /// <summary>Whether no value is present.</summary>
    public bool IsNone => !_isSome;

    /// <summary>The allocation-free way to read the value inside a loop.</summary>
    public bool TryGet(out T value)
    {
        value = _value!;
        return _isSome;
    }

    /// <summary>The value transformed, if present.</summary>
    public Option<TResult> Map<TResult>(Func<T, TResult> map) where TResult : notnull =>
        _isSome ? Option<TResult>.Some(map(_value!)) : Option<TResult>.None;

    /// <summary>The option the value leads to, if present.</summary>
    public Option<TResult> Bind<TResult>(Func<T, Option<TResult>> bind) where TResult : notnull =>
        _isSome ? bind(_value!) : Option<TResult>.None;

    /// <summary>The value if present and the predicate holds.</summary>
    public Option<T> Filter(Func<T, bool> predicate) =>
        _isSome && predicate(_value!) ? this : None;

    /// <summary>One of two answers, for present and absent.</summary>
    public TResult Match<TResult>(Func<T, TResult> some, Func<TResult> none) =>
        _isSome ? some(_value!) : none();

    /// <summary>The value, or the fallback.</summary>
    public T Reduce(T whenNone) => _isSome ? _value! : whenNone;

    /// <summary>The value, or the fallback computed.</summary>
    public T Reduce(Func<T> whenNone) => _isSome ? _value! : whenNone();

    /// <inheritdoc />
    public override string ToString() => _isSome ? $"Some({_value})" : "None";
}

/// <summary>Builds options with the type inferred.</summary>
public static class Option
{
    /// <summary>A present value.</summary>
    public static Option<T> Some<T>(T value) where T : notnull => Option<T>.Some(value);

    /// <summary>The absent value.</summary>
    public static Option<T> None<T>() where T : notnull => Option<T>.None;
}
