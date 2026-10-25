using System;

namespace Lattice.Testing;

/// <summary>
/// What a call allocates on the calling thread (Lattice's steady-state reads allocate nothing). The call
/// runs often enough to warm up (JIT, lazy caches, tier-up), then <c>repeat</c> times between two reads of
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>. The runtime may allocate once on the thread while it swaps in
/// recompiled code, so <see cref="AssertNone"/> measures up to three windows and keeps the smallest.
/// </summary>
public static class Allocations
{
    /// <summary>Bytes allocated over <paramref name="repeat"/> calls after warm-up.</summary>
    public static long Measure(Action call, int repeat = 100) => TestKit.Allocations.Allocations.Of(call, repeat);

    /// <summary>
    /// Throws <see cref="AllocationException"/> naming <paramref name="what"/> and the bytes when <paramref name="repeat"/>
    /// calls allocate in every measured window; any test framework reports it as a failure.
    /// </summary>
    public static void AssertNone(Action call, string what, int repeat = 100)
    {
        var least = TestKit.Allocations.Allocations.Least(call, repeat);
        if (least != 0)
            throw new AllocationException(what, least, TestKit.Allocations.Allocations.Allocated(what, least, repeat));
    }
}

/// <summary>A call that allocated where none was expected (<see cref="Allocations.AssertNone"/>).</summary>
public sealed class AllocationException : Exception
{
    internal AllocationException(string call, long bytes, string message)
        : base(message)
    {
        Call = call;
        Bytes = bytes;
    }

    /// <summary>What the test called the measured call.</summary>
    public string Call { get; }

    /// <summary>The fewest bytes the repeated calls allocated in one window.</summary>
    public long Bytes { get; }
}
