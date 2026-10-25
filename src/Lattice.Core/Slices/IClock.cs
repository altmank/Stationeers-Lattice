using System;
using System.Diagnostics;

namespace Lattice.Core.Slices;

/// <summary>Monotonic time the frame budgets are measured against. Reading it allocates nothing.</summary>
internal interface IClock
{
    /// <summary>Time since an arbitrary fixed start; never goes back.</summary>
    TimeSpan Now { get; }
}

/// <summary>The real clock: a <see cref="Stopwatch"/> started with the clock.</summary>
internal sealed class StopwatchClock : IClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public TimeSpan Now => _watch.Elapsed;
}
