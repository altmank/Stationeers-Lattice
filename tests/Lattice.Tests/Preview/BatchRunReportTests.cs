using System;
using System.Diagnostics;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Xunit.Abstractions;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Preview;

/// <summary>
/// A batch's preview carries the reports of its run and route steps: <see cref="Lattice.Api.Preview.Runs"/> one per
/// step in step order, nested batches flattened, and <see cref="Lattice.Api.Preview.Run"/> the first of them. Each is
/// the step's report as the batch lays it, against the earlier steps' ghosts.
/// </summary>
public sealed class BatchRunReportTests
{
    private static readonly RouteToggles Off = new(false, false, false, false, false);

    private static Command.Batch Batch(params Command[] steps) => new(ValueList.From(steps));

    private static Command Tint() =>
        new Command.Change(new Selector.MadeBy(TestBoard.TestMaker), new GhostEdit.SetTint(Option.Some(new Tint(200, 40, 40, 255))), Option<Revision>.None);

    private static Command Labelled() =>
        new Command.Change(new Selector.MadeBy(TestBoard.TestMaker), new GhostEdit.SetLabel(Option.Some(TestKitIds.Must(Label.Create("east")))), Option<Revision>.None);

    private static Reason Refused(Result<Lattice.Api.Preview, ValueList<Refusal>> answer) =>
        Assert.Single(answer.Match(static _ => throw new Xunit.Sdk.XunitException("not refused"), static why => why)).Why;

    private static RunReport Report(Lattice.Api.Preview preview) =>
        preview.Run.Match(static r => r, () => throw new Xunit.Sdk.XunitException("no run report"));

    /// <summary>A run across a real cable at x=3 that breaks no-bridging there, and fails the game's check at x=6.</summary>
    private static (PreviewBench Bench, Command.PlanRun Run) BrokenRun()
    {
        var bench = new PreviewBench();
        for (var z = 0; z <= 4; z++)
            bench.Runs.Real("StructureCableStraight", At(3, 2, z));
        bench.Runs.Check.Refuse(p => bench.Runs.CellsOf(p).Contains(At(6, 2, 2)), ReasonCodes.GameRefused, "in the way");
        bench.Start();
        var request = bench.Runs.Run(bench.Runs.Cable, At(0, 2, 2), At(8, 2, 2)) with { Rules = Option.Some(Off with { NoBridging = true }) };
        return (bench, new Command.PlanRun(request));
    }

    [Fact]
    public void A_run_then_a_tint_reports_the_run_as_it_reports_alone()
    {
        var (bench, run) = BrokenRun();

        var alone = bench.Ok(run);
        var batched = bench.Ok(Batch(run, Tint()));

        var report = Report(batched);
        Assert.Equal(Report(alone), report);
        Assert.Equal(At(3, 2, 2), report.FirstBreak.Match(static b => b.At, () => default));
        Assert.Equal(At(6, 2, 2), report.FirstFailure.Match(static f => f.At, () => default));
        Assert.Equal(report, Assert.Single(batched.Runs));
        Assert.Contains(batched.WouldChange.Added, g => g.Tint.IsSome);
    }

    [Fact]
    public void Two_runs_report_in_step_order_the_second_laid_against_the_firsts_ghosts()
    {
        var bench = new PreviewBench().Start();
        var trunk = new Command.PlanRun(bench.Runs.Run(bench.Runs.Cable, At(0, 0, 0), At(6, 0, 0)));
        var join = At(2, 0, 0);
        var branch = new Command.PlanRun(bench.Runs.Run(bench.Runs.Cable, join, At(2, 0, 3)));

        var preview = bench.Ok(Batch(trunk, branch));

        Assert.Equal(2, preview.Runs.Count);
        Assert.Equal(Report(preview), preview.Runs[0]);
        Assert.Equal(Report(bench.Ok(trunk)), preview.Runs[0]);
        var tee = Assert.Single(preview.Runs[1].Pieces, p => bench.Runs.CellsOf(p).Contains(join));
        Assert.Equal(Ways("x+,x-,z+"), bench.Runs.WaysOf(tee, join));
    }

    [Fact]
    public void The_second_runs_report_is_the_one_it_gives_alone_once_the_first_is_placed()
    {
        var batchedBench = new PreviewBench().Start();
        var trunk = new Command.PlanRun(batchedBench.Runs.Run(batchedBench.Runs.Cable, At(0, 0, 0), At(6, 0, 0)));
        var branch = new Command.PlanRun(batchedBench.Runs.Run(batchedBench.Runs.Cable, At(2, 0, 0), At(2, 0, 3)));
        var batched = batchedBench.Ok(Batch(trunk, branch)).Runs[1];

        var aloneBench = new PreviewBench().Start();
        TestBoard.Applied(Submit(aloneBench, trunk));
        var alone = Report(aloneBench.Ok(branch));

        Assert.Equal(alone.Cells, batched.Cells);
        Assert.Equal(alone.Pieces, batched.Pieces);
        Assert.Equal(alone.FirstFailure, batched.FirstFailure);
    }

    private static Outcome Submit(PreviewBench bench, Command command)
    {
        var id = new CommandId(Guid.NewGuid());
        Outcome? answered = null;
        void Answered(Ticket ticket, Outcome outcome)
        {
            if (ticket == new Ticket(id))
                answered = outcome;
        }

        bench.Events.Answered += Answered;
        try
        {
            if (bench.Commands.Submit(id, command, TestBoard.Alice) is Submission.Answered now)
                return now.Outcome;
            for (var i = 0; i < 200 && answered is null; i++)
                bench.Frame();
            return answered ?? throw new InvalidOperationException("not answered");
        }
        finally
        {
            bench.Events.Answered -= Answered;
        }
    }

    [Fact]
    public void A_route_then_a_label_reports_the_route_as_it_reports_alone()
    {
        var bench = new PreviewBench().Start();
        var route = new Command.PlanRoute(new RouteRequest(bench.Runs.Cable, At(2, 2, 2), At(6, 2, 6), Off, Option<PlanId>.None, TestBoard.TestMaker));

        var alone = bench.Ok(route);
        var batched = bench.Ok(Batch(route, Labelled()));

        Assert.Equal(Report(alone), Report(batched));
        Assert.Equal(Report(batched), Assert.Single(batched.Runs));
    }

    [Fact]
    public void A_route_that_finds_no_way_refuses_the_batch_with_its_stopped_causes_as_alone()
    {
        var bench = new PreviewBench();
        var to = At(6, 2, 6);
        foreach (var side in new[] { Direction.XPlus, Direction.XMinus, Direction.YPlus, Direction.YMinus, Direction.ZPlus, Direction.ZMinus })
            bench.Runs.Real("StructureCableStraight", Step(to, side));
        bench.Start();
        var route = new Command.PlanRoute(new RouteRequest(
            bench.Runs.Cable, At(4, 2, 6), to, Off with { KeepOffLines = true }, Option<PlanId>.None, TestBoard.TestMaker));

        var alone = Refused(bench.Ask(route));
        var batched = Refused(bench.Ask(Batch(route, Labelled())));

        Assert.Contains(alone.Code, new[] { ReasonCodes.NoRoute, ReasonCodes.SearchLimit });
        Assert.Contains(":along-line", alone.Facts["stopped"]);
        Assert.Equal(alone.Code, batched.Code);
        Assert.All(alone.Facts, fact => Assert.Equal(fact.Value, batched.Facts[fact.Key]));
    }

    [Fact]
    public void A_run_inside_a_nested_batch_is_found()
    {
        var (bench, run) = BrokenRun();
        TestBoard.Applied(Submit(bench, new Command.PlanRun(bench.Runs.Run(bench.Runs.Cable, At(0, 2, 8), At(2, 2, 8)))));

        var preview = bench.Ok(Batch(Batch(run), Tint()));
        var nestedLater = bench.Ok(Batch(Labelled(), Batch(run)));

        Assert.Equal(Report(bench.Ok(run)), Report(preview));
        Assert.Equal(Report(bench.Ok(run)), Assert.Single(nestedLater.Runs));
    }

    [Fact]
    public void A_batch_without_a_run_has_no_report_and_a_lone_run_a_list_of_one()
    {
        var (bench, run) = BrokenRun();
        TestBoard.Applied(Submit(bench, run));

        var tinted = bench.Ok(Batch(Tint(), Labelled()));
        var alone = bench.Ok(run);

        Assert.True(tinted.Run.IsNone);
        Assert.Empty(tinted.Runs);
        Assert.Equal(Report(alone), Assert.Single(alone.Runs));
    }
}

/// <summary>
/// A 256-cell run previewed as a batch's only step costs what it costs alone; with a tint of its ghosts after it, the
/// tint's own validation and bill come on top.
/// </summary>
[Collection(nameof(Timing))]
public sealed class BatchRunReportPerfTests(ITestOutputHelper output)
{
    private static TimeSpan Best(Func<PreviewBench, Command> command)
    {
        var best = TimeSpan.MaxValue;
        for (var run = 0; run < 5; run++)
        {
            var bench = new PreviewBench().Start();
            var asked = command(bench);
            Perf.Collect();
            var clock = Stopwatch.StartNew();
            var preview = bench.Ok(asked);
            clock.Stop();
            Assert.Equal(256, preview.Runs[0].Cells.Count);
            best = clock.Elapsed < best ? clock.Elapsed : best;
        }

        return best;
    }

    private static Command.PlanRun Long(PreviewBench bench) =>
        new(bench.Runs.Run(bench.Runs.Cable, At(0, 2, 2), At(255, 2, 2)));

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void A_256_cell_runs_preview_inside_a_batch_costs_what_it_costs_alone()
    {
        var alone = Best(Long);
        var batched = Best(bench => new Command.Batch(ValueList.Of<Command>(Long(bench))));
        var tinted = Best(bench => new Command.Batch(ValueList.From(new Command[]
        {
            Long(bench),
            new Command.Change(new Selector.MadeBy(TestBoard.TestMaker), new GhostEdit.SetTint(Option.Some(new Tint(1, 2, 3, 255))), Option<Revision>.None),
        })));

        output.WriteLine($"256-cell run preview: alone {alone.TotalMilliseconds:F2} ms, as a batch {batched.TotalMilliseconds:F2} ms, with a tint {tinted.TotalMilliseconds:F2} ms");
        Assert.True(batched < alone * 1.5 + TimeSpan.FromMilliseconds(1), $"batched {batched.TotalMilliseconds} ms against {alone.TotalMilliseconds} ms alone");
        Assert.True(tinted < alone * 2.5 + TimeSpan.FromMilliseconds(2), $"with a tint {tinted.TotalMilliseconds} ms against {alone.TotalMilliseconds} ms alone");
    }
}
