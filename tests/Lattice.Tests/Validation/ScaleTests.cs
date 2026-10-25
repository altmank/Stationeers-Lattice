using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Lattice.Api;
using Lattice.Core.Slices;
using Xunit.Abstractions;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>
/// Very large plans: validation's main-thread work stays in the frame budget every frame.
/// One unmeasured run first (JIT tiering and heap growth are one-off per process); then three measured runs, every
/// frame of each counted. A frame is judged on Lattice's own cost: the main thread's CPU time in it plus the collector
/// pauses in it (the main-thread steps allocate a small record per answered ask, <see cref="AskWorkTests"/>, and the
/// worker far more, so a pause is Lattice's own). Time the thread spends stalled, neither running nor paused for a
/// collection (descheduled on a shared machine, the runtime suspending it), is left out of the judged frame and
/// named in the output with the wall-clock frame.
/// </summary>
[Collection(nameof(Timing))]
[Trait(Perf.Trait, Perf.Value)]
public sealed class ScaleTests(ITestOutputHelper output)
{
    /// <summary>
    /// What a frame may spend on validation's main-thread work: the checking budget, plus the one step a slice always
    /// runs and the constant intake and drain around the slices.
    /// </summary>
    private static readonly TimeSpan FrameAllowance = FrameBudgets.Default.Checking.Length + TimeSpan.FromMilliseconds(2);

    [Fact]
    public void A_10000_ghost_plan_checks_within_the_frame_budget_on_every_frame()
    {
        var cpu = ThreadCpu.Calibrate();
        output.WriteLine(cpu.ToString());
        CheckOnce(100, cpu);
        var worst = TimeSpan.Zero;
        for (var run = 0; run < 3; run++)
        {
            var result = CheckOnce(100, cpu);
            output.WriteLine($"run {run}: {result}");
            if (result.Worst > worst)
                worst = result.Worst;
        }

        Assert.True(worst <= FrameAllowance, $"worst frame {worst.TotalMilliseconds:F3} ms of thread CPU and collector pauses");
    }

    // side * side frames, two large cells apart, so every neighbourhood is real and the game is asked for each one.
    private static RunTimes CheckOnce(int side, ThreadCpu cpu)
    {
        var bench = new ValidationBench(clock: new StopwatchClock()).Start();
        var pieces = new List<(string, Cell)>(side * side);
        for (var i = 0; i < side; i++)
            for (var j = 0; j < side; j++)
                pieces.Add(("StructureFrameIron", Large(2 * i, 0, 2 * j)));
        bench.Ghosts(pieces.ToArray());

        // The fake check records every call; fill its record past the asks so its own growth is not timed.
        for (var i = 0; i < 2 * side * side; i++)
            bench.Placement.Check(new Placement(Pieces.Named("StructureFrameIron").Key, Large(0, 0, 0), default));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var times = new RunTimes(cpu);
        for (var frame = 1; frame <= 10_000; frame++)
        {
            times.Main(() =>
            {
                bench.Events.Flush();
                bench.Service.Frame();
            });
            times.Worker(bench.Worker.RunAll);
            times.Main(() =>
            {
                bench.Service.Frame();
                bench.Scheduler.RunFrame();
            });
            times.Frame();
            if (bench.Worker.Idle && !bench.Service.Busy && bench.Service.Check(bench.All()).TryGet(out var answer) && answer is Answer<ValidationReport>.Ready)
            {
                Assert.Equal(side * side, bench.Report().Findings.Count);
                return times;
            }
        }

        throw new InvalidOperationException("Validation did not settle.");
    }

    /// <summary>
    /// One run's frames: the worst judged frame (thread CPU and collector pauses), the worst wall-clock frame, the
    /// stalls left out, and the collector pauses and bytes the main thread and the worker took.
    /// </summary>
    private sealed class RunTimes(ThreadCpu cpu)
    {
        private static readonly TimeSpan Named = TimeSpan.FromMilliseconds(0.5);
        private readonly Stopwatch _watch = new();
        private TimeSpan _frameWall;
        private TimeSpan _frameCpu;
        private TimeSpan _framePause;
        private long _frameBytes;

        public int Frames { get; private set; }

        public TimeSpan Worst { get; private set; }

        public TimeSpan WorstWall { get; private set; }

        public int Stalls { get; private set; }

        public TimeSpan LongestStall { get; private set; }

        public TimeSpan WorstPause { get; private set; }

        public TimeSpan Paused { get; private set; }

        public long MostFrameBytes { get; private set; }

        public long MainBytes { get; private set; }

        public long WorkerBytes { get; private set; }

        public TimeSpan WorkerTime { get; private set; }

        public void Main(Action work)
        {
            var pause = GC.GetTotalPauseDuration();
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var cycles = cpu.Now();
            _watch.Restart();
            work();
            _watch.Stop();
            _frameCpu += cpu.Between(cycles, cpu.Now());
            _framePause += GC.GetTotalPauseDuration() - pause;
            _frameBytes += GC.GetAllocatedBytesForCurrentThread() - bytes;
            _frameWall += _watch.Elapsed;
        }

        public void Worker(Action work)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            work();
            WorkerTime += watch.Elapsed;
            WorkerBytes += GC.GetAllocatedBytesForCurrentThread() - bytes;
        }

        public void Frame()
        {
            Frames++;
            var judged = cpu.Measures && _frameCpu + _framePause < _frameWall ? _frameCpu + _framePause : _frameWall;
            var stall = _frameWall - judged;
            Worst = Max(Worst, judged);
            WorstWall = Max(WorstWall, _frameWall);
            if (stall > Named)
                Stalls++;
            LongestStall = Max(LongestStall, stall);
            WorstPause = Max(WorstPause, _framePause);
            Paused += _framePause;
            MainBytes += _frameBytes;
            MostFrameBytes = Math.Max(MostFrameBytes, _frameBytes);
            (_frameWall, _frameCpu, _framePause, _frameBytes) = (TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0);
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        public override string ToString() =>
            $"{Frames} frames, worst frame {Worst.TotalMilliseconds:F3} ms of thread CPU and collector pauses; " +
            $"wall clock: worst frame {WorstWall.TotalMilliseconds:F3} ms, {Stalls} frames stalled over {Named.TotalMilliseconds} ms (longest stall {LongestStall.TotalMilliseconds:F1} ms); " +
            $"pauses in frames {Paused.TotalMilliseconds:F1} ms (worst {WorstPause.TotalMilliseconds:F1} ms); " +
            $"main thread allocated {MainBytes / 1024} KB (most in a frame {MostFrameBytes / 1024} KB), worker {WorkerBytes / 1024} KB in {WorkerTime.TotalMilliseconds:F0} ms";
    }
}
