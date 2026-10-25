using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;

namespace Lattice.Core.Consequences;

/// <summary>A sequence of steps (an iterator yielding once per small piece of work) as resumable sliced work.</summary>
internal sealed class StepsWork(IEnumerable<Unit> steps) : ISlicedWork
{
    private readonly IEnumerator<Unit> _steps = steps.GetEnumerator();

    /// <summary>Runs the next step; done when the sequence has no more.</summary>
    public StepOutcome Step() => _steps.MoveNext() ? StepOutcome.More : StepOutcome.Done;
}
