using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Board;
using Lattice.Core.Selectors;
using Lattice.Core.Styles;
using Lattice.Tests.Matching;
using Lattice.Tests.Selectors;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// One highlight over the ghosts a selector picks and the built structures a structure selector picks: a whole network
/// lit by one call and ended by one dispose; a refusal in either half, or a case this version does not know, refuses
/// the call and lights nothing.
/// </summary>
public sealed class NetworkHighlightTests
{
    private const string Cable = "StructureCableStraight";
    private const string Pipe = "StructurePipeStraight";

    private static readonly Style Flash = new() { Dim = Option.Some(Must(Fraction.Create(0.5f))) };

    [Fact]
    public void A_networks_ghosts_and_built_pieces_are_lit_by_one_call_and_ended_by_one_dispose()
    {
        var s = new NetworkScene();
        var network = s.Network;

        var highlight = Must(s.Bench.Styles.Highlight(new HighlightRequest(
            new HighlightTargets.PickedAndBuilt(new Selector.Network(network), new StructureSelector.Network(network)), Flash, Must(Duration.Create(5f)))));
        s.Bench.Settle();

        Assert.Equal(new StyleSource.Highlight(), s.Bench.Story(s.Joined.Id).Sources[StyleProperty.Dim]);
        Assert.Equal(new StyleSource.Default(), s.Bench.Story(s.Apart.Id).Sources[StyleProperty.Dim]);
        Assert.Equal(new HashSet<ReferenceId> { s.Low.Id, s.High.Id }, Overlaid(s.Bench));

        highlight.Dispose();
        s.Bench.Settle();

        Assert.Equal(new StyleSource.Default(), s.Bench.Story(s.Joined.Id).Sources[StyleProperty.Dim]);
        Assert.Empty(Overlaid(s.Bench));
    }

    [Fact]
    public void Either_half_alone_lights_what_it_picks()
    {
        var s = new NetworkScene();

        using (Must(s.Bench.Styles.Highlight(new HighlightRequest(
                   new HighlightTargets.PickedAndBuilt(new Selector.Network(s.Network), new StructureSelector.Refs(ValueList<StructureRef>.Empty)), Flash, Must(Duration.Create(5f))))))
        {
            s.Bench.Settle();
            Assert.Equal(new StyleSource.Highlight(), s.Bench.Story(s.Joined.Id).Sources[StyleProperty.Dim]);
            Assert.Empty(Overlaid(s.Bench));
        }

        using (Must(s.Bench.Styles.Highlight(new HighlightRequest(
                   new HighlightTargets.PickedAndBuilt(new Selector.Group(Must(GroupName.Create("nobody"))), new StructureSelector.Network(s.Network)), Flash, Must(Duration.Create(5f))))))
        {
            s.Bench.Settle();
            Assert.Equal(new StyleSource.Default(), s.Bench.Story(s.Joined.Id).Sources[StyleProperty.Dim]);
            Assert.Equal(new HashSet<ReferenceId> { s.Low.Id, s.High.Id }, Overlaid(s.Bench));
        }
    }

    [Fact]
    public void An_unknown_selector_kind_in_either_half_refuses_the_call_and_lights_nothing()
    {
        var s = new NetworkScene();

        var ghosts = Refused(s.Bench.Styles.Highlight(new HighlightRequest(
            new HighlightTargets.PickedAndBuilt(UnknownCase.Of<Selector>(), new StructureSelector.Network(s.Network)), Flash, Must(Duration.Create(5f)))));
        var built = Refused(s.Bench.Styles.Highlight(new HighlightRequest(
            new HighlightTargets.PickedAndBuilt(new Selector.Network(s.Network), UnknownCase.Of<StructureSelector>()), Flash, Must(Duration.Create(5f)))));
        s.Bench.Settle();

        Assert.Equal(ReasonCodes.UnknownKind, ghosts.Code);
        Assert.Equal(ReasonCodes.UnknownKind, built.Code);
        Assert.Equal(new StyleSource.Default(), s.Bench.Story(s.Joined.Id).Sources[StyleProperty.Dim]);
        Assert.Empty(Overlaid(s.Bench));
    }

    [Fact]
    public void A_target_case_this_version_does_not_know_is_refused_unknown_kind()
    {
        var bench = new StyleBench();
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        var why = Refused(bench.Styles.Highlight(new HighlightRequest(UnknownCase.Of<HighlightTargets>(), Flash, Must(Duration.Create(5f)))));
        bench.Settle();

        Assert.Equal(ReasonCodes.UnknownKind, why.Code);
        Assert.True(why.Facts.TryGetValue("kind", out var kind) && kind.StartsWith("Later", StringComparison.Ordinal));
        Assert.Equal(new StyleSource.Default(), bench.Story(ghost.Id).Sources[StyleProperty.Dim]);
    }

    [Fact]
    public void The_built_half_is_refused_module_missing_where_the_styles_have_no_built_world()
    {
        var styles = new StyleBench(drawing: true, library: null, paints: null, world: false);

        var why = Refused(styles.Styles.Highlight(new HighlightRequest(
            new HighlightTargets.PickedAndBuilt(new Selector.Everything(), new StructureSelector.Refs(ValueList<StructureRef>.Empty)), Flash, Must(Duration.Create(5f)))));

        Assert.Equal(ReasonCodes.ModuleMissing, why.Code);
    }

    [Fact]
    public void The_same_targets_are_equal_values()
    {
        var network = new NetworkRef(7);

        Assert.Equal(new HighlightTargets.PickedAndBuilt(new Selector.Network(network), new StructureSelector.Network(network)),
            new HighlightTargets.PickedAndBuilt(new Selector.Network(network), new StructureSelector.Network(network)));
        Assert.NotEqual(new HighlightTargets.PickedAndBuilt(new Selector.Network(network), new StructureSelector.Network(network)),
            new HighlightTargets.PickedAndBuilt(new Selector.Network(network), new StructureSelector.Network(new NetworkRef(8))));
    }

    private static Reason Refused<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static value => throw new Xunit.Sdk.XunitException($"accepted: {value}"), static why => why);

    private static HashSet<ReferenceId> Overlaid(StyleBench bench)
    {
        var found = new HashSet<ReferenceId>();
        for (var b = 0; b < bench.Styles.Plan.BatchCount; b++)
        {
            var batch = bench.Styles.Plan.BatchAt(b);
            for (var i = 0; i < batch.Count; i++)
                if (batch[i].Thing.Kind == ThingKind.Structure)
                    found.Add(batch[i].Thing.Structure.Id);
        }

        return found;
    }

    /// <summary>
    /// Two built cables on one network, a cable ghost joined to their end, and a pipe ghost apart, on a style bench
    /// whose board has the selectors module.
    /// </summary>
    private sealed class NetworkScene
    {
        public NetworkScene()
        {
            var shapes = new Shapes(Bench.T.Catalogue, Bench.T.Grid);
            var reads = new CountingWorld(Bench.World);
            var mirror = new WorldMirror(shapes, reads);
            Bench.T.Board.InstallSelectors(new GhostSelectors(shapes, Bench.T.Grid, mirror, reads));
            Low = Load(Cable, new Cell(100, 100, 100), 1001);
            High = Load(Cable, new Cell(100, 100, 105), 1002);
            Load(Pipe, new Cell(150, 100, 100), 1003);
            mirror.Start(Bench.World, reads);
            Network = Bench.World.NetworkOf(High.Id).Match(static n => n, static () => throw new Xunit.Sdk.XunitException("no network"));
            Joined = Bench.PlaceOne(Cable, new Cell(100, 100, 110));
            Apart = Bench.PlaceOne(Pipe, new Cell(100, 100, 150));
        }

        public StyleBench Bench { get; } = new();

        public NetworkRef Network { get; }

        public StructureRef Low { get; }

        public StructureRef High { get; }

        public Ghost Joined { get; }

        public Ghost Apart { get; }

        private StructureRef Load(string piece, Cell at, int id)
        {
            var info = new StructureInfo(new StructureRef(new ReferenceId(id), Bench.T.Catalogue.Named(piece).Key, at), default, new BuildState.Built(),
                Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);
            Bench.World.Load(info);
            return info.Ref;
        }
    }
}
