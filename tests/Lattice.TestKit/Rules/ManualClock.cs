using System;
using Lattice.Core.Slices;

namespace Lattice.TestKit.Rules;

/// <summary>A clock that stands still until a test moves it, so time limits are tested without sleeping.</summary>
internal sealed class ManualClock : IClock
{
    public TimeSpan Now { get; private set; }

    public void Advance(TimeSpan by) => Now += by;
}
