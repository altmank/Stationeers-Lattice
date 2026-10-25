using System;
using Lattice.Api;
using Lattice.Render.Materials;

namespace Lattice.Render.Drawing;

/// <summary>
/// Registrations by key for one world, copy-on-change: a reader in the middle of a frame or a callback sees one
/// whole list. A key is held at most once; disposing a registration frees its key.
/// </summary>
internal sealed class Registry<TKey, TValue>(string fact, Func<TKey, string> name, Action<TKey>? changed = null)
    where TKey : IEquatable<TKey>
{
    private (TKey Key, TValue Value, object Token)[] _held = Array.Empty<(TKey, TValue, object)>();

    public int Count => _held.Length;

    public bool TryGet(TKey key, out TValue value)
    {
        var held = _held;
        for (var i = 0; i < held.Length; i++)
            if (held[i].Key.Equals(key))
            {
                value = held[i].Value;
                return true;
            }

        value = default!;
        return false;
    }

    /// <summary>Refused (<c>already-registered</c>) for a key already held.</summary>
    public Result<IDisposable, Reason> Register(TKey key, TValue value)
    {
        var held = _held;
        for (var i = 0; i < held.Length; i++)
            if (held[i].Key.Equals(key))
                return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.AlreadyRegistered, RenderCodes.Rule, ValueMap.Of(fact, name(key))));
        var registration = new Registration(this, key);
        var next = new (TKey, TValue, object)[held.Length + 1];
        Array.Copy(held, next, held.Length);
        next[held.Length] = (key, value, registration);
        _held = next;
        changed?.Invoke(key);
        return Result<IDisposable, Reason>.Ok(registration);
    }

    /// <summary>Drops every registration (world end).</summary>
    public void End() => _held = Array.Empty<(TKey, TValue, object)>();

    private void Remove(TKey key, Registration registration)
    {
        var held = _held;
        var at = -1;
        for (var i = 0; i < held.Length; i++)
            if (ReferenceEquals(held[i].Token, registration))
                at = i;
        if (at < 0)
            return;
        var next = new (TKey, TValue, object)[held.Length - 1];
        Array.Copy(held, 0, next, 0, at);
        Array.Copy(held, at + 1, next, at, held.Length - at - 1);
        _held = next;
        changed?.Invoke(key);
    }

    private sealed class Registration(Registry<TKey, TValue> owner, TKey key) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            owner.Remove(key, this);
        }
    }
}
