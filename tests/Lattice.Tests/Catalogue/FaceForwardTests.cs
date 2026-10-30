using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Transforms;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// A face piece stands on the grid line of its forward's axis: the game's face cursor moves it back half a step along
/// its forward from the centre it aims into (decompile <c>Assets.Scripts.Inventory/InventoryManager.cs</c>
/// <c>UpdatePlacement</c>, <c>PlacementSnap.Face</c>), so a face piece turned to face along its face is one the game
/// never builds, and Lattice fails it <c>off-snap</c> with <c>axis</c> its forward's.
/// </summary>
public sealed class FaceForwardTests
{
    private const string FaceWall = "StructureFaceWall";

    private static Turn Of(SignedAxis x, SignedAxis y) => Turn.Of(x, y).TryGet(out var turn) ? turn : default;

    /// <summary>Forward down y.</summary>
    private static readonly Turn FacingDown = Of(SignedAxis.XPlus, SignedAxis.ZPlus);

    /// <summary>Forward up y.</summary>
    private static readonly Turn FacingUp = Of(SignedAxis.XPlus, SignedAxis.ZMinus);

    /// <summary>Forward along +z: no turn.</summary>
    private static readonly Turn FacingNorth = Turn.None;

    /// <summary>The six faces of the large cell at 10,10,10, each with the axis square to it.</summary>
    private static readonly (Cell At, Axis Square)[] Faces =
    [
        (new Cell(0, 10, 10), Axis.X), (new Cell(20, 10, 10), Axis.X),
        (new Cell(10, 0, 10), Axis.Y), (new Cell(10, 20, 10), Axis.Y),
        (new Cell(10, 10, 0), Axis.Z), (new Cell(10, 10, 20), Axis.Z),
    ];

    private static Option<Reason> Off(PieceFacts piece, Cell at, Turn turn) => Snaps.Off(piece, new Placement(piece.Key, at, turn));

    [Fact]
    public void A_wall_on_a_ceiling_facing_down_or_up_passes_and_facing_north_fails_off_snap_naming_z()
    {
        var b = new TransformBench();
        var wall = b.T.Catalogue.Named(FaceWall);
        var ceiling = new Cell(10, 40, 10);

        Assert.True(Off(wall, ceiling, FacingDown).IsNone);
        Assert.True(Off(wall, ceiling, FacingUp).IsNone);
        Assert.True(Off(wall, ceiling, FacingNorth).TryGet(out var why));
        Assert.Equal(ReasonCodes.OffSnap, why.Code);
        Assert.Equal("z", why.Facts["axis"]);
        Assert.Equal("10,40,10", why.Facts["cell"]);
    }

    [GameCatalogueFact]
    public void A_game_wall_on_each_face_in_each_turn_passes_exactly_where_the_games_cursor_can_put_it()
    {
        var catalogue = GameCatalogue.Load();
        var wall = catalogue.Named("StructureWallIron");
        var passed = 0;

        foreach (var (at, square) in Faces)
        foreach (var turn in wall.AllowedTurns.ToList())
        {
            var reachable = turn.Axes.Z.Axis == square;
            var off = Off(wall, at, turn);
            Assert.True(reachable == off.IsNone, $"{at} {turn}: {off}");
            if (off.TryGet(out var why))
                Assert.Equal(turn.Axes.Z.Axis switch { Axis.X => "x", Axis.Y => "y", _ => "z" }, why.Facts["axis"]);
            else
                passed++;
        }

        Assert.Equal(6 * 8, passed);
    }

    [Fact]
    public void Every_rotation_of_a_placed_wall_keeps_its_forward_on_its_face()
    {
        var b = new TransformBench();
        var wall = b.T.Catalogue.Named(FaceWall);
        var results = new List<Placement>();
        foreach (var (at, square) in Faces)
        foreach (var turn in TurnSet.All.ToList())
        {
            if (turn.Axes.Z.Axis != square)
                continue;
            var placed = new Placement(wall.Key, at, turn);
            Assert.True(Off(wall, at, turn).IsNone);
            foreach (var by in TurnSet.All.ToList())
                if (b.Placements.Apply(new Transformation.Rotate(new Cell(10, 10, 10), by), placed).TryGet(out var rotated))
                    results.Add(rotated);
            foreach (var normal in new[] { Axis.X, Axis.Y, Axis.Z })
                if (b.Placements.Apply(new Transformation.Mirror(new MirrorPlane(normal, 30)), placed).TryGet(out var mirrored))
                    results.Add(mirrored);
        }

        Assert.NotEmpty(results);
        foreach (var result in results)
            Assert.True(Snaps.Off((b.T.Catalogue.Piece(result.Piece).TryGet(out var p) ? p : wall), result).IsNone, result.ToString());
    }
}
