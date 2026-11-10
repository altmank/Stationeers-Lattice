using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Lattice.Api;
using Lattice.Core.EntryPoint;
using Lattice.Core.Executors;
using Lattice.Core.Grid;
using Lattice.Core.Modules;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.PlacementCheck;
using Lattice.TestKit.Saves;
using Lattice.TestKit.World;
using Lattice.Tests.Board;
using Lattice.Tests.EntryPoint;
using Lattice.Tests.Executors;
using Lattice.Tests.Validation;

namespace Lattice.Tests.Validation.RealBase;

/// <summary>
/// A whole world's Lattice over a real save read offline: the game's own catalogue fixture, the save's structures in a
/// fake world, saving in a temp folder, and the game's runtime (a stopwatch frame clock, Lattice's named worker and
/// rule threads). Every step is timed in wall time on .NET 8; the frame clock also notes the main thread's CPU at each
/// read (<see cref="CpuNotingClock"/>), so a frame's checking is given with its own thread CPU. The lines go to
/// <see cref="Lines"/>.
/// </summary>
internal sealed class RealBaseBench : IDisposable
{
    /// <summary>The save file of the library world every offline real-base suite reads.</summary>
    public const string BigBase = "lattice-gen-big.save";

    /// <summary>The warm-up sets compiled so far in this test process.</summary>
    public static WarmedSets Warmed { get; } = new();

    private static readonly Lazy<ThreadCpu> CpuClock = new(ThreadCpu.Calibrate);
    private readonly CpuNotingClock _clock = new(Cpu);
    private readonly FakePlacementCheck _check = new();
    private readonly List<string> _log = new();
    private PlanSaving? _saving;
    private int _saves;

    private RealBaseBench(FakeCatalogue catalogue, LoadedSave save, TimeSpan loadTime)
    {
        Catalogue = catalogue;
        Save = save;
        Root = Path.Combine(Path.GetTempPath(), "lattice-realsave-" + Guid.NewGuid().ToString("N"));
        Lines.Add($"save loaded: {save.Structures} structures, {save.UnknownPieces.Count} piece kinds the catalogue lacks, in {loadTime.TotalMilliseconds:F0} ms");
    }

    public FakeCatalogue Catalogue { get; }

    /// <summary>The main thread's CPU clock, calibrated once per test process.</summary>
    public static ThreadCpu Cpu => CpuClock.Value;

    public LoadedSave Save { get; }

    /// <summary>The world Lattice runs over: the save's, unless <see cref="Start"/> was given another.</summary>
    public FakeWorld World { get; private set; } = null!;

    public string Root { get; }

    public LatticeWorld Lattice { get; private set; } = null!;

    public List<string> Lines { get; } = new();

    /// <summary>The plan files the world's Lattice saves to.</summary>
    public PlanSaving Saving => _saving!;

    /// <summary>The placement check the game ports answer with; tests may add rules (or a cost) before starting.</summary>
    public FakePlacementCheck Check => _check;

    public static RealBaseBench Load(string saveName)
    {
        var catalogue = Fixtures.Catalogue();
        var watch = Stopwatch.StartNew();
        var save = TestKitIds.MustRead(SaveLoader.Load(Fixtures.Save(saveName).Reduce(""), catalogue));
        watch.Stop();
        return new RealBaseBench(catalogue, save, watch.Elapsed);
    }

    /// <summary>
    /// A suite's plan measures what it says only if it holds every family a base is made of: frames, walls, pipes, cables
    /// and devices, and with <paramref name="ventAndControl"/> a vent and a control piece. Fails when the generator's
    /// build order no longer puts them in the plan's window.
    /// </summary>
    public static void RequirePlanHolds(FakeCatalogue catalogue, IEnumerable<StructureInfo> plan, bool ventAndControl = false)
    {
        var facts = plan.Select(s => TestKitIds.Must(catalogue.Piece(s.Ref.Piece))).ToList();
        var missing = new[] { PieceFamilies.Frame, PieceFamilies.Wall, PieceFamilies.Pipe, PieceFamilies.Cable, PieceFamilies.Device }
            .Where(family => !facts.Any(f => f.Family == family))
            .Select(family => family.Value)
            .Concat(ventAndControl && !facts.Any(f => f.Vents) ? ["vent"] : [])
            .Concat(ventAndControl && !facts.Any(f => f.ControlFace.IsSome) ? ["control"] : [])
            .ToList();
        Assert.True(missing.Count == 0, $"the plan of {facts.Count} pieces holds no {string.Join(", ", missing)}");
    }

    /// <summary>Starts the world's Lattice as the host, opening saving at <paramref name="save"/>, compiling its methods ahead in idle frames when <paramref name="warmUp"/>; returns the start time.</summary>
    public TimeSpan Start(Option<SaveKey> save, FakeWorld? world = null, bool warmUp = false)
    {
        World = world ?? Save.World;
        Lattice?.End();
        var watch = Stopwatch.StartNew();
        var modules = ModuleSwitches.Decide(LatticeModules.All, new FakeMembers(), WorldSeat.Host);
        _saving = new PlanSaving(new WorldFolder(Root));
        var opened = _saving.Open(save);
        var ports = new WorldPorts(Catalogue, new CubeGrid(), modules, WorldSeat.Host, TestBoard.Alice, new ManualGameClock())
        {
            World = Option.Some(new GamePorts(World, new FakeScan(World), World, _check)),
            Saving = Option.Some(new OpenedSaving(_saving, opened)),
        };
        Lattice = TestKitIds.Must(LatticeWorld.Start(ports, WorldRuntime.Threads(_log.Add) with { Clock = _clock, WarmUp = warmUp ? Warmed : null }));
        watch.Stop();
        return watch.Elapsed;
    }

    /// <summary>Starts noting the frame clock's reads for the frame about to run.</summary>
    public void ClockStart() => _clock.StartFrame();

    /// <summary>Main-thread CPU in milliseconds between consecutive frame-clock reads of the last frame.</summary>
    public List<double> ClockGaps() => _clock.Gaps();

    /// <summary>The game saves: Lattice's records are written; returns the save's key.</summary>
    public SaveKey GameSave()
    {
        var key = new SaveKey(Path.Combine(Root, "game", "world.save"), $"save-{++_saves}");
        _saving!.OnGameSave(key, DateTimeOffset.UnixEpoch.AddMinutes(_saves));
        return key;
    }

    public Outcome Submit(Command command) =>
        Lattice.Commands.Submit(new CommandId(Guid.NewGuid()), command, TestBoard.Alice) switch
        {
            Submission.Answered answered => answered.Outcome,
            var sent => throw new InvalidOperationException($"not answered at once: {sent}"),
        };

    /// <summary>
    /// Frames until validation has nothing left and every plan's facts are ready, refused past <paramref name="limit"/>.
    /// Each frame is timed whole, with the checking work the scheduler ran in it (its wall time and its own thread CPU)
    /// and the GC pause it took.
    /// </summary>
    public BaseFrameTimes Settle(TimeSpan limit)
    {
        // What the setup left on the heap (the submitted drafts, the board work, the previous world) is collected before the first frame,
        // so the window holds the pauses Lattice's own checking causes and not the bench's.
        Perf.Collect();
        var frames = new List<BaseFrame>(1024);
        var all = Stopwatch.StartNew();
        var idleFrames = 0;
        var ready = TimeSpan.Zero;
        while (all.Elapsed < limit)
        {
            var before = Collector.Now();
            var cycles = Cpu.Now();
            var frame = Stopwatch.StartNew();
            _clock.StartFrame();
            Lattice.Frame();
            frame.Stop();
            var cpu = Cpu.Measures ? Cpu.Between(cycles, Cpu.Now()) : frame.Elapsed;
            var gc = Collector.Now() - before;
            var checking = Lattice.Scheduler.Spent(WorkKind.Checking);
            frames.Add(new BaseFrame(
                frame.Elapsed.TotalMilliseconds,
                checking.TotalMilliseconds,
                gc.Pause.TotalMilliseconds,
                cpu.TotalMilliseconds,
                _clock.CpuOver(checking).Reduce(cpu).TotalMilliseconds,
                gc));
            idleFrames = Lattice.ValidationModule is { Busy: true } || !AnswersReady() ? 0 : idleFrames + 1;
            if (idleFrames == 1)
                ready = all.Elapsed;
            if (idleFrames >= 3)
                return new BaseFrameTimes(frames, ready, true);
            Thread.Sleep(1);
        }

        return new BaseFrameTimes(frames, all.Elapsed, false);
    }

    /// <summary>Whether every plan on the board has its facts worked out.</summary>
    public bool AnswersReady() =>
        Lattice.Ghosts.Index.Plans.Keys.All(plan => TestKitIds.Must(Lattice.Plans.Facts(plan)) is Answer<PlanFacts>.Ready);

    /// <summary>Ghosts and plans on the board, and what keeps validation busy (its private state, for the report).</summary>
    public string Diagnose()
    {
        var index = Lattice.Ghosts.Index;
        var text = $"board: {index.Ghosts.Count} ghosts, {index.Plans.Count} plans";
        if (Lattice.ValidationModule is { } validation)
        {
            var type = validation.GetType();
            object? Field(string name) => type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(validation);
            text += $"; validation busy {validation.Busy} (in flight {Field("_inFlight")}, asking {Field("_asking")}, sweeping {Field("_sweeping")}, " +
                    $"pending {(Field("_pending") as System.Collections.ICollection)?.Count}, asks {(Field("_asks") as System.Collections.ICollection)?.Count}, trials {(Field("_trials") as System.Collections.ICollection)?.Count})";
        }

        return text;
    }

    /// <summary>What Lattice logged.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>The structures that stand built, the save's own order.</summary>
    public List<StructureInfo> Built() =>
        new FakeScan(Save.World).Structures().Where(s => s.State is BuildState.Built).ToList();

    public static GhostDraft Draft(Placement placement) => TestBoard.Draft(new Intent.Build(placement, Option<Finish>.None));

    public static PlanDraft Plan(string name) =>
        new(TestKitIds.Must(PlanName.Create(name)), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

    public void Dispose()
    {
        Lattice?.End();
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// One frame's cost in milliseconds: whole, the checking work in it, the GC pause in it, the main thread's CPU time in it
/// (whole when the thread's CPU is not measured), the main thread's CPU time over the checking work alone (the frame's
/// when it is not measured), and the collector's counts over it.
/// </summary>
internal readonly record struct BaseFrame(double Whole, double Checking, double Gc, double Cpu, double CheckingCpu, Collector Collector)
{
    /// <summary>
    /// Checking on Lattice's own cost: the scheduler's wall time for it, but no more than the checking work's own thread
    /// CPU plus the frame's collector pauses. A pause is the cost of Lattice's garbage and always counts; time the thread
    /// sat descheduled on a shared machine is the machine's and does not. Checking's own CPU and pauses fit in both, so
    /// a frame never judges below them, and the frame's other work (sending, saving, drawing, events) is not taken for
    /// checking's.
    /// </summary>
    public double JudgedChecking => Math.Min(Checking, CheckingCpu + Gc);

    /// <summary>Wall time the frame's thread spent neither running nor in a collector pause.</summary>
    public double Stall => Math.Max(0, Whole - Cpu - Gc);
}

/// <summary>
/// The collector's process-wide counters: collections by generation, pause time, and bytes allocated on every thread
/// (Lattice's worker and rule threads too). A difference of two readings is what happened between them.
/// </summary>
internal readonly record struct Collector(int Gen0, int Gen1, int Gen2, TimeSpan Pause, long Allocated)
{
    public static Collector Now() =>
        new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration(), GC.GetTotalAllocatedBytes());

    public static Collector operator -(Collector after, Collector before) =>
        new(after.Gen0 - before.Gen0, after.Gen1 - before.Gen1, after.Gen2 - before.Gen2, after.Pause - before.Pause, after.Allocated - before.Allocated);

    public static Collector operator +(Collector a, Collector b) =>
        new(a.Gen0 + b.Gen0, a.Gen1 + b.Gen1, a.Gen2 + b.Gen2, a.Pause + b.Pause, a.Allocated + b.Allocated);

    public override string ToString() =>
        $"gen0/1/2 {Gen0}/{Gen1}/{Gen2}, pause {Pause.TotalMilliseconds:F1} ms, allocated {Allocated / 1048576.0:F1} MB";
}

/// <summary>
/// Frame costs over a run, the time until answers were ready, and whether the run settled. Every frame counts,
/// collector pauses included; the worst frames are given with the pause in them.
/// </summary>
internal sealed record BaseFrameTimes(List<BaseFrame> Frames, TimeSpan Ready, bool Settled)
{
    public double P95Checking => Percentile(Frames.Select(frame => frame.Checking), 0.95);

    public double P99Checking => Percentile(Frames.Select(frame => frame.Checking), 0.99);

    public double P95JudgedChecking => Percentile(Frames.Select(frame => frame.JudgedChecking), 0.95);

    public BaseFrame WorstChecking => Frames.OrderByDescending(frame => frame.Checking).FirstOrDefault();

    public BaseFrame WorstWhole => Frames.OrderByDescending(frame => frame.Whole).FirstOrDefault();

    public override string ToString() =>
        $"answers ready in {Ready.TotalMilliseconds:F0} ms over {Frames.Count} frames (settled {Settled}); " +
        $"checking per frame p95 {P95Checking:F2} ms, p99 {P99Checking:F2} ms, worst {WorstChecking.Checking:F2} ms (collector pause in it {WorstChecking.Gc:F1} ms); " +
        $"whole frame median {Percentile(Frames.Select(f => f.Whole), 0.5):F2} ms, worst {WorstWhole.Whole:F2} ms (collector pause in it {WorstWhole.Gc:F1} ms); " +
        $"{Frames.Count(f => f.Gc > 0)} frames with a collector pause, longest {Frames.Select(f => f.Gc).DefaultIfEmpty().Max():F1} ms; " +
        $"worst checking frame {WorstChecking.Collector}; over the run {Frames.Aggregate(default(Collector), (sum, f) => sum + f.Collector)}; " +
        $"judged checking (its own thread CPU and pauses) p95 {P95JudgedChecking:F2} ms; " +
        $"slowest checking frames (checking, judged, checking CPU, thread CPU, pause, stall ms): {string.Join("; ", Frames.OrderByDescending(f => f.Checking).Take(3).Select(f => $"{f.Checking:F2} {f.JudgedChecking:F2} {f.CheckingCpu:F2} {f.Cpu:F2} {f.Gc:F1} {f.Stall:F2}"))}; " +
        $"checking p95 in frames without a collection {Percentile(Frames.Where(f => f.Gc == 0).Select(f => f.Checking), 0.95):F2} ms; " +
        $"frames with a collection (checking ms, collector): {string.Join("; ", Frames.Where(f => f.Gc > 0).Select(f => $"{f.Checking:F2} ms, {f.Collector}"))}";

    private static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(m => m).ToList();
        return sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
    }
}
