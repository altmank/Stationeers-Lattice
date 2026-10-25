using System;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Slices;
using Lattice.Core.Transforms;
using Lattice.Tests.Selectors;
using static Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Transforms.TransformBench;

namespace Lattice.Tests.Transforms;

/// <summary>
/// <see cref="Command.PlaceTemplateIn"/>: a template's ghosts placed into a plan that is already there, as one change set
/// that opens no plan and reverts like a place. Small cells are keys in multiples of 5; large cells centre on keys 10
/// mod 20.
/// </summary>
public sealed class TemplateInTests
{
    private static readonly TemplateName Pump = TestSelect.Ok(TemplateName.Create("pump"));
    private static readonly Cell Origin = new(10, 10, 10);

    private static Template TwoPieces(TransformBench b) => new(Pump, ValueList.From(new[]
    {
        b.T.Draft("StructureFrame", new Cell(0, 0, 0)),
        b.T.Draft("StructureCableStraight", new Cell(5, 0, -5)),
    }));

    private static PlanId Plan(TransformBench b, string name) => Assert.Single(Applied(b.Run(new Command.CreatePlan(NewPlan(name)))).PlansOpened).Id;

    private static Command.PlaceTemplateIn In(Template template, Option<PlanId> into) => new(template, Origin, Turn.None, into);

    [Fact]
    public void A_template_placed_into_a_plan_adds_its_ghosts_there_in_one_change_set_that_opens_no_plan()
    {
        var b = new TransformBench();
        var plan = Plan(b, "Engine room");
        var revision = b.T.Board.Revision.Value;

        var placed = Applied(b.Run(In(TwoPieces(b), Option.Some(plan))));

        Assert.Equal(2, placed.Added.Count);
        Assert.All(placed.Added, g => Assert.Equal(plan, g.Plan));
        Assert.Empty(placed.PlansOpened);
        Assert.Equal(revision + 1, b.T.Board.Revision.Value);
        Assert.Contains(new Placement(b.T.Catalogue.Named("StructureFrame").Key, Origin, Turn.None), b.Placed());
    }

    [Fact]
    public void Revert_removes_the_placed_ghosts_and_brings_back_a_ghost_they_replaced()
    {
        var b = new TransformBench();
        var plan = Plan(b, "Engine room");
        var standing = b.Build("StructureFrame", Origin);

        var placed = Applied(b.Run(In(TwoPieces(b), Option.Some(plan))));
        Assert.Equal(standing, Assert.Single(placed.Removed).Ghost.Id);

        Applied(b.Run(new Command.Revert(placed.Id)));

        Assert.Equal(standing, Assert.Single(b.T.Board.Index.Ghosts.Keys));
    }

    [Fact]
    public void With_no_plan_named_the_ghosts_go_into_the_default_plan()
    {
        var b = new TransformBench();

        var placed = Applied(b.Run(In(TwoPieces(b), Option<PlanId>.None)));

        Assert.All(placed.Added, g => Assert.Equal(GhostBoard.DefaultPlan, g.Plan));
        Assert.Empty(placed.PlansOpened);
    }

    [Fact]
    public void A_plan_the_board_does_not_hold_is_not_found()
    {
        var b = new TransformBench();

        var why = Refusal(b.Run(In(TwoPieces(b), Option.Some(new PlanId(999)))));

        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal("999", why.Facts["plan"]);
        Assert.Empty(b.T.Board.Index.Ghosts);
    }

    [Fact]
    public void A_plan_the_actor_may_not_see_is_not_found_as_one_the_board_does_not_hold()
    {
        var b = new TransformBench();
        var hidden = Plan(b, "Hidden");
        Assert.True(b.T.Policies.Install(new AccessPolicy((_, _) => true, (viewer, p) => p.Id != hidden || viewer == Bob, _ => Result<Unit, Reason>.Ok(Unit.Value))).IsOk);

        var why = Refusal(b.Run(In(TwoPieces(b), Option.Some(hidden))));

        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal(hidden.Value.ToString(), why.Facts["plan"]);
        Assert.Empty(b.T.Board.Index.Ghosts);
    }

    [Fact]
    public void A_draft_off_its_grid_is_refused_naming_the_drafts_as_for_a_new_plan()
    {
        var b = new TransformBench();
        var template = new Template(Pump, ValueList.From(new[]
        {
            b.T.Draft("StructureCableStraight", new Cell(0, 0, 0)),
            b.T.Draft("StructureFrame", new Cell(0, 0, 0)),
        }));

        var why = Refusal(b.Run(new Command.PlaceTemplateIn(template, new Cell(5, 5, 5), Turn.None, Option<PlanId>.None)));

        Assert.Equal("off-grid", why.Facts["problem"]);
        Assert.Equal("1", why.Facts["drafts"]);
    }

    [Fact]
    public void A_draft_about_a_standing_structure_is_refused_naming_the_drafts()
    {
        var b = new TransformBench();
        var frame = b.T.Catalogue.Named("StructureFrame").Key;
        var template = new Template(Pump, ValueList.Of(Draft(new Intent.Remove(new StructureRef(new ReferenceId(6), frame, Origin)))));

        var why = Refusal(b.Run(In(template, Option<PlanId>.None)));

        Assert.Equal("targets-a-structure", why.Facts["problem"]);
        Assert.Equal("0", why.Facts["drafts"]);
    }

    [Fact]
    public void A_draft_of_an_unknown_piece_is_refused_naming_the_drafts_as_for_a_new_plan()
    {
        var b = new TransformBench();
        var unknown = Draft(new Intent.Build(new Placement(new PrefabKey(424242), new Cell(0, 0, 0), Turn.None), Option<Finish>.None));
        var template = new Template(Pump, ValueList.From(new[] { b.T.Draft("StructureFrame", new Cell(0, 0, 0)), unknown }));

        var into = Refusal(b.Run(In(template, Option<PlanId>.None)));
        var asNew = Refusal(b.Run(new Command.PlaceTemplate(template, Origin, Turn.None, NewPlan("New"))));

        Assert.Equal(asNew.Code, into.Code);
        Assert.Equal(asNew.Facts, into.Facts);
        Assert.Equal("1", into.Facts["drafts"]);
    }

    [Fact]
    public void A_placement_past_the_plans_size_cap_is_refused()
    {
        var b = new TransformBench();
        var plan = Plan(b, "Small");
        Assert.True(b.T.Limit.Set(1).IsOk);

        var refused = Refused(b.Run(In(TwoPieces(b), Option.Some(plan))));

        Assert.NotEqual(ReasonCodes.UnknownKind, refused.Why.Code);
        Assert.Empty(b.T.Board.Index.Ghosts);
    }

    [Fact]
    public void In_a_batch_after_a_place_the_template_lands_against_that_steps_ghost()
    {
        var b = new TransformBench();
        var batch = new Command.Batch(ValueList.From(new Command[]
        {
            new Command.Place(ValueList.Of(b.T.Draft("StructureFrameIron", Origin))),
            In(TwoPieces(b), Option<PlanId>.None),
        }));

        var placed = Applied(b.Run(batch));

        Assert.Equal(2, placed.Added.Count);
        var atOrigin = Assert.Single(b.T.Board.Index.Ghosts.Values, e => ((Intent.Build)e.Ghost.Intent).Target.At == Origin);
        Assert.Equal("StructureFrame", b.T.Catalogue.NameOf(((Intent.Build)atOrigin.Ghost.Intent).Target.Piece));
    }

    [Fact]
    public void A_two_thousand_draft_template_is_worked_out_in_slices_and_the_preview_agrees_with_the_change()
    {
        var b = new TransformBench();
        var drafts = Enumerable.Range(0, 2000).Select(i => b.T.Draft("StructureFrame", new Cell(20 * (i % 50), 0, 20 * (i / 50)))).ToArray();
        var command = In(new Template(Pump, ValueList.From(drafts)), Option<PlanId>.None);
        var work = new TemplateInExpander(b.Placements, b.T.Board).Start(
            new ExpansionInput<Command.PlaceTemplateIn>(command, b.C.NextId(), Alice, b.T.View, new Lattice.TestKit.World.FakeWorldQuery(b.C.World)));
        var steps = 1;
        while (work.Step() == StepOutcome.More)
            steps++;

        Answer<Result<Command, ValueList<Refusal>>> expansion;
        while ((expansion = b.C.Service.Expansion(command, Alice)) is Answer<Result<Command, ValueList<Refusal>>>.Pending)
            b.C.Frame();
        var expanded = ((Answer<Result<Command, ValueList<Refusal>>>.Ready)expansion).Value.Match(c => c, why => throw new InvalidOperationException(why.ToString()));
        var place = Assert.IsType<Command.Place>(expanded);
        var applied = Applied(b.Run(command));

        Assert.Equal((2000 + TransformWork.PerStep - 1) / TransformWork.PerStep, steps);
        Assert.True(steps > 1);
        Assert.Equal(2000, applied.Added.Count);
        Assert.Equal(place.Ghosts.Select(g => g.Intent), applied.Added.Select(g => g.Intent));
    }

    [Fact]
    public void A_large_template_is_sent_then_answered_once_its_work_finishes()
    {
        var b = new TransformBench();
        var drafts = Enumerable.Range(0, TransformWork.PerStep * 3 + 1).Select(i => b.T.Draft("StructureFrame", new Cell(20 * (i % 50), 0, 20 * (i / 50)))).ToArray();

        var submission = b.C.Service.Submit(b.C.NextId(), In(new Template(Pump, ValueList.From(drafts)), Option<PlanId>.None), Alice);
        Assert.IsType<Submission.Sent>(submission);
        for (var frame = 0; frame < 100 && b.Answers.Count == 0; frame++)
            b.C.Frame();

        Assert.Equal(drafts.Length, Applied(Assert.Single(b.Answers)).Added.Count);
    }
}
