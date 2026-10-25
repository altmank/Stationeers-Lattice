using System;

namespace Lattice.TestKit.Allocations;

/// <summary>
/// Measures what a call allocates on the calling thread: the call runs often enough to warm up (JIT,
/// lazy caches, the runtime's tier-up after 30 calls), then <c>repeat</c> times between two reads of
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
/// </summary>
/// <remarks>
/// The runtime itself may allocate on the calling thread while it swaps in recompiled code, at a moment set by a
/// background timer. <see cref="AssertNone"/> therefore measures up to a few windows and keeps the smallest: a path
/// that allocates per call allocates in every window, the runtime's one-off work in at most one.
/// </remarks>
internal static class Allocations
{
    private const int Windows = 3;

    /// <summary>Bytes allocated over <paramref name="repeat"/> calls after warm-up.</summary>
    public static long Of(Action call, int repeat = 100, int warmUp = 64)
    {
        for (var i = 0; i < warmUp; i++)
            call();
        return Window(call, repeat);
    }

    /// <summary>
    /// The fewest bytes <paramref name="repeat"/> calls allocated in any of up to a few windows after warm-up; 0 as
    /// soon as one window allocates nothing.
    /// </summary>
    public static long Least(Action call, int repeat = 100)
    {
        var least = Of(call, repeat);
        for (var window = 1; window < Windows && least > 0; window++)
            least = Math.Min(least, Window(call, repeat));
        return least;
    }

    /// <summary>The message for a call that allocated in every measured window.</summary>
    public static string Allocated(string what, long bytes, int repeat) =>
        $"{what} allocated {bytes} bytes over {repeat} calls in each of {Windows} windows; expected none.";

    /// <summary>Fails the test when repeated calls allocate in every measured window.</summary>
    public static void AssertNone(Action call, string what, int repeat = 100)
    {
        var least = Least(call, repeat);
        if (least != 0)
            throw new KitCheckFailed(Allocated(what, least, repeat));
    }

    private static long Window(Action call, int repeat)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < repeat; i++)
            call();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
