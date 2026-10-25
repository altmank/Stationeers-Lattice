using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Openings;
using Lattice.Core.Routes;
using Lattice.Core.Runs;
using Lattice.Core.Transforms;
using Lattice.TestKit;
using Lattice.TestKit.PlacementCheck;
using Lattice.Tests.Routes;
using static Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Commands;

/// <summary>
/// A batch's expanded steps (runs, routes, templates) each expand against the board as the earlier steps of the same
/// batch leave it: what they lay meets what the earlier steps laid as it would meet ghosts already on the board, and
/// the batch is still one change set. Cables on small cells (<see cref="RunBench.At"/>); the game's straights run along
/// local z.
/// </summary>
public sealed class BatchStagedTests
{
    internal sealed class Bench
    {
        public Bench()
        {
            C = new TestCommands();
            var check = new FakePlacementCheck();
            Assert.True(C.Expanders.Register(new RunExpander(new RunPlanner(T.Catalogue, T.Grid, check)).Descriptor).IsOk);
            var routes = new RoutePlanner(
                T.Catalogue, T.Grid, check, new NoOpeningFacts(), new TestFrames(), OpeningBands.Default, RouteLimits.Default with { PerStep = 4 });
            Assert.True(C.Expanders.RegisterSliced(new RouteExpander(routes).Descriptor).IsOk);
            var placements = new Placements(T.Catalogue, T.Grid, new CatalogueAdditions(T.Catalogue));
            Assert.True(C.Expanders.RegisterSliced(new TemplateExpander(placements).Descriptor).IsOk);
            T.Events.Answered += (_, outcome) => Answers.Add(outcome);
        }

        public TestCommands C { get; }

        public Board.TestBoard T => C.T;

        public List<Outcome> Answers { get; } = new();

        public KitKey Cable => T.Catalogue.Named("StructureCableStraight").Kits[0].Kit;

        public Command.PlanRun Run(Cell from, Cell to) =>
            new(new RunRequest(Cable, from, ValueList<Cell>.Empty, to, Option<PlanId>.None, TestMaker));

        public Command.PlanRoute Route(Cell from, Cell to) =>
            new(new RouteRequest(Cable, from, to, new RouteToggles(false, false, false, false, false), Option<PlanId>.None, TestMaker));

        /// <summary>Submits the command and runs frames until it is answered; its outcome.</summary>
        public Outcome Submit(Command command)
        {
            var answered = Answers.Count;
            if (C.Service.Submit(C.NextId(), command, Alice) is Submission.Answered now)
                return now.Outcome;
            for (var frame = 0; frame < 1000 && Answers.Count == answered; frame++)
                C.Frame();
            Assert.True(Answers.Count > answered, "not answered within the frames given");
            return Answers[answered];
        }

        public string Piece(Cell at) => T.Catalogue.NameOf(((Intent.Build)Assert.Single(T.View.At(at)).Intent).Target.Piece);

        /// <summary>Every ghost's placement on the board, in a fixed order.</summary>
        public List<Placement> Placements() =>
            T.Board.Index.Ghosts.Values.Select(e => ((Intent.Build)e.Ghost.Intent).Target).OrderBy(p => p.At.X).ThenBy(p => p.At.Y).ThenBy(p => p.At.Z).ToList();
    }

    private static Command.Batch Batch(params Command[] steps) => new(ValueList.From(steps));

    private static readonly Cell TrunkFrom = At(0, 0, 0);
    private static readonly Cell TrunkTo = At(6, 0, 0);
    private static readonly Cell FirstJoin = At(2, 0, 0);
    private static readonly Cell SecondJoin = At(4, 0, 0);

    private static Command[] TrunkAndBranches(Bench b) =>
    [
        b.Run(TrunkFrom, TrunkTo),
        b.Run(FirstJoin, At(2, 0, 3)),
        b.Run(SecondJoin, At(4, 0, -3)),
    ];

    [Fact]
    public void A_trunk_and_two_branches_in_one_batch_make_two_tees_in_one_change_set_that_revert_undoes()
    {
        var b = new Bench();
        var revision = b.T.Board.Revision.Value;

        var placed = Applied(b.Submit(Batch(TrunkAndBranches(b))));

        Assert.Equal("StructureCableJunction", b.Piece(FirstJoin));
        Assert.Equal("StructureCableJunction", b.Piece(SecondJoin));
        Assert.Equal(revision + 1, b.T.Board.Revision.Value);
        Assert.Equal(placed.Added.Count, b.T.Board.Index.Ghosts.Count);

        Applied(b.Submit(new Command.Revert(placed.Id)));

        Assert.Empty(b.T.Board.Index.Ghosts);
    }

    [Fact]
    public void A_trunk_and_two_branches_in_one_batch_lay_what_they_lay_one_command_after_another()
    {
        var batched = new Bench();
        var oneByOne = new Bench();

        Applied(batched.Submit(Batch(TrunkAndBranches(batched))));
        foreach (var step in TrunkAndBranches(oneByOne))
            Applied(oneByOne.Submit(step));

        Assert.Equal(oneByOne.Placements(), batched.Placements());
    }

    [Fact]
    public void A_run_after_a_template_in_one_batch_crosses_the_templates_cable_as_a_junction()
    {
        var b = new Bench();
        var crossing = At(2, 0, 0);
        var template = new Template(
            TestKitIds.Must(TemplateName.Create("crossing")),
            ValueList.Of(b.T.Draft("StructureCableStraight", new Cell(0, 0, 0))));
        var plan = new PlanDraft(TestKitIds.Must(PlanName.Create("Crossing")), TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

        var placed = Applied(b.Submit(Batch(new Command.PlaceTemplate(template, crossing, Turn.None, plan), b.Run(At(0, 0, 0), At(4, 0, 0)))));

        Assert.Equal("StructureCableJunction4", b.Piece(crossing));
        Assert.Single(placed.PlansOpened);
    }

    [Fact]
    public void A_route_after_a_run_in_one_batch_crosses_the_run_as_a_junction_and_the_batch_is_answered_once_found()
    {
        var b = new Bench();
        var crossing = At(4, 2, 2);
        var revision = b.T.Board.Revision.Value;

        var outcome = b.Submit(Batch(b.Run(At(4, 2, -2), At(4, 2, 6)), b.Route(At(0, 2, 2), At(8, 2, 2))));

        Applied(outcome);
        Assert.Equal("StructureCableJunction4", b.Piece(crossing));
        Assert.Equal(revision + 1, b.T.Board.Revision.Value);
    }

    [Fact]
    public void A_run_after_a_route_in_one_batch_waits_for_the_route_and_meets_it()
    {
        var b = new Bench();
        var crossing = At(4, 2, 2);

        Applied(b.Submit(Batch(b.Route(At(0, 2, 2), At(8, 2, 2)), b.Run(At(4, 2, -2), At(4, 2, 6)))));

        Assert.Equal("StructureCableJunction4", b.Piece(crossing));
    }
}
