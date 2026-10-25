using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Selectors;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.Saves;
using Lattice.TestKit.World;
using Lattice.Tests.Board;
using Lattice.Tests.Validation;
using Xunit.Abstractions;

namespace Lattice.Tests.Selectors;

/// <summary>
/// A box selector picks every structure (or ghost) whose own cell is in the box, the answer a scan of the whole world
/// gives, at a cost in what the box holds rather than in the size of the base.
/// </summary>
public sealed class BoxIndexTests(ITestOutputHelper output)
{
    private const string RealSave = "Vulcan2_4.save";

    /// <summary>The mirror and selection over a world, started.</summary>
    private sealed class Over
    {
        public Over(ICatalogue catalogue, FakeWorld world)
        {
            var grid = new CubeGrid();
            var shapes = new Shapes(catalogue, grid);
            Mirror = new WorldMirror(shapes, world);
            Mirror.Start(world, new FakeScan(world));
            Selection = new StructureSelection(shapes, grid, Mirror, world);
        }

        public WorldMirror Mirror { get; }

        public StructureSelection Selection { get; }

        public List<StructureInfo> All()
        {
            var all = new List<StructureInfo>();
            Mirror.All(all);
            return all;
        }
    }

    /// <summary>What a whole-world scan picks: every structure whose own cell is in the box, in id order.</summary>
    private static List<StructureRef> Scanned(List<StructureInfo> all, Cell a, Cell b)
    {
        var box = CellBox.Between(a, b);
        return all.Where(s => box.Contains(s.Ref.At)).Select(static s => s.Ref).OrderBy(static r => r.Id.Value).ToList();
    }

    private static List<StructureRef> Selected(StructureSelection selection, Cell a, Cell b) =>
        TestKitIds.Must(selection.Select(new StructureSelector.Box(a, b))).ToList();

    /// <summary>Random boxes: around a structure, at sizes from a cell to tens of metres, and some far from anything.</summary>
    private static IEnumerable<(Cell A, Cell B)> Boxes(List<StructureInfo> all, int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var centre = i % 10 == 9 ? new Cell(random.Next(-90000, 90000), random.Next(-90000, 90000), random.Next(-90000, 90000)) : all[random.Next(all.Count)].Ref.At;
            var (dx, dy, dz) = (random.Next(0, 300), random.Next(0, 300), random.Next(0, 300));
            var off = random.Next(-20, 21);
            yield return (new Cell(centre.X - dx + off, centre.Y - dy, centre.Z - dz), new Cell(centre.X + dx, centre.Y + dy + off, centre.Z + dz));
        }
    }

    /// <summary>A base of hand pieces and face pieces at random cells: small pieces on half metres, large on cell centres and faces.</summary>
    private static (FakeCatalogue Catalogue, FakeWorld World) HandBase(int count, int seed)
    {
        var board = new TestBoard(default, FaceSpotTests.Pieces());
        var catalogue = board.Catalogue;
        var world = new FakeWorld(catalogue);
        var random = new Random(seed);
        var pieces = catalogue.Pieces.ToList();
        for (var i = 0; i < count; i++)
        {
            var piece = pieces[random.Next(pieces.Count)].Facts;
            var large = piece.Grid == GridSize.Large;
            Cell at;
            if (piece.Face.IsSome)
                at = new Cell(10 + 20 * random.Next(-40, 40), 10 + 20 * random.Next(-5, 5), 20 * random.Next(-40, 40));
            else if (large)
                at = new Cell(10 + 20 * random.Next(-40, 40), 10 + 20 * random.Next(-5, 5), 10 + 20 * random.Next(-40, 40));
            else
                at = new Cell(5 * random.Next(-160, 160), 5 * random.Next(-20, 20), 5 * random.Next(-160, 160));
            var turn = TestKitIds.Must(Turn.Create(random.Next(Turn.Count)));
            world.Load(new StructureInfo(new StructureRef(new ReferenceId(1 + i), piece.Key, at), turn, new BuildState.Built(),
                Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));
        }

        return (catalogue, world);
    }

    [Fact]
    public void Boxes_on_a_hand_base_pick_what_a_scan_of_the_whole_world_picks()
    {
        var (catalogue, world) = HandBase(3000, 7);
        var over = new Over(catalogue, world);
        var all = over.All();

        foreach (var (a, b) in Boxes(all, 400, 11))
            Assert.Equal(Scanned(all, a, b), Selected(over.Selection, a, b));
    }

    [Fact]
    public void Boxes_follow_the_world_as_structures_come_move_and_go()
    {
        var (catalogue, world) = HandBase(800, 3);
        var over = new Over(catalogue, world);
        var all = over.All();
        var (a, b) = (new Cell(-200, -100, -200), new Cell(200, 100, 200));
        Selected(over.Selection, a, b);

        var inside = all.First(s => CellBox.Between(a, b).Contains(s.Ref.At));
        world.Remove(inside.Ref.Id);
        var moved = all.First(s => !CellBox.Between(a, b).Contains(s.Ref.At) && s.Ref.Id != inside.Ref.Id);
        var to = new Cell(moved.Ref.At.X % 20, moved.Ref.At.Y % 20, moved.Ref.At.Z % 20);
        world.Move(ValueList.Of(new StructureMove(moved.Ref, to, moved.Turn)));

        Assert.Equal(Scanned(over.All(), a, b), Selected(over.Selection, a, b));
    }

    [Fact]
    public void Ghost_boxes_pick_what_a_scan_of_the_whole_board_picks()
    {
        var select = new TestSelect(FaceSpotTests.Pieces());
        select.Start();
        var random = new Random(5);
        var pieces = select.T.Catalogue.Pieces.ToList();
        for (var i = 0; i < 1500; i++)
        {
            var piece = pieces[random.Next(pieces.Count)].Facts;
            Cell at = piece.Face.IsSome ? new Cell(10 + 20 * random.Next(-20, 20), 10, 20 * random.Next(-20, 20))
                : piece.Grid == GridSize.Large ? new Cell(10 + 20 * random.Next(-20, 20), 10 + 20 * random.Next(-3, 3), 10 + 20 * random.Next(-20, 20))
                : new Cell(5 * random.Next(-80, 80), 5 * random.Next(-10, 10), 5 * random.Next(-80, 80));
            select.T.Place(select.T.Draft(select.T.Catalogue.NameOf(piece.Key), at, TestKitIds.Must(Turn.Create(random.Next(Turn.Count)))));
        }

        var ghosts = select.T.Board.Index.Ghosts.Values.ToList();
        foreach (var (a, b) in Boxes(ghosts.Select(static g => new StructureInfo(new StructureRef(new ReferenceId(g.Id.Value), default, GhostPieces.Of(g).Own), default,
                     new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None)).ToList(), 300, 13))
        {
            var box = CellBox.Between(a, b);
            var expected = ghosts.Where(g => box.Contains(GhostPieces.Of(g).Own)).Select(static g => g.Id).ToHashSet();

            Assert.Equal(expected, select.Pick(new Selector.Box(a, b)));
        }
    }

    [SaveFixtureFact(RealSave)]
    public void Boxes_on_the_real_base_pick_what_a_scan_of_the_whole_world_picks()
    {
        var catalogue = Fixtures.Catalogue();
        var save = TestKitIds.MustRead(SaveLoader.Load(Fixtures.Save(RealSave).Reduce(""), catalogue));
        var over = new Over(catalogue, save.World);
        var all = over.All();

        foreach (var (a, b) in Boxes(all, 300, 17))
            Assert.Equal(Scanned(all, a, b), Selected(over.Selection, a, b));
    }

    [SaveFixtureFact(RealSave)]
    [Trait("Category", "Perf")]
    public void A_ten_metre_box_on_the_real_base_answers_in_under_a_millisecond_and_reused_allocates_nothing()
    {
        var catalogue = Fixtures.Catalogue();
        var save = TestKitIds.MustRead(SaveLoader.Load(Fixtures.Save(RealSave).Reduce(""), catalogue));
        Measure(new Over(catalogue, save.World), $"real base ({save.World.Count} structures)");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_ten_metre_box_on_a_forty_thousand_structure_base_answers_in_under_a_millisecond_and_reused_allocates_nothing()
    {
        var (catalogue, world) = HandBase(40_000, 23);
        Measure(new Over(catalogue, world), "40,000-structure fake");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_ten_metre_box_over_forty_thousand_ghosts_answers_in_under_a_millisecond()
    {
        var select = new TestSelect();
        select.Start();
        var drafts = new GhostDraft[40_000];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = select.T.Draft("StructureFrame", new Cell(10 + 20 * (i % 200), 10, 10 + 20 * (i / 200)));
        select.T.Place(drafts);
        var random = new Random(31);
        var boxes = Enumerable.Range(0, 200).Select(_ =>
        {
            var c = new Cell(10 + 20 * random.Next(200), 10, 10 + 20 * random.Next(200));
            return new Selector.Box(new Cell(c.X - 50, c.Y - 50, c.Z - 50), new Cell(c.X + 50, c.Y + 50, c.Z + 50));
        }).ToList();
        select.T.View.Select(boxes[0]);

        var clock = Stopwatch.StartNew();
        foreach (var box in boxes.Skip(1))
            TestKitIds.Must(select.T.View.Select(box));
        var each = clock.Elapsed.TotalMilliseconds / (boxes.Count - 1);

        output.WriteLine($"40,000 ghosts: a 10 m box {each:F3} ms each");
        Assert.True(each < 1.0, $"{each:F3} ms a box");
    }

    /// <summary>Asks 200 different 10 m boxes around structures, then one box again and again.</summary>
    private void Measure(Over over, string what)
    {
        var all = over.All();
        var random = new Random(29);
        var boxes = Enumerable.Range(0, 200).Select(_ =>
        {
            var c = all[random.Next(all.Count)].Ref.At;
            return new StructureSelector.Box(new Cell(c.X - 50, c.Y - 50, c.Z - 50), new Cell(c.X + 50, c.Y + 50, c.Z + 50));
        }).ToList();
        over.Selection.Select(boxes[0]);

        var clock = Stopwatch.StartNew();
        var picked = 0;
        foreach (var box in boxes.Skip(1))
            picked += TestKitIds.Must(over.Selection.Select(box)).Count;
        var each = clock.Elapsed.TotalMilliseconds / (boxes.Count - 1);

        var again = boxes[^1];
        over.Selection.Select(again);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            over.Selection.Select(again);
        var steady = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"{what}: a 10 m box {each:F3} ms each ({picked / (boxes.Count - 1)} structures on average); reused 100 times {steady} B");
        Assert.True(each < 1.0, $"{each:F3} ms a box");
        Assert.Equal(0, steady);
    }
}

/// <summary>A test over a save fixture: skipped until make-fixtures.ps1 has copied it into tests/fixtures/generated.</summary>
public sealed class SaveFixtureFactAttribute : FactAttribute
{
    public SaveFixtureFactAttribute(string save)
    {
        if (Fixtures.Save(save).IsNone)
            Skip = $"{save} is not in tests/fixtures/generated: run tests/fixtures/make-fixtures.ps1.";
    }
}

