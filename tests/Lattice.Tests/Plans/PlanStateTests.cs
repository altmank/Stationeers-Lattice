using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Plans;
using Lattice.Core.Safety;
using Lattice.Core.Slices;
using Lattice.Core.Steps;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Slices;
using Lattice.Tests.Validation;
using Xunit;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Plans;

/// <summary>Validation's bench with bills, a build safety stand-in and the plan-state module, framed together.</summary>
internal sealed class PlanBench
{
    public PlanBench(Action<ValidationBench>? before = null, BoardSeed? seed = null, IPlanSafety? safety = null)
    {
        V = new ValidationBench(seed: seed);
        before?.Invoke(V);
        V.Start();
        var steps = new StepPlanner(V.Board, V.Mirror, Pieces, V.Shapes, new NoOrder());
        Bills = new BillMaker(V.Board, steps, V.Mirror);
        Module = new PlanStateModule(new PlanStateParts(
            V.Board, V.Events, V.Mirror, V.Service, Bills, safety ?? Safety, V.Scheduler));
        Module.Start();
        V.Events.PlanStateChanged += plan => StateChanges.Add(plan);
    }

    public ValidationBench V { get; }

    public PlanStateModule Module { get; }

    public BillMaker Bills { get; }

    public TestSafety Safety { get; } = new();

    public List<PlanId> StateChanges { get; } = [];

    public IPlans Plans => new PlansView(V.Board, TestBoard.Alice);

    public PlanId Default => GhostBoard.DefaultPlan;

    /// <summary>Frames until validation has settled and the plan's facts are ready.</summary>
    public PlanFacts Facts(PlanId? plan = null)
    {
        for (var i = 0; i < 256; i++)
        {
            V.Frame();
            Module.Frame();
            V.Events.Flush();
            if (V.Settled && TestKitIds.Must(Plans.Facts(plan ?? Default)) is Answer<PlanFacts>.Ready ready)
                return ready.Value;
        }

        throw new InvalidOperationException("Plan facts did not settle.");
    }

    public PlanState State(PlanId? plan = null)
    {
        Facts(plan);
        return Assert.IsType<Answer<PlanState>.Ready>(TestKitIds.Must(Plans.State(plan ?? Default))).Value;
    }

    internal sealed class TestSafety : IPlanSafety
    {
        public ValueList<SafetyWarning> Warnings { get; set; } = ValueList<SafetyWarning>.Empty;

        /// <summary>How many plan checks were started: one each time a plan's facts are worked out.</summary>
        public int Starts { get; private set; }

        public ISafetyRun Start(IReadOnlyList<Ghost> plan)
        {
            Starts++;
            return new Run(Warnings);
        }

        private sealed class Run(ValueList<SafetyWarning> warnings) : ISafetyRun
        {
            public bool Step(int units) => true;

            public ValueList<SafetyWarning> Warnings() => warnings;
        }
    }
}

public sealed class PlanStateTests
{
    // A build safety check whose every step spends more than the checking budget, done after Steps steps.
    private sealed class SlowSafety : IPlanSafety
    {
        private const int Steps = 20;

        public FakeClock Clock { get; set; } = null!;

        public ISafetyRun Start(IReadOnlyList<Ghost> plan) => new Run(this);

        private sealed class Run(SlowSafety owner) : ISafetyRun
        {
            private int _steps;

            public bool Step(int units)
            {
                owner.Clock.Advance(FrameBudgets.Default.Checking.Length + TimeSpan.FromMilliseconds(1));
                return ++_steps == Steps;
            }

            public ValueList<SafetyWarning> Warnings() => ValueList<SafetyWarning>.Empty;
        }
    }

    private static void Frames(PlanBench bench, int count)
    {
        for (var i = 0; i < count; i++)
        {
            bench.V.Frame();
            bench.Module.Frame();
            bench.V.Events.Flush();
        }
    }

    private static Completion Seen => new(Option<StructureRef>.None, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_plan_with_no_ghosts_is_empty()
    {
        var bench = new PlanBench();

        Assert.IsType<PlanState.Empty>(bench.State());
    }

    [Fact]
    public void A_plan_whose_ghosts_all_pass_is_valid_with_its_facts()
    {
        var bench = new PlanBench();
        var frames = bench.V.Ghosts(("StructureFrameIron", Large(0, 0, 0)), ("StructureFrameIron", Large(3, 0, 0)));

        var facts = bench.Facts();

        Assert.IsType<PlanState.Valid>(bench.State());
        Assert.IsType<Validity.Valid>(facts.ValidAlone);
        Assert.IsType<Validity.Valid>(facts.ValidInWorld);
        Assert.Equal(frames, facts.BuildableNext);
        Assert.Equal(new Progress(0, 0, 2), facts.Progress);
        Assert.NotEmpty(facts.Cost.Needed);
        Assert.Equal(bench.V.Board.Revision, facts.At);
    }

    [Fact]
    public void A_ghost_that_will_fail_gives_has_errors_against_the_plan_alone()
    {
        var bench = new PlanBench(v => v.Rules.Register(new RuleDescriptor(Rule("no"), _ => new RuleVerdict.Fail(Why("no")))));
        var frame = bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));

        var facts = bench.Facts();

        Assert.IsType<PlanState.HasErrors>(bench.State());
        var failing = Assert.Single(Assert.IsType<Validity.Invalid>(facts.ValidAlone).Failing);
        Assert.Equal(frame, failing.Ghost);
        Assert.IsType<Validity.Valid>(facts.ValidInWorld);
        Assert.Empty(facts.BuildableNext);
    }

    [Fact]
    public void A_started_ghost_puts_the_plan_in_progress()
    {
        var bench = new PlanBench();
        var frames = bench.V.Ghosts(("StructureFrameIron", Large(0, 0, 0)), ("StructureFrameIron", Large(3, 0, 0)));
        var partBuilt = bench.V.Built("StructureFrameIron", Large(0, 0, 0));
        TestKitIds.Must(bench.V.Board.MarkStarted(frames[0], partBuilt));

        var facts = bench.Facts();

        Assert.IsType<PlanState.InProgress>(bench.State());
        Assert.Equal(new Progress(0, 1, 1), facts.Progress);
    }

    [Fact]
    public void Done_ghosts_count_and_a_plan_with_every_ghost_done_is_complete()
    {
        var bench = new PlanBench();
        var frames = bench.V.Ghosts(("StructureFrameIron", Large(0, 0, 0)), ("StructureFrameIron", Large(3, 0, 0)));
        TestKitIds.Must(bench.V.Board.Complete(frames[0], Seen));

        Assert.Equal(new Progress(1, 0, 1), bench.Facts().Progress);
        Assert.IsType<PlanState.InProgress>(bench.State());

        TestKitIds.Must(bench.V.Board.Complete(frames[1], Seen));

        Assert.Equal(new Progress(2, 0, 0), bench.Facts().Progress);
        Assert.IsType<PlanState.Complete>(bench.State());
    }

    [Fact]
    public void A_world_change_that_breaks_a_valid_plan_moves_it_to_has_errors()
    {
        var framed = false;
        var bench = new PlanBench(v => v.Placement.Refuse(
            p => framed && p.Piece == Pieces.Named("StructureWallIron").Key, ReasonCodes.GameRefused, "Grid side blocked"));
        var wall = bench.V.Ghost("StructureWallIron", Large(5, 0, 1));
        Assert.IsType<PlanState.Valid>(bench.State());
        bench.StateChanges.Clear();

        framed = true;
        bench.V.Built("StructureFrameIron", Large(5, 0, 0));
        var facts = bench.Facts();

        Assert.IsType<PlanState.HasErrors>(bench.State());
        var failing = Assert.Single(Assert.IsType<Validity.Invalid>(facts.ValidInWorld).Failing);
        Assert.Equal(wall, failing.Ghost);
        Assert.IsType<Validity.Valid>(facts.ValidAlone);
        Assert.Equal(new[] { bench.Default }, bench.StateChanges);
    }

    [Fact]
    public void A_stale_ghost_fails_the_plan_against_the_world()
    {
        var bench = new PlanBench();
        var frame = bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));
        TestKitIds.Must(bench.V.Board.MarkStale(frame, Why("different")));

        var facts = bench.Facts();

        Assert.Equal(frame, Assert.Single(Assert.IsType<Validity.Invalid>(facts.ValidInWorld).Failing).Ghost);
        Assert.Equal(new Progress(0, 0, 1), facts.Progress);
        Assert.IsType<PlanState.HasErrors>(bench.State());
    }

    [Fact]
    public void Plan_state_work_goes_after_validation_s_asks_in_the_checking_budget()
    {
        var safety = new SlowSafety();
        var bench = new PlanBench(safety: safety);
        safety.Clock = bench.V.Clock;
        bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.Facts();
        var asked = bench.V.Placement.Calls.Count;

        bench.V.Built("StructureFrameIron", Large(1, 0, 1));
        bench.V.Frame(runWorker: false);
        bench.Module.Frame();
        bench.V.Events.Flush();
        Frames(bench, 3);

        Assert.Equal(new[] { Large(0, 0, 0) }, bench.V.Placement.Calls.Skip(asked).Select(p => p.At));
        Assert.True(bench.V.Settled);
        Assert.IsType<Answer<PlanFacts>.Pending>(TestKitIds.Must(bench.Plans.Facts(bench.Default)));
        Assert.Equal(1, bench.Facts().Progress.Remaining);
    }

    [Fact]
    public void Facts_are_pending_until_worked_out_then_answers_ready_is_raised()
    {
        var bench = new PlanBench();
        var ready = new List<Revision>();
        bench.V.Events.AnswersReady += ready.Add;
        bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));

        Assert.IsType<Answer<PlanFacts>.Pending>(TestKitIds.Must(bench.Plans.Facts(bench.Default)));
        Assert.IsType<Answer<PlanState>.Pending>(TestKitIds.Must(bench.Plans.State(bench.Default)));
        bench.Facts();

        Assert.Contains(bench.V.Board.Revision, ready);
    }

    [Fact]
    public void The_state_change_event_is_raised_once_per_change()
    {
        var bench = new PlanBench();
        bench.State();
        Assert.Equal(new[] { bench.Default }, bench.StateChanges);

        bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.State();
        bench.Facts();

        Assert.Equal(new[] { bench.Default, bench.Default }, bench.StateChanges);
    }

    [Fact]
    public void A_validation_run_that_changes_no_finding_leaves_the_plan_facts_as_worked_out()
    {
        var bench = new PlanBench();
        bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.Facts();
        Frames(bench, 8);
        var worked = bench.Safety.Starts;
        var runs = 0;
        bench.V.Service.Ran += _ => runs++;

        bench.V.Rules.Register(new RuleDescriptor(Rule("passes"), _ => new RuleVerdict.Pass()));
        Frames(bench, 32);

        Assert.True(runs > 0);
        Assert.Equal(worked, bench.Safety.Starts);
        Assert.IsType<Answer<PlanFacts>.Ready>(TestKitIds.Must(bench.Plans.Facts(bench.Default)));
    }

    [Fact]
    public void A_validation_run_that_changes_a_finding_works_the_plan_facts_out_again()
    {
        var bench = new PlanBench();
        bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));
        Assert.IsType<PlanState.Valid>(bench.State());

        bench.V.Rules.Register(new RuleDescriptor(Rule("fails"), _ => new RuleVerdict.Fail(Why("fails"))));
        for (var i = 0; i < 32 && !bench.V.Settled; i++)
            bench.V.Frame();

        Assert.IsType<PlanState.HasErrors>(bench.State());
    }

    [Fact]
    public void Consequences_are_build_safety_warnings_and_never_errors()
    {
        var bench = new PlanBench();
        bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));
        var warning = new SafetyWarning(Why("hot"), ValueList<GhostId>.Empty);
        bench.Safety.Warnings = ValueList.Of(warning);

        var facts = bench.Facts();

        Assert.Equal(ValueList.Of(warning), facts.Consequences);
        Assert.IsType<PlanState.Valid>(bench.State());
    }

    [Fact]
    public void Each_plan_has_its_own_facts_and_an_unknown_plan_is_refused()
    {
        var bench = new PlanBench();
        bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));
        var other = Assert.Single(TestBoard.Applied(bench.V.Apply(new Command.CreatePlan(new PlanDraft(
            TestKitIds.Must(PlanName.Create("Other")), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty)))).PlansOpened).Id;

        Assert.IsType<PlanState.Valid>(bench.State());
        Assert.IsType<PlanState.Empty>(bench.State(other));
        Assert.True(bench.Plans.Facts(new PlanId(999)).TryGetError(out var why));
        Assert.Equal(ReasonCodes.NotFound, why.Code);
    }

    [Fact]
    public void Without_the_module_facts_are_module_missing()
    {
        var bench = new ValidationBench().Start();

        Assert.True(new PlansView(bench.Board, TestBoard.Alice).Facts(GhostBoard.DefaultPlan).TryGetError(out var why));

        Assert.Equal(ReasonCodes.ModuleMissing, why.Code);
    }

    [Fact]
    public void A_repeated_read_of_ready_facts_allocates_nothing()
    {
        var bench = new PlanBench();
        bench.V.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.Facts();
        bench.Module.Facts(bench.Default);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            bench.Module.Facts(bench.Default);
            bench.Module.State(bench.Default);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
