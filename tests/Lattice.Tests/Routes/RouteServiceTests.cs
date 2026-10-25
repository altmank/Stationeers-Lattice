using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Openings;
using Lattice.Core.Routes;
using Lattice.TestKit.PlacementCheck;
using Lattice.Tests.Commands;
using Xunit;
using static Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Routes;

public sealed class RouteServiceTests
{
    [Fact]
    public void A_route_submitted_on_the_host_is_sent_then_placed_and_answered_once_its_search_finishes()
    {
        var c = new TestCommands();
        var planner = new RoutePlanner(
            c.T.Catalogue, c.T.Grid, new FakePlacementCheck(), new NoOpeningFacts(), new TestFrames(), OpeningBands.Default,
            RouteLimits.Default with { PerStep = 4 });
        Assert.True(c.Expanders.RegisterSliced(new RouteExpander(planner).Descriptor).IsOk);
        var answers = new List<Outcome>();
        c.T.Events.Answered += (_, outcome) => answers.Add(outcome);
        var cable = c.T.Catalogue.Named("StructureCableStraight").Kits[0].Kit;
        var id = c.NextId();

        var submission = c.Service.Submit(
            id, new Command.PlanRoute(new RouteRequest(cable, At(2, 2, 2), At(6, 2, 6), new RouteToggles(false, false, false, false, false), Option<PlanId>.None, TestMaker)), Alice);

        Assert.IsType<Submission.Sent>(submission);
        Assert.Empty(c.T.Board.Index.Ghosts);
        for (var frame = 0; frame < 100 && answers.Count == 0; frame++)
            c.Frame();
        var outcome = Assert.Single(answers);
        Assert.NotEmpty(Applied(outcome).Added);
        Assert.NotEmpty(c.T.Board.Index.Ghosts);
    }
}
