using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Support;
using static Lattice.Tests.Support.SupportBench;

namespace Lattice.Tests.Support;

/// <summary>One build order across all plans: waits across plans, removals first in a spot, one pipe join per stage, cycles.</summary>
public sealed class OrderTests
{
    private static readonly Cell OnTopOfOrigin = new(10, 20, 10);
    private static readonly Cell OnFace = new(20, 10, 10);

    [Fact]
    public void A_ghost_in_plan_b_on_plan_as_unbuilt_frame_waits_for_it()
    {
        // The game has no support rule for walls (a wall needs no
        // frame), so plan B holds the piece that does rest on a frame: a wall-mounted light.
        var built = Scenario()
            .Ghost("ModWallLight", OnFace, out var light, Facing(Direction.XPlus))
            .Ghost("StructureFrameIron", Large(0, 0, 0), out var frame)
            .Build();
        var planB = new PlanId(2);
        var ghosts = ValueList.From(built.Ghosts.Select(g => g.Id == light ? g with { Plan = planB } : g));

        var report = Ready(Order(ghosts, built));

        AssertWaitsFor(Verdict(report, light), frame);
        Assert.IsType<Finding.Passes>(Verdict(report, frame));
        AssertStages(report, [frame], [light]);
    }

    [Fact]
    public void A_build_in_a_spot_a_removal_clears_waits_for_the_removal()
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0), out var old)
            .Ghost("StructureFrame", Large(0, 0, 0), out var frame)
            .Removal(old, out var removal)
            .Build();

        var report = Ready(Order(built));

        AssertWaitsFor(Verdict(report, frame), removal);
        Assert.IsType<Finding.Passes>(Verdict(report, removal));
        AssertStages(report, [removal], [frame]);
    }

    [Fact]
    public void A_removal_of_what_a_ghost_rests_on_comes_after_it_and_warns()
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0), out var frame)
            .Removal(frame, out var removal)
            .Ghost("ModFloorMachine", OnTopOfOrigin, out var machine)
            .Build();

        var report = Ready(Order(built));

        Assert.IsType<Finding.Passes>(Verdict(report, machine));
        Assert.IsType<Finding.Passes>(Verdict(report, removal));
        var warning = Assert.Single(Warnings(report, removal));
        Assert.Equal(ReasonCodes.NeedsSupport, warning.Code);
        Assert.Equal(machine.Value.ToString(), warning.Facts["leaves-unsupported"]);
        AssertStages(report, [machine], [removal]);
    }

    [Fact]
    public void Two_pipe_joins_that_could_go_together_are_ordered_into_two_stages_with_no_wait()
    {
        // Four separate built pipes, each its own network; each ghost pipe links two of them.
        var built = Scenario()
            .Built("StructurePipeStraight", new Cell(5, 5, 0))
            .Built("StructurePipeStraight", new Cell(5, 5, 10))
            .Built("StructurePipeStraight", new Cell(50, 5, 0))
            .Built("StructurePipeStraight", new Cell(50, 5, 10))
            .Ghost("StructurePipeStraight", new Cell(5, 5, 5), out var first)
            .Ghost("StructurePipeStraight", new Cell(50, 5, 5), out var second)
            .Build();

        var report = Ready(Order(built));

        AssertStages(report, [first], [second]);
        Assert.IsType<Finding.Passes>(Verdict(report, first));
        Assert.IsType<Finding.Passes>(Verdict(report, second));
        Assert.Equal(ValueSet.From([first, second]), report.PipeJoins);
    }

    [Fact]
    public void Pipe_ghosts_that_only_extend_one_network_share_a_stage()
    {
        var built = Scenario()
            .Built("StructurePipeStraight", new Cell(5, 5, 0))
            .Ghost("StructurePipeStraight", new Cell(5, 5, 5), out var first)
            .Ghost("StructurePipeStraight", new Cell(50, 5, 5), out var elsewhere)
            .Build();

        var report = Ready(Order(built));

        AssertStages(report, [first, elsewhere]);
        Assert.Empty(report.PipeJoins);
    }

    [Fact]
    public void Ghosts_that_wait_round_a_loop_all_fail_with_the_ghosts_in_it()
    {
        // Two swaps, each building in the spot the other clears.
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0), out var left)
            .Built("StructureFrameIron", Large(1, 0, 0), out var right)
            .Build();
        var frame = Pieces.Named("StructureFrame").Key;
        var ghosts = ValueList.From(new[]
        {
            Swap(1, left, new Placement(frame, right.At, Turn.None)),
            Swap(2, right, new Placement(frame, left.At, Turn.None)),
        });

        var report = Ready(Order(ghosts, built));

        foreach (var ghost in ghosts)
        {
            var fails = Assert.IsType<Finding.WillFail>(Verdict(report, ghost.Id));
            Assert.Equal(ReasonCodes.Cycle, fails.Why.Code);
            Assert.Equal("1,2", fails.Why.Facts["ghosts"]);
        }

        Assert.Empty(report.Stages);
    }

    [Fact]
    public void A_stale_ghost_is_left_out_and_a_finish_only_ghost_passes()
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0), out var frame)
            .Ghost("ModFloorMachine", OnTopOfOrigin, out var stale)
            .Build();
        var colour = Lattice.TestKit.TestKitIds.Must(PaintColour.Create(3));
        var finish = Lattice.TestKit.TestKitIds.Must(Finish.Create(Option.Some(colour), Option<ThingName>.None, Option<Setting>.None));
        var ghosts = ValueList.From(new[]
        {
            built.Ghosts[0] with { State = new GhostState.Stale(Reason.Of(ReasonCodes.Different, SupportCodes.Order)) },
            built.Ghosts[0] with { Id = new GhostId(9), Intent = new Intent.FinishOnly(frame, finish) },
        });

        var report = Ready(Order(ghosts, built));

        Assert.DoesNotContain(report.Findings, f => f.Ghost == stale);
        Assert.IsType<Finding.Passes>(Verdict(report, new GhostId(9)));
    }

    private static Ghost Swap(long id, StructureRef old, Placement put)
    {
        var shape = Scenario().Ghost("StructureFrame", put.At).Build().Ghosts[0];
        return shape with { Id = new GhostId(id), Intent = new Intent.Swap(old, put, Option<Finish>.None) };
    }
}
