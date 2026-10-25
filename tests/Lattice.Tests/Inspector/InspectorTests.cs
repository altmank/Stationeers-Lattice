using System;
using System.Linq;
using Lattice.Api;
using Lattice.Tests.Board;
using Lattice.TestKit;
using Xunit;
using static Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Inspector.InspectorBench;

namespace Lattice.Tests.Inspector;

/// <summary>A ghost's and a plan's story from the plan files, branches included, and the change-log export.</summary>
public sealed class InspectorTests
{
    // Ghost stories

    [Fact]
    public void A_ghost_edited_three_times_shows_four_entries_with_actors()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place(actor: Alice);
        bench.Relabel(ghost, "one", Bob);
        bench.Relabel(ghost, "two", Alice);
        bench.Relabel(ghost, "three", Bob);

        var story = Told(bench.Inspector.Story(ghost));

        Assert.Equal(ghost, story.Ghost);
        Assert.Equal([Alice, Bob, Alice, Bob], story.Entries.Select(e => e.By));
        Assert.Equal([1L, 2L, 3L, 4L], story.Entries.Select(e => e.Line.Value));
    }

    [Fact]
    public void An_entry_carries_its_line_as_written_with_its_command_and_time()
    {
        var bench = new InspectorBench();
        var placed = bench.Apply(new Command.Place(ValueList.Of(bench.T.Draft("StructureFrame", new Cell(10, 10, 10)))), Alice);
        var ghost = placed.Added[0].Id;

        var entry = Assert.Single(Told(bench.Inspector.Story(ghost)).Entries);

        Assert.Equal(placed.Command, entry.Command);
        Assert.Equal(placed.At, entry.At);
        Assert.True(entry.Follows.IsNone);
        Assert.Equal(bench.Files.Text(Loose).Split('\n')[1], entry.Written);
    }

    [Fact]
    public void A_ghost_story_leaves_out_lines_of_other_ghosts()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place();
        var other = bench.Place();
        bench.Relabel(other, "other");
        bench.Relabel(ghost, "mine");

        Assert.Equal([1L, 4L], Told(bench.Inspector.Story(ghost)).Entries.Select(e => e.Line.Value));
    }

    [Fact]
    public void A_ghost_no_longer_on_the_board_still_has_its_story()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place();
        bench.Apply(new Command.Remove(Ids(ghost)));

        var story = Told(bench.Inspector.Story(ghost));

        Assert.Equal(2, story.Entries.Count);
        Assert.Contains(" remove ", story.Entries[1].Written);
    }

    [Fact]
    public void A_ghost_story_runs_across_branches_in_file_order()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place();
        bench.Relabel(ghost, "tuesday");
        bench.Files.Rewind(Loose, 1);
        bench.Relabel(ghost, "monday-again");

        var story = Told(bench.Inspector.Story(ghost));

        Assert.Equal([1L, 2L, 3L], story.Entries.Select(e => e.Line.Value));
        Assert.Equal(Option.Some(new LineNumber(1)), story.Entries[2].Follows);
    }

    [Fact]
    public void A_ghost_moved_to_another_plan_has_one_entry_per_change_across_both_files()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place();
        var store = bench.CreatePlan("Store");
        bench.Apply(new Command.MoveToPlan(Ids(ghost), store));
        bench.Relabel(ghost, "in-store");

        var story = Told(bench.Inspector.Story(ghost));

        Assert.Equal(3, story.Entries.Count);
        Assert.Equal(story.Entries.OrderBy(e => e.At).Select(e => e.Command), story.Entries.Select(e => e.Command));
        Assert.Equal(3, story.Entries.Select(e => e.Command).Distinct().Count());
    }

    [Fact]
    public void A_ghost_of_a_modded_piece_has_its_story()
    {
        var bench = new InspectorBench(Pieces(
            "piece ModBeam kit=ItemModBeamKit option=0 grid=large\n" +
            "  step material=ItemModBeamKit*1 exit=ItemModSpanner air=no\n"));
        var ghost = bench.Place("ModBeam");
        bench.Relabel(ghost, "modded");

        Assert.Equal(2, Told(bench.Inspector.Story(ghost)).Entries.Count);
    }

    [Fact]
    public void An_unknown_ghost_is_refused_not_found()
    {
        var bench = new InspectorBench();
        bench.Place();

        Assert.True(bench.Inspector.Story(new GhostId(999)).TryGetError(out var why));
        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal("999", why.Facts["ghost"]);
    }

    // Plan stories and branches

    [Fact]
    public void A_branched_plan_lists_both_branches()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place();
        bench.Relabel(ghost, "a");
        bench.Files.Save("monday");
        bench.Relabel(ghost, "b");
        bench.Relabel(ghost, "c");
        bench.Files.Save("tuesday");
        bench.Files.Rewind(Loose, 2);
        bench.Relabel(ghost, "monday-again");

        var story = Told(bench.Inspector.Story(Loose));

        Assert.Equal(2, story.Branches.Count);
        Assert.Equal(new LineNumber(0), story.Branches[0].Follows);
        Assert.Equal(new LineNumber(4), story.Branches[0].Head);
        Assert.Equal(["monday", "tuesday"], story.Branches[0].Saves.Select(s => s.Value));
        Assert.Equal(new LineNumber(2), story.Branches[1].Follows);
        Assert.Equal(new LineNumber(5), story.Branches[1].Head);
        Assert.Empty(story.Branches[1].Saves);
    }

    [Fact]
    public void A_plan_story_shows_the_lines_of_the_branch_it_is_loaded_on()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place();
        bench.Relabel(ghost, "a");
        bench.Relabel(ghost, "b");
        bench.Files.Rewind(Loose, 2);
        bench.Relabel(ghost, "c");

        Assert.Equal([1L, 2L, 4L], Told(bench.Inspector.Story(Loose)).Entries.Select(e => e.Line.Value));

        bench.Files.Rewind(Loose, 3);

        Assert.Equal([1L, 2L, 3L], Told(bench.Inspector.Story(Loose)).Entries.Select(e => e.Line.Value));
    }

    [Fact]
    public void A_plan_not_open_shows_the_branch_of_its_newest_line()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place();
        bench.Relabel(ghost, "a");
        bench.Files.Rewind(Loose, 1);
        bench.Relabel(ghost, "b");
        bench.Files.Close(Loose);

        Assert.Equal([1L, 3L], Told(bench.Inspector.Story(Loose)).Entries.Select(e => e.Line.Value));
    }

    [Fact]
    public void A_branch_off_a_branch_is_listed_with_its_own_start()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place();
        bench.Relabel(ghost, "a");
        bench.Files.Rewind(Loose, 1);
        bench.Relabel(ghost, "b");
        bench.Relabel(ghost, "c");
        bench.Files.Rewind(Loose, 3);
        bench.Relabel(ghost, "d");

        var branches = Told(bench.Inspector.Story(Loose)).Branches;

        Assert.Equal([(0L, 2L), (1L, 4L), (3L, 5L)], branches.Select(b => (b.Follows.Value, b.Head.Value)));
    }

    [Fact]
    public void A_plan_without_lines_has_an_empty_story()
    {
        var bench = new InspectorBench();

        var story = Told(bench.Inspector.Story(Loose));

        Assert.Empty(story.Entries);
        Assert.Empty(story.Branches);
    }

    [Fact]
    public void An_unknown_plan_is_refused_not_found()
    {
        var bench = new InspectorBench();

        Assert.True(bench.Inspector.Story(new PlanId(77)).TryGetError(out var why));
        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.True(bench.Inspector.Export(new PlanId(77)).IsError);
    }

    [Fact]
    public void A_line_this_version_cannot_read_is_left_out_of_stories_but_kept_in_the_export()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place();
        var kept = bench.Files.Keep(Loose);
        bench.Relabel(ghost, "after");

        Assert.Equal([1L, 3L], Told(bench.Inspector.Story(Loose)).Entries.Select(e => e.Line.Value));
        Assert.Equal(2, Told(bench.Inspector.Story(ghost)).Entries.Count);
        Assert.Contains(kept, Told(bench.Inspector.Export(Loose)));
    }

    // Export

    [Fact]
    public void Export_is_the_plan_file_as_written_every_branch_included()
    {
        var bench = new InspectorBench();
        var ghost = bench.Place();
        bench.Relabel(ghost, "a");
        bench.Files.Rewind(Loose, 1);
        bench.Relabel(ghost, "b");

        var exported = Told(bench.Inspector.Export(Loose));

        Assert.Equal(bench.Files.Text(Loose).TrimEnd('\n').Split('\n'), exported);
        Assert.StartsWith("lattice-plan ", exported[0]);
        Assert.Equal(4, exported.Count);
    }

    private static T Told<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new InvalidOperationException(result.ToString());
}
