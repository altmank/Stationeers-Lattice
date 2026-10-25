using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Scenarios;

namespace Lattice.Tests.TestKit;

/// <summary>A scenario built by the builder reads back the same through <see cref="IWorldView"/>.</summary>
public sealed class ScenarioTests
{
    [Fact]
    public void Placed_pieces_read_back_by_id_and_cell()
    {
        var s = Scenario.With(FakeCatalogue.Hand())
            .Built("StructureFrameIron", new Cell(10, 10, 10), out var frame, colour: 3, name: "Store")
            .Built("StructureCableCorner", new Cell(0, 0, 10), out var corner, turn: 2)
            .Build();
        IWorldView view = s.World;

        Assert.True(view.Structure(frame.Id).TryGet(out var f));
        Assert.Equal(frame, f.Ref);
        Assert.IsType<BuildState.Built>(f.State);
        Assert.Equal(3, f.Colour.Reduce(default(PaintColour)).Index);
        Assert.Equal("Store", f.Name.Map(n => n.Value).Reduce(""));
        Assert.Contains(f, view.At(new Cell(10, 10, 10)));

        Assert.True(view.Structure(corner.Id).TryGet(out var c));
        Assert.Equal(2, c.Turn.Index);
        Assert.Equal(PrefabHash.Piece("StructureCableCorner"), c.Ref.Piece);
    }

    [Fact]
    public void A_part_built_piece_is_not_fully_built()
    {
        var s = Scenario.With(FakeCatalogue.Hand()).Built("StructureFrameIron", new Cell(10, 10, 10), out var frame, step: 1).Build();
        Assert.True(s.World.Structure(frame.Id).TryGet(out var f));
        Assert.Equal(1, Assert.IsType<BuildState.Building>(f.State).At.Value);
    }

    [Fact]
    public void Ghosts_are_numbered_in_order_with_their_intents()
    {
        var s = Scenario.With(FakeCatalogue.Hand())
            .Built("StructureFrameIron", new Cell(10, 10, 10), out var frame)
            .Ghost("StructureCableStraight", new Cell(0, 0, 0), out var build, turn: 1)
            .Removal(frame, out var removal)
            .Build();

        Assert.Equal(new GhostId(1), build);
        Assert.Equal(new GhostId(2), removal);
        Assert.Equal(
            new Intent.Build(new Placement(PrefabHash.Piece("StructureCableStraight"), new Cell(0, 0, 0), Turn.Create(1).ToOption().Reduce(Turn.None)), Option<Finish>.None),
            s.Ghosts[0].Intent);
        Assert.Equal(new Intent.Remove(frame), s.Ghosts[1].Intent);
        Assert.IsType<GhostState.Planned>(s.Ghosts[0].State);
    }

    [Fact]
    public void A_long_straight_is_in_every_cell_it_spans()
    {
        var s = Scenario.With(FakeCatalogue.Hand()).Built("StructureCableStraight3", new Cell(50, 0, 0), out var straight).Build();
        foreach (var z in new[] { 0, -5, -10 })
            Assert.Contains(s.World.At(new Cell(50, 0, z)), i => i.Ref == straight);
        Assert.Empty(s.World.At(new Cell(50, 0, -15)));
    }

    [Theory]
    [InlineData("StructurePipeStraight3", 0, 0, 5)]
    [InlineData("StructureChuteStraight3", -5, 0, 0)]
    [InlineData("StructureRoboticArmRailStraightStop", 0, -5, 0)]
    public void Long_pieces_run_the_way_the_game_registers_them(string piece, int dx, int dy, int dz)
    {
        var s = Scenario.With(FakeCatalogue.Hand()).Built(piece, new Cell(50, 0, 0), out var built).Build();
        var cell = new Cell(50, 0, 0);
        var spanned = 0;
        while (s.World.At(cell).Count > 0)
        {
            Assert.Contains(s.World.At(cell), i => i.Ref == built);
            spanned++;
            cell = cell.Plus(new CellOffset(dx, dy, dz));
        }

        Assert.True(spanned >= 3, $"{piece} spans {spanned} cells from its pivot");
        Assert.Empty(s.World.At(new Cell(50, 0, 0).Plus(new CellOffset(-dx, -dy, -dz))));
    }

    [Fact]
    public void Connected_cables_and_the_device_they_feed_share_a_network()
    {
        var w = Worlds.Mixed().World;
        Assert.True(w.NetworkOf(new ReferenceId(2)).TryGet(out var line));
        Assert.True(w.Network(line).TryGet(out var info));

        Assert.Equal(EndKinds.Cable, info.Kinds);
        Assert.Equal(new[] { new ReferenceId(2), new ReferenceId(3), new ReferenceId(4) }, info.Pieces);
        Assert.Equal(new[] { new ReferenceId(5) }, info.Devices);
        Assert.Equal(Option.Some(line), w.NetworkOf(new ReferenceId(5)));
        Assert.Equal(5000, info.Rating.Reduce(default(Watts)).Value);
    }

    [Fact]
    public void A_gap_or_another_kind_starts_another_network()
    {
        var w = Worlds.Mixed().World;
        var line = w.NetworkOf(new ReferenceId(2));
        Assert.NotEqual(line, w.NetworkOf(new ReferenceId(6)));
        Assert.True(w.NetworkOf(new ReferenceId(8)).TryGet(out var pipe));
        Assert.Contains(new NetworkType.GasPipe(), w.Network(pipe).Map(n => n.Kinds).Reduce(ValueSet<NetworkType>.Empty));
        Assert.True(w.NetworkOf(new ReferenceId(1)).IsNone);
    }

    [Fact]
    public void Rooms_read_back()
    {
        var w = Worlds.Mixed().World;
        Assert.Equal(Option.Some(new RoomRef(7)), w.RoomAt(new Cell(10, 10, 10)));
        Assert.True(w.Room(new RoomRef(7)).TryGet(out var room));
        Assert.True(room.Sealed);
        Assert.Equal(ValueList.Of(new Cell(10, 10, 10)), room.Cells);
    }

    [Fact]
    public void Reads_in_a_handler_see_the_world_as_of_the_event()
    {
        var w = Worlds.Mixed().World;
        var seen = new System.Collections.Generic.List<int>();
        var first = true;
        w.Painted += (r, _) =>
        {
            if (first)
            {
                first = false;
                w.Paint(r.Id, PaintColour.Create(9).ToOption().Reduce(default(PaintColour)));
            }

            seen.Add(w.Structure(r.Id).Bind(s => s.Colour).Reduce(default(PaintColour)).Index);
        };

        w.Paint(new ReferenceId(1), PaintColour.Create(4).ToOption().Reduce(default(PaintColour)));

        Assert.Equal(new[] { 4, 9 }, seen);
    }
}
