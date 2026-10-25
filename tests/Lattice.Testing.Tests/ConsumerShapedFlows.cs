using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>
/// A renderer and planner's flow: a run dragged out cell by cell (each preview pending, then ready
/// when Lattice raises AnswersReady), placed, validated, selected by a selector, and a built cable swapped in place.
/// </summary>
public sealed class PlannerShapedFlow
{
    private static readonly KitKey Coil = TestCatalogue.Hand.Kit("ItemCableCoil").Reduce(default(KitKey));
    private static readonly KitKey HeavyCoil = TestCatalogue.Hand.Kit("ItemCableCoilHeavy").Reduce(default(KitKey));

    private static Command Run(int length) =>
        new Command.PlanRun(new RunRequest(Coil, At(0), ValueList<Cell>.Empty, At(0, 0, length), Option<PlanId>.None, Made));

    [Fact]
    public void A_dragged_run_previews_pending_then_ready_and_places_validates_selects_and_swaps()
    {
        using var world = Start(TestScenario.Empty);
        var lattice = world.System;
        var ready = new List<Revision>();
        lattice.Events.AnswersReady += ready.Add;

        var previews = new List<Preview>();
        for (var length = 1; length <= 3; length++)
        {
            var first = lattice.Preview.Of(Run(length), world.Viewer);
            Assert.IsType<Answer<Result<Preview, ValueList<Refusal>>>.Pending>(first);
            Must(world.Settle());
            var answer = Assert.IsType<Answer<Result<Preview, ValueList<Refusal>>>.Ready>(lattice.Preview.Of(Run(length), world.Viewer));
            previews.Add(answer.Value.Match(static p => p, static why => throw new Xunit.Sdk.XunitException(why[0].Why.ToString())));
        }

        Assert.NotEmpty(ready);
        Assert.Equal(new[] { 2, 3, 4 }, previews.Select(static p => p.Run.Map(static r => r.Cells.Count).Reduce(0)).ToArray());

        var placed = Assert.IsType<Outcome.Applied>(Submit(world, Run(3))).Changes.Added;
        Assert.Equal(previews[2].WouldChange.Added.Count, placed.Count);
        var report = Ready(world, () => Must(lattice.Validation.Check(new Selector.MadeBy(Made))));
        Assert.Equal(placed.Count, report.Findings.Count);
        Assert.All(report.Findings, static f => Assert.IsType<Finding.Passes>(f));

        var through = Must(lattice.Board.Select(new Selector.RunThrough(At(0, 0, 1))));
        Assert.NotEmpty(through);
        Assert.All(through, ghost => Assert.Contains(placed, p => p.Id == ghost.Id));

        var cable = Must(world.Build(Hand("StructureCableStraight"), At(20), default));
        Must(world.Settle());
        var options = Must(lattice.Swaps.OptionsFor(cable));
        Assert.Contains(options, static o => o.New.Piece == Hand("StructureCableStraightH"));
        var swap = Submit(world, new Command.PlanSwap(new StructureSelector.Refs(ValueList.Of(cable)), new SwapTo.SameShapeFrom(HeavyCoil), Option<PlanId>.None, Made));
        var swapped = Assert.Single(Assert.IsType<Outcome.Applied>(swap).Changes.Added);
        Assert.Equal(cable, Assert.IsType<Intent.Swap>(swapped.Intent).Old);
    }
}

/// <summary>
/// An agent's flow: a dry run of swapping a whole cable network, submitted as an agent, its
/// findings read, with a rule the agent registered and its findings seen.
/// </summary>
public sealed class AgentShapedFlow
{
    private static readonly ModId Foreman = Must(ModId.Create("foreman"));
    private static readonly KitKey HeavyCoil = TestCatalogue.Hand.Kit("ItemCableCoilHeavy").Reduce(default(KitKey));

    [Fact]
    public void An_agents_dry_run_swap_of_a_network_is_submitted_and_its_rules_findings_are_read()
    {
        var straight = Hand("StructureCableStraight");
        var scenario = TestScenario.Empty
            .WithStructure(straight, At(0), default)
            .WithStructure(straight, At(0, 0, 1), default)
            .WithStructure(straight, At(0, 0, 2), default);
        using var world = Start(scenario);
        var lattice = world.System;
        var agent = new Actor.Agent(Must(AgentId.Create("foreman-agent")));
        var heavy = Must(ReasonCode.Create(Foreman, "heavy-cable"));
        var ruleId = Must(RuleId.Create(Foreman, "heavy-cable"));
        using var rule = Must(lattice.Registries.Register(new RuleDescriptor(ruleId, input =>
            input.Ghost.Intent is Intent.Swap ? new RuleVerdict.Warn(Reason.Of(heavy, ruleId)) : new RuleVerdict.Pass())));

        var first = Assert.Single(lattice.World.At(At(0)));
        var network = lattice.World.NetworkOf(first.Ref.Id).Reduce(() => throw new Xunit.Sdk.XunitException("no network"));
        Command swap = new Command.PlanSwap(new StructureSelector.Network(network), new SwapTo.SameShapeFrom(HeavyCoil), Option<PlanId>.None,
            Must(Maker.Create(Foreman, "agent")));

        var dryRun = Ready(world, () => lattice.Preview.Of(swap, agent))
            .Match(static p => p, static why => throw new Xunit.Sdk.XunitException(why[0].Why.ToString()));
        Assert.Equal(3, dryRun.WouldChange.Added.Count);
        Assert.Equal(0, lattice.Board.Select(new Selector.Everything()).Match(static g => g.Count, static _ => -1));

        var added = Assert.IsType<Outcome.Applied>(Submit(world, swap, agent)).Changes.Added;
        Assert.Equal(3, added.Count);
        Assert.All(added, ghost => Assert.Equal<Actor>(agent, ghost.PlacedBy));
        var report = Ready(world, () => Must(lattice.Validation.Check(new Selector.Owner(agent))));
        var warned = report.Findings.OfType<Finding.Warns>().ToList();
        Assert.Equal(3, warned.Count);
        Assert.All(warned, w => Assert.Equal(heavy, w.Why.Code));
    }
}
