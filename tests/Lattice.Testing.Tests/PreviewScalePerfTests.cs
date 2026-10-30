using System;
using System.Diagnostics;
using System.Threading;
using Lattice.Api;
using Lattice.Testing;
using Xunit.Abstractions;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>
/// A small run's preview costs what the run costs, not what the board holds: an 11-cell cable run asked every frame
/// over 0, 1,000 and 5,000 planned frames far from it answers in as many frames as over an empty board (within 2x),
/// settled and while the plan that placed them is still being judged, and no frame spends more than 2 ms on the main
/// thread while it waits. Frames are counted on the test world's step clock, so the count is the same on any machine;
/// the frame time is wall-clock, a Perf test.
/// </summary>
public sealed class PreviewScalePerfTests(ITestOutputHelper output)
{
    private const int MostFrames = 2000;
    private static readonly TimeSpan FrameBudget = TimeSpan.FromMilliseconds(2);
    private static readonly KitKey CableKit = TestCatalogue.Hand.Kit("ItemCableCoil").Reduce(default(KitKey));

    private static Command.PlanRun Run() =>
        new(new RunRequest(CableKit, At(0), ValueList<Cell>.Empty, At(10), Option<PlanId>.None, Made));

    /// <summary>Planned frames on a large-grid lattice well away from the run.</summary>
    private static Command.Place Far(int count)
    {
        var frame = Hand("StructureFrame");
        var drafts = new GhostDraft[count];
        for (var i = 0; i < count; i++)
            drafts[i] = Draft(frame, new Cell(2000 + 20 * (i % 50), 2000 + 20 * (i / 50 % 50), 2000 + 20 * (i / 2500)));
        return new Command.Place(ValueList.From(drafts));
    }

    private static TestWorld Planned(TestScenario scenario, int ghosts, bool settle)
    {
        var world = Start(scenario);
        if (ghosts > 0)
            Assert.IsType<Outcome.Applied>(Submit(world, Far(ghosts)));
        if (settle)
            Must(world.Settle(MostFrames));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return world;
    }

    /// <summary>Frames until the preview is ready, and the longest frame meanwhile (the Lattice frame and the ask).</summary>
    private static (int Frames, TimeSpan Worst) Asked(TestWorld world, bool threads)
    {
        var run = Run();
        var worst = TimeSpan.Zero;
        for (var frames = 0; frames < MostFrames; frames++)
        {
            var clock = Stopwatch.StartNew();
            var answer = world.System.Preview.Of(run, world.Viewer);
            if (answer is Answer<Result<Preview, ValueList<Refusal>>>.Ready)
                return (frames, clock.Elapsed > worst ? clock.Elapsed : worst);
            world.Frame();
            clock.Stop();
            worst = clock.Elapsed > worst ? clock.Elapsed : worst;
            if (threads)
                Thread.Sleep(1);
        }

        throw new Xunit.Sdk.XunitException($"the preview stayed pending for {MostFrames} frames");
    }

    private int Frames(int ghosts, bool settle)
    {
        using var world = Planned(TestScenario.Empty, ghosts, settle);
        var (frames, _) = Asked(world, threads: false);
        output.WriteLine($"{ghosts} ghosts, {(settle ? "settled" : "just placed")}: {frames} frames to answer");
        return frames;
    }

    private TimeSpan Worst(int ghosts)
    {
        using var world = Planned(TestScenario.Empty.WithWorkerThreads(), ghosts, settle: true);
        var (frames, worst) = Asked(world, threads: true);
        output.WriteLine($"{ghosts} ghosts, settled, worker threads: {frames} frames, worst main-thread frame {worst.TotalMilliseconds:F2} ms");
        return worst;
    }

    [Fact]
    public void A_small_runs_preview_answers_in_as_many_frames_over_5000_unrelated_ghosts()
    {
        var empty = Math.Max(1, Frames(0, settle: true));

        foreach (var ghosts in new[] { 1000, 5000 })
        {
            Assert.InRange(Frames(ghosts, settle: true), 0, 2 * empty);
            Assert.InRange(Frames(ghosts, settle: false), 0, 2 * empty);
        }
    }

    [Fact]
    public void A_preview_answers_while_an_unrelated_plan_still_waits_on_a_mods_main_thread_rule()
    {
        var asked = 0;
        TestWorld Ruled()
        {
            var world = Start(TestScenario.Empty);
            Must(world.System.Registries.Register(new RuleDescriptor(Must(RuleId.Create(Mod, "main-rule")), _ =>
            {
                asked++;
                return new RuleVerdict.Pass();
            }) { Thread = CallbackThread.Main }));
            return world;
        }

        int empty;
        using (var bare = Ruled())
            empty = Math.Max(1, Asked(bare, threads: false).Frames);
        using var planned = Ruled();
        Assert.IsType<Outcome.Applied>(Submit(planned, Far(5000)));
        asked = 0;

        var (frames, _) = Asked(planned, threads: false);

        output.WriteLine($"5000 ghosts with a mod's main-thread rule, just placed: {frames} frames to answer (0 ghosts: {empty}); the rule asked about {asked} ghosts by then");
        Assert.InRange(frames, 0, 2 * empty);
        Assert.InRange(asked, 0, 4999);
        Must(planned.Settle(MostFrames));
        Assert.InRange(asked, 5000, int.MaxValue);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void No_frame_spends_more_than_2_ms_on_the_main_thread_while_the_preview_waits()
    {
        Worst(0);
        foreach (var ghosts in new[] { 1000, 5000 })
        {
            var worst = Worst(ghosts);
            Assert.True(worst < FrameBudget, $"{ghosts} ghosts: worst frame {worst.TotalMilliseconds:F2} ms");
        }
    }
}
