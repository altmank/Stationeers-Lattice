using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.Tests.Board;
using Xunit.Abstractions;

namespace Lattice.Tests.Saving;

/// <summary>
/// Plan files and game saves, loading an older save included.
/// </summary>
public sealed class SavingTests(ITestOutputHelper output) : IDisposable
{
    private readonly SavedWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Monday_and_tuesday_each_show_their_own_plan()
    {
        var a = _world.Place(10);
        var monday = _world.Save("monday");
        var b = _world.Place(30);
        var tuesday = _world.Save("tuesday");

        _world.Load(Option.Some(monday));
        Assert.Equal(new[] { a }, _world.Ghosts.ToArray());
        var c = _world.Place(50);
        _world.WriteQueued();

        _world.Load(Option.Some(tuesday));
        Assert.Equal(new[] { a, b }, _world.Ghosts.OrderBy(g => g.Value).ToArray());

        _world.Load(Option.Some(monday));
        Assert.Equal(new[] { a }, _world.Ghosts.ToArray());
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void A_ghost_added_after_loading_an_older_save_branches_the_file()
    {
        _world.Place(10);
        var monday = _world.Save("monday");
        _world.Place(30);
        _world.Save("tuesday");
        _world.Load(Option.Some(monday));
        _world.Place(50);
        _world.WriteQueued();

        var text = File.ReadAllText(_world.Folder.PlanPath(GhostBoard.DefaultPlan));
        var (tree, problems) = PlanTree.Read(GhostBoard.DefaultPlan, text);

        Assert.Empty(problems);
        Assert.Equal(3, tree.Count);
        Assert.Equal(1, tree.Line(3).Follows);
        Assert.Equal(1, tree.Line(2).Follows);
    }

    [Fact]
    public void Ids_are_never_reused_across_branches()
    {
        _world.Place(10);
        var monday = _world.Save("monday");
        var b = _world.Place(30);
        _world.Save("tuesday");

        _world.Load(Option.Some(monday));
        var c = _world.Place(50);

        Assert.True(c.Value > b.Value);
    }

    [Fact]
    public void A_save_with_no_record_opens_the_newest_lines_and_reports_it()
    {
        var a = _world.Place(10);
        _world.Save("monday");
        var b = _world.Place(30);
        _world.WriteQueued();

        var report = _world.Load(Option.Some(new SaveKey(Path.Combine(_world.Root, "elsewhere.save"), "unknown")));

        Assert.Equal(LoadBasis.NoRecord, report.Basis);
        Assert.Equal(Option.Some(new LoadReport(new SaveRecord.Missing(), ValueList<PlanAhead>.Empty)), _world.Saving.LastLoad);
        Assert.Equal(new[] { a, b }, _world.Ghosts.OrderBy(g => g.Value).ToArray());
    }

    [Fact]
    public void A_new_world_opens_empty()
    {
        Assert.Equal(LoadBasis.NewWorld, _world.Report.Basis);
        Assert.Empty(_world.Ghosts);
    }

    [Fact]
    public void A_copied_save_is_found_by_its_fingerprint()
    {
        var a = _world.Place(10);
        var monday = _world.Save("monday");
        _world.Place(30);
        _world.Save("tuesday");

        var report = _world.Load(Option.Some(monday with { Path = Path.Combine(_world.Root, "game", "copy-of-monday.save") }));

        Assert.Equal(LoadBasis.Record, report.Basis);
        Assert.Equal(new[] { a }, _world.Ghosts.ToArray());
    }

    [Fact]
    public void Killed_mid_write_the_torn_line_is_dropped_and_later_changes_are_offered()
    {
        _world.Place(10);
        var save = _world.Save("monday");
        var after = _world.Place(30);
        _world.WriteQueued();
        var path = _world.Folder.PlanPath(GhostBoard.DefaultPlan);
        File.AppendAllText(path, "3 2 00000000000000000000000000000003 player:1 2026-01-01T00:00:00");

        var report = _world.Load(Option.Some(save));

        Assert.Contains(report.Problems, p => p is FileProblem.TornLine);
        var offer = Assert.Single(report.Offers);
        Assert.Equal(1, offer.Changes);
        Assert.Equal(Option.Some(new LoadReport(new SaveRecord.Found(), ValueList.Of(new PlanAhead(offer.Plan, 1)))), _world.Saving.LastLoad);
        Assert.Equal(Result<int, Reason>.Ok(1), _world.Saving.AheadOfSave(offer.Plan));
        Assert.DoesNotContain(after, _world.Ghosts);

        _world.Start(_world.Saving.MoveForward(new[] { offer.Plan }));
        Assert.Contains(after, _world.Ghosts);
    }

    [Fact]
    public void After_a_crash_catch_up_moves_the_board_and_the_file_to_the_newest_line_without_writing_it_again()
    {
        _world.Place(10);
        var save = _world.Save("monday");
        var after = _world.Place(30);
        _world.WriteQueued();
        var path = _world.Folder.PlanPath(GhostBoard.DefaultPlan);
        _world.Load(Option.Some(save));
        _world.Board.Board.InstallSaving(_world.Saving);
        var length = File.ReadAllText(path).Length;

        var set = _world.Apply(new Command.CatchUp(GhostBoard.DefaultPlan));
        _world.Saving.Flush();

        Assert.Equal(after, Assert.Single(set.Added).Id);
        Assert.Equal(length, File.ReadAllText(path).Length);
        Assert.Equal(Result<int, Reason>.Ok(0), _world.Saving.AheadOfSave(GhostBoard.DefaultPlan));

        _world.Place(50);
        _world.Saving.Flush();
        var (tree, _) = PlanTree.Read(GhostBoard.DefaultPlan, File.ReadAllText(path));
        Assert.Equal(3, tree.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, tree.NewestAfter(0));
    }

    [Fact]
    public void A_plan_the_board_finishes_goes_to_the_archive_with_its_index_entry()
    {
        var plan = _world.Apply(CreatePlan("Hall")).PlansOpened[0].Id;
        _world.Place(30, Option.Some(plan));

        _world.Apply(new Command.FinishPlan(plan));
        _world.Saving.Flush();

        Assert.True(File.Exists(_world.Folder.ArchivedPath(plan)));
        Assert.Contains(_world.Saving.Finished, f => f.Id == plan);
        Assert.Contains(plan, _world.Load(Option.Some(_world.Save("later"))).Finished);
    }

    [Fact]
    public void A_torn_line_is_kept_aside_and_the_file_stays_readable_after_the_next_line()
    {
        _world.Place(10);
        var save = _world.Save("monday");
        var path = _world.Folder.PlanPath(GhostBoard.DefaultPlan);
        File.AppendAllText(path, "2 1 torn");
        _world.Load(Option.Some(save));

        _world.Place(30);
        _world.WriteQueued();

        var (tree, problems) = PlanTree.Read(GhostBoard.DefaultPlan, File.ReadAllText(path));
        Assert.Empty(problems);
        Assert.Equal(2, tree.Count);
        Assert.Contains("2 1 torn", File.ReadAllText(Path.Combine(_world.Folder.Plans, "1.torn")));
    }

    [Fact]
    public void Changes_saved_by_a_later_save_are_not_offered_when_an_older_save_loads()
    {
        _world.Place(10);
        var monday = _world.Save("monday");
        _world.Place(30);
        _world.Save("tuesday");

        var report = _world.Load(Option.Some(monday));

        Assert.Empty(report.Offers);
    }

    [Fact]
    public void A_plan_made_after_a_save_is_hidden_when_that_save_loads()
    {
        _world.Place(10);
        var monday = _world.Save("monday");
        var plan = _world.Apply(CreatePlan("Hall")).PlansOpened[0].Id;
        _world.Place(30, Option.Some(plan));
        _world.Save("tuesday");

        var report = _world.Load(Option.Some(monday));

        Assert.Contains(plan, report.Hidden);
        Assert.Single(_world.Ghosts);
    }

    [Fact]
    public void A_finished_plan_goes_to_the_archive_and_an_older_save_reopens_it()
    {
        var plan = _world.Apply(CreatePlan("Hall")).PlansOpened[0].Id;
        var ghost = _world.Place(30, Option.Some(plan));
        var built = _world.Place(50, Option.Some(plan));
        var before = _world.Save("before");
        var structure = new StructureRef(new ReferenceId(77), _world.Board.Catalogue.Named("StructureFrame").Key, new Cell(50, 10, 10));
        var done = _world.Board.Board.Complete(built, new Completion(Option.Some(structure), DateTimeOffset.UnixEpoch)).TryGet(out var completed) ? completed : throw new InvalidOperationException("not completed");
        _world.Saving.Record(done);
        _world.Saving.Record(done with { Id = new ChangeSetId(done.Id.Value + 1), Removed = ValueList<GhostRemoval>.Empty, PlansFinished = ValueList.Of(plan) });
        var after = _world.Save("after");

        Assert.True(File.Exists(_world.Folder.ArchivedPath(plan)));
        Assert.False(File.Exists(_world.Folder.PlanPath(plan)));
        var entry = Assert.Single(_world.Folder.Finished());
        Assert.Equal(plan, entry.Plan.Id);
        Assert.Equal(1, entry.Plan.Pieces);
        Assert.Equal(new Cell(50, 10, 10), entry.Plan.Min);
        Assert.Contains(_world.Saving.Finished, f => f.Id == plan);

        var finished = _world.Load(Option.Some(after));
        Assert.Contains(plan, finished.Finished);
        Assert.DoesNotContain(ghost, _world.Ghosts);

        _world.Load(Option.Some(before));
        Assert.Contains(ghost, _world.Ghosts);
    }

    [Fact]
    public void Lines_reach_the_disk_in_slices_and_all_of_them_before_a_save()
    {
        var sets = 4 * PlanSaving.OpsPerStep;
        for (var i = 0; i < sets; i++)
            _world.Place(10 + (20 * i));
        Assert.Equal(sets, _world.Saving.Pending);

        Assert.Equal(StepOutcome.More, _world.Saving.Step());
        Assert.True(_world.Saving.Pending > 0);

        _world.Save("monday");
        Assert.Equal(0, _world.Saving.Pending);
    }

    [Fact]
    public void One_step_writes_every_line_it_works_out_for_a_plan()
    {
        _world.Place(10);
        _world.Saving.Flush();
        var plan = _world.Folder.PlanPath(GhostBoard.DefaultPlan);
        var lines = File.ReadAllLines(plan).Length;
        const int sets = PlanSaving.OpsPerStep / 4;
        for (var i = 1; i <= sets; i++)
            _world.Place(10 + (20 * i));

        Assert.Equal(StepOutcome.Done, _world.Saving.Step());
        Assert.Equal(lines + sets, File.ReadAllLines(plan).Length);
    }

    [Fact]
    public void A_checkpoint_is_written_a_part_a_step_and_reads_back_as_the_plan_at_its_line()
    {
        using var world = new SavedWorld(checkpointEvery: 2);
        var ghosts = PlanSaving.CheckpointUnits * 3;
        world.Saving.Record(world.Board.Placed(Enumerable.Range(0, ghosts).Select(i => world.Board.Draft("StructureFrame", new Cell(10 + (20 * i), 10, 10))).ToArray()));
        while (world.Saving.Step() == StepOutcome.More && world.Folder.CheckpointLines(GhostBoard.DefaultPlan).Count == 0)
        {
        }

        world.Place(10 + (20 * ghosts));
        var steps = 0;
        while (world.Folder.CheckpointLines(GhostBoard.DefaultPlan).Count == 0 && steps < 1000)
        {
            world.Saving.Step();
            steps++;
        }

        Assert.InRange(steps, 3, 1000 - 1);
        var line = Assert.Single(world.Folder.CheckpointLines(GhostBoard.DefaultPlan));
        var read = world.Folder.ReadCheckpoint(GhostBoard.DefaultPlan, line);
        Assert.True(read.TryGet(out var state));
        Assert.Equal(ghosts + 1, state.Ghosts.Count);
    }

    [Fact]
    public void A_plan_of_100000_lines_rebuilds_from_its_nearest_checkpoint_in_recorded_time()
    {
        using var world = new SavedWorld(checkpointEvery: 2000);
        var first = world.Board.Placed(world.Board.Draft("StructureFrame", new Cell(10, 10, 10)));
        world.Saving.Record(first);
        var ghost = first.Added[0];
        for (var i = 1; i < 100_000; i++)
        {
            var changed = ghost with { Revision = new Revision(i + 1) };
            world.Saving.Record(first with
            {
                Id = new ChangeSetId(i + 1),
                Revision = new Revision(i + 1),
                Added = ValueList<Ghost>.Empty,
                Changed = ValueList.Of(new GhostChange(ghost, changed)),
            });
            ghost = changed;
            if (world.Saving.Pending > 4096)
                world.Saving.Flush();
        }

        var save = world.Save("big");

        var watch = Stopwatch.StartNew();
        var report = world.Load(Option.Some(save));
        watch.Stop();

        output.WriteLine($"rebuild of a 100000-line plan from its nearest checkpoint: {watch.ElapsedMilliseconds} ms");
        Assert.Empty(report.Problems);
        Assert.Equal(new[] { ghost.Id }, world.Ghosts.ToArray());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.ElapsedMilliseconds} ms");
    }

    private static Command CreatePlan(string name) => new Command.CreatePlan(new PlanDraft(
        PlanName.Create(name).TryGet(out var plan) ? plan : throw new InvalidOperationException(name),
        TestBoard.TestMaker,
        new Visibility.Everyone(),
        ValueMap<AttachmentKey, Attachment>.Empty));
}
