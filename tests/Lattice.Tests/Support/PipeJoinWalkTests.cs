using System.Linq;
using Lattice.Api;
using Lattice.Core.Support;
using Lattice.TestKit.Scenarios;
using static Lattice.Tests.Support.SupportBench;

namespace Lattice.Tests.Support;

/// <summary>
/// The one-pipe-join-per-stage walk over joins held back stage after stage: a held-back pipe is judged on the networks
/// as the earlier stages left them (merges followed, pieces put beside it counted), on lines long enough that a walk
/// re-reading every held-back pipe each stage would not finish in a test.
/// </summary>
public sealed class PipeJoinWalkTests
{
    [Fact]
    public void A_line_of_pipe_joins_goes_one_join_a_stage_through_the_merges_before_it()
    {
        // Built pipes 1 m apart, each its own network; each ghost pipe between two of them joins them.
        var (built, ghosts) = Line(40);

        var report = Ready(Order(built));

        Assert.Equal(ghosts.Select(ghost => new[] { ghost }).ToArray(), report.Stages.Select(stage => stage.ToArray()).ToArray());
        Assert.All(ghosts, ghost => Assert.IsType<Finding.Passes>(Verdict(report, ghost)));
        Assert.Equal(ValueSet.From(ghosts), report.PipeJoins);
    }

    [Fact]
    public void A_pipe_that_a_put_beside_it_turns_into_a_join_takes_a_stage_of_its_own()
    {
        var built = Scenario()
            .Built("StructurePipeStraight", new Cell(5, 5, 0))
            .Built("StructurePipeStraight", new Cell(5, 5, 10))
            .Built("StructurePipeStraight", new Cell(5, 5, 25))
            .Ghost("StructurePipeStraight", new Cell(5, 5, 5), out var join)
            .Ghost("StructurePipeStraight", new Cell(5, 5, 15), out var extends)
            .Ghost("StructurePipeStraight", new Cell(5, 5, 20), out var between)
            .Build();

        var report = Ready(Order(built));

        AssertStages(report, [join, extends], [between]);
        Assert.Equal(ValueSet.From([join, between]), report.PipeJoins);
    }

    [Fact]
    public void Two_thousand_pipe_joins_in_a_line_are_ordered_into_two_thousand_stages()
    {
        var (built, ghosts) = Line(2000);

        var report = Ready(Order(built));

        Assert.Equal(ghosts.Length, report.Stages.Count);
        Assert.Equal(ghosts.Length, report.PipeJoins.Count);
    }

    private static (BuiltScenario Built, GhostId[] Ghosts) Line(int joins)
    {
        var scenario = Scenario();
        for (var i = 0; i <= joins; i++)
            scenario = scenario.Built("StructurePipeStraight", new Cell(5, 5, 10 * i));
        var ghosts = new GhostId[joins];
        for (var i = 0; i < joins; i++)
        {
            scenario = scenario.Ghost("StructurePipeStraight", new Cell(5, 5, 10 * i + 5), out var ghost);
            ghosts[i] = ghost;
        }

        return (scenario.Build(), ghosts);
    }
}
