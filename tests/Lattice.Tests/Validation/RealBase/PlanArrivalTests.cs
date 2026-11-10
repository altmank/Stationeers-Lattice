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
/// the first). The run is made three times in one process: the first is cold (first calls compile), the later ones are
/// warm. The lines go to <c>tests/fixtures/generated/plan-arrival.txt</c>.
/// <para>
/// A warm arrival is judged: no frame's thread CPU, less the collector pause in it, passes <see cref="WarmFrame"/>.
/// The cold run is reported, not judged: its over-budget steps are first calls compiling.
/// </para>
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class PlanArrivalTests(ITestOutputHelper output)
{
    private const int PlanSize = 5000;
    private const double WarmFrame = 10.0;
    private readonly List<string> _lines = new();
    private Renderer? _renderer;

    [RealBaseFact(RealBaseBench.BigBase)]
    public void A_big_plan_arriving_costs_no_warm_frame_past_the_budgets()
    {
        var latency = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.Batch;
        try
        {
            var worst = new List<double>();
            foreach (var run in new[] { "cold", "warm", "warm 2" })
            {
                _lines.Add($"=== {run} ===");
                var frames = Arrive();
                if (run != "cold")
                    worst.Add(frames.Max());
            }

            foreach (var line in _lines)
                output.WriteLine(line);
            File.WriteAllLines(Path.Combine(Fixtures.Generated, "plan-arrival.txt"), _lines);
            Assert.True(worst.Max() <= WarmFrame, $"worst warm frame {worst.Max():F1} ms of thread CPU without collector pauses, at most {WarmFrame}");
        }
        finally
        {
            GCSettings.LatencyMode = latency;
        }
    }

    private List<double> Arrive()
    {
        using var bench = RealBaseBench.Load(RealBaseBench.BigBase);
        var held = bench.Built().Take(PlanSize).ToList();
        var heldIds = held.Select(s => s.Ref.Id).ToHashSet();
        var world = new FakeWorld(bench.Catalogue);
        foreach (var s in new FakeScan(bench.Save.World).Structures().Where(s => !heldIds.Contains(s.Ref.Id)))
            world.Load(s);
        bench.Start(Option<SaveKey>.None, world);
        Open(bench);
        var costs = new List<double>();
        costs.AddRange(Frames("start", bench, 30));
        var drafts = ValueList.From(held.Select(s => RealBaseBench.Draft(new Placement(s.Ref.Piece, s.Ref.At, s.Turn))).ToList());
        Assert.IsType<Outcome.Applied>(bench.Submit(new Command.CreatePlan(RealBaseBench.Plan("Held back")) { Ghosts = drafts }));
        costs.AddRange(Frames("arrival", bench, 400));
        var key = bench.GameSave();
        bench.Start(Option.Some(key), world);
        Open(bench);
        costs.AddRange(Frames("reload", bench, 400));
        return costs;
    }

    private void Open(RealBaseBench bench)
    {
        var opened = Renderer.Open(bench.Lattice, new RenderRig().Ports);
        Assert.True(opened.IsOk, "Render opens over the bench's world");
        _renderer = opened.TryGet(out var renderer) ? renderer : null;
    }

    private IEnumerable<double> Frames(string window, RealBaseBench bench, int count)
    {
        var scheduler = bench.Lattice.Scheduler;
        var kinds = new[] { WorkKind.Checking, WorkKind.Sending, WorkKind.Saving, WorkKind.Drawing };
        for (var f = 0; f < count; f++)
        {
            var collector = Collector.Now();
            var jit = JitInfo.GetCompilationTime(true);
            var methods = JitInfo.GetCompiledMethodCount(true);
            var cycles = RealBaseBench.Cpu.Now();
            bench.ClockStart();
            bench.Lattice.Frame();
            var submitted = Stopwatch.GetTimestamp();
            _renderer?.Frame(f);
            var submit = (Stopwatch.GetTimestamp() - submitted) * 1000.0 / Stopwatch.Frequency;
            var cpu = RealBaseBench.Cpu.Between(cycles, RealBaseBench.Cpu.Now()).TotalMilliseconds;
            var gc = Collector.Now() - collector;
            var gaps = bench.ClockGaps();
            var judged = cpu - gc.Pause.TotalMilliseconds;
            yield return judged;
            if (cpu < 3)
                continue;
            _lines.Add($"{window} frame {f}: cpu {cpu:F1} ms, collector pause {gc.Pause.TotalMilliseconds:F1} ms, " +
                       $"compiling {(JitInfo.GetCompilationTime(true) - jit).TotalMilliseconds:F1} ms in {JitInfo.GetCompiledMethodCount(true) - methods} methods; " +
                       $"spent {string.Join(" ", kinds.Select(k => $"{k} {scheduler.Spent(k).TotalMilliseconds:F1}"))}; " +
                       $"longest clock gaps {string.Join(", ", gaps.OrderByDescending(g => g).Take(3).Select(g => g.ToString("F1")))} ms; " +
                       $"render submit {submit:F1} ms; allocated {gc.Allocated / 1048576.0:F1} MB; saving pending {bench.Saving.Pending}");
        }
    }
}
