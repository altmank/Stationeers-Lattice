using System;

namespace Lattice.Core.Ports;

/// <summary>Real time, for when a ghost was placed and a change applied. Injected so tests fix it.</summary>
internal interface ITimeSource
{
    DateTimeOffset Now { get; }
}

/// <summary>The machine's clock in UTC.</summary>
internal sealed class SystemTime : ITimeSource
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}
