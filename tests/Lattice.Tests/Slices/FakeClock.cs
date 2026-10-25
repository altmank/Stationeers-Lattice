using System;
using Lattice.Core.Slices;

namespace Lattice.Tests.Slices;

/// <summary>A clock that moves only when a test or a test job moves it.</summary>
internal sealed class FakeClock : IClock
{
    public TimeSpan Now { get; private set; }

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
