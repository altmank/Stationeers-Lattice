using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Support;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Scenarios;
using Lattice.Tests.Slices;

namespace Lattice.Tests.Support;

/// <summary>Support facts the tests hand out by piece name.</summary>
internal sealed class TestSupportFacts(FakeCatalogue catalogue) : ISupportFacts
{
    private readonly Dictionary<PrefabKey, SupportFacts> _facts = [];

    public TestSupportFacts With(string piece, SupportFacts facts)
    {
        _facts[catalogue.Named(piece).Key] = facts;
        return this;
    }

    public Option<SupportFacts> Of(PrefabKey piece) =>
        _facts.TryGetValue(piece, out var facts) ? Option.Some(facts) : Option<SupportFacts>.None;
}

/// <summary>
/// The pieces support tests use: the hand catalogue plus modded pieces (kits no base game has) in each census group,
/// a piece the census does not know (ask-game), one in a group Lattice does not model (rocket) and one in a mod's own
/// group. Frames allow mounting and block their cell; walls offer nothing.
/// </summary>
internal static class SupportBench
{
    private const string ModdedText = """
        lattice-catalogue 3
        piece ModFloorMachine kit=ItemModMachineKit option=0 grid=small support=lattice.support.frame-below
          step material=ItemModMachineKit*1 exit=ItemWrench air=no

        piece ModBigMachine kit=ItemModBigKit option=0 grid=large support=lattice.support.frame-below
          step material=ItemModBigKit*1 exit=ItemWrench air=no

        piece ModFlag kit=ItemModFlagKit option=0 grid=small support=lattice.support.frame-or-ground
          step material=ItemModFlagKit*1 exit=ItemWrench air=no

        piece ModWallLight kit=ItemModLightKit option=0 grid=small support=lattice.support.wall-mount
          step material=ItemModLightKit*1 exit=ItemWrench air=no

        piece ModFuse kit=ItemModFuseKit option=0 grid=small support=lattice.support.on-cable
          step material=ItemModFuseKit*1 exit=ItemWrench air=no
          end 0 0 0 z+ power,data
          end 0 0 0 z- power,data
          cell 0 0 0 device

        piece ModPipeGauge kit=ItemModGaugeKit option=0 grid=small support=lattice.support.on-pipe pipe=gas
          step material=ItemModGaugeKit*1 exit=ItemWrench air=no
          cell 0 0 0 device

        piece ModLiquidGauge kit=ItemModLiquidGaugeKit option=0 grid=small support=lattice.support.on-pipe pipe=liquid
          step material=ItemModLiquidGaugeKit*1 exit=ItemWrench air=no
          cell 0 0 0 device

        piece ModFrameLight kit=ItemModFrameLightKit option=0 grid=small support=lattice.support.wall-mount
          step material=ItemModFrameLightKit*1 exit=ItemWrench air=no

        piece ModFiltration kit=ItemModFilterKit option=0 grid=large support=lattice.support.outside
          step material=ItemModFilterKit*1 exit=ItemWrench air=no

        piece ModShaft kit=ItemModShaftKit option=0 grid=large support=lattice.support.elevator-shaft
          step material=ItemModShaftKit*1 exit=ItemWrench air=no

        piece ModOddity kit=ItemModOddKit option=0 grid=small support=lattice.support.ask-game
          step material=ItemModOddKit*1 exit=ItemWrench air=no

        piece ModRocketPart kit=ItemModRocketKit option=0 grid=large support=lattice.support.rocket
          step material=ItemModRocketKit*1 exit=ItemWrench air=no

        piece ModGadget kit=ItemModGadgetKit option=0 grid=small support=othermod/gadget
          step material=ItemModGadgetKit*1 exit=ItemWrench air=no
        """;

    public static readonly CubeGrid Grid = new();

    public static readonly FakeCatalogue Pieces = Make();

    public static SupportFacts Frame => new(true, true, false, ValueSet<HostRole>.Empty);

    /// <summary>A ghost or structure cell: large cells centre on odd metres (10, 30, ... decimetres).</summary>
    public static Cell Large(int x, int y, int z) => new(10 + 20 * x, 10 + 20 * y, 10 + 20 * z);

    public static TestSupportFacts Facts() =>
        new TestSupportFacts(Pieces)
            .With("StructureFrame", Frame)
            .With("StructureFrameIron", Frame)
            .With("StructureWallIron", SupportFacts.Nothing)
            .With("ModFiltration", SupportFacts.Nothing)
            .With("ModBigMachine", SupportFacts.Nothing)
            .With("ModShaft", SupportFacts.Nothing)
            .With("ModWallLight", SupportFacts.Nothing)
            .With("ModFrameLight", SupportFacts.Nothing with { RequiresFrame = true });

    /// <summary>The turn whose forward (+Z) points the given way.</summary>
    public static int Facing(Direction direction)
    {
        for (var index = 0; index < Turn.Count; index++)
            if (Grid.Apply(TestKitIds.Must(Turn.Create(index)), Direction.ZPlus) == direction)
                return index;
        throw new InvalidOperationException($"No turn faces {direction}.");
    }

    /// <summary>Support rules on a clock that never moves: no call times out unless the test moves the clock.</summary>
    public static SupportRules Rules(FakeClock? clock = null) => new(SupportRules.DefaultLimit, clock ?? new FakeClock());

    public static Scenario Scenario() => global::Lattice.TestKit.Scenarios.Scenario.With(Pieces);

    /// <summary>Orders and judges the scenario's ghosts.</summary>
    public static OrderOutcome Order(
        BuiltScenario built,
        GameChecks? checks = null,
        SupportRules? rules = null,
        ISupportFacts? facts = null)
    {
        var geometry = new SupportGeometry(Grid, facts ?? Facts(), built.Catalogue);
        var judge = new SupportJudge(rules ?? Rules(), geometry);
        return new BuildOrder(built.World, built.Catalogue, Grid, judge, checks ?? GameChecks.None).Plan(built.Ghosts);
    }

    public static OrderOutcome Order(ValueList<Ghost> ghosts, BuiltScenario built, GameChecks? checks = null, SupportRules? rules = null) =>
        Order(built with { Ghosts = ghosts }, checks, rules);

    public static OrderReport Ready(OrderOutcome outcome) =>
        outcome is OrderOutcome.Ready ready ? ready.Report : throw new InvalidOperationException($"Not ready: {outcome}");

    /// <summary>The single non-warning finding for a ghost.</summary>
    public static Finding Verdict(OrderReport report, GhostId ghost) =>
        report.Findings.Single(f => f.Ghost == ghost && f is not Finding.Warns);

    public static List<Reason> Warnings(OrderReport report, GhostId ghost) =>
        report.Findings.OfType<Finding.Warns>().Where(f => f.Ghost == ghost).Select(f => f.Why).ToList();

    public static GameChecks Answers(params (Placement At, GameAnswer Answer)[] answers) =>
        new(answers.ToDictionary(a => a.At, a => a.Answer));

    public static Placement PlacementOf(BuiltScenario built, GhostId ghost) =>
        built.Ghosts.Single(g => g.Id == ghost).Intent switch
        {
            Intent.Build build => build.Target,
            Intent.Swap swap => swap.New,
            var other => throw new InvalidOperationException($"No placement in {other}"),
        };

    public static void AssertNeedsSupport(Finding finding, RuleId group, string gameString)
    {
        var fails = Assert.IsType<Finding.WillFail>(finding);
        Assert.Equal(ReasonCodes.NeedsSupport, fails.Why.Code);
        Assert.Equal(group, fails.Why.From);
        Assert.Equal(gameString, fails.Why.Facts["game"]);
    }

    public static void AssertStages(OrderReport report, params GhostId[][] stages) =>
        Assert.Equal(stages, report.Stages.Select(stage => stage.ToArray()).ToArray());

    public static void AssertWaitsFor(Finding finding, params GhostId[] others)
    {
        var waits = Assert.IsType<Finding.WaitsFor>(finding);
        Assert.Equal(others.OrderBy(o => o.Value), waits.Others.OrderBy(o => o.Value));
    }

    private static FakeCatalogue Make()
    {
        var modded = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(ModdedText), "support.lcat"));
        return new FakeCatalogue([.. FakeCatalogue.Hand().Pieces, .. modded.Pieces]);
    }
}
