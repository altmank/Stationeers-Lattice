using System;
using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Slices;

namespace Lattice.Core.Sync;

/// <summary>
/// Lets a recurring problem through at a rate a log can bear. A report passes at once the first time its key (the
/// reason's code and the connection it names, if any) is seen. Repeats inside the window are counted and dropped; the
/// first report after the window passes with fact <c>more</c>, the number dropped since the last one that passed, and
/// the window starts again. At most <see cref="MaxKeys"/> keys are tracked; every key after that shares one overflow
/// key, so a peer that sends many distinct codes cannot grow the gate. A repeat costs one dictionary lookup and no
/// allocation. Main thread.
/// </summary>
internal sealed class ReportGate
{
    /// <summary>The window and key cap sync reports go through.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(300);

    public const int DefaultMaxKeys = 64;

    private readonly Action<Reason> _inner;
    private readonly IClock _clock;
    private readonly double _windowSeconds;
    private readonly Dictionary<Key, Slot> _slots = new();
    private readonly Slot _overflow = new();

    /// <summary>A gate in front of <paramref name="inner"/>; the window must be positive and at least one key tracked.</summary>
    public ReportGate(Action<Reason> inner, IClock clock, TimeSpan window, int maxKeys = DefaultMaxKeys)
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), window, "The window must be positive.");
        if (maxKeys < 1)
            throw new ArgumentOutOfRangeException(nameof(maxKeys), maxKeys, "At least one key must be tracked.");
        _inner = inner;
        _clock = clock;
        _windowSeconds = window.TotalSeconds;
        MaxKeys = maxKeys;
    }

    /// <summary>The most keys tracked apart from the overflow key.</summary>
    public int MaxKeys { get; }

    /// <summary>Keys tracked now, the overflow key not counted.</summary>
    public int Keys => _slots.Count;

    /// <summary>Takes a report; passes it on, or counts it, as the key's window says.</summary>
    public void Report(Reason why)
    {
        var key = new Key(why.Code, why.Facts.TryGetValue("connection", out var connection) ? connection : "");
        var slot = SlotOf(key);
        var now = _clock.Now.TotalSeconds;
        if (slot.Passed && now - slot.PassedAt < _windowSeconds)
        {
            slot.Suppressed++;
            return;
        }

        var suppressed = slot.Suppressed;
        slot.Passed = true;
        slot.PassedAt = now;
        slot.Suppressed = 0;
        _inner(suppressed == 0 ? why : why with { Facts = why.Facts.With("more", suppressed.ToString(CultureInfo.InvariantCulture)) });
    }

    private Slot SlotOf(Key key)
    {
        if (_slots.TryGetValue(key, out var slot))
            return slot;
        if (_slots.Count >= MaxKeys)
            return _overflow;
        slot = new Slot();
        _slots.Add(key, slot);
        return slot;
    }

    private readonly record struct Key(ReasonCode Code, string Connection);

    private sealed class Slot
    {
        public bool Passed;
        public double PassedAt;
        public int Suppressed;
    }
}
