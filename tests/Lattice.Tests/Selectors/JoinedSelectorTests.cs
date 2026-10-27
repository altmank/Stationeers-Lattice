using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit;
using static Lattice.Tests.Selectors.TestSelect;

namespace Lattice.Tests.Selectors;

/// <summary>
/// <see cref="Selector.Joined"/>: the planned run pieces joined end to end to the run ghost at a cell, by its ends'
/// kinds, through junctions, stopping at devices and at built pieces. Test cells are hand-catalogue values, not game claims.
/// </summary>
public sealed class JoinedSelectorTests
{
    private const string Cable = "StructureCableStraight";
    private const string Junction = "StructureCableJunction";
    private const string Pipe = "StructurePipeStraight";

    /// <summary>A modded monorail: a kit the game does not have, a network kind Lattice does not model, a straight and a junction.</summary>
    private const string Monorail =
        "piece ModMonorailStraight kit=ModKitMonorail option=0 grid=small\n" +
        "  step air=no\n  end 0 0 0 z+ mod:Monorail:4096\n  end 0 0 0 z- mod:Monorail:4096\n  cell 0 0 0 rail\n" +
        "piece ModMonorailJunction kit=ModKitMonorail option=1 grid=small\n" +
        "  step air=no\n  end 0 0 0 x- mod:Monorail:4096\n  end 0 0 0 z+ mod:Monorail:4096\n  end 0 0 0 z- mod:Monorail:4096\n  cell 0 0 0 rail\n";

    /// <summary>A planned cable tee at (100, 100, 100): two pieces up z, two down z, three along x towards x-.</summary>
    private sealed class Tee
    {
        public Tee(TestSelect s)
        {
            Junction = s.Build(JoinedSelectorTests.Junction, At(100, 100, 100));
            Up = Set(s.Build(Cable, At(100, 100, 105)), s.Build(Cable, At(100, 100, 110)));
            Down = Set(s.Build(Cable, At(100, 100, 95)), s.Build(Cable, At(100, 100, 90)));
            Side = Set(s.Build(Cable, At(95, 100, 100), s.AlongX), s.Build(Cable, At(90, 100, 100), s.AlongX), s.Build(Cable, At(85, 100, 100), s.AlongX));
            All = new(Up);
            All.UnionWith(Down);
            All.UnionWith(Side);
            All.Add(Junction);
        }

        public GhostId Junction { get; }

        public HashSet<GhostId> Up { get; }

        public HashSet<GhostId> Down { get; }

        public HashSet<GhostId> Side { get; }

        public HashSet<GhostId> All { get; }
    }

    [Theory]
    [InlineData(100, 100, 110)]
    [InlineData(100, 100, 90)]
    [InlineData(85, 100, 100)]
    [InlineData(100, 100, 100)]
    public void From_any_arm_of_a_planned_tee_it_picks_every_piece_the_junction_included(int x, int y, int z)
    {
        var s = new TestSelect();
        var tee = new Tee(s);

        Assert.Equal(tee.All, s.Pick(new Selector.Joined(At(x, y, z))));
    }

    [Fact]
    public void Run_through_from_the_same_cell_still_stops_at_the_junction()
    {
        var s = new TestSelect();
        var tee = new Tee(s);

        Assert.Equal(tee.Up, s.Pick(new Selector.RunThrough(At(100, 100, 110))));
        Assert.Equal(tee.Side, s.Pick(new Selector.RunThrough(At(85, 100, 100))));
    }

    [Fact]
    public void A_planned_pipe_crossing_the_cable_network_in_its_cells_is_not_picked()
    {
        var s = new TestSelect();
        var tee = new Tee(s);
        var pipe = Set(s.Build(Pipe, At(95, 100, 105), s.AlongX), s.Build(Pipe, At(100, 100, 105), s.AlongX), s.Build(Pipe, At(105, 100, 105), s.AlongX));

        Assert.Equal(tee.All, s.Pick(new Selector.Joined(At(100, 100, 110))));
        Assert.Equal(pipe, s.Pick(new Selector.Joined(At(105, 100, 105))));
    }

    [Fact]
    public void Each_run_piece_in_a_shared_cell_starts_a_walk_by_its_own_kinds()
    {
        var s = new TestSelect();
        var tee = new Tee(s);
        var pipe = Set(s.Build(Pipe, At(95, 100, 105), s.AlongX), s.Build(Pipe, At(100, 100, 105), s.AlongX), s.Build(Pipe, At(105, 100, 105), s.AlongX));
        var both = new HashSet<GhostId>(tee.All);
        both.UnionWith(pipe);

        Assert.Equal(both, s.Pick(new Selector.Joined(At(100, 100, 105))));
    }

    [Fact]
    public void A_heavy_cable_joined_to_a_normal_cable_is_picked_by_their_shared_kinds()
    {
        var s = new TestSelect();
        var tee = new Tee(s);
        var heavy = s.Build("StructureCableStraightH", At(100, 100, 85));
        var beyond = s.Build(Cable, At(100, 100, 80));
        tee.All.Add(heavy);
        tee.All.Add(beyond);

        Assert.Equal(tee.All, s.Pick(new Selector.Joined(At(100, 100, 110))));
        Assert.Equal(tee.All, s.Pick(new Selector.Joined(At(100, 100, 85))));
    }

    [Fact]
    public void It_stops_at_a_device_and_neither_the_device_nor_the_cable_beyond_it_is_picked()
    {
        var s = new TestSelect();
        var junction = s.Build(Junction, At(100, 100, 100));
        var up = s.Build(Cable, At(100, 100, 105));
        var side = s.Build(Cable, At(95, 100, 100), s.AlongX);
        var device = s.Build("StructureAreaPowerControl", At(90, 100, 100));
        var beyond = s.Build(Cable, At(85, 100, 100), s.AlongX);

        Assert.Equal(Set(junction, up, side), s.Pick(new Selector.Joined(At(100, 100, 105))));
        Assert.Empty(s.Pick(new Selector.Joined(At(90, 100, 100))));
        Assert.Equal(Set(beyond), s.Pick(new Selector.Joined(At(85, 100, 100))));
        Assert.DoesNotContain(device, s.Pick(new Selector.Joined(At(95, 100, 100))));
    }

    [Fact]
    public void It_stops_at_a_built_piece_and_reads_no_world()
    {
        var s = new TestSelect();
        s.Built(Cable, At(100, 100, 95));
        s.Start();
        var planned = Set(s.Build(Cable, At(100, 100, 100)), s.Build(Cable, At(100, 100, 105)));
        var pastBuilt = Set(s.Build(Cable, At(100, 100, 90)), s.Build(Cable, At(100, 100, 85)));
        var reads = s.Reads.Reads;
        var scans = s.Reads.Scans;

        Assert.Equal(planned, s.Pick(new Selector.Joined(At(100, 100, 105))));
        Assert.Equal(pastBuilt, s.Pick(new Selector.Joined(At(100, 100, 85))));
        Assert.Equal(reads, s.Reads.Reads);
        Assert.Equal(scans, s.Reads.Scans);
    }

    [Fact]
    public void A_removal_or_finish_ghost_at_the_cell_and_an_empty_cell_pick_nothing()
    {
        var s = new TestSelect();
        var removed = s.Built(Cable, At(100, 100, 95));
        var finished = s.Built(Cable, At(100, 100, 90));
        s.Start();
        s.Build(Cable, At(100, 100, 100));
        s.Place(new Intent.Remove(removed));
        var paint = TestKitIds.Must(Finish.Create(Option.Some(TestKitIds.Must(PaintColour.Create(3))), Option<ThingName>.None, Option<Setting>.None));
        s.Place(new Intent.FinishOnly(finished, paint));

        Assert.Empty(s.Pick(new Selector.Joined(At(100, 100, 95))));
        Assert.Empty(s.Pick(new Selector.Joined(At(100, 100, 90))));
        Assert.Empty(s.Pick(new Selector.Joined(At(300, 300, 300))));
    }

    [Fact]
    public void A_swap_ghost_joined_to_the_planned_run_is_picked_and_the_walk_passes_it()
    {
        var s = new TestSelect();
        var built = s.Built(Cable, At(100, 100, 95));
        s.Start();
        var planned = Set(s.Build(Cable, At(100, 100, 100)), s.Build(Cable, At(100, 100, 105)));
        var swap = s.Place(new Intent.Swap(built, new Placement(s.T.Catalogue.Named("StructureCableStraightH").Key, At(100, 100, 95), default), Option<Finish>.None));
        var beyond = s.Build(Cable, At(100, 100, 90));
        planned.Add(swap);
        planned.Add(beyond);

        Assert.Equal(planned, s.Pick(new Selector.Joined(At(100, 100, 105))));
        Assert.Equal(planned, s.Pick(new Selector.Joined(At(100, 100, 95))));
    }

    [Fact]
    public void A_modded_network_of_a_kind_lattice_does_not_model_is_walked_through_its_junctions()
    {
        var s = new TestSelect(Pieces(Monorail));
        var network = Set(
            s.Build("ModMonorailStraight", At(100, 100, 105)),
            s.Build("ModMonorailJunction", At(100, 100, 110)),
            s.Build("ModMonorailStraight", At(100, 100, 115)),
            s.Build("ModMonorailStraight", At(95, 100, 110), s.AlongX));
        s.Build(Cable, At(100, 100, 100));

        Assert.Equal(network, s.Pick(new Selector.Joined(At(100, 100, 105))));
    }

    [Fact]
    public void Inside_an_and_it_narrows_like_any_other_selector()
    {
        var s = new TestSelect();
        var tee = new Tee(s);
        var box = new Selector.Box(At(100, 100, 100), At(100, 100, 120));
        var up = new HashSet<GhostId>(tee.Up) { tee.Junction };

        Assert.Equal(up, s.Pick(new Selector.All(ValueList.From<Selector>([new Selector.Joined(At(85, 100, 100)), box]))));
    }
}
