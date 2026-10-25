using System;

namespace Lattice.Api;

/// <summary>
/// The outcome of an operation whose failure is expected: a value, or an error saying why not.
/// A <c>default</c> Result was never created by <see cref="Ok"/> or <see cref="Error"/>; reading it is a bug and throws.
/// </summary>
public readonly record struct Result<T, TError> where T : notnull where TError : notnull
{
    private const byte Unset = 0;
    private const byte IsOkState = 1;
    private const byte IsErrorState = 2;

    private readonly T? _value;
    private readonly TError? _error;
    private readonly byte _state;

    private Result(T value)
    {
        _value = value;
        _error = default;
        _state = IsOkState;
    }

    private Result(TError error)
    {
        _value = default;
        _error = error;
        _state = IsErrorState;
    }

    /// <summary>A success; null is a bug and throws.</summary>
    public static Result<T, TError> Ok(T value) =>
        value is null ? throw new ArgumentNullException(nameof(value)) : new(value);

    /// <summary>A failure; null is a bug and throws.</summary>
    public static Result<T, TError> Error(TError error) =>
        error is null ? throw new ArgumentNullException(nameof(error)) : new(error);

    /// <summary>Whether it succeeded.</summary>
    public bool IsOk => State == IsOkState;

    /// <summary>Whether it failed.</summary>
    public bool IsError => State == IsErrorState;

    /// <summary>The allocation-free way to read the value inside a loop.</summary>
    public bool TryGet(out T value)
    {
        value = _value!;
        return State == IsOkState;
    }

    /// <summary>The allocation-free way to read the error.</summary>
    public bool TryGetError(out TError error)
    {
        error = _error!;
        return State == IsErrorState;
    }

    /// <summary>The value transformed, if it succeeded.</summary>
    public Result<TResult, TError> Map<TResult>(Func<T, TResult> map) where TResult : notnull =>
        State == IsOkState ? Result<TResult, TError>.Ok(map(_value!)) : Result<TResult, TError>.Error(_error!);

    /// <summary>The result the value leads to, if it succeeded.</summary>
    public Result<TResult, TError> Bind<TResult>(Func<T, Result<TResult, TError>> bind) where TResult : notnull =>
        State == IsOkState ? bind(_value!) : Result<TResult, TError>.Error(_error!);

    /// <summary>The error translated, if it failed.</summary>
    public Result<T, TOther> MapError<TOther>(Func<TError, TOther> map) where TOther : notnull =>
        State == IsOkState ? Result<T, TOther>.Ok(_value!) : Result<T, TOther>.Error(map(_error!));

    /// <summary>One of two answers, for success and failure.</summary>
    public TResult Match<TResult>(Func<T, TResult> ok, Func<TError, TResult> error) =>
        State == IsOkState ? ok(_value!) : error(_error!);

    /// <summary>The value if it succeeded; the error is dropped.</summary>
    public Option<T> ToOption() => State == IsOkState ? Option<T>.Some(_value!) : Option<T>.None;

    /// <inheritdoc />
    public override string ToString() => State == IsOkState ? $"Ok({_value})" : $"Error({_error})";

    private byte State =>
        _state == Unset ? throw new InvalidOperationException("A default Result was read; create one with Ok or Error.") : _state;
}
