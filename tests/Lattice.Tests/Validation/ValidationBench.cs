using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.Core.Support;
using Lattice.Core.Validation;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.PlacementCheck;
using Lattice.TestKit.World;
using Lattice.Tests.Board;
using Lattice.Tests.Slices;
using Lattice.Tests.Support;

namespace Lattice.Tests.Validation;

/// <summary>A worker the test runs by hand, on the test thread: deterministic.</summary>
internal sealed class ManualWorker : IValidationWorker
{
    private readonly Queue<Action> _work = new();

    public int Runs { get; private set; }

    public bool Idle => _work.Count == 0;

    public bool Stopped { get; private set; }

    /// <summary>Whether posted work is running now.</summary>
    public bool Running { get; private set; }

    public void Post(Action work) => _work.Enqueue(work);

    public void Stop(TimeSpan join) => Stopped = true;

    public void RunAll()
    {
        while (_work.Count > 0)
        {
            Runs++;
            Running = true;
            try
            {
                _work.Dequeue()();
            }
            finally
            {
                Running = false;
            }
        }
    }
}

/// <summary>A scan of a fake world's structures.</summary>
internal sealed class FakeScan(FakeWorld world) : IWorldScan
{
    public ValueList<StructureInfo> Structures() => ValueList.From(world.All);
}

/// <summary>
/// A world for validation tests: the support pieces (hand catalogue and modded ones) plus a modded frame that stands
/// on the ground or on another frame; a board, a fake world under a started world mirror, a fake placement check, the
/// frame scheduler on a fake clock, rules on that clock (no call times out unless a rule moves it) and a worker the
/// test runs by hand. <see cref="Pump"/> runs frames until validation has settled.
/// </summary>
internal sealed class ValidationBench
{
    private const string ExtraText = """
        lattice-catalogue 3
        piece ModStackFrame kit=ItemModStackKit option=0 grid=large support=lattice.support.frame-or-ground
          step material=ItemModStackKit*1 exit=ItemWrench air=no
        """;

    public static readonly CubeGrid Grid = new();

    public static readonly FakeCatalogue Pieces = Make();

    private long _structures;
    private int _commands;

    public ValidationBench(IClock? clock = null, Func<FakeCatalogue, ISupportFacts>? facts = null, IRuleCalls? workerCalls = null, BoardSeed? seed = null)
    {
        Clock = new FakeClock();
        SchedulerClock = clock ?? Clock;
        World = new FakeWorld(Pieces);
        Shapes = new Shapes(Pieces, Grid);
        Board = new GhostBoard(Shapes, new FixedTime(), Policies, new PlanSizeLimit(), Events, seed is null ? default : Option.Some(seed));
        Mirror = new WorldMirror(Shapes, World);
        Scheduler = new FrameScheduler(SchedulerClock, FrameBudgets.Default);
        var inline = new InlineRuleCalls(Clock);
        SupportRules = new SupportRules(new RuleGuard(RuleGuard.DefaultLimit, workerCalls ?? inline, inline));
        Rules = new ValidationRules(new RuleGuard(RuleGuard.DefaultLimit, workerCalls ?? inline, inline));
        Judge = new SupportJudge(SupportRules, new SupportGeometry(Grid, (facts ?? DefaultFacts)(Pieces), Pieces));
    }

    public FakeClock Clock { get; }

    public IClock SchedulerClock { get; }

    public FakeWorld World { get; }

    public Shapes Shapes { get; }

    public BoardEvents Events { get; } = new();

    public AccessPolicies Policies { get; } = new();

    public GhostBoard Board { get; }

    public WorldMirror Mirror { get; }

    public FrameScheduler Scheduler { get; }

    public FakePlacementCheck Placement { get; } = new();

    public SupportRules SupportRules { get; }

    public ValidationRules Rules { get; }

    public SupportJudge Judge { get; }

    public ManualWorker Worker { get; } = new();

    public Lattice.Core.Analysis.Analyzers Analyzers { get; } = new();

    /// <summary>The worker analyzers' own thread; none runs them on <see cref="Worker"/> right after a trial.</summary>
    public ManualWorker? AnalyzerWorker { get; init; }

    public ValidationService Service { get; private set; } = null!;

    /// <summary>Every run's counts, in order.</summary>
    public List<RunCounts> Runs { get; } = [];

    public static ISupportFacts DefaultFacts(FakeCatalogue pieces) =>
        new TestSupportFacts(pieces)
            .With("StructureFrame", SupportBench.Frame)
            .With("StructureFrameIron", SupportBench.Frame)
            .With("ModStackFrame", SupportBench.Frame)
            .With("StructureWallIron", SupportFacts.Nothing)
            .With("ModWallLight", SupportFacts.Nothing)
            .With("ModFrameLight", SupportFacts.Nothing with { RequiresFrame = true });

    /// <summary>A ghost or structure cell: large cells centre on odd metres (10, 30, ... decimetres).</summary>
    public static Cell Large(int x, int y, int z) => SupportBench.Large(x, y, z);

    public static Turn Facing(Direction direction) => TestKitIds.Must(Turn.Create(SupportBench.Facing(direction)));

    public ValidationBench Start()
    {
        Mirror.Start(World, new FakeScan(World));
        Service = new ValidationService(new ValidationParts(
            Board, TestBoard.Alice, Mirror, World, Placement, Pieces, Grid, Judge, Rules, Scheduler, Clock, Worker) { Analyzers = Analyzers, AnalyzerWorker = AnalyzerWorker });
        Service.Ran += Runs.Add;
        Service.Start();
        return this;
    }

    /// <summary>A built structure with a new id, not yet in the world.</summary>
    public StructureInfo Structure(string piece, Cell at, Turn turn = default) =>
        new(new StructureRef(new ReferenceId(++_structures), Pieces.Named(piece).Key, at), turn, new BuildState.Built(),
            Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);

    /// <summary>A built structure: loaded before <see cref="Start"/>, registered (signalled) after.</summary>
    public StructureRef Built(string piece, Cell at, Turn turn = default)
    {
        var info = Structure(piece, at, turn);
        if (Service is null)
            World.Load(info);
        else
            World.Register(info);
        return info.Ref;
    }

    public GhostId Ghost(string piece, Cell at, Turn turn = default) =>
        Place(new Intent.Build(new Placement(Pieces.Named(piece).Key, at, turn), Option<Finish>.None));

    public GhostId Removal(StructureRef target) => Place(new Intent.Remove(target));

    public ValueList<GhostId> Ghosts(params (string Piece, Cell At)[] pieces)
    {
        var drafts = pieces.Select(p => TestBoard.Draft(new Intent.Build(new Placement(Pieces.Named(p.Piece).Key, p.At, default), Option<Finish>.None))).ToArray();
        var changes = TestBoard.Applied(Apply(new Command.Place(ValueList.From(drafts))));
        return ValueList.From(changes.Added.Select(g => g.Id));
    }

    public Outcome Apply(Command command) =>
        Board.Apply(new CommandId(new Guid(++_commands, 0, 0, new byte[8])), command, TestBoard.Alice);

    /// <summary>One frame: board events, validation's frame, the scheduler's slices; the worker runs when told.</summary>
    public void Frame(bool runWorker = true)
    {
        Events.Flush();
        Service.Frame();
        if (runWorker)
            Worker.RunAll();
        Service.Frame();
        Scheduler.RunFrame();
    }

    /// <summary>Runs frames until validation has a report for the board's revision and nothing is left to do.</summary>
    public int Pump(int frames = 64)
    {
        for (var i = 1; i <= frames; i++)
        {
            Frame();
            if (Settled)
                return i;
        }

        throw new InvalidOperationException("Validation did not settle.");
    }

    public bool Settled =>
        Worker.Idle && !Service.Busy && Service.Check(All()).TryGet(out var answer) && answer is Answer<ValidationReport>.Ready;

    public Selector All() => new Selector.Ids(ValueList.From(Board.Index.Ghosts.Keys.OrderBy(id => id.Value)));

    public ValidationReport Report(Selector? which = null)
    {
        var answer = TestKitIds.Must(Service.Check(which ?? All()));
        return answer is Answer<ValidationReport>.Ready ready ? ready.Value : throw new InvalidOperationException($"Pending: {answer}");
    }

    public static Finding Verdict(ValidationReport report, GhostId ghost) =>
        report.Findings.First(f => f.Ghost == ghost && f is not Finding.Warns);

    public static List<Reason> Warnings(ValidationReport report, GhostId ghost) =>
        report.Findings.OfType<Finding.Warns>().Where(f => f.Ghost == ghost).Select(f => f.Why).ToList();

    public static void AssertWaitsFor(Finding finding, params GhostId[] others)
    {
        var waits = Assert.IsType<Finding.WaitsFor>(finding);
        Assert.Equal(others.OrderBy(o => o.Value), waits.Others.OrderBy(o => o.Value));
    }

    public static RuleId Rule(string name) => TestKitIds.Must(RuleId.Create(TestKitIds.Mod, name));

    public static Reason Why(string name) => Reason.Of(TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, name)), Rule(name));

    private GhostId Place(Intent intent)
    {
        var changes = TestBoard.Applied(Apply(new Command.Place(ValueList.Of(TestBoard.Draft(intent)))));
        return Assert.Single(changes.Added).Id;
    }

    private static FakeCatalogue Make()
    {
        var extra = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(ExtraText), "validation.lcat"));
        return new FakeCatalogue([.. SupportBench.Pieces.Pieces, .. extra.Pieces]);
    }
}
