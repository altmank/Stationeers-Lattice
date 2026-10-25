using System;

namespace Lattice.TestKit.World;

/// <summary>
/// A subscriber array copied on every change: a raise walks the array it started with, so subscribing or
/// unsubscribing during a raise is safe and takes effect from the next raise.
/// </summary>
internal sealed class Subscribers<THandler> where THandler : Delegate
{
    private THandler[] _handlers = [];

    public THandler[] Current => _handlers;

    public void Add(THandler? handler)
    {
        if (handler is null)
            return;
        var next = new THandler[_handlers.Length + 1];
        Array.Copy(_handlers, next, _handlers.Length);
        next[^1] = handler;
        _handlers = next;
    }

    /// <summary>Removes the last subscription of the handler, as a multicast delegate does.</summary>
    public void Remove(THandler? handler)
    {
        if (handler is null)
            return;
        var at = Array.LastIndexOf(_handlers, handler);
        if (at < 0)
            return;
        var next = new THandler[_handlers.Length - 1];
        Array.Copy(_handlers, 0, next, 0, at);
        Array.Copy(_handlers, at + 1, next, at, _handlers.Length - at - 1);
        _handlers = next;
    }

    public void Clear() => _handlers = [];
}
