using System;
using Lattice.Api;
using Lattice.Consumer.Tests;
using Lattice.Testing;
using Lattice.Tests.EntryPoint;
using Lattice.Tests.Runs;

namespace Lattice.Tests.Styles;

/// <summary>The renderer flows over the recording style fake beside a real host world over fakes, with a modded piece.</summary>
public sealed class FakeStyleFlows : StyleFlows, IDisposable
{
    private readonly WorldBench _bench = new();
    private readonly FakeGhostStyles _styles = new();
    private readonly StructureRef _built;

    public FakeStyleFlows()
    {
        _built = _bench.Runs.Real("ModConduitLine", RunBench.At(40, 0, 0));
        _bench.Start();
    }

    private protected override IGhostStyles Styles => _styles;

    private protected override IGhostSystem System => _bench.World;

    private protected override Actor Player => WorldBench.Viewer;

    private protected override StructureRef Built => _built;

    private protected override GhostDraft Draft(int spot) => _bench.Draft("ModConduitLine", RunBench.At(3 * spot, 0, 0));

    private protected override void Frames() => _bench.Settle();

    private protected override void EndFrame() => _styles.Frame();

    private protected override bool Shows(DraftsId drafts) =>
        _styles.DraftHandles.TryGetValue(drafts, out var handle) && handle.ShowsAnything;

    private protected override void WillPick(PickHit hit) => _styles.WillPick(hit);

    private protected override void WillExplain(GhostId ghost, StyleStory story) => _styles.WillExplain(ghost, story);

    private protected override void WillMatch(StyleMatch match, StructureRef structure) => _styles.WillMatch(match, structure);

    public void Dispose() => _bench.Dispose();
}
