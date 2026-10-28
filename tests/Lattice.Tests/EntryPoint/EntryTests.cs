using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.EntryPoint;
using Lattice.Core.Executors;
using Lattice.Core.Slices;
using Lattice.Core.World;
using Lattice.TestKit;
using Lattice.TestKit.Allocations;
using Lattice.Tests.Runs;

namespace Lattice.Tests.EntryPoint;

[Collection(LatticeSystemCollection.Name)]
public sealed class EntryTests : IDisposable
{
    private readonly WorldBench _bench = new();

    public void Dispose()
    {
        _bench.Dispose();
        Assert.False(LatticeSystem.Current.TryGet(out _));
    }

    private static ModuleState StateOf(IGhostSystem system, string module) =>
        system.Modules.Single(m => m.Name.Value == module).State;

    private Ghost PlaceConduit(Cell at)
    {
        var outcome = _bench.Submit(new Command.Place(ValueList.Of(_bench.Draft("ModConduitLine", at))));
        return Assert.Single(Assert.IsType<Outcome.Applied>(outcome).Changes.Added);
    }

    [Fact]
    public void Publish_sets_current_then_raises_started_with_the_same_system()
    {
        var world = _bench.Start();
        IGhostSystem? seen = null;
        var currentInHandler = false;
        Action<IGhostSystem> started = system =>
        {
            seen = system;
            currentInHandler = LatticeSystem.Current.TryGet(out var current) && ReferenceEquals(current, system);
        };
        LatticeSystem.Started += started;
        try
        {
            Assert.False(LatticeSystem.Current.TryGet(out _));
            world.Publish();
        }
        finally
        {
            LatticeSystem.Started -= started;
        }

        Assert.Same(world, seen);
        Assert.True(currentInHandler);
        Assert.Equal(ApiVersion.Current, world.Version);
        Assert.True(world.Has(IntentKind.Swap));
        Assert.False(world.Has((IntentKind)99));
    }

    [Fact]
    public void Planned_pipes_that_join_two_networks_carry_one_pipe_join_per_tick_in_their_steps()
    {
        // Separate built pipes, each its own network; a joining ghost links two of them (pipes run along local z).
        _bench.Runs.Real("StructurePipeStraight", RunBench.At(0, 0, 0));
        _bench.Runs.Real("StructurePipeStraight", RunBench.At(0, 0, 2));
        _bench.Runs.Real("StructurePipeStraight", RunBench.At(10, 0, 0));
        _bench.Runs.Real("StructurePipeStraight", RunBench.At(10, 0, 2));
        _bench.Runs.Real("StructurePipeStraight", RunBench.At(20, 0, 0));
        var world = _bench.Start();
        var first = PlacePipe(RunBench.At(0, 0, 1));
        var second = PlacePipe(RunBench.At(10, 0, 1));
        var extending = PlacePipe(RunBench.At(20, 0, 1));
        _bench.Settle();

        bool Joins(Ghost ghost) =>
            TestKitIds.Must(world.Steps.For(ghost.Id)).Constraints.Contains(new Constraint.OnePipeJoinPerTick());

        Assert.True(Joins(first));
        Assert.True(Joins(second));
        Assert.False(Joins(extending));
    }

    private Ghost PlacePipe(Cell at)
    {
        var outcome = _bench.Submit(new Command.Place(ValueList.Of(_bench.Draft("StructurePipeStraight", at))));
        return Assert.Single(Assert.IsType<Outcome.Applied>(outcome).Changes.Added);
    }

    [Fact]
    public void Every_module_is_on_for_a_host_with_all_members_and_every_service_answers()
    {
        var world = _bench.Start();
        var ghost = PlaceConduit(RunBench.At(0, 0, 0));
        _bench.Settle();

        Assert.All(world.Modules, m => Assert.IsType<ModuleState.On>(m.State));
        Assert.IsType<Answer<ValidationReport>.Ready>(TestKitIds.Must(world.Validation.Check(new Selector.Ids(ValueList.Of(ghost.Id)))));
        Assert.True(world.Steps.For(ghost.Id).TryGet(out _));
        Assert.True(world.Bills.For(new Selector.Ids(ValueList.Of(ghost.Id))).TryGet(out _));
        Assert.True(world.Analysis.Of(new Selector.Ids(ValueList.Of(ghost.Id))).TryGet(out _));
        Assert.True(world.Inspector.Story(ghost.Id).TryGet(out _));
        Assert.True(world.Board.Select(new Selector.Box(RunBench.At(-1, -1, -1), RunBench.At(1, 1, 1))).TryGet(out var boxed));
        Assert.Contains(boxed, g => g.Id == ghost.Id);
        Assert.True(world.Plans.State(world.Plans.Default).TryGet(out _));
        Assert.True(world.Match.TryGet(out _));
    }

    [Fact]
    public void Lattices_own_expanders_work_inside_a_started_handler()
    {
        var world = _bench.Start();
        Outcome? outcome = null;
        Action<IGhostSystem> started = system =>
        {
            var request = _bench.Runs.Run(_bench.Runs.Conduit, RunBench.At(0, 0, 0), RunBench.At(0, 0, 3));
            outcome = system.Commands.Submit(new CommandId(Guid.NewGuid()), new Command.PlanRun(request), WorldBench.Viewer) is Submission.Answered a ? a.Outcome : null;
        };
        LatticeSystem.Started += started;
        try
        {
            world.Publish();
        }
        finally
        {
            LatticeSystem.Started -= started;
        }

        var applied = Assert.IsType<Outcome.Applied>(outcome);
        Assert.NotEmpty(applied.Changes.Added);
        Assert.All(applied.Changes.Added, g => Assert.StartsWith("ModConduit", _bench.Runs.NameOf(Assert.IsType<Intent.Build>(g.Intent).Target.Piece)));
    }

    [Fact]
    public void With_the_world_off_its_dependants_answer_module_missing_and_the_rest_work()
    {
        var world = _bench.Start(WorldSeat.Host, WorldManifest.SetCustomColor);

        var why = Assert.IsType<ModuleState.Off>(StateOf(world, "validation")).Why;
        Assert.Equal("mirror", why.Facts["needs"]);
        Assert.Equal(why, Refused(world.Validation.Check(new Selector.InPlan(world.Plans.Default))));
        Assert.Equal(why, Refused(world.Registries.Register(new RuleDescriptor(
            TestKitIds.Must(RuleId.Create(TestKitIds.Mod, "a-rule")), _ => throw new InvalidOperationException()))));
        Assert.Equal("selectors", world.Board.Select(new Selector.Box(RunBench.At(0, 0, 0), RunBench.At(1, 1, 1))).TryGetError(out var box) ? box.Facts["module"] : "");
        Assert.Equal(ReasonCodes.ModuleMissing, Refused(world.Steps.For(new GhostId(1))).Code);
        Assert.Equal(ReasonCodes.ModuleMissing, Refused(world.Swaps.OptionsFor(new StructureRef(new ReferenceId(1), default, default))).Code);
        Assert.False(world.Match.TryGet(out _));

        var ghost = PlaceConduit(RunBench.At(0, 0, 0));
        world.Frame();
        world.Frame();
        Assert.Contains("ghost=" + ghost.Id.Value, _bench.PlanText(world.Plans.Default));
        Assert.True(world.Inspector.Story(ghost.Id).TryGet(out _));
        Assert.Contains(_bench.Log, line => line.Contains("module validation is off"));
    }

    private static Reason Refused<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGetError(out var why) ? why : throw new Xunit.Sdk.XunitException("not refused");

    [Fact]
    public void A_change_reaches_saving_in_the_frame_it_is_made_after_the_scheduler()
    {
        var world = _bench.Start();
        var ghost = PlaceConduit(RunBench.At(0, 0, 0));
        Assert.DoesNotContain("ghost=" + ghost.Id.Value, _bench.PlanText(world.Plans.Default));

        world.Frame();
        world.Frame();

        Assert.Contains("ghost=" + ghost.Id.Value, _bench.PlanText(world.Plans.Default));
    }

    [Fact]
    public void A_consumer_changed_handler_that_throws_is_logged_and_does_not_keep_the_change_from_saving()
    {
        var world = _bench.Start();
        Action<ChangeSet> throwing = _ => throw new InvalidOperationException("mod bug");
        world.Events.Changed += throwing;
        var ghost = PlaceConduit(RunBench.At(0, 0, 0));

        world.Frame();
        world.Events.Changed -= throwing;
        world.Frame();

        Assert.Contains("ghost=" + ghost.Id.Value, _bench.PlanText(world.Plans.Default));
        Assert.Single(_bench.Log, line => line.Contains("event handler") && line.Contains("mod bug"));
    }

    [Fact]
    public void A_throwing_lattice_job_is_logged_once_and_the_other_jobs_run()
    {
        var world = _bench.Start();
        var faulty = new Faulty();
        TestKitIds.Must(world.Scheduler.Schedule(WorkKind.Saving, faulty, Priority.High));
        TestKitIds.Must(world.Scheduler.Schedule(WorkKind.Saving, new Faulty(), Priority.High));
        var ghost = PlaceConduit(RunBench.At(0, 0, 0));

        world.Frame();
        world.Frame();

        Assert.Contains("ghost=" + ghost.Id.Value, _bench.PlanText(world.Plans.Default));
        Assert.Equal(2, world.Faults.Count(nameof(Faulty)));
        Assert.Single(_bench.Log, line => line.Contains("Faulty failed"));
    }

    [Fact]
    public void A_module_that_throws_in_its_frame_is_logged_and_the_frame_goes_on()
    {
        var world = _bench.Start();
        world.Frame();
        var ghost = PlaceConduit(RunBench.At(0, 0, 0));

        world.Faults.Run(LatticeModules.Validation, () => throw new InvalidOperationException("validation bug"));
        world.Frame();
        world.Frame();

        Assert.Contains("ghost=" + ghost.Id.Value, _bench.PlanText(world.Plans.Default));
        Assert.Single(_bench.Log, line => line.Contains("validation failed"));
    }

    [Fact]
    public void End_clears_current_first_ends_every_module_and_raises_board_reset_last()
    {
        var world = _bench.Start();
        world.Publish();
        var ghost = PlaceConduit(RunBench.At(0, 0, 0));
        var resets = new List<bool>();
        world.Events.BoardReset += () => resets.Add(LatticeSystem.Current.TryGet(out _));

        world.End();
        world.End();

        Assert.False(LatticeSystem.Current.TryGet(out _));
        Assert.Equal(new[] { false }, resets);
        Assert.True(_bench.Worker.Stopped);
        Assert.All(_bench.RuleCalls, calls => Assert.True(calls.Disposed));
        Assert.Equal(2, _bench.RuleCalls.Count);
        Assert.Contains("ghost=" + ghost.Id.Value, _bench.PlanText(world.Plans.Default));
        world.Frame();
    }

    [Fact]
    public void A_start_that_fails_mid_way_ends_what_started_and_leaves_lattice_off()
    {
        _bench.Scan = new ThrowingScan();

        var started = _bench.TryStart();

        var why = started.TryGetError(out var r) ? r : throw new Xunit.Sdk.XunitException("started");
        Assert.Equal("mirror", why.Facts["module"]);
        Assert.Equal("start-failed", why.Facts["problem"]);
        Assert.Contains("scan failed", why.Facts["error"]);
        Assert.False(LatticeSystem.Current.TryGet(out _));
        Assert.Contains(_bench.Log, line => line.Contains("does not start"));
    }

    [Fact]
    public void Without_the_catalogue_lattice_does_not_start()
    {
        var missing = Lattice.Core.Catalogue.CatalogueManifest.Module.Uses[0];

        var started = _bench.TryStart(WorldSeat.Host, missing);

        Assert.True(started.TryGetError(out var why));
        Assert.Equal("catalogue", why.Facts["module"]);
    }

    [Fact]
    public void A_started_handler_that_throws_leaves_publish_and_the_world_stays_current_until_it_ends()
    {
        var world = _bench.Start();
        Action<IGhostSystem> throwing = _ => throw new InvalidOperationException("mod bug");
        LatticeSystem.Started += throwing;
        try
        {
            Assert.Throws<InvalidOperationException>(world.Publish);
        }
        finally
        {
            LatticeSystem.Started -= throwing;
        }

        Assert.True(LatticeSystem.Current.TryGet(out var current) && ReferenceEquals(current, world));
        PlaceConduit(RunBench.At(0, 0, 0));
        world.End();
        Assert.False(LatticeSystem.Current.TryGet(out _));
    }

    [Fact]
    public void An_idle_frame_allocates_nothing()
    {
        var world = _bench.Start();
        PlaceConduit(RunBench.At(0, 0, 0));
        _bench.Settle();

        Allocations.AssertNone(world.Frame, "LatticeWorld.Frame");
    }

    [Fact]
    public void A_client_validates_and_selects_over_its_replica_and_sends_commands_to_the_host()
    {
        var world = _bench.Start(WorldSeat.Client);

        Assert.Equal("client", Assert.IsType<ModuleState.Off>(StateOf(world, "matching")).Why.Facts["seat"]);
        Assert.IsType<ModuleState.On>(StateOf(world, "validation"));
        Assert.True(world.Validation.Check(new Selector.InPlan(world.Plans.Default)).TryGet(out _));
        Assert.True(world.Board.Select(new Selector.Box(RunBench.At(0, 0, 0), RunBench.At(1, 1, 1))).TryGet(out _));
        Assert.False(world.Match.TryGet(out _));
        Assert.Equal(ReasonCodes.HostOnly, Refused(world.Inspector.Story(new GhostId(1))).Code);

        var sent = world.Commands.Submit(new CommandId(Guid.NewGuid()), new Command.Place(ValueList.Of(_bench.Draft("ModConduitLine", RunBench.At(0, 0, 0)))), WorldBench.Viewer);

        Assert.IsType<Submission.Sent>(sent);
        Assert.Single(_bench.Link.ToHostSent);
        Assert.Empty(world.Ghosts.Index.Ghosts);
    }

    private sealed class Faulty : ISlicedWork
    {
        public StepOutcome Step() => throw new InvalidOperationException("lattice job bug");
    }
}
