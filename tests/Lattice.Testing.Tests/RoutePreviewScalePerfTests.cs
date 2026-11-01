using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Lattice.Api;
using Lattice.Testing;
using Xunit.Abstractions;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>
/// A short route's preview costs what its search box holds, not what the board holds: a Connect-shaped cable route
/// between the power ports of two planned devices eight cells apart, asked every frame over 0, 1,000, 5,000 and 20,000
/// planned frames far from it, answers in as many frames as over an empty board (within 2x), settled and
/// while the plan that placed them is still being judged, and no frame spends more than 2 ms on the main thread while
/// it waits.
/// Planned frames inside the search box cost in proportion to the box: the route still answers. Frames are counted on
/// the test world's step clock, so the count is the same on any machine; the times are wall-clock, a Perf test.
/// </summary>
public sealed class RoutePreviewScalePerfTests(ITestOutputHelper output)
{
    private const int MostFrames = 4000;
    private static readonly TimeSpan FrameBudget = TimeSpan.FromMilliseconds(2);
    private static readonly KitKey CableKit = TestCatalogue.Hand.Kit("ItemCableCoil").Reduce(default(KitKey));

    /// <summary>A small device in its cell's device slot, with one power input facing +X and one data output facing +Y.</summary>
    private static readonly TestPiece Charger = Must(TestPiece.Create("ModCharger", "ItemModCharger", PieceGrid.Small))
        .WithPort(new CellOffset(0, 0, 0), SignedAxis.XPlus, ValueSet.Of(TestNetwork.Power), PortRole.Input)
        .WithPort(new CellOffset(0, 0, 0), SignedAxis.YPlus, ValueSet.Of(TestNetwork.Data), PortRole.Output)
        .WithCell(new CellOffset(0, 0, 0), TestSlot.Device)
        .WithCollision(4);

    private static readonly Turn HalfYaw = Enumerable.Range(0, Turn.Count)
        .Select(static i => Must(Turn.Create(i)))
        .Single(static t => t.Axes.X == SignedAxis.XMinus && t.Axes.Y == SignedAxis.YPlus);

    private static readonly PortIndex PowerPort = Must(PortIndex.Create(0));

    /// <summary>Planned frames on a large-grid lattice well away from the route.</summary>
    private static Command.Place Far(int count) => Frames(count, static i => new Cell(2000 + 20 * (i % 50), 2000 + 20 * (i / 50 % 50), 2000 + 20 * (i / 2500)));

    /// <summary>Planned frames filling the route's search box, large cell by large cell, nearest the route first.</summary>
    private static Command.Place Near(int count)
    {
        var cells = (
            from x in Enumerable.Range(-3, 19)
            from y in Enumerable.Range(-8, 17)
            from z in Enumerable.Range(-8, 17)
            orderby Math.Abs(x - 1) + Math.Abs(y) + Math.Abs(z), x, y, z
            select new Cell(100 + 20 * x, 100 + 20 * y, 100 + 20 * z)).Take(count).ToArray();
        Assert.Equal(count, cells.Length);
        return Frames(count, i => cells[i]);
    }

    private static Command.Place Frames(int count, Func<int, Cell> at)
    {
        var frame = Hand("StructureFrame");
        var drafts = new GhostDraft[count];
        for (var i = 0; i < count; i++)
            drafts[i] = Draft(frame, at(i));
        return new Command.Place(ValueList.From(drafts));
    }

    /// <summary>Two planned chargers eight cells apart, power ports facing, and Connect's route between those ports.</summary>
    private static (TestWorld World, Command.PlanRoute Route) Planned(TestScenario scenario, Func<int, Command.Place> where, int ghosts, bool settle)
    {
        var world = Start(scenario.WithPiece(Charger));
        var devices = Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.From(new[]
        {
            Draft(Charger.Key, At(0)),
            Draft(Charger.Key, At(8), HalfYaw),
        })))).Changes.Added;
        if (ghosts > 0)
            Assert.IsType<Outcome.Applied>(Submit(world, where(ghosts)));
        if (settle)
            Must(world.Settle(MostFrames));
        var toggles = new RouteToggles(false, false, false, KeepPortsFree: true, NoBridging: true)
        {
            First = Option.Some(FirstLeg.Level),
            HugFramesAndWalls = true,
        };
        var route = new Command.PlanRoute(new RouteRequest(CableKit, At(1), At(7), toggles, Option<PlanId>.None, Made)
        {
            FromPort = Option.Some<PortEnd>(new PortEnd.OnGhost(devices[0].Id, PowerPort)),
            ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(devices[1].Id, PowerPort)),
            Lay = new LayOptions(false, true),
        });
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return (world, route);
    }

    /// <summary>Frames until the preview is ready, the time all of them took, and the longest frame meanwhile (the Lattice frame and the ask).</summary>
    private static (int Frames, TimeSpan Total, TimeSpan Worst, Result<Preview, ValueList<Refusal>> Answer) Asked(TestWorld world, Command route, bool threads)
    {
        var (worst, total) = (TimeSpan.Zero, TimeSpan.Zero);
        for (var frames = 0; frames < MostFrames; frames++)
        {
            var clock = Stopwatch.StartNew();
            var answer = world.System.Preview.Of(route, world.Viewer);
            if (answer is Answer<Result<Preview, ValueList<Refusal>>>.Ready ready)
                return (frames, total + clock.Elapsed, clock.Elapsed > worst ? clock.Elapsed : worst, ready.Value);
            world.Frame();
            clock.Stop();
            total += clock.Elapsed;
            worst = clock.Elapsed > worst ? clock.Elapsed : worst;
            if (threads)
                Thread.Sleep(1);
        }

        throw new Xunit.Sdk.XunitException($"the route's preview stayed pending for {MostFrames} frames");
    }

    private static Preview Laid(Result<Preview, ValueList<Refusal>> answer) =>
        answer.Match(static p => p, static why => throw new Xunit.Sdk.XunitException(Describe(why[0].Why)));

    /// <summary>Frames to answer inline, and the wall time of those frames (main thread and the inline worker together).</summary>
    private int Inline(string where, Func<int, Command.Place> place, int ghosts, bool settle)
    {
        var (world, route) = Planned(TestScenario.Empty, place, ghosts, settle);
        using (world)
        {
            var (frames, total, _, answer) = Asked(world, route, threads: false);
            var run = Laid(answer).Run.Reduce(() => throw new Xunit.Sdk.XunitException("no run in the preview"));
            Assert.True(run.FirstFailure.IsNone);
            output.WriteLine($"{ghosts,6} {where} ghosts, {(settle ? "settled    " : "just placed")}: {frames,4} frames to answer, {total.TotalMilliseconds,8:F1} ms inline (main + worker)");
            return frames;
        }
    }

    /// <summary>The longest main-thread frame while the preview waits, Lattice's worker on its own threads.</summary>
    private TimeSpan Threaded(string where, Func<int, Command.Place> place, int ghosts)
    {
        var (world, route) = Planned(TestScenario.Empty.WithWorkerThreads(), place, ghosts, settle: true);
        using (world)
        {
            var (frames, total, worst, answer) = Asked(world, route, threads: true);
            Laid(answer);
            output.WriteLine($"{ghosts,6} {where} ghosts, settled, worker threads: {frames,4} frames, main thread {total.TotalMilliseconds,8:F1} ms in all, worst frame {worst.TotalMilliseconds:F2} ms");
            return worst;
        }
    }

    [Fact]
    public void A_short_routes_preview_answers_in_as_many_frames_over_20000_settled_unrelated_ghosts()
    {
        var empty = Math.Max(1, Inline("far", Far, 0, settle: true));

        foreach (var ghosts in new[] { 1000, 5000, 20000 })
            Assert.InRange(Inline("far", Far, ghosts, settle: true), 0, 2 * empty);
    }

    [Fact]
    public void A_short_routes_preview_answers_in_as_many_frames_over_5000_just_placed_unrelated_ghosts()
    {
        var empty = Math.Max(1, Inline("far", Far, 0, settle: true));

        foreach (var ghosts in new[] { 1000, 5000 })
            Assert.InRange(Inline("far", Far, ghosts, settle: false), 0, 2 * empty);
    }

    [Fact]
    public void A_short_routes_preview_answers_in_as_many_frames_over_20000_just_placed_unrelated_ghosts()
    {
        var empty = Math.Max(1, Inline("far", Far, 0, settle: true));

        Assert.InRange(Inline("far", Far, 20000, settle: false), 0, 2 * empty);
    }

    [Fact]
    public void Planned_frames_in_the_routes_box_cost_in_proportion_to_the_box()
    {
        var empty = Math.Max(1, Inline("near", Near, 0, settle: true));

        foreach (var ghosts in new[] { 1000, 5000 })
            Assert.InRange(Inline("near", Near, ghosts, settle: true), 0, 4 * empty);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void No_frame_spends_more_than_2_ms_on_the_main_thread_while_a_routes_preview_waits()
    {
        Threaded("far", Far, 0);
        foreach (var ghosts in new[] { 1000, 5000, 20000 })
        {
            var worst = Threaded("far", Far, ghosts);
            Assert.True(worst < FrameBudget, $"{ghosts} far ghosts: worst frame {worst.TotalMilliseconds:F2} ms");
        }

        foreach (var ghosts in new[] { 1000, 5000 })
            Threaded("near", Near, ghosts);
    }
}
