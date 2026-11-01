using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Executors;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Commands;

namespace Lattice.Tests.Executors;

/// <summary>
/// Build modes on the host: a <see cref="Command.SetBuildMode"/> reaches the builder's handler with the submitting actor
/// and comes back as the handler answers it, touching no board; what the builder says about its mode and its batch is
/// published on its record; and the builder's batch set follows the board.
/// </summary>
public sealed class BuildModeTests
{
    private static readonly Reason Busy = new(
        TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "busy")),
        ValueMap.Of("job", "hall").With("left", "3"),
        Option.Some("The dock is busy"),
        Builders.Dock);

    private static ExecutorId Dock => Builders.Id("dock");

    /// <summary>Commands on a host board with its executors taking mode requests.</summary>
    private sealed class ModeBench
    {
        public ModeBench()
        {
            Executors = new WorldExecutors(Host.T.Board, Clock, WorldSeat.Host);
            Host.Service.Modes = Executors.SetMode;
        }

        public TestCommands Host { get; } = new();

        public ManualGameClock Clock { get; } = new();

        public WorldExecutors Executors { get; }

        public IExecutorHandle Register(string name) => TestKitIds.Must(Executors.Register(ExecutorsBench.Info(name)));

        public Outcome Ask(BuildMode mode, Actor? actor = null) => Host.Submit(new Command.SetBuildMode(Dock, mode), actor);
    }

    private static Builder Read(WorldExecutors executors, ExecutorId id) =>
        executors.Find(id).Reduce(() => throw new Xunit.Sdk.XunitException($"{id} is not registered"));

    private static Refusal RefusedOnce(Outcome outcome) => Assert.Single(Assert.IsType<Outcome.Refused>(outcome).Why);

    // ---- the command ----

    [Fact]
    public void A_request_reaches_the_handler_with_the_actor_and_mode_and_its_ok_comes_back_accepted()
    {
        var bench = new ModeBench();
        var dock = bench.Register("dock");
        var asked = new List<(Actor, BuildMode)>();
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.OwnerAndHost(), (actor, mode) =>
        {
            asked.Add((actor, mode));
            return Result<Unit, Reason>.Ok(Unit.Value);
        }));

        var outcome = bench.Ask(new BuildMode.Batch(), TestBoard.Bob);

        Assert.IsType<Outcome.Accepted>(outcome);
        Assert.Equal(new[] { ((Actor)TestBoard.Bob, (BuildMode)new BuildMode.Batch()) }, asked);
    }

    [Fact]
    public void The_handler_s_error_comes_back_refused_with_its_reason_as_given()
    {
        var bench = new ModeBench();
        var dock = bench.Register("dock");
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.Anyone(), (_, _) => Result<Unit, Reason>.Error(Busy)));

        var refusal = RefusedOnce(bench.Ask(new BuildMode.On()));

        Assert.Equal(Busy, refusal.Why);
        Assert.Equal(Option.Some("The dock is busy"), refusal.Why.GameMessage);
        Assert.Equal("3", refusal.Why.Facts["left"]);
        Assert.Empty(refusal.Ghosts);
    }

    [Fact]
    public void An_unregistered_builder_is_not_found()
    {
        var bench = new ModeBench();

        var refusal = RefusedOnce(bench.Ask(new BuildMode.On()));

        Assert.Equal(ReasonCodes.NotFound, refusal.Why.Code);
        Assert.Equal("executor", refusal.Why.Facts["problem"]);
        Assert.Equal(Dock.Value, refusal.Why.Facts["executor"]);
    }

    [Fact]
    public void A_builder_with_no_handler_or_one_that_set_none_does_not_take_modes()
    {
        var bench = new ModeBench();
        var dock = bench.Register("dock");
        var never = RefusedOnce(bench.Ask(new BuildMode.On()));

        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.Anyone(), (_, _) => Result<Unit, Reason>.Ok(Unit.Value)));
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.None(), null));
        var takenAway = RefusedOnce(bench.Ask(new BuildMode.On()));

        Assert.All(new[] { never, takenAway }, refusal =>
        {
            Assert.Equal(ReasonCodes.NotSupported, refusal.Why.Code);
            Assert.Equal("modes", refusal.Why.Facts["problem"]);
        });
        Assert.IsType<BuildModeAccess.None>(Read(bench.Executors, Dock).ControlledBy);
    }

    [Fact]
    public void Calling_accept_modes_again_replaces_the_access_and_the_handler()
    {
        var bench = new ModeBench();
        var dock = bench.Register("dock");
        var calls = new List<string>();
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.Anyone(), (_, _) =>
        {
            calls.Add("first");
            return Result<Unit, Reason>.Ok(Unit.Value);
        }));
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.OwnerAndHost(), (_, _) =>
        {
            calls.Add("second");
            return Result<Unit, Reason>.Ok(Unit.Value);
        }));

        bench.Ask(new BuildMode.Off());

        Assert.Equal(new[] { "second" }, calls);
        Assert.IsType<BuildModeAccess.OwnerAndHost>(Read(bench.Executors, Dock).ControlledBy);
    }

    [Fact]
    public void Accept_modes_refuses_a_null_access_and_a_missing_handler()
    {
        var bench = new ModeBench();
        var dock = bench.Register("dock");

        Assert.Equal(ReasonCodes.InvalidValue, dock.AcceptModes(null!, null).ErrorOf().Code);
        Assert.Equal(ReasonCodes.InvalidValue, dock.AcceptModes(new BuildModeAccess.Anyone(), null).ErrorOf().Code);
        Assert.IsType<BuildModeAccess.None>(Read(bench.Executors, Dock).ControlledBy);
    }

    [Fact]
    public void An_accepted_request_makes_no_board_change()
    {
        var bench = new ModeBench();
        var dock = bench.Register("dock");
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.Anyone(), (_, _) => Result<Unit, Reason>.Ok(Unit.Value)));
        var board = bench.Host.T.Board;
        var revision = board.Revision;
        var committed = 0;
        var changed = 0;
        board.Committed += _ => committed++;
        bench.Host.T.Events.Changed += _ => changed++;

        Assert.IsType<Outcome.Accepted>(bench.Ask(new BuildMode.On()));
        bench.Host.Frame();
        bench.Host.T.Events.Flush();

        Assert.Equal(revision, board.Revision);
        Assert.Equal(0, committed);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void A_request_is_answered_once_per_command_id()
    {
        var bench = new ModeBench();
        var dock = bench.Register("dock");
        var calls = 0;
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.Anyone(), (_, _) =>
        {
            calls++;
            return Result<Unit, Reason>.Ok(Unit.Value);
        }));
        var id = bench.Host.NextId();
        var command = new Command.SetBuildMode(Dock, new BuildMode.On());

        bench.Host.Service.Submit(id, command, TestBoard.Alice);
        var again = TestCommands.Answer(bench.Host.Service.Submit(id, command, TestBoard.Alice));

        Assert.IsType<Outcome.Accepted>(again);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_command_the_handler_submits_is_applied_after_it()
    {
        var bench = new ModeBench();
        var dock = bench.Register("dock");
        var revisionInside = default(Revision);
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.Anyone(), (_, _) =>
        {
            bench.Host.Service.Submit(bench.Host.NextId(), new Command.Place(ValueList.Of(bench.Host.T.Draft("StructureFrame", new Cell(10, 10, 10)))), TestBoard.Alice);
            revisionInside = bench.Host.T.Board.Revision;
            return Result<Unit, Reason>.Ok(Unit.Value);
        }));
        var before = bench.Host.T.Board.Revision;

        Assert.IsType<Outcome.Accepted>(bench.Ask(new BuildMode.On()));

        Assert.Equal(before, revisionInside);
        Assert.Single(bench.Host.T.Board.Index.Ghosts);
    }

    [Fact]
    public void A_mode_request_inside_a_batch_is_refused_as_no_board_change()
    {
        var bench = new ModeBench();
        bench.Register("dock");

        var refusal = RefusedOnce(bench.Host.Submit(new Command.Batch(ValueList.Of<Command>(new Command.SetBuildMode(Dock, new BuildMode.On())))));

        Assert.Equal(ReasonCodes.InvalidValue, refusal.Why.Code);
        Assert.Equal("not-a-board-change", refusal.Why.Facts["problem"]);
    }

    [Fact]
    public void With_no_executors_a_mode_request_is_refused_module_missing()
    {
        var host = new TestCommands();

        var refusal = RefusedOnce(host.Submit(new Command.SetBuildMode(Dock, new BuildMode.On())));

        Assert.Equal(ReasonCodes.ModuleMissing, refusal.Why.Code);
    }

    // ---- what the builder says ----

    [Fact]
    public void Mode_and_skipped_ghosts_are_published_and_an_equal_say_raises_nothing()
    {
        var bench = new ExecutorsBench();
        var dock = bench.Register("dock");
        var told = new List<ExecutorId>();
        bench.T.Events.BuilderChanged += told.Add;
        bench.Flush();
        told.Clear();
        var skipped = ValueList.Of(new SkippedGhost(new GhostId(7), Busy));

        TestKitIds.Must(dock.SayMode(new BuildMode.Off(), skipped));
        bench.Flush();
        TestKitIds.Must(dock.SayMode(new BuildMode.Off(), ValueList.Of(new SkippedGhost(new GhostId(7), Busy))));
        bench.Flush();

        var read = Read(bench.Executors, Dock);
        Assert.Equal(Option.Some<BuildMode>(new BuildMode.Off()), read.Mode);
        Assert.Equal(skipped, read.BatchSkipped);
        Assert.Equal(new[] { Dock }, told);
    }

    [Fact]
    public void More_than_256_skipped_ghosts_are_refused_too_large_and_nothing_changes()
    {
        var bench = new ExecutorsBench();
        var dock = bench.Register("dock");
        var many = new SkippedGhost[257];
        for (var i = 0; i < many.Length; i++)
            many[i] = new SkippedGhost(new GhostId(i + 1), Busy);

        var refused = dock.SayMode(new BuildMode.On(), ValueList.From(many)).ErrorOf();

        Assert.Equal(ReasonCodes.TooLarge, refused.Code);
        Assert.Equal("256", refused.Facts["most"]);
        Assert.True(Read(bench.Executors, Dock).Mode.IsNone);
    }

    [Fact]
    public void A_builder_starts_with_no_mode_no_control_no_batch_and_a_disposed_one_is_gone()
    {
        var bench = new ExecutorsBench();
        var dock = bench.Register("dock");

        var read = Read(bench.Executors, Dock);
        Assert.IsType<BuildModeAccess.None>(read.ControlledBy);
        Assert.True(read.Mode.IsNone);
        Assert.Equal(0, read.BatchLeft);
        Assert.Empty(read.BatchSkipped);

        dock.Dispose();
        Assert.True(bench.Executors.Find(Dock).IsNone);
        Assert.Equal(ReasonCodes.NotFound, dock.SayMode(new BuildMode.On(), ValueList<SkippedGhost>.Empty).ErrorOf().Code);
    }

    // ---- compatibility ----

    [Fact]
    public void A_builder_built_as_before_compares_and_deconstructs_as_before()
    {
        var info = ExecutorsBench.Info("dock");
        var a = new Builder(info, Option<BuilderArea>.None, new BuilderState.Idle(), Option<Supply>.None);
        var b = new Builder(info, Option<BuilderArea>.None, new BuilderState.Idle(), Option<Supply>.None);

        var (gotInfo, area, state, supply) = a;

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(info, gotInfo);
        Assert.True(area.IsNone);
        Assert.IsType<BuilderState.Idle>(state);
        Assert.True(supply.IsNone);
        Assert.NotEqual(a, a with { BatchLeft = 1 });
        Assert.Equal(a, a with { BatchSkipped = ValueList<SkippedGhost>.Empty });
    }

    // ---- the batch set ----

    [Fact]
    public void Setting_a_batch_leaves_out_ghosts_not_on_the_board_and_publishes_its_size()
    {
        var bench = new ExecutorsBench();
        var dock = bench.Register("dock");
        var a = bench.Ghost();
        var b = bench.Ghost();

        TestKitIds.Must(dock.SetBatch(ValueSet.From(new[] { a, b, new GhostId(999) })));

        Assert.Equal(ValueSet.From(new[] { a, b }), dock.Batch);
        Assert.Equal(2, Read(bench.Executors, Dock).BatchLeft);
        Assert.Same(dock.Batch, dock.Batch);
    }

    [Fact]
    public void An_empty_batch_ends_it()
    {
        var bench = new ExecutorsBench();
        var dock = bench.Register("dock");
        TestKitIds.Must(dock.SetBatch(ValueSet.Of(bench.Ghost())));

        TestKitIds.Must(dock.SetBatch(ValueSet<GhostId>.Empty));

        Assert.Empty(dock.Batch);
        Assert.Equal(0, Read(bench.Executors, Dock).BatchLeft);
    }

    [Fact]
    public void A_batch_past_the_cap_is_refused_too_large_and_never_cut_down()
    {
        var bench = new ExecutorsBench();
        var dock = bench.Register("dock");
        var keep = bench.Ghost();
        TestKitIds.Must(dock.SetBatch(ValueSet.Of(keep)));
        var many = new GhostId[BatchSets.MostGhosts + 1];
        for (var i = 0; i < many.Length; i++)
            many[i] = new GhostId(i + 1);

        var refused = dock.SetBatch(ValueSet.From(many)).ErrorOf();

        Assert.Equal(ReasonCodes.TooLarge, refused.Code);
        Assert.Equal("16384", refused.Facts["most"]);
        Assert.Equal(ValueSet.Of(keep), dock.Batch);
    }

    [Fact]
    public void A_done_or_removed_ghost_leaves_the_batch_in_the_change_set_that_takes_it()
    {
        var bench = new ExecutorsBench();
        var dock = bench.Register("dock");
        var done = bench.Ghost();
        var removed = bench.Ghost();
        var stays = bench.Ghost();
        TestKitIds.Must(dock.SetBatch(ValueSet.From(new[] { done, removed, stays })));
        var seenInside = new List<int>();
        bench.T.Board.Committed += _ => seenInside.Add(Read(bench.Executors, Dock).BatchLeft);

        TestKitIds.Must(bench.T.Board.Complete(done, new Completion(Option<StructureRef>.None, System.DateTimeOffset.UnixEpoch)));
        TestBoard.Applied(bench.T.Apply(new Command.Remove(TestBoard.Ids(removed))));

        Assert.Equal(ValueSet.Of(stays), dock.Batch);
        Assert.Equal(new[] { 2, 1 }, seenInside);
    }

    [Fact]
    public void A_ghost_of_a_finished_plan_leaves_the_batch()
    {
        var bench = new ExecutorsBench();
        var dock = bench.Register("dock");
        var plan = Builders.NewPlan(bench);
        var ghost = Builders.GhostIn(bench, plan, 0);
        TestKitIds.Must(dock.SetBatch(ValueSet.Of(ghost)));

        TestBoard.Applied(bench.T.Apply(new Command.FinishPlan(plan)));

        Assert.Empty(dock.Batch);
    }

    [Fact]
    public void A_ghost_replaced_in_its_spot_is_followed_by_the_ghost_that_replaced_it()
    {
        var bench = new ExecutorsBench();
        var dock = bench.Register("dock");
        var cell = new Cell(40, 10, 10);
        var old = TestBoard.Applied(bench.T.Place(bench.T.Draft("StructureFrame", cell))).Added[0].Id;
        TestKitIds.Must(dock.SetBatch(ValueSet.Of(old)));

        var replacing = TestBoard.Applied(bench.T.Place(bench.T.Draft("StructureFrame", cell)));

        var by = Assert.IsType<RemovalCause.Replaced>(Assert.Single(replacing.Removed).Cause).By;
        Assert.Equal(ValueSet.Of(by), dock.Batch);
        Assert.Equal(1, Read(bench.Executors, Dock).BatchLeft);
    }

    [Fact]
    public void A_modded_piece_in_a_batch_is_kept_and_followed_like_any_other()
    {
        var bench = new ExecutorsBench(WorldSeat.Host, ExecutorsBench.Pieces(
            "piece ModBeam kit=ItemModBeamKit option=0 grid=large\n" +
            "  step material=ItemModBeamKit*1 exit=ItemModSpanner air=no\n"));
        var dock = bench.Register("dock");
        var cell = new Cell(40, 10, 10);
        var beam = TestBoard.Applied(bench.T.Place(bench.T.Draft("ModBeam", cell))).Added[0].Id;
        TestKitIds.Must(dock.SetBatch(ValueSet.Of(beam)));

        var replacing = TestBoard.Applied(bench.T.Place(bench.T.Draft("ModBeam", cell)));

        Assert.Equal(ValueSet.Of(replacing.Added[0].Id), dock.Batch);
    }

    [Fact]
    public void A_disposed_builder_s_batch_is_kept_still_follows_the_board_and_comes_back_on_registering_again()
    {
        var bench = new ExecutorsBench();
        var dock = bench.Register("dock");
        var a = bench.Ghost();
        var b = bench.Ghost();
        TestKitIds.Must(dock.SetBatch(ValueSet.From(new[] { a, b })));
        dock.Dispose();

        TestBoard.Applied(bench.T.Apply(new Command.Remove(TestBoard.Ids(a))));
        var again = bench.Register("dock");

        Assert.Equal(ValueSet.Of(b), again.Batch);
        Assert.Equal(1, Read(bench.Executors, Dock).BatchLeft);
    }

    [Fact]
    public void Batch_sets_are_the_host_s_a_client_handle_does_not_exist()
    {
        var bench = new ExecutorsBench(WorldSeat.Client);

        Assert.Null(bench.Executors.Batches);
        Assert.Equal(ReasonCodes.HostOnly, bench.Executors.Register(ExecutorsBench.Info("dock")).ErrorOf().Code);
    }
}
