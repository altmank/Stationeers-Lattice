using System;
using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

public sealed class HostTests
{
    [Fact]
    public void A_modded_piece_is_placed_and_validated_like_any_other()
    {
        using var world = Start(WithConduit);

        var placed = Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.Of(Draft(Conduit.Key, At(0)))))).Changes.Added;
        var report = Ready(world, () => Must(world.System.Validation.Check(new Selector.OfPiece(Conduit.Key))));

        Assert.IsType<Finding.Passes>(Assert.Single(report.Findings));
        Assert.Equal(Assert.Single(placed).Id, report.Findings[0].Ghost);
    }

    [Fact]
    public void A_modded_piece_joining_a_base_kit_takes_the_next_option_and_is_placed()
    {
        var thin = Must(TestPiece.Create("ModCableThin", "ItemCableCoil", PieceGrid.Small))
            .WithEnd(new CellOffset(0, 0, 0), SignedAxis.ZPlus, TestNetwork.Power)
            .WithCell(new CellOffset(0, 0, 0), TestSlot.Cable)
            .WithCollision(2);
        using var world = Start(TestScenario.Empty.WithPiece(thin));

        Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.Of(Draft(thin.Key, At(0))))));
    }

    [Fact]
    public void A_hand_build_completes_the_ghost_it_stands_in()
    {
        var frame = Hand("StructureFrame");
        using var world = Start(TestScenario.Empty);
        var completed = new List<GhostId>();
        world.System.Events.Completed += (ghost, _) => completed.Add(ghost);
        var ghost = Assert.Single(Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.Of(Draft(frame, new Cell(11, 11, 11)))))).Changes.Added);
        Must(world.Settle());

        var built = Must(world.Build(frame, new Cell(11, 11, 11), default));
        Must(world.Settle());
        var state = Option<BuildState>.None;
        for (var i = 0; i < 16 && !state.Map(static s => s is BuildState.Built).Reduce(false); i++)
        {
            state = Option.Some(Must(world.Advance(built)));
            Must(world.Settle());
        }

        Assert.True(state.Map(static s => s is BuildState.Built).Reduce(false));
        Assert.Contains(ghost.Id, completed);
        Assert.True(world.Advance(built).IsError);
    }

    [Fact]
    public void One_scenario_run_twice_gives_equal_change_sets_and_reports()
    {
        Assert.Equal(Run(), Run());

        static string Run()
        {
            using var world = Start(WithConduit.WithStructure(Hand("StructureFrame"), new Cell(11, 11, 11), default));
            var changes = new List<ChangeSet>();
            world.System.Events.Changed += changes.Add;
            for (var i = 0; i < 4; i++)
                world.System.Commands.Submit(new CommandId(new Guid(i + 1, 0, 0, new byte[8])),
                    new Command.Place(ValueList.Of(Draft(Conduit.Key, At(3 * i)))), world.Viewer);
            Must(world.Settle());
            var report = Ready(world, () => Must(world.System.Validation.Check(new Selector.Everything())));
            var text = new System.Text.StringBuilder();
            foreach (var change in changes)
                text.Append(change).Append('|').Append(string.Join(",", change.Added)).Append('\n');
            text.Append(string.Join(",", report.Findings)).Append('@').Append(report.At);
            return text.ToString();
        }
    }

    [Fact]
    public void Board_reads_on_an_unchanged_board_allocate_nothing_and_an_allocating_call_is_caught()
    {
        using var world = Start(WithConduit);
        var ghost = Assert.Single(Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.Of(Draft(Conduit.Key, At(0)))))).Changes.Added);
        Must(world.Settle());
        var board = world.System.Board;

        Allocations.AssertNone(() => board.Find(ghost.Id), "Board.Find");
        Allocations.AssertNone(() => board.At(At(0)), "Board.At");
        Assert.Equal(0, Allocations.Measure(() => _ = board.Revision));
        var caught = Assert.Throws<AllocationException>(() => Allocations.AssertNone(() => _ = new byte[64], "new byte[64]"));
        Assert.Equal("new byte[64]", caught.Call);
        Assert.True(caught.Bytes > 0);
    }

    [Fact]
    public void One_world_runs_at_a_time_and_disposing_clears_the_current_system()
    {
        var world = Start(TestScenario.Empty);
        Assert.True(LatticeSystem.Current.TryGet(out var current) && ReferenceEquals(current, world.System));

        var second = TestWorld.Start(TestScenario.Empty);
        Assert.True(second.TryGetError(out var why) && why.Code == TestCodes.WorldRunning);

        world.Dispose();
        world.Dispose();
        Assert.True(LatticeSystem.Current.IsNone);
        world.Frame();
        Assert.Equal(0, world.FrameCount);
        Start(TestScenario.Empty).Dispose();
    }

    [Fact]
    public void Settle_is_refused_while_a_consumers_slice_never_ends()
    {
        using var world = Start(TestScenario.Empty);
        using var forever = Must(world.System.Slices.Schedule(new SlicedWorkDescriptor(Must(SliceId.Create(Mod, "forever")), static () => true)));

        var settled = world.Settle(maxFrames: 5);

        Assert.True(settled.TryGetError(out var why) && why.Code == TestCodes.NotSettled);
        Assert.Equal(5, world.FrameCount);
    }

    [Fact]
    public void A_scenario_that_does_not_fit_is_refused_with_a_reason()
    {
        var copy = Must(TestPiece.Create("StructureFrame", "ItemSteelFrames", PieceGrid.Large));
        var noNetwork = Conduit.WithEnd(new CellOffset(0, 0, 0), SignedAxis.XPlus, default(TestNetwork));

        Assert.Equal(ReasonCodes.AlreadyRegistered, Code(TestWorld.Start(TestScenario.Empty.WithPiece(copy))));
        Assert.Equal(ReasonCodes.InvalidValue, Code(TestWorld.Start(TestScenario.Empty.WithPiece(noNetwork))));
        Assert.Equal(ReasonCodes.UnknownPiece, Code(TestWorld.Start(TestScenario.Empty.WithStructure(Conduit.Key, At(0), default))));
        Assert.Equal(ReasonCodes.NotFound, Code(TestWorld.Start(TestScenario.Empty.WithSave(Path.Combine(Path.GetTempPath(), "no-such.save")))));
        Assert.Equal(ReasonCodes.NotFound, TestCatalogue.FromFile(Path.Combine(Path.GetTempPath(), "no-such.lcat")).Match(static _ => default, static r => r.Code));
        Assert.True(TestPiece.Create("has space", "Kit", PieceGrid.Small).IsError);
        Assert.True(TestNetwork.Modded("", 1).IsError);
        Assert.True(LatticeSystem.Current.IsNone);

        static ReasonCode Code(Result<TestWorld, Reason> started) => started.Match(static w => { w.Dispose(); return default; }, static r => r.Code);
    }

    [Fact]
    public void A_catalogue_the_game_wrote_reads_from_its_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "lattice-testing-" + Guid.NewGuid().ToString("N") + ".lcat");
        File.WriteAllText(path, "lattice-catalogue 3\npiece ModBlock kit=ItemModBlock option=0 grid=large\n  step material=ItemModBlock*1 exit=ItemWrench air=no\n");
        try
        {
            var catalogue = Must(TestCatalogue.FromFile(path));
            Assert.Equal(1, catalogue.Count);
            using var world = Start(TestScenario.Empty.WithCatalogue(catalogue));
            Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.Of(Draft(catalogue.Key("ModBlock").Reduce(default(PrefabKey)), new Cell(11, 11, 11))))));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_worlds_styles_are_Lattices_own_resolution()
    {
        using var world = Start(WithConduit);
        var ghost = Assert.Single(Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.Of(Draft(Conduit.Key, At(0)))))).Changes.Added);

        var set = Must(world.System.Styles.Set(Must(StyleSetId.Create(Mod, "selected"))));
        set.Put(ghost.Id);
        Must(world.Settle());

        Assert.True(set.Contains(ghost.Id));
        Assert.True(world.System.Styles.Explain(ghost.Id).IsOk);
    }

    [Fact]
    public void The_board_lists_its_groups_with_counts_and_reads_them_without_allocating()
    {
        using var world = Start(WithConduit);
        var hydro = Must(GroupName.Create("Hydro"));
        var drafts = new List<GhostDraft>();
        for (var i = 0; i < 3; i++)
            drafts.Add(Draft(Conduit.Key, At(3 * i)) with { Groups = ValueSet.Of(hydro) });
        Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.From(drafts))));
        Must(world.Settle());
        var board = world.System.Board;

        Assert.Equal(new[] { new GroupCount(hydro, 3) }, board.Groups);
        Allocations.AssertNone(() => _ = board.Groups, "Board.Groups");
    }

    [Fact]
    public void Game_time_moves_only_when_the_test_moves_it()
    {
        using var world = Start(TestScenario.Empty);
        var start = world.GameTime;

        world.Frames(10);
        world.Advance(new GameTime(30));

        Assert.Equal(start.Seconds + 30, world.GameTime.Seconds);
        Assert.Equal(10, world.FrameCount);
    }
}
