using System;
using Lattice.Core.Slices;

namespace Lattice.Tests.Slices;

/// <summary>A clock that moves only when a test or a test job moves it.</summary>
internal sealed class FakeClock : IClock
{
    private TimeSpan _now;

    /// <summary>Time every read of <see cref="Now"/> adds, so work that never advances the clock itself still takes time.</summary>
    public TimeSpan Tick { get; set; }

    public TimeSpan Now
    {
        get => _now += Tick;
        private set => _now = value;
    }

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A job of a fixed number of steps, each costing a fixed time on the fake clock.</summary>
internal sealed class CountingJob(FakeClock clock, int steps, TimeSpan stepCost, Action<CountingJob>? onStep = null) : ISlicedWork
{
    public int Done { get; private set; }

    public StepOutcome Step()
    {
        clock.Advance(stepCost);
        Done++;
        onStep?.Invoke(this);
        return Done == steps ? StepOutcome.Done : StepOutcome.More;
    }
}
