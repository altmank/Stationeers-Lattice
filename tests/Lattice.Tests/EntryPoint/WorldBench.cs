using System;
using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.EntryPoint;
using Lattice.Core.Executors;
using Lattice.Core.Modules;
using Lattice.Core.Ports;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.Core.Styles;
using Lattice.Core.Sync;
using Lattice.Core.Validation;
using Lattice.Core.World;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Executors;
using Lattice.Tests.Runs;
using Lattice.Tests.Slices;
using Lattice.Tests.Validation;

namespace Lattice.Tests.EntryPoint;

/// <summary>Tests that publish a world share the one static <see cref="LatticeSystem.Current"/>, so they never run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LatticeSystemCollection
{
    public const string Name = "LatticeSystem";
}

/// <summary>What a sync link sent.</summary>
internal sealed class RecordingLink : ISyncLink
{
    public List<(SyncKind Kind, string Text)> ToHostSent { get; } = new();

    public List<(ConnectionId To, SyncKind Kind, string Text)> ToClientSent { get; } = new();

    public void ToHost(SyncKind kind, string text) => ToHostSent.Add((kind, text));

    public void ToClient(ConnectionId to, SyncKind kind, string text) => ToClientSent.Add((to, kind, text));
}

/// <summary>Rule calls inline on a fake clock that say when they were disposed, as rule threads are at world end.</summary>
internal sealed class DisposableRuleCalls(IClock clock) : IRuleCalls, IDisposable
{
    private readonly InlineRuleCalls _calls = new(clock);

    public bool Disposed { get; private set; }

    public CallResult<T> Call<T>(RuleId rule, TimeSpan limit, Func<T> call) => _calls.Call(rule, limit, call);

    public void Dispose() => Disposed = true;
}

/// <summary>A world scan that fails: starting the mirror throws, as a Lattice bug would.</summary>
internal sealed class ThrowingScan : IWorldScan
{
    public ValueList<StructureInfo> Structures() => throw new InvalidOperationException("scan failed");
}

/// <summary>
/// A whole world's Lattice over fakes: the run bench's catalogue (hand pieces and a modded conduit kit) and fake world,
/// validation on a worker the test runs by hand, inline rule calls, a fake frame clock, saving in a temp folder and a
/// recording sync link. <see cref="Start"/> builds it with the modules the seat and the missing members leave on.
/// </summary>
internal sealed class WorldBench : IDisposable
{
    /// <param name="extraText">More pieces for the run bench's catalogue, in the catalogue file format (with its header).</param>
    public WorldBench(string? extraText = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "lattice-entry-" + Guid.NewGuid().ToString("N"));
        Runs = new RunBench(extraText);
    }

    public RunBench Runs { get; }

    public FakeClock Clock { get; } = new();

    public ManualWorker Worker { get; } = new();

    public ManualGameClock GameClock { get; } = new();

    public RecordingLink Link { get; } = new();

    public List<DisposableRuleCalls> RuleCalls { get; } = new();

    public List<string> Log { get; } = new();

    public string Root { get; }

    public PlanSaving? Saving { get; private set; }

    public IWorldScan Scan { get; set; } = null!;

    /// <summary>When set, the world's changes come through this relay, as the bridge gives them, flushed by the world's frame.</summary>
    public WorldChangeRelay? Relay { get; set; }

    /// <summary>The game's paint colours the world is started with.</summary>
    public IPaintColours Paints { get; set; } = new NoPaintColours();

    public LatticeWorld World { get; private set; } = null!;

    public static Actor Viewer => TestBoard.Alice;

    /// <summary>Whether the seat draws; false stands for batch mode (a dedicated server, a batch-mode client).</summary>
    public bool Drawing { get; set; } = true;

    public ModuleTable Modules(WorldSeat seat, params GameMember[] missing) =>
        ModuleSwitches.Decide(LatticeModules.All, new FakeMembers(missing), seat, Drawing);

    public Result<LatticeWorld, Reason> TryStart(WorldSeat seat = WorldSeat.Host, params GameMember[] missing)
    {
        var modules = Modules(seat, missing);
        Option<OpenedSaving> saving = Option<OpenedSaving>.None;
        if (seat == WorldSeat.Host && modules.IsOn(LatticeModules.Saving))
        {
            Saving = new PlanSaving(new WorldFolder(Root));
            saving = Option.Some(new OpenedSaving(Saving, Saving.Open(Option<SaveKey>.None)));
        }

        Scan ??= new FakeScan(Runs.World);
        var ports = new WorldPorts(Runs.Catalogue, Runs.Grid, modules, seat, Viewer, GameClock)
        {
            World = modules.IsOn(LatticeModules.World)
                ? Option.Some(Relay is { } relay
                    ? new GamePorts(Runs.World, Scan, relay, Runs.Check) { FlushChanges = relay.Flush }
                    : new GamePorts(Runs.World, Scan, Runs.World, Runs.Check))
                : Option<GamePorts>.None,
            Saving = saving,
            Link = Option.Some<ISyncLink>(Link),
            Paints = Paints,
        };
        var runtime = new WorldRuntime(Clock, new FixedTime(), FrameBudgets.Default, _ => Worker, NewRuleCalls, Log.Add);
        var started = LatticeWorld.Start(ports, runtime);
        if (started.TryGet(out var world))
            World = world;
        return started;
    }

    public LatticeWorld Start(WorldSeat seat = WorldSeat.Host, params GameMember[] missing) =>
        TestKitIds.Must(TryStart(seat, missing));

    /// <summary>Frames, running the worker between them, until validation has nothing left to do.</summary>
    public void Settle()
    {
        for (var i = 0; i < 64; i++)
        {
            World.Frame();
            Worker.RunAll();
            World.Frame();
            if (Worker.Idle && World.ValidationModule is not { Busy: true })
                return;
        }

        throw new InvalidOperationException("The world did not settle.");
    }

    public Outcome Submit(Command command, Actor? actor = null) =>
        World.Commands.Submit(new CommandId(Guid.NewGuid()), command, actor ?? Viewer) switch
        {
            Submission.Answered answered => answered.Outcome,
            var sent => throw new InvalidOperationException($"not answered at once: {sent}"),
        };

    public GhostDraft Draft(string piece, Cell at) => Runs.Boards.Draft(piece, at);

    public string PlanText(PlanId plan)
    {
        var folder = new WorldFolder(Root);
        var path = folder.PlanPath(plan);
        return File.Exists(path) ? folder.ReadText(path) : "";
    }

    public void Dispose()
    {
        World?.End();
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private IRuleCalls NewRuleCalls()
    {
        var calls = new DisposableRuleCalls(Clock);
        RuleCalls.Add(calls);
        return calls;
    }
}
