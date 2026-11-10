using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.Render;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.World;
using Lattice.Tests.EntryPoint;
using Lattice.Tests.Render;
using Xunit.Abstractions;

namespace Lattice.Tests.Validation.RealBase;

/// <summary>
/// What each frame costs when a 5,000-ghost plan arrives over lattice-gen-big and again when the world reloads from its
/// save, with styles and Render's rebuilds running over fakes. Each frame is given on the main thread's CPU, the
/// collector pause in it, the runtime's compiling on the thread (methods and ms), the time the scheduler spent by kind
/// and the longest gaps between two reads of the frame clock (each is one step of sliced work, or the stretch before
/// the first). The run is made three times in one process: the first arrival is the process's first (cold: first
/// calls compile), the later ones are warm. The lines go to <c>tests/fixtures/generated/plan-arrival-*.txt</c>.
/// <para>
/// Warm-up is on unless <c>LATTICE_PLAN_WARMUP=0</c>. With it, each world first runs idle frames until Lattice's
/// <see cref="WarmUp"/> has compiled its sets, then the plan arrives: no arrival frame of any run's thread CPU, less
/// the collector pause in it, passes <see cref="Frame"/> on the process's first arrival (the later ones are warm
/// whatever the warm-up did, and the runtime's own tier-up lands in them), nor does an idle frame pass
/// <see cref="IdleFrame"/>, and the main thread compiles at most <see cref="Compiled"/> methods over that arrival and
/// its reload. Without it (the run that shows what the warm-up removes) the
/// cold arrival is reported, not judged. The pin is cold only in a process where nothing ran the same code before:
/// it is a Perf test (thread CPU is shared-core sensitive); run it alone (<c>--filter FullyQualifiedName~PlanArrivalTests</c>) for the cold pin: in a shared process the code is already compiled and the frames are noisier.
/// </para>
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class PlanArrivalTests(ITestOutputHelper output)
{
    private const int PlanSize = 5000;
    private const double Frame = 10.0;
    private const double IdleFrame = 10.0;
    private const long Compiled = 500;
    private readonly List<string> _lines = new();
    private Renderer? _renderer;
    private long _compiledInArrival;

    [RealBaseFact(RealBaseBench.BigBase)]
    [Trait(Perf.Trait, Perf.Value)]
    public void A_big_plan_arriving_costs_no_frame_past_the_budgets()
    {
        var warmUp = Environment.GetEnvironmentVariable("LATTICE_PLAN_WARMUP") != "0";
        var latency = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.Batch;
        try
        {
            var first = default((List<double> Idle, List<double> Arrival));
            long firstCompiled = 0;
            foreach (var run in new[] { "first", "second", "third" })
            {
                _lines.Add($"=== {run}, warm-up {(warmUp ? "on" : "off")} ===");
                _compiledInArrival = 0;
                var costs = Arrive(warmUp);
                _lines.Add($"{run}: worst arrival frame {costs.Arrival.Max():F1} ms of thread CPU without pauses; worst idle frame {costs.Idle.DefaultIfEmpty().Max():F1} ms; " +
                           $"methods compiled by the main thread over the arrival and reload {_compiledInArrival}");
                if (run == "first")
                    (first, firstCompiled) = (costs, _compiledInArrival);
            }

            foreach (var line in _lines)
                output.WriteLine(line);
            File.WriteAllLines(Path.Combine(Fixtures.Generated, $"plan-arrival-{(warmUp ? "warm-up" : "no-warm-up")}.txt"), _lines);
            if (!warmUp)
                return;
            Assert.True(first.Arrival.Max() <= Frame, $"worst first arrival frame {first.Arrival.Max():F1} ms of thread CPU without collector pauses, at most {Frame}");
            Assert.True(first.Idle.Skip(10).Max() <= IdleFrame, $"worst idle frame after the first ten {first.Idle.Skip(10).Max():F1} ms, at most {IdleFrame}");
            Assert.True(firstCompiled <= Compiled, $"the main thread compiled {firstCompiled} methods over the first arrival and reload, at most {Compiled}");
        }
        finally
        {
            GCSettings.LatencyMode = latency;
        }
    }

    private (List<double> Idle, List<double> Arrival) Arrive(bool warmUp)
    {
        using var bench = RealBaseBench.Load(RealBaseBench.BigBase);
        var held = bench.Built().Take(PlanSize).ToList();
        var heldIds = held.Select(s => s.Ref.Id).ToHashSet();
        var world = new FakeWorld(bench.Catalogue);
        foreach (var s in new FakeScan(bench.Save.World).Structures().Where(s => !heldIds.Contains(s.Ref.Id)))
            world.Load(s);
        var idle = new List<double>();
        var arrival = new List<double>();
        bench.Start(Option<SaveKey>.None, world, warmUp);
        Open(bench, warmUp);
        idle.AddRange(Idle(bench, warmUp));
        var drafts = ValueList.From(held.Select(s => RealBaseBench.Draft(new Placement(s.Ref.Piece, s.Ref.At, s.Turn))).ToList());
        Assert.IsType<Outcome.Applied>(bench.Submit(new Command.CreatePlan(RealBaseBench.Plan("Held back")) { Ghosts = drafts }));
        arrival.AddRange(Frames("arrival", bench, 400));
        var key = bench.GameSave();
        bench.Start(Option.Some(key), world, warmUp);
        Open(bench, warmUp);
        arrival.AddRange(Frames("reload", bench, 400));
        return (idle, arrival);
    }

    private void Open(RealBaseBench bench, bool warmUp)
    {
        var opened = Renderer.Open(bench.Lattice, new RenderRig().Ports);
        Assert.True(opened.IsOk, "Render opens over the bench's world");
        _renderer = opened.TryGet(out var renderer) ? renderer : null;
        if (warmUp)
        {
            bench.Lattice.Publish();
            RenderWarmUp.Schedule(bench.Lattice);
        }
    }

    // Idle frames until Lattice's warm-up has compiled both sets (the sets are done once per process, so a later world has none to run).
    private IEnumerable<double> Idle(RealBaseBench bench, bool warmUp)
    {
        var costs = new List<double>();
        if (!warmUp)
            return Frames("start", bench, 30);
        var frames = 0;
        var wall = Stopwatch.StartNew();
        while (!(RealBaseBench.Warmed.IsDone("core") && RealBaseBench.Warmed.IsDone("api") && RealBaseBench.Warmed.IsDone("render")) && wall.Elapsed < TimeSpan.FromSeconds(300))
        {
            costs.AddRange(Frames("warm-up", bench, 1, frames++));
            System.Threading.Thread.Sleep(1);
        }

        _lines.Add($"warm-up finished after {frames} idle frames (core {RealBaseBench.Warmed.IsDone("core")}, render {RealBaseBench.Warmed.IsDone("render")})");
        Assert.True(RealBaseBench.Warmed.IsDone("core") && RealBaseBench.Warmed.IsDone("api") && RealBaseBench.Warmed.IsDone("render"), $"the warm-up never finished: work {bench.Lattice.Scheduler.WorkThisFrame.TotalMilliseconds:F2} ms, idle {bench.Lattice.Scheduler.Idle}, validation busy {bench.Lattice.ValidationModule?.Busy}, saving pending {bench.Saving.Pending}, answers {bench.AnswersReady()}; log {string.Join(" | ", bench.Log.Where(l => l.Contains("fail")).Take(3))}; jobs {Jobs(bench)}");
        costs.AddRange(Frames("settled", bench, 30));
        return costs;
    }

    private static string Jobs(RealBaseBench bench)
    {
        var drawing = typeof(FrameScheduler).GetField("_drawing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bench.Lattice.Scheduler)!;
        var jobs = (System.Collections.IEnumerable)drawing.GetType().GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(drawing)!;
        var texts = new List<string>();
        foreach (SlicedJob job in jobs)
            texts.Add($"{job.Work} {job.State} last {job.LastOutcome}");
        return string.Join("; ", texts);
    }

    private IEnumerable<double> Frames(string window, RealBaseBench bench, int count, int first = 0)
    {
        var scheduler = bench.Lattice.Scheduler;
        var steps = new List<(string Work, double Ms)>();
        scheduler.StepObserver = (work, time) => steps.Add((work.GetType().Name, time.TotalMilliseconds));
        var kinds = new[] { WorkKind.Checking, WorkKind.Sending, WorkKind.Saving, WorkKind.Drawing };
        var costs = new List<double>(count);
        for (var f = first; f < first + count; f++)
        {
            var collector = Collector.Now();
            var jit = JitInfo.GetCompilationTime(true);
            var methods = JitInfo.GetCompiledMethodCount(true);
            var cycles = RealBaseBench.Cpu.Now();
            steps.Clear();
            bench.Lattice.Frame();
            var submitted = Stopwatch.GetTimestamp();
            _renderer?.Frame(f);
            var submit = (Stopwatch.GetTimestamp() - submitted) * 1000.0 / Stopwatch.Frequency;
            var cpu = RealBaseBench.Cpu.Between(cycles, RealBaseBench.Cpu.Now()).TotalMilliseconds;
            var gc = Collector.Now() - collector;
            var compiled = JitInfo.GetCompiledMethodCount(true) - methods;
            if (window is "arrival" or "reload")
                _compiledInArrival += compiled;
            costs.Add(cpu - gc.Pause.TotalMilliseconds);
            if (cpu < 3 && window != "warm-up")
                continue;
            if (window == "warm-up" && f % 25 != 0 && cpu < 3)
                continue;
            _lines.Add($"{window} frame {f}: cpu {cpu:F1} ms, collector pause {gc.Pause.TotalMilliseconds:F1} ms, " +
                       $"compiling {(JitInfo.GetCompilationTime(true) - jit).TotalMilliseconds:F1} ms in {compiled} methods; " +
                       $"spent {string.Join(" ", kinds.Select(k => $"{k} {scheduler.Spent(k).TotalMilliseconds:F1}"))}; " +
                       $"longest steps {string.Join(", ", steps.OrderByDescending(s => s.Ms).Take(3).Select(s => $"{s.Work} {s.Ms:F1}"))} ms; " +
                       $"render submit {submit:F1} ms; allocated {gc.Allocated / 1048576.0:F1} MB; saving pending {bench.Saving.Pending}");
        }

        return costs;
    }
}
