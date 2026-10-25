using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Safety;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Safety;
using Lattice.Tests.Swaps;
using static Lattice.Tests.Runs.RunBench;
using PreviewAnswer = Lattice.Api.Answer<Lattice.Api.Result<Lattice.Api.Preview, Lattice.Api.ValueList<Lattice.Api.Refusal>>>;

namespace Lattice.Tests.Preview;

/// <summary>The three flows of architecture section 10 over the real modules, and what a preview promises.</summary>
public sealed class PreviewFlowTests
{
    private static readonly ReasonCode Blocked = TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "blocked"));

    [Fact]
    public void A_players_run_preview_gives_cells_pieces_the_first_failing_cell_and_its_cost_for_a_modded_kit()
    {
        var bench = new PreviewBench();
        bench.Runs.Check.Refuse(p => bench.Runs.CellsOf(p).Contains(At(0, 3, 0)), Blocked, "in the way");
        bench.Start();
        var run = new Command.PlanRun(bench.Runs.Run(bench.Runs.Conduit, At(0, 0, 0), At(0, 5, 0)));

        var preview = bench.Ok(run);

        var report = preview.Run.Match(r => r, () => throw new Xunit.Sdk.XunitException("no run report"));
        Assert.Equal(6, report.Cells.Count);
        Assert.NotEmpty(report.Pieces);
        Assert.All(report.Pieces, p => Assert.StartsWith("ModConduit", bench.Runs.NameOf(p.Piece)));
        var failure = report.FirstFailure.Match(f => f, () => throw new Xunit.Sdk.XunitException("no failure"));
        Assert.Equal(At(0, 3, 0), failure.At);
        Assert.Contains(report.CostAsSingles.Needed, q => q.Item == new ItemKey(bench.Runs.KitOf("ModConduitLine").Hash));
        Assert.Equal(preview.WouldChange.Added.Count, preview.Findings.Where(f => f is not Finding.Warns).Select(f => f.Ghost).Distinct().Count());
        Assert.NotEmpty(preview.Bill.Needed);
    }

    [Fact]
    public void A_route_preview_is_pending_while_its_search_runs_then_gives_its_run_report()
    {
        var bench = new PreviewBench().Start();
        var route = new Command.PlanRoute(new RouteRequest(
            bench.Runs.Cable, At(2, 2, 2), At(6, 2, 6), new RouteToggles(false, false, false, false, false), Option<PlanId>.None, TestBoard.TestMaker));

        Assert.IsType<PreviewAnswer.Pending>(bench.Preview.Of(route, TestBoard.Alice));
        var preview = bench.Ok(route);

        var report = preview.Run.Match(r => r, () => throw new Xunit.Sdk.XunitException("no run report"));
        Assert.Equal(At(2, 2, 2), report.Cells[0]);
        Assert.Equal(At(6, 2, 6), report.Cells[^1]);
        Assert.Equal(report.Pieces.Count, preview.WouldChange.Added.Count);
    }

    [Fact]
    public void An_agents_swap_dry_run_gives_the_change_set_findings_bill_and_effects_and_a_swap_keeps_its_network()
    {
        var bench = new PreviewBench();
        var alongX = bench.Runs.Sending(Direction.ZPlus, Direction.XPlus);
        var cables = new[]
        {
            bench.Runs.Real("StructureCableStraight", At(0, 0, 0), alongX),
            bench.Runs.Real("StructureCableStraight", At(1, 0, 0), alongX),
            bench.Runs.Real("StructureCableStraight", At(2, 0, 0), alongX),
        };
        var network = bench.Runs.World.NetworkOf(cables[0].Id).Match(n => n, () => throw new Xunit.Sdk.XunitException("no network"));
        bench.Start();
        var swap = new Command.PlanSwap(
            new StructureSelector.Network(network), new SwapTo.SameShapeFrom(bench.Runs.KitOf("StructureCableStraightH")), Option<PlanId>.None, SwapBench.Asker);
        var agent = new Actor.Agent(PreviewBench.Agent);

        Assert.IsType<PreviewAnswer.Pending>(bench.Preview.Of(swap, agent));
        var preview = bench.Ok(swap, agent);

        Assert.Equal(3, preview.WouldChange.Added.Count);
        Assert.All(preview.WouldChange.Added, g => Assert.IsType<Intent.Swap>(g.Intent));
        Assert.Equal(
            preview.WouldChange.Added.Select(g => g.Id).OrderBy(id => id.Value),
            preview.Findings.Where(f => f is not Finding.Warns).Select(f => f.Ghost).Distinct().OrderBy(id => id.Value));
        Assert.Empty(preview.Effects.Networks);
        Assert.Contains(preview.Bill.Needed, q => q.Item == new ItemKey(bench.Runs.KitOf("StructureCableStraightH").Hash));
        Assert.Equal(Option<RunReport>.None, preview.Run);
    }

    [Fact]
    public void A_builders_bill_in_a_preview_is_the_bill_of_the_ghosts_once_placed()
    {
        var bench = new PreviewBench().Start();
        var place = new Command.Place(ValueList.From(new[]
        {
            bench.Runs.Boards.Draft("StructureCableStraight", At(0, 0, 0)),
            bench.Runs.Boards.Draft("StructureCableCorner", At(1, 0, 0)),
        }));

        var bill = bench.Ok(place).Bill;

        var applied = TestBoard.Applied(bench.Board.Apply(new CommandId(Guid.NewGuid()), place, TestBoard.Alice));
        var plans = applied.Added.Select(g => TestKitIds.Must(bench.Steps.For(g))).ToList();
        Assert.Equal(Lattice.Core.Steps.BillMaker.Of(plans), bill);
        Assert.NotEmpty(bill.Needed);
    }
}

public sealed class PreviewPromiseTests
{
    private static readonly ReasonCode NotYours = TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "not-yours"));

    private static Command.Place Cable(PreviewBench bench, Cell at) =>
        new(ValueList.Of(bench.Runs.Boards.Draft("StructureCableStraight", at)));

    [Fact]
    public void A_preview_changes_nothing()
    {
        var bench = new PreviewBench();
        bench.Runs.Real("StructureCableStraight", At(4, 0, 0));
        bench.Start();
        bench.Runs.Boards.Placed(bench.Runs.Boards.Draft("StructureCableStraight", At(6, 0, 0)));
        bench.Settle();
        var revision = bench.Board.Revision;
        var ghosts = bench.Board.Index.Ghosts.Keys.ToList();
        var world = bench.Runs.World.All.ToList();
        var mirror = bench.Mirror.Revision;
        var report = TestKitIds.Must(bench.Validation.Check(new Selector.Ids(ValueList.From(ghosts))));
        var heard = 0;
        bench.Events.Changed += _ => heard++;

        var preview = bench.Ok(Cable(bench, At(5, 0, 0)));

        Assert.Single(preview.WouldChange.Added);
        Assert.Equal(revision, bench.Board.Revision);
        Assert.Equal(ghosts, bench.Board.Index.Ghosts.Keys.ToList());
        Assert.Equal(world, bench.Runs.World.All.ToList());
        Assert.Equal(mirror, bench.Mirror.Revision);
        Assert.Equal(0, heard);
        Assert.Equal(report, TestKitIds.Must(bench.Validation.Check(new Selector.Ids(ValueList.From(ghosts)))));
        var placed = TestBoard.Applied(bench.Board.Apply(new CommandId(Guid.NewGuid()), Cable(bench, At(5, 0, 0)), TestBoard.Alice));
        Assert.Equal(preview.WouldChange.Added[0].Id, placed.Added[0].Id);
    }

    [Fact]
    public void A_preview_is_pending_then_ready_from_the_cache_without_allocating_and_a_new_revision_starts_again()
    {
        var bench = new PreviewBench().Start();
        var command = Cable(bench, At(0, 0, 0));
        var ready = 0;
        bench.Events.AnswersReady += _ => ready++;

        Assert.IsType<PreviewAnswer.Pending>(bench.Preview.Of(command, TestBoard.Alice));
        bench.Ok(command);
        Assert.True(ready > 0);
        var first = bench.Preview.Of(command, TestBoard.Alice);
        Assert.IsType<PreviewAnswer.Ready>(first);
        Assert.Same(first, bench.Preview.Of(command, TestBoard.Alice));
        var before = GC.GetAllocatedBytesForCurrentThread();
        bench.Preview.Of(command, TestBoard.Alice);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        bench.Runs.Boards.Placed(bench.Runs.Boards.Draft("StructureCableStraight", At(3, 0, 0)));
        var after = bench.Preview.Of(command, TestBoard.Alice);
        Assert.IsType<PreviewAnswer.Pending>(after);
        Assert.Equal(bench.Board.Revision, after.At);
    }

    [Fact]
    public void A_policy_refusal_and_an_unknown_piece_come_back_as_refusals()
    {
        var bench = new PreviewBench().Start();
        Assert.True(bench.Runs.Boards.Policies.Install(new AccessPolicy(
            (_, _) => true, (_, _) => true, request => request.Actor == TestBoard.Bob
                ? Result<Unit, Reason>.Error(Reason.Of(NotYours, RuleId.Contract))
                : Result<Unit, Reason>.Ok(default))).IsOk);

        var refused = bench.Ask(Cable(bench, At(0, 0, 0)), TestBoard.Bob);
        var unknown = bench.Ask(new Command.Place(ValueList.Of(TestBoard.Draft(new Intent.Build(
            new Placement(new PrefabKey(424242), At(0, 0, 0), default), Option<Finish>.None)))));

        Assert.Equal(NotYours, Assert.Single(refused.Match(_ => ValueList<Refusal>.Empty, why => why)).Why.Code);
        Assert.NotEmpty(unknown.Match(_ => ValueList<Refusal>.Empty, why => why));
    }

    [Fact]
    public void A_build_safety_warning_is_a_finding_never_a_refusal()
    {
        var bench = new PreviewBench();
        var cable = bench.Runs.Real("StructureCableStraight", At(0, 0, 0));
        var large = bench.Runs.Grid.Snap(bench.Runs.Grid.CentreOf(At(0, 0, 0), GridSize.Small), GridSize.Large);
        bench.Runs.World.SetRoom(new RoomInfo(new RoomRef(7), ValueList.Of(large), true, Option<ValueList<GasHeld>>.None));
        bench.Start();
        var paint = new Command.Place(ValueList.Of(TestBoard.Draft(new Intent.FinishOnly(cable, SafetyBench.Colour))));

        var preview = bench.Ok(paint);

        var warns = Assert.Single(preview.Findings.OfType<Finding.Warns>(), w => w.Why.Code == SafetyCodes.PaintIndoors);
        Assert.Equal(preview.WouldChange.Added[0].Id, warns.Ghost);
        Assert.Contains(preview.Effects.Gas, g => g is GasEffect.Pollutes { Room.Value: 7 });
    }
}

public sealed class PreviewAnalyzerTests
{
    private static readonly ReasonCode Count = TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "count"));

    private static AnalyzerDescriptor Counting(string name, CallbackThread thread, Action? also = null) =>
        new(TestKitIds.Must(RuleId.Create(TestKitIds.Mod, name)), input =>
        {
            also?.Invoke();
            return ValueList.Of(new AnalyzerFact(TestKitIds.Must(RuleId.Create(TestKitIds.Mod, name)), Count, ValueMap<string, string>.Of("ghosts", input.Ghosts.Count.ToString())));
        })
        {
            Thread = thread,
        };

    [Fact]
    public void Modded_analyzers_on_both_threads_add_their_facts_in_registration_order()
    {
        var bench = new PreviewBench();
        var onWorker = new List<bool>();
        bench.Analyzers.Register(Counting("main", CallbackThread.Main, () => onWorker.Add(bench.Worker.Running)));
        bench.Analyzers.Register(Counting("worker", CallbackThread.Worker, () => onWorker.Add(bench.Worker.Running)));
        bench.Start();

        var preview = bench.Ok(new Command.Place(ValueList.From(new[]
        {
            bench.Runs.Boards.Draft("StructureCableStraight", At(0, 0, 0)),
            bench.Runs.Boards.Draft("StructureCableStraight", At(2, 0, 0)),
        })));

        Assert.Equal(new[] { "main", "worker" }, preview.Effects.Facts.Select(f => f.From.Value.Split('/')[1]));
        Assert.All(preview.Effects.Facts, f => Assert.Equal("2", f.Values["ghosts"]));
        Assert.Equal(new[] { false, true }, onWorker);
    }

    [Fact]
    public void A_command_an_analyzer_submits_is_queued_and_applied_after_the_staged_step()
    {
        var bench = new PreviewBench();
        var submitted = false;
        var stagedGhostsSeen = -1;
        bench.Analyzers.Register(Counting("submits", CallbackThread.Main, () =>
        {
            if (submitted)
                return;
            submitted = true;
            stagedGhostsSeen = bench.Board.ViewFor(TestBoard.Alice).At(At(0, 0, 0)).Count;
            bench.Commands.Submit(new CommandId(Guid.NewGuid()), new Command.Place(ValueList.Of(bench.Runs.Boards.Draft("StructureCableStraight", At(9, 0, 0)))), TestBoard.Alice);
            Assert.Empty(bench.Board.ViewFor(TestBoard.Alice).At(At(9, 0, 0)));
        }));
        bench.Start();
        var revision = bench.Board.Revision;

        bench.Preview.Of(new Command.Place(ValueList.Of(bench.Runs.Boards.Draft("StructureCableStraight", At(0, 0, 0)))), TestBoard.Alice);
        bench.Frame();

        Assert.True(submitted);
        Assert.Equal(1, stagedGhostsSeen);
        Assert.Equal(revision.Value + 1, bench.Board.Revision.Value);
        var ghost = Assert.Single(bench.Board.Index.Ghosts.Values);
        Assert.Equal(At(9, 0, 0), Assert.IsType<Intent.Build>(ghost.Ghost.Intent).Target.At);
    }
}
