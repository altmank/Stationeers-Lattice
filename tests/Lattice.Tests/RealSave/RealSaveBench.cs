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

namespace Lattice.Tests.RealSave;

/// <summary>
/// A whole world's Lattice over a real save read offline: the game's own catalogue fixture, the save's structures in a
/// fake world, saving in a temp folder, and the game's runtime (a stopwatch frame clock, Lattice's named worker and
/// rule threads). Every step is timed in wall time on .NET 8; the lines go to <see cref="Lines"/>.
/// </summary>
internal sealed class RealSaveBench : IDisposable
{
    private readonly FakePlacementCheck _check = new();
    private readonly List<string> _log = new();
    private PlanSaving? _saving;
    private int _saves;

    private RealSaveBench(FakeCatalogue catalogue, LoadedSave save, TimeSpan loadTime)
    {
        Catalogue = catalogue;
        Save = save;
        Root = Path.Combine(Path.GetTempPath(), "lattice-realsave-" + Guid.NewGuid().ToString("N"));
        Lines.Add($"save loaded: {save.Structures} structures, {save.UnknownPieces.Count} piece kinds the catalogue lacks, in {loadTime.TotalMilliseconds:F0} ms");
    }

    public FakeCatalogue Catalogue { get; }

    public LoadedSave Save { get; }

    /// <summary>The world Lattice runs over: the save's, unless <see cref="Start"/> was given another.</summary>
    public FakeWorld World { get; private set; } = null!;

    public string Root { get; }

    public LatticeWorld Lattice { get; private set; } = null!;

    public List<string> Lines { get; } = new();

    public static RealSaveBench Load(string saveName)
    {
        var catalogue = Fixtures.Catalogue();
        var watch = Stopwatch.StartNew();
        var save = TestKitIds.MustRead(SaveLoader.Load(Fixtures.Save(saveName).Reduce(""), catalogue));
        watch.Stop();
        return new RealSaveBench(catalogue, save, watch.Elapsed);
    }

    /// <summary>Starts the world's Lattice as the host, opening saving at <paramref name="save"/>; returns the start time.</summary>
    public TimeSpan Start(Option<SaveKey> save, FakeWorld? world = null)
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
        Lattice = TestKitIds.Must(LatticeWorld.Start(ports, WorldRuntime.Threads(_log.Add)));
        watch.Stop();
        return watch.Elapsed;
    }

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

    /// <summary>Frames until validation has nothing left, each frame timed; refused past <paramref name="limit"/>.</summary>
    public FrameTimes Settle(TimeSpan limit)
    {
        var times = new List<double>(1024);
        var all = Stopwatch.StartNew();
        var idleFrames = 0;
        while (all.Elapsed < limit)
        {
            var frame = Stopwatch.StartNew();
            Lattice.Frame();
            frame.Stop();
            times.Add(frame.Elapsed.TotalMilliseconds);
            idleFrames = Lattice.ValidationModule is { Busy: true } ? 0 : idleFrames + 1;
            if (idleFrames >= 3)
                return new FrameTimes(times, all.Elapsed, true);
            Thread.Sleep(1);
        }

        return new FrameTimes(times, all.Elapsed, false);
    }

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

/// <summary>Frame costs over a run, in milliseconds, and whether the run settled.</summary>
internal sealed record FrameTimes(List<double> Milliseconds, TimeSpan Wall, bool Settled)
{
    public double Max => Milliseconds.Count == 0 ? 0 : Milliseconds.Max();

    public double P99 => Percentile(0.99);

    public double Median => Percentile(0.5);

    public override string ToString() =>
        $"{Milliseconds.Count} frames in {Wall.TotalMilliseconds:F0} ms, settled {Settled}; frame cost median {Median:F2} ms, p99 {P99:F2} ms, max {Max:F2} ms";

    private double Percentile(double p)
    {
        if (Milliseconds.Count == 0)
            return 0;
        var sorted = Milliseconds.OrderBy(m => m).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
    }
}
