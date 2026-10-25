using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;
using Lattice.Core.Transforms;
using Lattice.Tests.Selectors;
using static Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Transforms.TransformBench;

namespace Lattice.Tests.Transforms;

/// <summary>
/// Moving, turning and mirroring ghosts through <see cref="Command.Transform"/> (a piece mirrors when its reflected ends match it under one of the 24 turns).
/// </summary>
public sealed class TransformTests
{
    private static readonly Axis[] Axes = [Axis.X, Axis.Y, Axis.Z];

    [Fact]
    public void Mirroring_a_cable_run_twice_returns_the_original()
    {
        var b = new TransformBench();
        var alongX = b.Sending(Direction.ZPlus, Direction.XPlus);
        b.Build("StructureCableStraight", new Cell(5, 0, 0), alongX);
        b.Build("StructureCableStraight", new Cell(10, 0, 0), alongX);
        b.Build("StructureCableCorner", new Cell(15, 0, 0), b.Sending(Direction.XMinus, Direction.XMinus));
        b.Build("StructureCableJunction", new Cell(15, 0, 5), b.Sending(Direction.ZPlus, Direction.ZPlus));
        b.Build("StructureCableStraight", new Cell(15, 0, 10));
        var original = b.Placed();
        var plane = new Transformation.Mirror(new MirrorPlane(Axis.X, 30));

        Applied(b.Transform(All, plane));
        var once = b.Placed();
        Applied(b.Transform(All, plane));

        Assert.NotEqual(original, once);
        Assert.Equal(original, b.Placed());
    }

    [Fact]
    public void A_mirror_image_takes_the_reflection_of_every_spot_and_end_of_the_original()
    {
        var b = new TransformBench();
        var pieces = new[] { "StructureCableStraight", "StructureCableCorner", "StructureCableJunction", "StructureCableJunction5", "StructureTankSmall", "ModSteamCorner" };
        foreach (var name in pieces)
            foreach (var axis in Axes)
                for (var index = 0; index < Turn.Count; index += 5)
                {
                    var placement = new Placement(b.T.Catalogue.Named(name).Key, new Cell(15, 25, -5), TestKitTurn(index));
                    var plane = new MirrorPlane(axis, 40);

                    var image = Ok(b.Placements.Apply(new Transformation.Mirror(plane), placement));

                    SameShape(Reflected(Shape(b, placement), plane), Footprint(Shape(b, image)));
                }
    }

    [Fact]
    public void A_wall_mirrors_onto_the_reflected_face()
    {
        var b = new TransformBench();
        var wall = new Placement(b.T.Catalogue.Named("StructureWallIron").Key, new Cell(10, 10, 10), Turn.None);
        var plane = new MirrorPlane(Axis.Z, 40);

        var image = Ok(b.Placements.Apply(new Transformation.Mirror(plane), wall));

        SameShape(Reflected(Shape(b, wall), plane), Footprint(Shape(b, image)));
    }

    [Fact]
    public void A_machine_with_uneven_ports_is_reported_not_mirrored()
    {
        var b = new TransformBench();
        var machine = b.Build("ModChiralMachine", new Cell(5, 0, 0));
        b.Build("StructureCableStraight", new Cell(10, 0, 0));
        var before = b.Placed();

        var why = Refusal(b.Transform(All, new Transformation.Mirror(new MirrorPlane(Axis.X, 20)), Copying));

        Assert.Equal(ReasonCodes.NoMirrorImage, why.Code);
        Assert.Equal(machine.Value.ToString(), why.Facts["ghosts"]);
        Assert.Equal(b.T.Catalogue.Named("ModChiralMachine").Key.Hash.ToString(), why.Facts["pieces"]);
        Assert.Equal(before, b.Placed());
    }

    [Fact]
    public void A_modded_piece_of_a_modded_network_kind_mirrors_by_its_own_ends()
    {
        var b = new TransformBench();
        b.Build("ModSteamCorner", new Cell(5, 0, 0));

        var copy = Applied(b.Transform(All, new Transformation.Mirror(new MirrorPlane(Axis.X, 20)), Copying));

        var image = Assert.Single(copy.Added);
        Assert.Equal(new Cell(35, 0, 0), ((Intent.Build)image.Intent).Target.At);
    }

    [Fact]
    public void A_copy_keeps_the_originals_and_places_images_with_their_plan_and_looks_made_by_the_copier()
    {
        var b = new TransformBench();
        var plan = Applied(b.T.Apply(new Command.CreatePlan(NewPlan("Pumps")))).PlansOpened[0].Id;
        var group = TestKitGroup();
        var original = Assert.Single(b.T.Placed(Draft(
            new Intent.Build(new Placement(b.T.Catalogue.Named("StructureCableStraight").Key, new Cell(5, 0, 0), Turn.None), Option<Finish>.None),
            Option.Some(plan), ValueSet.Of(group))).Added);

        var copy = Applied(b.Transform(All, new Transformation.Move(new CellOffset(0, 5, 0)), Copying));

        var image = Assert.Single(copy.Added);
        Assert.Equal(original with { Id = image.Id, Intent = image.Intent, Revision = image.Revision, MadeBy = Copier }, image);
        Assert.Equal(new Cell(5, 5, 0), ((Intent.Build)image.Intent).Target.At);
        Assert.Equal(2, b.T.Board.Index.Ghosts.Count);
    }

    [Fact]
    public void A_move_takes_the_ghosts_away_from_where_they_were_and_keeps_their_ids()
    {
        var b = new TransformBench();
        var id = b.Build("StructureFrame", new Cell(10, 10, 10));

        var moved = Applied(b.Transform(new Selector.Ids(ValueList.Of(id)), new Transformation.Move(new CellOffset(40, 0, 0))));

        Assert.Empty(moved.Removed);
        Assert.Empty(moved.Added);
        Assert.Equal(id, Assert.Single(moved.Changed).After.Id);
        Assert.Equal(new[] { new Cell(50, 10, 10) }, b.Placed().Select(static p => p.At));
    }

    [Fact]
    public void Four_quarter_turns_about_a_pivot_give_back_every_placement()
    {
        var b = new TransformBench();
        b.Build("StructureCableCorner", new Cell(5, 0, 0));
        b.Build("StructureFrame", new Cell(30, 10, 10));
        b.Build("ModSteamCorner", new Cell(20, 5, -15), b.Sending(Direction.XPlus, Direction.YPlus));
        var original = b.Placed();
        var quarter = new Transformation.Rotate(new Cell(10, 10, 10), b.Sending(Direction.ZPlus, Direction.XPlus));

        var shapes = new List<HashSet<Placement>>();
        for (var i = 0; i < 4; i++)
        {
            Applied(b.Transform(All, quarter));
            shapes.Add(b.Placed());
        }

        Assert.NotEqual(original, shapes[0]);
        Assert.Equal(original, shapes[3]);
    }

    [Fact]
    public void A_turned_piece_takes_its_spots_and_ends_turned_about_the_pivot()
    {
        var b = new TransformBench();
        var placement = new Placement(b.T.Catalogue.Named("StructureCableCorner").Key, new Cell(15, 5, 0), Turn.None);
        var pivot = new Cell(10, 10, 10);
        var turn = b.Sending(Direction.XPlus, Direction.YPlus);

        var turned = Ok(b.Placements.Apply(new Transformation.Rotate(pivot, turn), placement));

        var expected = Shape(b, placement).Ends.Select(e => (Cell: Turned(b, e.Cell, pivot, turn), Facing: b.T.Grid.Apply(turn, e.Facing))).ToHashSet();
        Assert.Equal(expected, Shape(b, turned).Ends.Select(static e => (e.Cell, e.Facing)).ToHashSet());
    }

    [Fact]
    public void A_move_off_the_grid_is_refused_naming_the_ghosts_it_would_move_off()
    {
        var b = new TransformBench();
        var frame = b.Build("StructureFrame", new Cell(10, 10, 10));
        b.Build("StructureCableStraight", new Cell(5, 0, 0));

        var why = Refusal(b.Transform(All, new Transformation.Move(new CellOffset(5, 0, 0))));

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Equal("off-grid", why.Facts["problem"]);
        Assert.Equal(frame.Value.ToString(), why.Facts["ghosts"]);
        Assert.Equal(2, b.T.Board.Index.Ghosts.Count);
    }

    [Fact]
    public void A_ghost_about_a_standing_structure_is_reported_not_transformed()
    {
        var b = new TransformBench();
        b.Build("StructureFrame", new Cell(10, 10, 10));
        var removal = Assert.Single(b.T.Placed(Draft(new Intent.Remove(
            new StructureRef(new ReferenceId(77), b.T.Catalogue.Named("StructureFrame").Key, new Cell(30, 10, 10))))).Added).Id;

        var why = Refusal(b.Transform(All, new Transformation.Move(new CellOffset(0, 20, 0)), Copying));

        Assert.Equal("targets-a-structure", why.Facts["problem"]);
        Assert.Equal(removal.Value.ToString(), why.Facts["ghosts"]);
    }

    [Fact]
    public void A_transformation_this_version_does_not_know_is_refused_unknown_kind()
    {
        var b = new TransformBench();
        b.Build("StructureFrame", new Cell(10, 10, 10));

        var why = Refusal(b.Transform(All, UnknownCase.Of<Transformation>()));

        Assert.Equal(ReasonCodes.UnknownKind, why.Code);
        Assert.Single(b.T.Board.Index.Ghosts);
    }

    [Fact]
    public void An_empty_selection_is_refused_with_the_selectors_reason()
    {
        var b = new TransformBench();

        var why = Refusal(b.Transform(All, new Transformation.Move(new CellOffset(20, 0, 0))));

        Assert.Equal(ReasonCodes.NotFound, why.Code);
    }

    [Fact]
    public void A_large_selection_is_sent_then_answered_once_its_work_finishes()
    {
        var b = new TransformBench();
        var drafts = Frames(b, TransformWork.PerStep * 3 + 1);

        var submission = b.C.Service.Submit(b.C.NextId(), new Command.Transform(All, new Transformation.Move(new CellOffset(0, 20, 0)), Copying), Alice);
        Assert.IsType<Submission.Sent>(submission);
        for (var frame = 0; frame < 100 && b.Answers.Count == 0; frame++)
            b.C.Frame();

        Assert.Equal(drafts, Applied(Assert.Single(b.Answers)).Added.Count);
    }

    [Fact]
    public void The_work_takes_one_slice_of_ghosts_a_step()
    {
        var b = new TransformBench();
        Frames(b, TransformWork.PerStep * 2 + 1);
        var work = new TransformWork(b.Placements, new Command.Transform(All, new Transformation.Move(new CellOffset(0, 20, 0)), Copying), b.T.View);

        Assert.Equal(Lattice.Core.Slices.StepOutcome.More, work.Step());
        Assert.Equal(Lattice.Core.Slices.StepOutcome.More, work.Step());
        Assert.Equal(Lattice.Core.Slices.StepOutcome.Done, work.Step());
    }

    [Fact]
    public void A_board_that_moved_while_the_work_ran_is_transformed_as_it_stands()
    {
        var b = new TransformBench();
        Frames(b, TransformWork.PerStep * 2);
        var work = new TransformWork(b.Placements, new Command.Transform(All, new Transformation.Move(new CellOffset(0, 20, 0)), Copying), b.T.View);

        Assert.Equal(Lattice.Core.Slices.StepOutcome.More, work.Step());
        b.T.Placed(b.T.Draft("StructureFrame", new Cell(10, 10, 50)));
        while (work.Step() == Lattice.Core.Slices.StepOutcome.More)
        {
        }

        var place = Assert.IsType<Command.Place>(TestSelect.Ok(work.Found));
        Assert.Equal(TransformWork.PerStep * 2 + 1, place.Ghosts.Count);
        Assert.Contains(place.Ghosts, static d => ((Intent.Build)d.Intent).Target.At == new Cell(10, 30, 50));
    }

    private static int Frames(TransformBench b, int count)
    {
        var drafts = new GhostDraft[count];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = b.T.Draft("StructureFrame", new Cell(10 + 20 * i, 10, 10));
        b.T.Placed(drafts);
        return count;
    }

    private static void SameShape(
        (HashSet<SpotKey> Spots, HashSet<(Cell, Direction, MillimetrePoint?)> Ends) expected,
        (HashSet<SpotKey> Spots, HashSet<(Cell, Direction, MillimetrePoint?)> Ends) actual)
    {
        Assert.Equal(expected.Spots, actual.Spots);
        Assert.Equal(expected.Ends, actual.Ends);
    }

    private static Turn TestKitTurn(int index) => Turn.Create(index).TryGet(out var turn) ? turn : Turn.None;

    private static GroupName TestKitGroup() => GroupName.Create("pumps").TryGet(out var group) ? group : default;

    private static T Ok<T>(Result<T, TransformProblem> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new System.InvalidOperationException(result.ToString());

    private static PlacedShape Shape(TransformBench b, Placement placement) => TestSelect.Ok(b.Shapes.Of(placement));

    private static Cell Turned(TransformBench b, Cell cell, Cell pivot, Turn turn)
    {
        var offset = b.T.Grid.Apply(turn, new CellOffset(cell.X - pivot.X, cell.Y - pivot.Y, cell.Z - pivot.Z));
        return pivot.Plus(offset);
    }

    private static (HashSet<SpotKey> Spots, HashSet<(Cell, Direction, MillimetrePoint?)> Ends) Footprint(PlacedShape shape) =>
        (shape.Spots.ToHashSet(), shape.Ends.Select(static e => (e.Cell, e.Facing, e.Point.TryGet(out var p) ? p : (MillimetrePoint?)null)).ToHashSet());

    private static (HashSet<SpotKey> Spots, HashSet<(Cell, Direction, MillimetrePoint?)> Ends) Reflected(PlacedShape shape, MirrorPlane plane)
    {
        var grid = new Lattice.Core.Grid.CubeGrid();
        var spots = shape.Spots.Select(s => ReflectSpot(grid, s, plane)).ToHashSet();
        var ends = shape.Ends.Select(e => (grid.Reflect(e.Cell, plane), grid.Reflect(e.Facing, plane.Normal),
            e.Point.TryGet(out var p) ? ReflectPoint(p, plane) : (MillimetrePoint?)null)).ToHashSet();
        return (spots, ends);
    }

    internal static SpotKey ReflectSpot(Lattice.Core.Grid.CubeGrid grid, SpotKey spot, MirrorPlane plane)
    {
        var cell = grid.Reflect(spot.Cell, plane);
        return spot.Slot switch
        {
            Slot.FaceXPlus when plane.Normal == Axis.X => new SpotKey(cell, Slot.FaceXMinus),
            Slot.FaceXMinus when plane.Normal == Axis.X => new SpotKey(cell, Slot.FaceXPlus),
            Slot.FaceYPlus when plane.Normal == Axis.Y => new SpotKey(cell, Slot.FaceYMinus),
            Slot.FaceYMinus when plane.Normal == Axis.Y => new SpotKey(cell, Slot.FaceYPlus),
            Slot.FaceZPlus when plane.Normal == Axis.Z => new SpotKey(cell, Slot.FaceZMinus),
            Slot.FaceZMinus when plane.Normal == Axis.Z => new SpotKey(cell, Slot.FaceZPlus),
            _ => new SpotKey(cell, spot.Slot),
        };
    }

    private static MillimetrePoint ReflectPoint(MillimetrePoint p, MirrorPlane plane) => plane.Normal switch
    {
        Axis.X => p with { X = 2L * plane.At * 100 - p.X },
        Axis.Y => p with { Y = 2L * plane.At * 100 - p.Y },
        _ => p with { Z = 2L * plane.At * 100 - p.Z },
    };
}
