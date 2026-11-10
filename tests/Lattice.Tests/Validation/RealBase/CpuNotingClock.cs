using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;

namespace Lattice.Tests.Validation.RealBase;

/// <summary>
/// The game's stopwatch frame clock, read as it is, that also notes the main thread's CPU cycle count at each of the
/// main thread's reads in a frame. The scheduler's time for a kind of work is the difference of two of its reads (the
/// queue's start and its last step's end), so the reads that differ by exactly that time bound the work, and the
/// cycles between them are its own thread CPU. Checking's queue runs first in the frame, so the earliest such pair is
/// taken. Main-thread reads are kept without allocating, up to a frame's worth.
/// </summary>
internal sealed class CpuNotingClock(ThreadCpu cpu) : IClock
{
    private const int Room = 1 << 15;
    private readonly StopwatchClock _wall = new();
    private readonly long[] _ticks = new long[Room];
    private readonly ulong[] _cycles = new ulong[Room];
    private int _count;
    private int _main = -1;

    public TimeSpan Now
    {
        get
        {
            var now = _wall.Now;
            if (Environment.CurrentManagedThreadId == _main && _count < Room)
            {
                _ticks[_count] = now.Ticks;
                _cycles[_count++] = cpu.Now();
            }

            return now;
        }
    }

    /// <summary>Starts noting a frame's reads, on the calling thread (the main thread).</summary>
    public void StartFrame()
    {
        _main = Environment.CurrentManagedThreadId;
        _count = 0;
    }

    /// <summary>The CPU time between each two consecutive main-thread reads of this frame, in milliseconds.</summary>
    public List<double> Gaps()
    {
        var gaps = new List<double>(Math.Max(0, _count - 1));
        if (cpu.Measures)
            for (var i = 1; i < _count; i++)
                gaps.Add(cpu.Between(_cycles[i - 1], _cycles[i]).TotalMilliseconds);
        return gaps;
    }

    /// <summary>
    /// The main thread's CPU over the first span of this frame's reads that lasts exactly <paramref name="spent"/>; none
    /// when the thread's CPU is not measured, the reads overflowed, or no such span was read.
    /// </summary>
    public Option<TimeSpan> CpuOver(TimeSpan spent)
    {
        if (!cpu.Measures || _count == Room)
            return Option<TimeSpan>.None;
        if (spent <= TimeSpan.Zero)
            return Option.Some(TimeSpan.Zero);
        for (var start = 0; start < _count - 1; start++)
        {
            var end = Array.BinarySearch(_ticks, start + 1, _count - start - 1, _ticks[start] + spent.Ticks);
            if (end > start)
                return Option.Some(cpu.Between(_cycles[start], _cycles[end]));
        }

        return Option<TimeSpan>.None;
    }
}
