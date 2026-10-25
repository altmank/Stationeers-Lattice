using System;
using Lattice.Api;
using Lattice.Consumer.Tests;
using Lattice.Tests.Runs;

namespace Lattice.Tests.EntryPoint;

/// <summary>The consumer flows on Lattice's real system: a published host world over fakes, with a modded piece.</summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class RealFlows : ConsumerFlows, IDisposable
{
    private readonly WorldBench _bench = new();

    public RealFlows()
    {
        _bench.Start().Publish();
    }

    private protected override IGhostSystem System =>
        LatticeSystem.Current.TryGet(out var current) ? current : throw new InvalidOperationException("no world");

    private protected override Actor Player => WorldBench.Viewer;

    private protected override GhostDraft Draft(int spot) => _bench.Draft("ModConduitLine", RunBench.At(3 * spot, 0, 0));

    private protected override void Frames() => _bench.Settle();

    public void Dispose() => _bench.Dispose();
}
