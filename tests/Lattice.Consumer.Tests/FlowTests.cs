using System.Collections.Generic;
using Lattice.Api;
using static Lattice.Consumer.Tests.Make;

namespace Lattice.Consumer.Tests;

/// <summary>The three flows of the architecture's section 10, written against the contract alone.</summary>
public sealed class FlowTests
{
    private static readonly KitKey HeavyCable = new(1001);

    [Fact]
    public void A_player_lays_a_cable_run_on_a_client()
    {
        var lattice = new StubSystem(isHost: false);
        var player = Player();
        var runTool = Maker("planner", "run-tool");
        var run = new Command.PlanRun(new RunRequest(HeavyCable, new Cell(0, 0, 0), ValueList<Cell>.Empty, new Cell(40, 0, 0), Option<PlanId>.None, runTool));
        var placed = ValueList.From(new[] { Ghost(1, Build(2001, 0), runTool), Ghost(2, Build(2001, 20), runTool) });
        lattice.PreviewAnswer = (command, actor) => Result<Preview, ValueList<Refusal>>.Ok(
            new Preview(Changes(default, actor, placed), ValueList<Finding>.Empty, EmptyBill(), NoEffects(), Option<RunReport>.None));
        lattice.HostAnswer = (id, _, actor) => new Outcome.Applied(Changes(id, actor, placed));

        // The drag readout: a big run may not be worked out yet; the readout waits for AnswersReady and asks again.
        lattice.FirstAskPending = true;
        var first = lattice.Preview.Of(run, player);
        var readout = -1;
        lattice.Events.AnswersReady += _ =>
        {
            if (lattice.Preview.Of(run, player) is Answer<Result<Preview, ValueList<Refusal>>>.Ready ready)
                readout = ready.Value.Match(p => p.WouldChange.Added.Count, _ => -1);
        };
        lattice.FinishPendingWork();

        // Release: submit the same command; the result arrives as an event.
        var undo = new Stack<ChangeSetId>();
        var drawn = new List<GhostId>();
        lattice.Events.Changed += changes =>
        {
            foreach (var ghost in changes.Added)
                drawn.Add(ghost.Id);
        };
        lattice.Events.Answered += (_, outcome) =>
        {
            if (outcome is Outcome.Applied applied)
                undo.Push(applied.Changes.Id);
        };
        var submission = lattice.Commands.Submit(CommandId.New(), run, player);
        lattice.HostReplies();

        Assert.IsType<Answer<Result<Preview, ValueList<Refusal>>>.Pending>(first);
        Assert.Equal(2, readout);
        Assert.IsType<Submission.Sent>(submission);
        Assert.Equal(new[] { new GhostId(1), new GhostId(2) }, drawn);
        Assert.Single(undo);
    }

    [Fact]
    public void An_agent_swaps_a_networks_cable_to_heavy_on_the_host()
    {
        var lattice = new StubSystem(isHost: true);
        var agent = Foreman();
        var swap = new Command.PlanSwap(
            new StructureSelector.Network(new NetworkRef(4630)), new SwapTo.SameShapeFrom(HeavyCable), Option<PlanId>.None, Maker("foreman", "job-412"));
        var swaps = new List<Ghost>();
        for (var i = 0; i < 96; i++)
            swaps.Add(Ghost(i + 1, Build(2002, i * 5), Maker("foreman", "job-412")));
        var planned = ValueList.From(swaps);
        lattice.PreviewAnswer = (_, actor) => Result<Preview, ValueList<Refusal>>.Ok(
            new Preview(Changes(default, actor, planned), ValueList<Finding>.Empty, EmptyBill(), NoEffects(), Option<RunReport>.None));
        lattice.HostAnswer = (id, _, actor) => new Outcome.Applied(Changes(id, actor, planned));

        var dryRun = lattice.Preview.Of(swap, agent);
        var answer = lattice.Commands.Submit(CommandId.New(), swap, agent);

        var ready = Assert.IsType<Answer<Result<Preview, ValueList<Refusal>>>.Ready>(dryRun);
        Assert.Equal(96, ready.Value.Match(p => p.WouldChange.Added.Count, _ => 0));
        var answered = Assert.IsType<Submission.Answered>(answer);
        Assert.IsType<Outcome.Applied>(answered.Outcome);
    }

    [Fact]
    public void A_builder_works_through_ghosts_on_the_host()
    {
        var lattice = new StubSystem(isHost: true);
        var target = new StructureRef(new ReferenceId(77), new PrefabKey(2001), new Cell(0, 0, 0));
        lattice.Report = new ValidationReport(new Revision(5), ValueList.From(new Finding[]
        {
            new Finding.Passes(new GhostId(1)),
            new Finding.WaitsFor(new GhostId(2), ValueList.Of(new GhostId(1))),
        }));
        lattice.StepPlans[new GhostId(1)] = new StepPlan(
            new GhostId(1),
            ValueList.From(new Step[]
            {
                new Step.TakeBack(target, new ItemKey(3001), ValueList<ItemQuantity>.Empty),
                new Step.PlaceKit(new KitKey(1001), Ok(KitOption.Create(0)), Ok(ItemQuantity.Create(new ItemKey(1001), 1)), new Placement(new PrefabKey(2003), new Cell(0, 0, 0), Turn.None)),
            }),
            ValueList.Of<Constraint>(new Constraint.OnePipeJoinPerTick()));

        var builder = Ok(lattice.Executors.Register(new ExecutorInfo(Ok(ExecutorId.Create(Mod("builder"), "drone-1")), Ok(DisplayName.Create("Drone 1")))));
        var lease = Ok(Lease.Create(30));
        var claims = new List<Claim>();
        var answer = Ok(lattice.Validation.Check(new Selector.Box(new Cell(-100, -100, -100), new Cell(100, 100, 100))));
        var report = Assert.IsType<Answer<ValidationReport>.Ready>(answer).Value;
        foreach (var finding in report.Findings)
            if (finding is Finding.Passes && builder.Claim(finding.Ghost, lease).TryGet(out var claim))
                claims.Add(claim);

        var skipped = new List<Step>();
        foreach (var claim in claims)
        {
            var plan = Ok(lattice.Steps.For(claim.Ghost));
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                switch (plan.Steps[i])
                {
                    case Step.PlaceKit or Step.Advance or Step.TakeBack or Step.Merge or Step.Paint or Step.Rename or Step.SetValue:
                        builder.Report(claim.Ghost, new ExecutorStatus.Working(i + 1, plan.Steps.Count));
                        break;
                    default:
                        skipped.Add(plan.Steps[i]);
                        break;
                }
            }
        }

        lattice.Events.Completed += (ghost, _) =>
        {
            foreach (var claim in claims)
                if (claim.Ghost == ghost)
                    builder.Release(claim);
        };
        lattice.Complete(new GhostId(1));
        builder.Dispose();

        Assert.Single(claims);
        Assert.Empty(skipped);
        Assert.Equal(2, lattice.Reports.Count);
        Assert.Empty(lattice.Claims);
    }

    [Fact]
    public void A_consumer_reads_the_version_it_was_built_against_and_asks_for_kinds()
    {
        var lattice = new StubSystem(isHost: true);

        Assert.Equal(new ApiVersion(ApiVersion.ContractMajor, ApiVersion.ContractMinor), lattice.Version);
        Assert.True(lattice.Has(IntentKind.Swap));
        Assert.False(lattice.Has((IntentKind)99));
        Assert.True(LatticeSystem.Current.IsNone);
        Assert.Empty(lattice.Modules);
    }
}
