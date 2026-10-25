using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

/// <summary>
/// Pieces the game places on a face (walls and their kin, wall mounts): each blocking point, turned and added to the
/// position, lies in the large cell nearest it on the side of the piece's centre, at the slot it makes there
/// (decompile Assets.Scripts/GridController.cs AddGridStructure, Grid3.GridCenter; Assets.Scripts.GridSystem/Cell.cs
/// AddStructural, StructuralArray.Get). Large cell centres are at odd
/// metres, so (1, 1, 2) m is the face between the cells at z = 1 m and z = 3 m.
/// </summary>
public sealed class FaceSpotTests
{
    private static readonly Cell OnFace = new(10, 10, 20);
    private static readonly Cell Below = new(10, 10, 10);
    private static readonly Cell Above = new(10, 10, 30);

    internal const string Wall = "StructureFaceWall";
    internal const string Tall = "StructureFaceWallTall";
    internal const string Corner = "StructureFaceCorner";
    internal const string Mount = "StructureFaceMount";
    internal const string ModdedWall = "ModGlassWall";

    /// <summary>Face pieces as the catalogue data file writes them.</summary>
    internal const string Text = """
        lattice-catalogue 3
        piece StructureFaceWall grid=large place=face
          step entry=ItemIronSheets material=ItemIronSheets*2 exit=ItemCrowbar air=yes gravity=yes
          centre 0 0 0.05

        piece StructureFaceWallTall grid=large place=face
          step entry=ItemIronSheets material=ItemIronSheets*2 exit=ItemCrowbar air=yes gravity=yes
          block 0 0 0
          block 0 20 0
          centre 0 1 0.05

        piece StructureFaceCorner grid=large place=face
          step entry=ItemIronSheets material=ItemIronSheets*2 exit=ItemCrowbar air=yes gravity=yes
          block 0 0 0
          block 10 0 0
          centre 0 0 0.05

        piece StructureFaceMount grid=large place=face-mount
          step entry=ItemIronSheets material=ItemIronSheets*1 exit=ItemWrench air=no
          centre 0 0 0.2

        piece ModGlassWall grid=large place=face
          step entry=ItemIronSheets material=ItemIronSheets*2 exit=ItemCrowbar air=yes gravity=yes
          centre 0 0 -0.05
        """;

    internal static NamedPiece[] Pieces() =>
        [.. Prefabs(CatalogueFile.Read(new StringReader(Text), "face-pieces.lcat"))];

    private static IReadOnlyList<NamedPiece> Prefabs(Result<FakeCatalogue, Lattice.TestKit.FileError> read) =>
        read.TryGet(out var catalogue) ? catalogue.Pieces : throw new Xunit.Sdk.XunitException($"face pieces did not read: {read}");

    private static TestBoard Board() => new(default, Pieces());

    private static IReadOnlyList<SpotKey> Spots(TestBoard t, string piece, Cell at, Turn turn = default)
    {
        var shapes = new Shapes(t.Catalogue, t.Grid);
        return shapes.Of(new Placement(t.Catalogue.Named(piece).Key, at, turn)).TryGet(out var shape)
            ? shape.Spots
            : throw new Xunit.Sdk.XunitException(piece + " has no shape");
    }

    private static Turn Flipped(TestBoard t) => TurnSending(Direction.ZPlus, Direction.ZMinus, t.Grid);

    [Fact]
    public void A_face_piece_lies_in_the_cell_its_centre_is_in_at_the_face_toward_its_position()
    {
        var t = Board();

        Assert.Equal([new SpotKey(Above, Slot.FaceZMinus)], Spots(t, Wall, OnFace));
    }

    [Fact]
    public void Turned_to_face_the_other_way_it_lies_in_the_cell_on_the_other_side()
    {
        var t = Board();

        Assert.Equal([new SpotKey(Below, Slot.FaceZPlus)], Spots(t, Wall, OnFace, Flipped(t)));
    }

    [Fact]
    public void A_back_to_back_pair_on_one_boundary_is_two_spots_and_both_stay_on_the_board()
    {
        var t = Board();
        var front = Assert.Single(t.Placed(t.Draft(Wall, OnFace)).Added);

        var changes = t.Placed(t.Draft(Wall, OnFace, Flipped(t)));

        Assert.Single(changes.Added);
        Assert.Empty(changes.Removed);
        Assert.True(t.View.Find(front.Id).IsSome);
    }

    [Fact]
    public void The_same_wall_twice_at_one_position_and_turn_is_one_spot()
    {
        var t = Board();
        var first = Assert.Single(t.Placed(t.Draft(Wall, OnFace)).Added);

        var changes = t.Placed(t.Draft(Wall, OnFace));

        Assert.Equal(first.Id, Assert.Single(changes.Removed).Ghost.Id);
    }

    [Fact]
    public void Each_blocking_point_takes_the_slot_it_makes_in_its_own_cell()
    {
        var t = Board();

        Assert.Equal(
            [new SpotKey(Above, Slot.FaceZMinus), new SpotKey(new Cell(10, 30, 30), Slot.FaceZMinus)],
            Spots(t, Tall, OnFace));
    }

    [Fact]
    public void A_point_on_a_cell_edge_makes_no_slot_and_takes_nothing()
    {
        var t = Board();

        Assert.Equal([new SpotKey(Above, Slot.FaceZMinus)], Spots(t, Corner, OnFace));
    }

    [Fact]
    public void A_wall_mount_takes_the_face_of_the_cell_it_reaches_into()
    {
        var t = Board();

        Assert.Equal([new SpotKey(Above, Slot.FaceZMinus)], Spots(t, Mount, OnFace));
    }

    [Fact]
    public void A_modded_wall_subclass_takes_its_face_from_its_own_data()
    {
        var t = Board();

        Assert.Equal([new SpotKey(Below, Slot.FaceZPlus)], Spots(t, ModdedWall, OnFace));
        Assert.Equal([new SpotKey(Above, Slot.FaceZMinus)], Spots(t, ModdedWall, OnFace, Flipped(t)));
    }

    [Fact]
    public void A_wall_on_a_side_face_turns_with_its_placement()
    {
        var t = Board();
        var east = TurnSending(Direction.ZPlus, Direction.XPlus, t.Grid);

        Assert.Equal([new SpotKey(new Cell(30, 10, 10), Slot.FaceXMinus)], Spots(t, Wall, new Cell(20, 10, 10), east));
    }

    [Fact]
    public void A_face_piece_has_a_direction_so_it_does_not_match_in_any_turn()
    {
        var w = new Matching.TestWorld(Pieces());

        Assert.False(w.TurnFree.Has(w.T.Catalogue.Named(Wall).Key));
    }

    [Fact]
    public void A_back_to_back_pair_both_build_to_done()
    {
        var w = new Matching.TestWorld(Pieces());
        w.Run();
        var flipped = Flipped(w.T);
        var front = w.Build(Wall, OnFace);
        var back = w.Build(Wall, OnFace, flipped);
        var sets = w.Recorded();

        w.World.Register(w.Structure(Wall, OnFace));
        w.World.Register(w.Structure(Wall, OnFace, flipped));
        w.T.Events.Flush();

        var done = sets.SelectMany(s => s.Removed).Where(r => r.Cause is RemovalCause.Done).Select(r => r.Ghost.Id).ToList();
        Assert.Contains(front.Id, done);
        Assert.Contains(back.Id, done);
    }

    [Fact]
    public void A_face_wall_beside_its_back_to_back_twin_is_not_stale()
    {
        var w = new Matching.TestWorld(Pieces());
        w.Run();
        var back = w.Build(Wall, OnFace, Flipped(w.T));

        w.World.Register(w.Structure(Wall, OnFace));

        Assert.IsType<GhostState.Planned>(w.StateOf(back.Id));
    }
}
