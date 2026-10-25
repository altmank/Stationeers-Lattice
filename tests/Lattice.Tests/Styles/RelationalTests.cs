using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// Relational selectors in a rule: picked by the board as a whole, picked afresh in drawing slices when
/// the board or the built world moves, and only ghosts whose membership changed take new signatures.
/// </summary>
public sealed class RelationalTests
{
    private static readonly Selector OnNetwork = new Selector.Network(new NetworkRef(7));

    [Fact]
    public void A_network_rule_follows_the_built_worlds_answer_and_re_signatures_only_the_ghosts_that_moved()
    {
        var bench = new StyleBench();
        var selectors = new FakeSelectors();
        bench.T.Board.InstallSelectors(selectors);
        var drafts = new GhostDraft[10];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = bench.Draft("StructureFrame", new Cell(10 + 20 * i, 10, 10));
        var ghosts = bench.Place(drafts).Added;
        selectors.Picked = new HashSet<GhostId> { ghosts[0].Id, ghosts[1].Id };
        bench.Add(LayerOf("network", StyleBand.View, 0, Rule(new StyleMatch.Picks(OnNetwork), Edge(LineStyle.Solid, LibraryPalette.Next))));
        bench.Settle();
        Assert.IsType<EdgeLook.Line>(bench.Story(ghosts[1].Id).Resolved.Edge.Reduce(new EdgeLook.None()));
        Assert.IsType<EdgeLook.None>(bench.Story(ghosts[2].Id).Resolved.Edge.Reduce(new EdgeLook.None()));
        var before = bench.Styles.Counters.Resignatures;

        selectors.Picked = new HashSet<GhostId> { ghosts[1].Id, ghosts[2].Id };
        selectors.Revision++;
        bench.Settle();

        Assert.Equal(2, bench.Styles.Counters.Resignatures - before);
        Assert.IsType<EdgeLook.None>(bench.Story(ghosts[0].Id).Resolved.Edge.Reduce(new EdgeLook.None()));
        Assert.IsType<EdgeLook.Line>(bench.Story(ghosts[2].Id).Resolved.Edge.Reduce(new EdgeLook.None()));
    }

    [Fact]
    public void A_pick_filter_with_a_network_reads_the_boards_answer_as_of_now()
    {
        var bench = new StyleBench();
        var selectors = new FakeSelectors();
        bench.T.Board.InstallSelectors(selectors);
        var low = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        var high = bench.PlaceOne("StructureFrame", new Cell(10, 30, 10));
        selectors.Picked = new HashSet<GhostId> { low.Id };
        var ray = Must(PickRay.Create(new WorldPoint(1f, 10f, 1f), new WorldVector(0f, -1f, 0f), 20f));
        var options = PickOptions.Default with { Only = Option.Some<StyleMatch>(new StyleMatch.Picks(OnNetwork)) };

        var hit = Assert.IsType<PickHit.OnGhost>(bench.Styles.Pick(ray, options).Match(static h => h, static () => null!));
        Assert.Equal(low.Id, hit.Ghost);

        selectors.Picked = new HashSet<GhostId> { high.Id };
        selectors.Revision++;
        hit = Assert.IsType<PickHit.OnGhost>(bench.Styles.Pick(ray, options).Match(static h => h, static () => null!));
        Assert.Equal(high.Id, hit.Ghost);
    }

    private sealed class FakeSelectors : IGhostSelectors
    {
        public long Revision { get; set; }

        public HashSet<GhostId> Picked { get; set; } = new();

        public Result<HashSet<GhostId>, Reason> Pick(BoardIndex index, Selector selector) =>
            Result<HashSet<GhostId>, Reason>.Ok(new HashSet<GhostId>(Picked));
    }
}
