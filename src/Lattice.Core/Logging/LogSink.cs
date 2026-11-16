using System;

namespace Lattice.Core.Logging;

/// <summary>
/// Where Lattice's log lines go, one delegate per level: <paramref name="Info"/> for what Lattice decides and reports,
/// <paramref name="Warning"/> for a degraded state or a refusal, <paramref name="Error"/> for a failure or a feature
/// turned off. The call site names the level, so no string is built that was not built before.
/// </summary>
internal sealed record LogSink(Action<string> Info, Action<string> Warning, Action<string> Error)
{
    /// <summary>A sink that drops every line.</summary>
    public static LogSink Silent { get; } = To(static _ => { });

    /// <summary>A sink that sends every level to <paramref name="all"/>.</summary>
    public static LogSink To(Action<string> all) => new(all, all, all);

    /// <summary>A single-action log is a sink that takes every level.</summary>
    public static implicit operator LogSink(Action<string> all) => To(all);
}
