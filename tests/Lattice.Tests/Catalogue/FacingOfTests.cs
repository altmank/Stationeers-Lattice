using System;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Styles;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Board;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// <see cref="IPieceCatalogue.FacingOf"/>: the axis a face piece's face is square to under a placement, worked out from
/// the spots the piece takes there, as <see cref="StyleMatch.Facing"/> judges it; none for a piece off the faces.
/// </summary>
public sealed class FacingOfTests
{
    private static readonly TestBoard T = new(default, FaceSpotTests.Pieces());
    private static readonly FakeCatalogue Catalogue = T.Catalogue;
    private static readonly PieceCatalogue View =
        new(Catalogue.Listing, new CatalogueAdditions(Catalogue), PiecePorts.Over(Catalogue), Facings.Over(Catalogue));

    private static Placement At(string piece, Cell at, Turn turn = default) => new(Catalogue.Named(piece).Key, at, turn);

    private static Option<Axis> Facing(Placement placement) => Prefabs.Ok(View.FacingOf(placement));

    private static Turn Sending(Direction from, Direction to) => TestBoard.TurnSending(from, to, T.Grid);

    [Theory]
    [InlineData(10, 10, 20, (int)Direction.ZPlus, Axis.Z)]
    [InlineData(10, 10, 0, (int)Direction.ZMinus, Axis.Z)]
    [InlineData(20, 10, 10, (int)Direction.XPlus, Axis.X)]
    [InlineData(0, 10, 10, (int)Direction.XMinus, Axis.X)]
    [InlineData(10, 20, 10, (int)Direction.YPlus, Axis.Y)]
    [InlineData(10, 0, 10, (int)Direction.YMinus, Axis.Y)]
    public void A_wall_turned_to_face_an_axis_answers_that_axis(int x, int y, int z, int toward, Axis axis)
    {
        Assert.Equal(Option.Some(axis), Facing(At(FaceSpotTests.Wall, new Cell(x, y, z), Sending(Direction.ZPlus, (Direction)toward))));
    }

    [Fact]
    public void A_wall_laid_flat_answers_y()
    {
        Assert.Equal(Option.Some(Axis.Y), Facing(At(FaceSpotTests.Wall, new Cell(10, 20, 10), Sending(Direction.ZPlus, Direction.YPlus))));
    }

    [Theory]
    [InlineData(FaceSpotTests.Wall)]
    [InlineData(FaceSpotTests.Tall)]
    [InlineData(FaceSpotTests.Corner)]
    [InlineData(FaceSpotTests.ModdedWall)]
    [InlineData("StructureWallIron")]
    public void At_every_turn_and_place_the_answer_is_the_axis_its_spots_make(string piece)
    {
        foreach (var at in new[] { new Cell(10, 10, 20), new Cell(20, 10, 10), new Cell(10, 20, 10), new Cell(10, 10, 10), new Cell(-10, 30, -30) })
            for (var i = 0; i < Turn.Count; i++)
            {
                var placement = At(piece, at, Prefabs.Ok(Turn.Create(i)));
                var shape = Prefabs.Ok(new Shapes(Catalogue, T.Grid).Of(placement));
                var spots = FaceAxes.Of(shape.Spots);

                Assert.Equal(spots is { } axis ? Option.Some(axis) : Option<Axis>.None, Facing(placement));
            }
    }

    [Theory]
    [InlineData("StructureFrame", 10, 10, 10)]
    [InlineData("StructureCableStraight", 100, 100, 100)]
    [InlineData("StructureAreaPowerControl", 100, 100, 100)]
    public void A_frame_a_cable_and_a_device_face_nothing(string piece, int x, int y, int z)
    {
        for (var i = 0; i < Turn.Count; i++)
            Assert.True(Facing(At(piece, new Cell(x, y, z), Prefabs.Ok(Turn.Create(i)))).IsNone);
    }

    [Fact]
    public void A_modded_face_piece_answers_by_its_own_points()
    {
        Assert.Equal(Option.Some(Axis.Z), Facing(At(FaceSpotTests.ModdedWall, new Cell(10, 10, 20))));
        Assert.Equal(Option.Some(Axis.X), Facing(At(FaceSpotTests.ModdedWall, new Cell(20, 10, 10), Sending(Direction.ZPlus, Direction.XPlus))));
    }

    [Fact]
    public void Back_to_back_walls_on_one_boundary_answer_the_same_axis()
    {
        var front = At(FaceSpotTests.Wall, new Cell(10, 10, 20));
        var back = At(FaceSpotTests.Wall, new Cell(10, 10, 20), Sending(Direction.ZPlus, Direction.ZMinus));

        Assert.Equal(Facing(front), Facing(back));
        Assert.Equal(Option.Some(Axis.Z), Facing(back));
    }

    [Fact]
    public void An_unknown_piece_is_refused()
    {
        var why = Prefabs.Error(View.FacingOf(new Placement(PrefabHash.Piece("NoSuchPiece"), new Cell(10, 10, 20), default)));

        Assert.Equal(ReasonCodes.UnknownPiece, why.Code);
    }

    [Fact]
    public void Asking_again_allocates_nothing()
    {
        var placement = At(FaceSpotTests.Tall, new Cell(30, 10, 20), Sending(Direction.ZPlus, Direction.XMinus));
        var moved = placement with { At = new Cell(70, 50, -20) };

        // A miss fills the cache; a hit then runs the lookup's one-off set-up (its key's comparers), which a test run
        // on its own would otherwise pay inside the measurement.
        View.FacingOf(placement);
        View.FacingOf(placement);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var first = View.FacingOf(placement);
        var second = View.FacingOf(moved);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(first, second);
    }
}
