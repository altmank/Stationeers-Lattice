using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Transforms;
using Lattice.Tests.Board;
using Lattice.Tests.Selectors;
using static Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Transforms.TransformBench;

namespace Lattice.Tests.Transforms;

/// <summary>
/// Moving, turning, mirroring and templating pieces the game places on a face: the result takes the spots the
/// original's spots are taken to, worked out by the same rules as <see cref="Shapes"/> (the game's position key is a
/// face point, the cell is on the piece's centre side; <see cref="FaceSpotTests"/>). Large cell centres are at odd
/// metres, so (1, 1, 2) m is the face between the cells at z = 1 m and z = 3 m.
/// </summary>
public sealed class FaceTransformTests
{
    private static readonly Cell OnFace = new(10, 10, 20);
    private static readonly Cell Above = new(10, 10, 30);
    private static readonly string[] FacePieces =
        [FaceSpotTests.Wall, FaceSpotTests.Tall, FaceSpotTests.Corner, FaceSpotTests.Mount, FaceSpotTests.ModdedWall];
    private static readonly Axis[] Axes = [Axis.X, Axis.Y, Axis.Z];
    private static readonly Cell[] Positions = [OnFace, new(-30, 50, 120), new(-110, -70, -20)];

    [Fact]
    public void A_wall_moved_by_whole_cells_takes_the_same_face_of_the_moved_cell()
    {
        var b = new TransformBench();
        var wall = b.Build(FaceSpotTests.Wall, OnFace);

        Applied(b.Transform(new Selector.Ids(ValueList.Of(wall)), new Transformation.Move(new CellOffset(20, 0, 40))));

        var moved = Assert.Single(b.Placed());
        Assert.Equal(new Cell(30, 10, 60), moved.At);
        Assert.Equal([new SpotKey(new Cell(30, 10, 70), Slot.FaceZMinus)], Spots(b, moved));
    }

    [Fact]
    public void Every_face_piece_moved_by_whole_cells_takes_its_spots_moved()
    {
        var b = new TransformBench();
        var by = new CellOffset(-40, 60, 20);
        foreach (var name in FacePieces)
            for (var index = 0; index < Turn.Count; index++)
            {
                var placement = Placed(b, name, Turned(b, OnFace, index), index);

                var moved = Ok(b.Placements.Apply(new Transformation.Move(by), placement));

                Assert.Equal(Spots(b, placement).Select(s => s with { Cell = s.Cell.Plus(by) }).ToHashSet(), Spots(b, moved).ToHashSet());
            }
    }

    [Fact]
    public void A_wall_turned_about_a_pivot_lands_on_the_turned_face()
    {
        var b = new TransformBench();
        b.Build(FaceSpotTests.Wall, OnFace);
        var pivot = new Cell(50, 10, 10);
        var turn = b.Sending(Direction.ZPlus, Direction.XPlus);

        Applied(b.Transform(All, new Transformation.Rotate(pivot, turn)));

        var turned = Assert.Single(b.Placed());
        Assert.Equal([TurnedSpot(b, new SpotKey(Above, Slot.FaceZMinus), pivot, turn)], Spots(b, turned));
    }

    [Fact]
    public void Every_face_piece_turned_any_way_about_a_cell_centre_takes_its_spots_turned()
    {
        var b = new TransformBench();
        var pivot = new Cell(-10, 30, 50);
        foreach (var name in FacePieces)
            for (var from = 0; from < Turn.Count; from += 5)
                for (var by = 0; by < Turn.Count; by++)
                {
                    var placement = Placed(b, name, Turned(b, OnFace, from), from);
                    var turn = TurnOf(by);

                    var turned = Ok(b.Placements.Apply(new Transformation.Rotate(pivot, turn), placement));

                    Assert.Equal(Spots(b, placement).Select(s => TurnedSpot(b, s, pivot, turn)).ToHashSet(), Spots(b, turned).ToHashSet());
                }
    }

    [Fact]
    public void A_wall_mirrored_across_its_face_takes_the_face_on_the_other_side()
    {
        var b = new TransformBench();
        var plane = new MirrorPlane(Axis.Z, 40);

        var image = Ok(b.Placements.Apply(new Transformation.Mirror(plane), Placed(b, FaceSpotTests.Wall, OnFace, 0)));

        Assert.Equal(new Cell(10, 10, 60), image.At);
        Assert.Equal([new SpotKey(new Cell(10, 10, 50), Slot.FaceZPlus)], Spots(b, image));
    }

    [Fact]
    public void Every_face_piece_mirrored_across_any_axis_takes_the_reflection_of_its_spots_wherever_it_stands()
    {
        var b = new TransformBench();
        foreach (var name in FacePieces)
            foreach (var at in Positions)
                foreach (var axis in Axes)
                    foreach (var planeAt in new[] { 40, 30, -60 })
                        for (var index = 0; index < Turn.Count; index += 3)
                        {
                            var placement = Placed(b, name, Turned(b, at, index), index);
                            var plane = new MirrorPlane(axis, planeAt);

                            var image = Ok(b.Placements.Apply(new Transformation.Mirror(plane), placement));

                            Assert.Equal(
                                Spots(b, placement).Select(s => TransformTests.ReflectSpot(new CubeGrid(), s, plane)).ToHashSet(),
                                Spots(b, image).ToHashSet());
                        }
    }

    [Fact]
    public void Every_face_piece_mirrored_twice_is_the_original_placement()
    {
        var b = new TransformBench();
        foreach (var name in FacePieces)
            foreach (var axis in Axes)
                for (var index = 0; index < Turn.Count; index++)
                {
                    var placement = Placed(b, name, Turned(b, OnFace, index), index);
                    var mirror = new Transformation.Mirror(new MirrorPlane(axis, 40));

                    var twice = Ok(b.Placements.Apply(mirror, Ok(b.Placements.Apply(mirror, placement))));

                    Assert.Equal(placement, twice);
                }
    }

    [Fact]
    public void Walls_mirrored_through_the_command_come_back_after_a_second_mirror()
    {
        var b = new TransformBench();
        b.Build(FaceSpotTests.Wall, OnFace);
        b.Build(FaceSpotTests.ModdedWall, new Cell(20, 10, 10), b.Sending(Direction.ZPlus, Direction.XPlus));
        b.Build(FaceSpotTests.Tall, new Cell(30, 20, 10), b.Sending(Direction.ZPlus, Direction.YMinus));
        var original = b.Placed();
        var plane = new Transformation.Mirror(new MirrorPlane(Axis.X, 60));

        Applied(b.Transform(All, plane));
        var once = b.Placed();
        Applied(b.Transform(All, plane));

        Assert.NotEqual(original, once);
        Assert.Equal(original, b.Placed());
    }

    [Fact]
    public void A_modded_wall_whose_centre_is_on_the_other_side_mirrors_onto_its_own_reflected_face()
    {
        var b = new TransformBench();
        var glass = Placed(b, FaceSpotTests.ModdedWall, OnFace, 0);
        var plane = new MirrorPlane(Axis.Z, 40);

        var image = Ok(b.Placements.Apply(new Transformation.Mirror(plane), glass));

        Assert.Equal([new SpotKey(new Cell(10, 10, 10), Slot.FaceZPlus)], Spots(b, glass));
        Assert.Equal([new SpotKey(new Cell(10, 10, 70), Slot.FaceZMinus)], Spots(b, image));
    }

    [Fact]
    public void A_face_piece_with_a_registered_partner_mirrors_into_the_partner_on_the_reflected_face()
    {
        var b = new TransformBench();
        using var pair = b.Partners(FaceSpotTests.Wall, FaceSpotTests.ModdedWall);
        var plane = new MirrorPlane(Axis.Z, 40);

        var image = Ok(b.Placements.Apply(new Transformation.Mirror(plane), Placed(b, FaceSpotTests.Wall, OnFace, 0)));

        Assert.Equal(new Placement(b.T.Catalogue.Named(FaceSpotTests.ModdedWall).Key, new Cell(10, 10, 60), Turn.None), image);
        Assert.Equal([new SpotKey(new Cell(10, 10, 50), Slot.FaceZPlus)], Spots(b, image));
    }

    [Fact]
    public void A_wall_kept_as_a_template_places_back_onto_the_same_face()
    {
        var b = new TransformBench();
        var plan = Applied(b.T.Apply(new Command.CreatePlan(NewPlan("Room")))).PlansOpened[0].Id;
        b.Build(FaceSpotTests.Wall, OnFace, plan: Option.Some(plan));
        b.Build(FaceSpotTests.ModdedWall, new Cell(40, 10, 10), b.Sending(Direction.ZPlus, Direction.XMinus), Option.Some(plan));
        var kept = b.Placed();
        var template = TestSelect.Ok(b.Plans.AsTemplate(plan, Pump));
        Applied(b.T.Apply(new Command.Remove(new Selector.InPlan(plan))));

        Applied(b.Run(new Command.PlaceTemplate(template, new Cell(10, 10, 10), Turn.None, NewPlan("Again"))));

        Assert.Equal(kept, b.Placed());
    }

    [Fact]
    public void Every_face_piece_placed_from_a_template_at_an_origin_and_turn_lands_on_the_right_face()
    {
        var b = new TransformBench();
        var origin = new Cell(50, -30, 70);
        var reference = new Cell(10, 10, 10);
        foreach (var name in FacePieces)
            for (var from = 0; from < Turn.Count; from += 7)
                for (var by = 0; by < Turn.Count; by++)
                {
                    var world = Placed(b, name, Turned(b, OnFace, from), from);
                    var local = world with { At = new Cell(world.At.X - reference.X, world.At.Y - reference.Y, world.At.Z - reference.Z) };
                    var turn = TurnOf(by);

                    var placed = Ok(b.Placements.Place(local, origin, turn));

                    var expected = Spots(b, world).Select(s => Moved(TurnedSpot(b, s, reference, turn), reference, origin)).ToHashSet();
                    Assert.Equal(expected, Spots(b, placed).ToHashSet());
                }
    }

    [Fact]
    public void A_wall_moved_off_its_face_is_refused_off_grid()
    {
        var b = new TransformBench();
        var wall = Placed(b, FaceSpotTests.Wall, OnFace, 0);

        Assert.Equal(TransformProblem.OffGrid, Problem(b.Placements.Apply(new Transformation.Move(new CellOffset(0, 0, 10)), wall)));
        Assert.Equal(TransformProblem.OffGrid, Problem(b.Placements.Apply(new Transformation.Move(new CellOffset(10, 0, -10)), wall)));
        Assert.Equal(TransformProblem.OffGrid, Problem(b.Placements.Apply(new Transformation.Move(new CellOffset(5, 0, 0)), wall)));
    }

    [Fact]
    public void A_wall_turned_about_a_point_that_is_not_a_cell_centre_or_mirrored_off_the_grid_is_refused_off_grid()
    {
        var b = new TransformBench();
        var wall = Placed(b, FaceSpotTests.Wall, OnFace, 0);
        var quarter = b.Sending(Direction.ZPlus, Direction.XPlus);

        Assert.Equal(TransformProblem.OffGrid, Problem(b.Placements.Apply(new Transformation.Rotate(OnFace, quarter), wall)));
        Assert.Equal(TransformProblem.OffGrid, Problem(b.Placements.Apply(new Transformation.Mirror(new MirrorPlane(Axis.Z, 45)), wall)));
    }

    [Fact]
    public void A_wall_template_placed_off_a_cell_centre_is_refused_off_grid()
    {
        var b = new TransformBench();
        var local = Placed(b, FaceSpotTests.Wall, new Cell(0, 0, 10), 0);

        Assert.Equal(TransformProblem.OffGrid, Problem(b.Placements.Place(local, new Cell(20, 10, 10), Turn.None)));
    }

    private static readonly TemplateName Pump = TestSelect.Ok(TemplateName.Create("walls"));

    private static Placement Placed(TransformBench b, string piece, Cell at, int turn) =>
        new(b.T.Catalogue.Named(piece).Key, at, TurnOf(turn));

    /// <summary>The face point a piece turned by <paramref name="turn"/> stands on: <paramref name="at"/>'s z face turned about the cell below it.</summary>
    private static Cell Turned(TransformBench b, Cell at, int turn)
    {
        var below = at with { Z = at.Z - 10 };
        return below.Plus(b.T.Grid.Apply(TurnOf(turn), new CellOffset(0, 0, 10)));
    }

    private static IReadOnlyList<SpotKey> Spots(TransformBench b, Placement placement) => TestSelect.Ok(b.Shapes.Of(placement)).Spots;

    private static SpotKey TurnedSpot(TransformBench b, SpotKey spot, Cell pivot, Turn turn)
    {
        var cell = pivot.Plus(b.T.Grid.Apply(turn, new CellOffset(spot.Cell.X - pivot.X, spot.Cell.Y - pivot.Y, spot.Cell.Z - pivot.Z)));
        return Faces.DirectionOf(spot.Slot) is { } face ? new SpotKey(cell, Faces.SlotOf(b.T.Grid.Apply(turn, face))) : spot with { Cell = cell };
    }

    private static SpotKey Moved(SpotKey spot, Cell from, Cell to) =>
        spot with { Cell = new Cell(spot.Cell.X - from.X + to.X, spot.Cell.Y - from.Y + to.Y, spot.Cell.Z - from.Z + to.Z) };

    private static Turn TurnOf(int index) => Turn.Create(index).TryGet(out var turn) ? turn : Turn.None;

    private static T Ok<T>(Result<T, TransformProblem> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new Xunit.Sdk.XunitException(result.ToString());

    private static TransformProblem Problem(Result<Placement, TransformProblem> result) =>
        result.TryGetError(out var problem) ? problem : throw new Xunit.Sdk.XunitException("not refused: " + result);
}
