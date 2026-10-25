using System;
using Lattice.Api;
using Lattice.Consumer.Tests;
using Lattice.Tests.EntryPoint;
using Lattice.Tests.Runs;

namespace Lattice.Tests.Styles;

/// <summary>The renderer flows over the real styles of a host world over fakes, with a modded piece.</summary>
public sealed class RealStyleFlows : StyleFlows, IDisposable
{
    private readonly WorldBench _bench = new();
    private readonly StructureRef _built;

    public RealStyleFlows()
    {
        _built = _bench.Runs.Real("ModConduitLine", RunBench.At(40, 0, 0));
        _bench.Start();
    }

    private protected override IGhostStyles Styles => _bench.World.Styles;

    private protected override IGhostSystem System => _bench.World;

    private protected override Actor Player => WorldBench.Viewer;

    private protected override StructureRef Built => _built;

    private protected override GhostDraft Draft(int spot) => _bench.Draft("ModConduitLine", RunBench.At(3 * spot, 0, 0));

    private protected override void Frames()
    {
        _bench.Settle();
        for (var i = 0; i < 64 && _bench.World.StyleModule!.Busy; i++)
            _bench.World.Frame();
    }

    private protected override void EndFrame() => _bench.World.Frame();

    private protected override bool Shows(DraftsId drafts)
    {
        foreach (var handle in _bench.World.StyleModule!.DraftHandles)
            if (handle.Id == drafts && handle.ShowsAnything)
                return true;
        return false;
    }

    private protected override void WillPick(PickHit hit)
    {
    }

    private protected override void WillExplain(GhostId ghost, StyleStory story)
    {
    }

    private protected override void WillMatch(StyleMatch match, StructureRef structure)
    {
        if (match is StyleMatch.Family family)
            Must(System.Registries.Register(new CatalogueAddition.InFamily(structure.Piece, family.Of)));
    }

    private protected override PickRay RayOnto(Ghost ghost)
    {
        var at = Assert.IsType<Intent.Build>(ghost.Intent).Target.At;
        return Must(PickRay.Create(new WorldPoint(at.X * 0.1f, at.Y * 0.1f + 3f, at.Z * 0.1f), new WorldVector(0f, -1f, 0f), 10f));
    }

    public void Dispose() => _bench.Dispose();
}
