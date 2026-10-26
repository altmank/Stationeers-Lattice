using System;
using Lattice.Api;
using Lattice.Core.Analysis;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Commands;
using Lattice.Core.Openings;
using Lattice.Core.Preview;
using Lattice.Core.Routes;
using Refusal = Lattice.Api.Refusal;
using Lattice.Core.Runs;
using Lattice.Core.Safety;
using Lattice.Core.Slices;
using Lattice.Core.Steps;
using Lattice.Core.Support;
using Lattice.Core.Swaps;
using Lattice.Core.Validation;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Routes;
using Lattice.Tests.Runs;
using Lattice.Tests.Safety;
using Lattice.Tests.Slices;
using Lattice.Tests.Support;
using Lattice.Tests.Swaps;
using Lattice.Tests.Validation;

namespace Lattice.Tests.Preview;

/// <summary>
/// Every module a preview reads, over fakes: the run bench's board, catalogue (hand pieces, the modded conduit kit, the
/// heavy cables and the modded panels) and world, a started world mirror, validation on a worker the test runs by hand,
/// the command service with the run and swap expanders, steps, build safety, effects and analyzers. Load the world, then
/// <see cref="Start"/>; <see cref="Ask"/> runs frames until the preview is ready.
/// </summary>
internal sealed class PreviewBench
{
    public static readonly AgentId Agent = TestKitIds.Must(AgentId.Create("swapper"));

    /// <param name="moreText">More pieces in the catalogue file format without its header, for benches built on this one.</param>
    public PreviewBench(string? moreText = null)
    {
        Runs = new RunBench(moreText is null ? SwapBench.ExtraText : SwapBench.ExtraText + "\n\n" + moreText);
        Shapes = new Shapes(Runs.Catalogue, Runs.Grid);
        Mirror = new WorldMirror(Shapes, Runs.World);
        Scheduler = new FrameScheduler(Clock, FrameBudgets.Default);
        Book = new RunReportBook(Board);
        Expanders.Register(new RunExpander(Runs.Planner, Book).Descriptor);
        Expanders.RegisterSliced(new RouteExpander(new RoutePlanner(
            Runs.Catalogue, Runs.Grid, Runs.Check, new NoOpeningFacts(), new TestFrames(), OpeningBands.Default,
            RouteLimits.Default with { PerStep = 4 }), Book).Descriptor);
        Expanders.RegisterSliced(new SwapExpander(new SwapPlanner(Runs.Catalogue, Shapes, Runs.World)).Descriptor);
        Commands = new CommandService(Board, Expanders, Runs.Query, new ClientChecks(CommandLimits.Default, Runs.Catalogue, new AttachmentLimits()), Scheduler);
    }

    public RunBench Runs { get; }

    public GhostBoard Board => Runs.Boards.Board;

    public BoardEvents Events => Runs.Boards.Events;

    public Shapes Shapes { get; }

    public FakeClock Clock { get; } = new();

    public WorldMirror Mirror { get; }

    public FrameScheduler Scheduler { get; }

    public CommandExpanders Expanders { get; } = new();

    public CommandService Commands { get; }

    public RunReportBook Book { get; }

    public Analyzers Analyzers { get; } = new();

    public ManualWorker Worker { get; } = new();

    public TestSafetyFacts SafetyFacts { get; } = new();

    public ValidationService Validation { get; private set; } = null!;

    public StepPlanner Steps { get; private set; } = null!;

    public PreviewService Preview { get; private set; } = null!;

    public PreviewBench Start()
    {
        Mirror.Start(Runs.World, new FakeScan(Runs.World));
        var calls = new InlineRuleCalls(Clock);
        var judge = new SupportJudge(
            new SupportRules(new RuleGuard(RuleGuard.DefaultLimit, calls, calls)),
            new SupportGeometry(Runs.Grid, new TestSupportFacts(Runs.Catalogue), Runs.Catalogue));
        Validation = new ValidationService(new ValidationParts(
            Board, TestBoard.Alice, Mirror, Runs.World, Runs.Check, Runs.Catalogue, Runs.Grid, judge,
            new ValidationRules(new RuleGuard(RuleGuard.DefaultLimit, calls, calls)), Scheduler, Clock, Worker) { Analyzers = Analyzers });
        Validation.Start();
        Steps = new StepPlanner(Board, Mirror, Runs.Catalogue, Shapes, new NoOrder());
        var safety = new BuildSafety(Runs.Catalogue, Runs.Grid, SafetyFacts, new PieceOpeningFacts(Runs.Catalogue), OpeningBands.Default, SafetyLimits.Default);
        Preview = new PreviewService(new PreviewParts(
            Board, Commands, Runs.World, Mirror, Steps, new PlanEffects(Runs.Catalogue, Runs.Grid), safety, Analyzers, Book, Scheduler, Validation));
        Settle();
        return this;
    }

    /// <summary>One frame: board events, validation's frame and its worker, the scheduler's slices.</summary>
    public void Frame()
    {
        Events.Flush();
        Validation.Frame();
        Worker.RunAll();
        Validation.Frame();
        Scheduler.RunFrame();
        Events.Flush();
    }

    /// <summary>Frames until validation has nothing left to do.</summary>
    public void Settle()
    {
        for (var i = 0; i < 64; i++)
        {
            Frame();
            if (Worker.Idle && !Validation.Busy)
                return;
        }

        throw new InvalidOperationException("Validation did not settle.");
    }

    /// <summary>Asks for the preview, running frames until it is ready.</summary>
    public Result<Lattice.Api.Preview, ValueList<Refusal>> Ask(Command command, Actor? actor = null)
    {
        for (var i = 0; i < 64; i++)
        {
            if (Preview.Of(command, actor ?? TestBoard.Alice) is Answer<Result<Lattice.Api.Preview, ValueList<Refusal>>>.Ready ready)
                return ready.Value;
            Frame();
        }

        throw new InvalidOperationException("The preview did not settle.");
    }

    public Lattice.Api.Preview Ok(Command command, Actor? actor = null) =>
        Ask(command, actor).Match(static preview => preview, static why => throw new Xunit.Sdk.XunitException($"Refused: {why}"));
}
