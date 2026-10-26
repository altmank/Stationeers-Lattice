using System;
using Lattice.Api;

namespace Lattice.Core.EntryPoint;

/// <summary>A copy of Lattice's plugin: its version and the file its Lattice.Game loaded from.</summary>
internal sealed record LatticeCopy(string Version, string Location);

/// <summary>What a starting plugin does: run Lattice, or stand down because another copy already runs it.</summary>
internal abstract record CopyStart
{
    private CopyStart() { }

    /// <summary>This copy is the first to start: it runs Lattice.</summary>
    public sealed record Owns : CopyStart;

    /// <summary>Another copy started first and runs Lattice; this one does nothing.</summary>
    public sealed record StandsDown(LatticeCopy Running) : CopyStart;
}

/// <summary>The process-wide mark of the copy that runs Lattice. Main thread.</summary>
internal interface IRunningCopySlot
{
    Option<LatticeCopy> Read();

    void Write(LatticeCopy copy);
}

/// <summary>
/// The running copy kept in an AppDomain data slot, as a <c>string[]</c> of version and location. Every copy of
/// Lattice reads the same slot, whichever copy of each Lattice assembly it binds to: a static in a Lattice assembly can
/// be two statics when two copies load, and both the slot and its value are the runtime's own types.
/// </summary>
internal sealed class AppDomainCopySlot(string key) : IRunningCopySlot
{
    /// <summary>The slot every copy of Lattice uses. Never changes: copies of every version must find each other.</summary>
    public const string Key = "net.xceled.stationeers.lattice.running";

    public Option<LatticeCopy> Read() =>
        AppDomain.CurrentDomain.GetData(key) is string[] { Length: 2 } held
            ? Option.Some(new LatticeCopy(held[0], held[1]))
            : Option<LatticeCopy>.None;

    public void Write(LatticeCopy copy) => AppDomain.CurrentDomain.SetData(key, new[] { copy.Version, copy.Location });
}

/// <summary>
/// Decides, at plugin start, which start of Lattice's plugin runs Lattice: the first in the process. A Workshop copy
/// and a local copy both enabled start the plugin twice, from two files or (when the second copy's assemblies resolve
/// to the ones already loaded) twice from the same file; every later start stands down before it touches the game.
/// No process starts the plugin twice otherwise.
/// </summary>
internal static class PluginStart
{
    /// <summary>Owns when no start has claimed yet, and marks this one; else stands down, whatever file it runs from.</summary>
    public static CopyStart Claim(IRunningCopySlot slot, LatticeCopy me)
    {
        if (slot.Read().TryGet(out var running))
            return new CopyStart.StandsDown(running);
        slot.Write(me);
        return new CopyStart.Owns();
    }

    /// <summary>The one line a copy that stands down logs.</summary>
    public static string StandDownLine(LatticeCopy me, LatticeCopy running) =>
        SameFile(me.Location, running.Location)
            ? $"Lattice is enabled twice: Lattice {running.Version} from {running.Location} runs; its plugin started again (the second copy's files resolved to these) and this start does nothing. Keep one copy of Lattice enabled."
            : $"Lattice is enabled twice: Lattice {running.Version} from {running.Location} runs; this copy, Lattice {me.Version} from {me.Location}, does nothing. Keep one copy of Lattice enabled.";

    private static bool SameFile(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
