using System;
using Lattice.Api;
using Lattice.Consumer.Tests;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>The consumer flows on a test world: Lattice's real system, with a modded piece.</summary>
public sealed class HostedConsumerFlows : ConsumerFlows, IDisposable
{
    private readonly TestWorld _world = Hosted.Start(WithConduit);

    private protected override IGhostSystem System =>
        LatticeSystem.Current.TryGet(out var current) ? current : throw new InvalidOperationException("no world");

    private protected override Actor Player => _world.Viewer;

    private protected override GhostDraft Draft(int spot) => Hosted.Draft(Conduit.Key, At(3 * spot));

    private protected override void Frames() => Must(_world.Settle());

    public void Dispose() => _world.Dispose();
}

/// <summary>The renderer flows over the recording style fake beside a test world, with a modded piece.</summary>
public sealed class HostedStyleFlows : StyleFlows, IDisposable
{
    private readonly TestWorld _world = Hosted.Start(WithConduit);
    private readonly FakeGhostStyles _styles = new();
    private readonly StructureRef _built;

    public HostedStyleFlows() => _built = Must(_world.Build(Conduit.Key, At(40), default));

    private protected override IGhostStyles Styles => _styles;

    private protected override IGhostSystem System => _world.System;

    private protected override Actor Player => _world.Viewer;

    private protected override StructureRef Built => _built;

    private protected override GhostDraft Draft(int spot) => Hosted.Draft(Conduit.Key, At(3 * spot));

    private protected override void Frames() => Must(_world.Settle());

    private protected override void EndFrame() => _styles.Frame();

    private protected override bool Shows(DraftsId drafts) =>
        _styles.DraftHandles.TryGetValue(drafts, out var handle) && handle.ShowsAnything;

    private protected override void WillPick(PickHit hit) => _styles.WillPick(hit);

    private protected override void WillExplain(GhostId ghost, StyleStory story) => _styles.WillExplain(ghost, story);

    private protected override void WillMatch(StyleMatch match, StructureRef structure) => _styles.WillMatch(match, structure);

    public void Dispose() => _world.Dispose();
}
