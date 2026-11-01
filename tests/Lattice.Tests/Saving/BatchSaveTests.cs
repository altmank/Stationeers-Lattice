using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Executors;
using Lattice.Core.Saving;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Executors;

namespace Lattice.Tests.Saving;

/// <summary>
/// Builders' batch sets are saved in the save's record, as they stood when the save started, and given back after a
/// load to a builder that registers with the same id, however late. A record with no sets reads as before, and the
/// sets are items an older reader passes over.
/// </summary>
public sealed class BatchSaveTests
{
    private static ExecutorId Dock => Builders.Id("dock");

    /// <summary>The executors of the world's current board, with the load's batch sets put back and saved with the world.</summary>
    private static WorldExecutors Executors(SavedWorld world)
    {
        var executors = new WorldExecutors(world.Board.Board, new ManualGameClock(), WorldSeat.Host);
        var batches = executors.Batches!;
        batches.Restore(world.Opened.Batches);
        world.Saving.Batches = batches.Saved;
        return executors;
    }

    private static IExecutorHandle Register(WorldExecutors executors) => TestKitIds.Must(executors.Register(ExecutorsBench.Info("dock")));

    [Fact]
    public void A_load_gives_the_saved_set_back_to_the_builder_registering_with_the_same_id()
    {
        using var world = new SavedWorld();
        var executors = Executors(world);
        var dock = Register(executors);
        var a = world.Place(0);
        var b = world.Place(2);
        world.Place(4);
        TestKitIds.Must(dock.SetBatch(ValueSet.From(new[] { a, b })));
        var save = world.Save("one");

        world.Load(Option.Some(save));
        var loaded = Executors(world);
        world.Place(6);
        var again = Register(loaded);

        Assert.Equal(ValueSet.From(new[] { a, b }), again.Batch);
        Assert.Equal(2, loaded.Find(Dock).Reduce(() => throw new Xunit.Sdk.XunitException("no dock")).BatchLeft);
    }

    [Fact]
    public void The_mode_word_is_not_saved_and_reads_as_default_until_the_builder_says_it_again()
    {
        using var world = new SavedWorld();
        var executors = Executors(world);
        var dock = Register(executors);
        var a = world.Place(0);
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.OwnerAndHost(), (_, _) => Result<Unit, Reason>.Ok(Unit.Value)));
        TestKitIds.Must(dock.SayMode(new BuildMode.Batch(), ValueList.Of(new SkippedGhost(a, new Reason(TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "blocked")), ValueMap.Of("ghost", "a"), Option<string>.None, Builders.Dock)))));
        TestKitIds.Must(dock.SetBatch(ValueSet.Of(a)));
        var save = world.Save("one");

        world.Load(Option.Some(save));
        var loaded = Executors(world);
        var again = Register(loaded);
        var read = loaded.Find(Dock).Reduce(() => throw new Xunit.Sdk.XunitException("no dock"));

        Assert.True(read.Mode.IsNone);
        Assert.Empty(read.BatchSkipped);
        Assert.IsType<BuildModeAccess.None>(read.ControlledBy);
        Assert.Equal(1, read.BatchLeft);

        TestKitIds.Must(again.SayMode(new BuildMode.Off(), ValueList<SkippedGhost>.Empty));
        TestKitIds.Must(again.AcceptModes(new BuildModeAccess.Anyone(), (_, _) => Result<Unit, Reason>.Ok(Unit.Value)));
        var said = loaded.Find(Dock).Reduce(() => throw new Xunit.Sdk.XunitException("no dock"));

        Assert.Equal(Option.Some<BuildMode>(new BuildMode.Off()), said.Mode);
        Assert.IsType<BuildModeAccess.Anyone>(said.ControlledBy);
    }

    [Fact]
    public void A_set_whose_builder_has_not_registered_yet_still_loses_ghosts_that_leave()
    {
        using var world = new SavedWorld();
        var executors = Executors(world);
        var dock = Register(executors);
        var a = world.Place(0);
        var b = world.Place(2);
        TestKitIds.Must(dock.SetBatch(ValueSet.From(new[] { a, b })));
        var save = world.Save("one");

        world.Load(Option.Some(save));
        var loaded = Executors(world);
        world.Apply(new Command.Remove(TestBoard.Ids(a)));
        var late = Register(loaded);

        Assert.Equal(ValueSet.Of(b), late.Batch);
    }

    [Fact]
    public void Each_save_keeps_its_own_set()
    {
        using var world = new SavedWorld();
        var executors = Executors(world);
        var dock = Register(executors);
        var a = world.Place(0);
        var b = world.Place(2);
        TestKitIds.Must(dock.SetBatch(ValueSet.From(new[] { a, b })));
        var one = world.Save("one");
        world.Apply(new Command.Remove(TestBoard.Ids(b)));
        var two = world.Save("two");

        world.Load(Option.Some(one));
        var fromOne = Register(Executors(world)).Batch;
        world.Load(Option.Some(two));
        var fromTwo = Register(Executors(world)).Batch;

        Assert.Equal(ValueSet.From(new[] { a, b }), fromOne);
        Assert.Equal(ValueSet.Of(a), fromTwo);
    }

    [Fact]
    public void A_save_with_no_sets_loads_as_before_and_its_record_has_no_batch_items()
    {
        using var world = new SavedWorld();
        Executors(world);
        var a = world.Place(0);
        var save = world.Save("plain");
        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(world.Folder.Saves, "*.lsave")));

        world.Load(Option.Some(save));

        Assert.DoesNotContain("batch=", text);
        Assert.Equal(LoadBasis.Record, world.Report.Basis);
        Assert.Empty(world.Opened.Batches);
        Assert.Contains(a, world.Ghosts);
    }

    [Fact]
    public void A_batch_item_names_no_plan_so_a_reader_of_plan_heads_passes_it_over()
    {
        using var world = new SavedWorld();
        var dock = Register(Executors(world));
        var plan = world.Apply(new Command.CreatePlan(new PlanDraft(
            TestKitIds.Must(PlanName.Create("Hall")), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty))).PlansOpened[0].Id;
        var a = world.Place(0, Option.Some(plan));
        TestKitIds.Must(dock.SetBatch(ValueSet.Of(a)));
        world.Save("one");

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(world.Folder.Saves, "*.lsave")));
        var batchLine = Assert.Single(lines, line => line.Contains("batch="));
        var record = Assert.Single(world.Folder.Records());

        Assert.DoesNotContain("plan=", batchLine);
        Assert.True(record.Heads.ContainsKey(plan));
        Assert.Equal(ValueSet.Of(a), record.Batches[Dock]);
    }

    [Fact]
    public void The_capture_is_taken_as_the_save_starts()
    {
        using var world = new SavedWorld();
        var dock = Register(Executors(world));
        var a = world.Place(0);
        var b = world.Place(2);
        TestKitIds.Must(dock.SetBatch(ValueSet.Of(a)));

        var capture = world.Saving.BeforeGameSave();
        TestKitIds.Must(dock.SetBatch(ValueSet.Of(b)));
        var record = world.Saving.AfterGameSave(capture, new SaveKey(Path.Combine(world.Root, "game", "x.save"), "x-1"), System.DateTimeOffset.UnixEpoch);

        Assert.Equal(ValueSet.Of(a), record.Batches[Dock]);
        Assert.Equal(ValueSet.Of(a), world.Folder.Records().Single().Batches[Dock]);
    }
}
