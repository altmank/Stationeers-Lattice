using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Support;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>A plan checks in build order on the world with earlier ghosts assumed built; the game's own check answers on the built world.</summary>
public sealed class OrderAndGameCheckTests
{
    private const string SideBlocked = "Wall side is blocked";

    [Fact]
    public void A_room_checks_in_order_frames_on_frames_and_wall_mounted_pieces_wait_and_a_blocked_wall_fails()
    {
        // The game has no frame rule for walls; a wall with a frame in the
        // cell in front is refused by the game (Wall.IsSideBlocked), which the built-world check asks.
        var bench = new ValidationBench();
        var standing = bench.Built("StructureFrameIron", Large(5, 0, 0));
        bench.Placement.Refuse(p => p.Piece == Pieces.Named("StructureWallIron").Key, ReasonCodes.GameRefused, SideBlocked);
        bench.Start();
        var floor = bench.Ghost("ModStackFrame", Large(0, 0, 0));
        var upper = bench.Ghost("ModStackFrame", Large(0, 1, 0));
        var lowLight = bench.Ghost("ModFrameLight", new Cell(20, 10, 10), Facing(Direction.XPlus));
        var highLight = bench.Ghost("ModWallLight", new Cell(20, 30, 10), Facing(Direction.XPlus));
        var wall = bench.Ghost("StructureWallIron", Large(5, 0, 1));

        bench.Pump();
        var report = bench.Report();

        Assert.IsType<Finding.Passes>(Verdict(report, floor));
        AssertWaitsFor(Verdict(report, upper), floor);
        AssertWaitsFor(Verdict(report, lowLight), floor);
        AssertWaitsFor(Verdict(report, highLight), upper);
        var blocked = Assert.IsType<Finding.WillFail>(Verdict(report, wall));
        Assert.Equal(ReasonCodes.GameRefused, blocked.Why.Code);
        Assert.Equal(Option.Some(SideBlocked), blocked.Why.GameMessage);
        Assert.Equal(new[] { floor, wall, upper, lowLight, highLight }, report.Findings.Select(f => f.Ghost).Distinct());
        Assert.NotEqual(default, standing);
    }

    [Fact]
    public void The_game_is_asked_only_where_the_neighbourhood_is_real()
    {
        var bench = new ValidationBench().Start();
        var alone = bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        var first = bench.Ghost("StructureFrameIron", Large(4, 0, 0));
        var next = bench.Ghost("ModStackFrame", Large(4, 1, 0));

        bench.Pump();

        var asked = bench.Placement.Calls.Select(p => p.At).ToList();
        Assert.Contains(Large(0, 0, 0), asked);
        Assert.Contains(Large(4, 0, 0), asked);
        Assert.DoesNotContain(Large(4, 1, 0), asked);
        Assert.All(new[] { alone, first }, g => Assert.IsType<Finding.Passes>(Verdict(bench.Report(), g)));
        AssertWaitsFor(Verdict(bench.Report(), next), first);
    }

    [Fact]
    public void A_ghost_next_to_a_planned_one_is_asked_once_that_one_is_built()
    {
        var bench = new ValidationBench().Start();
        var lower = bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.Ghost("StructureFrameIron", Large(0, 1, 0));
        bench.Pump();
        Assert.DoesNotContain(Large(0, 1, 0), bench.Placement.Calls.Select(p => p.At));

        bench.Built("StructureFrameIron", Large(0, 0, 0));
        bench.Apply(new Command.Remove(new Selector.Ids(ValueList.Of(lower))));
        bench.Pump();

        Assert.Contains(Large(0, 1, 0), bench.Placement.Calls.Select(p => p.At));
    }

    [Fact]
    public void A_replacing_support_rule_skips_the_game_check()
    {
        var bench = new ValidationBench();
        bench.SupportRules.Register(new SupportRuleDescriptor(
            Rule("mount"), piece => piece == Pieces.Named("StructureWallIron").Key, _ => new SupportVerdict.SupportedBy(ValueList<StructureRef>.Empty))
        {
            Mode = SupportMode.Replaces,
        });
        bench.Placement.Refuse(_ => true, ReasonCodes.GameRefused, SideBlocked);
        bench.Start();
        var wall = bench.Ghost("StructureWallIron", Large(0, 0, 0));

        bench.Pump();

        Assert.IsType<Finding.Passes>(Verdict(bench.Report(), wall));
        Assert.Empty(bench.Placement.Calls);
    }

    [Fact]
    public void A_modded_piece_of_a_class_lattice_does_not_know_is_refused_by_the_games_own_check()
    {
        var bench = new ValidationBench();
        bench.Placement.Refuse(p => p.Piece == Pieces.Named("ModOddity").Key, ReasonCodes.GameRefused, "No");
        bench.Start();
        var odd = bench.Ghost("ModOddity", new Cell(5, 5, 5));

        bench.Pump();

        var fails = Assert.IsType<Finding.WillFail>(Verdict(bench.Report(), odd));
        Assert.Equal(ReasonCodes.GameRefused, fails.Why.Code);
        Assert.Equal(SupportGroups.AskGame, fails.Why.From);
    }

    [Fact]
    public void Check_answers_pending_until_the_asks_are_answered_then_ready_with_answers_ready_raised()
    {
        var bench = new ValidationBench().Start();
        var raised = new System.Collections.Generic.List<Revision>();
        bench.Events.AnswersReady += raised.Add;
        bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.Events.Flush();

        var first = Lattice.TestKit.TestKitIds.Must(bench.Service.Check(bench.All()));
        bench.Frame();
        var afterRun = Lattice.TestKit.TestKitIds.Must(bench.Service.Check(bench.All()));
        bench.Pump();
        bench.Events.Flush();

        Assert.IsType<Answer<ValidationReport>.Pending>(first);
        Assert.IsType<Answer<ValidationReport>.Pending>(afterRun);
        Assert.Contains(bench.Board.Revision, raised);
        Assert.Single(bench.Report().Findings);
    }

    [Fact]
    public void A_selection_lists_only_its_ghosts_in_build_order_and_a_selector_the_board_refuses_is_refused()
    {
        var bench = new ValidationBench().Start();
        var floor = bench.Ghost("ModStackFrame", Large(0, 0, 0));
        var upper = bench.Ghost("ModStackFrame", Large(0, 1, 0));
        bench.Ghost("StructureFrameIron", Large(6, 0, 0));
        bench.Pump();

        var report = bench.Report(new Selector.Ids(ValueList.From(new[] { upper, floor })));
        var refused = bench.Service.Check(new Selector.RunThrough(new Cell(0, 0, 0)));

        Assert.Equal(new[] { floor, upper }, report.Findings.Select(f => f.Ghost));
        Assert.True(refused.IsError);
    }
}
