using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Tests.Selectors;
using static Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Transforms.TransformBench;

namespace Lattice.Tests.Transforms;

/// <summary>Keeping and placing templates (a plan in local coordinates, placed as ordinary unlinked ghosts).</summary>
public sealed class TemplateTests
{
    private static readonly TemplateName Pump = TestSelect.Ok(TemplateName.Create("pump"));

    [Fact]
    public void A_placed_templates_ghosts_have_no_link_back_to_it()
    {
        var b = new TransformBench();
        var template = new Template(Pump, ValueList.From(new[]
        {
            b.T.Draft("StructureFrame", new Cell(0, 0, 0)),
            b.T.Draft("StructureCableStraight", new Cell(5, 0, -5)),
        }));

        var first = Applied(b.Run(new Command.PlaceTemplate(template, new Cell(10, 10, 10), Turn.None, NewPlan("First"))));
        var second = Applied(b.Run(new Command.PlaceTemplate(template, new Cell(50, 10, 10), Turn.None, NewPlan("Second"))));

        var plan = Assert.Single(first.PlansOpened).Id;
        Assert.All(first.Added, ghost => Assert.Equal(plan, ghost.Plan));
        Assert.All(first.Added.Concat(second.Added), ghost =>
        {
            Assert.Empty(ghost.Attached);
            Assert.Empty(ghost.Groups);
            Assert.False(ghost.Label.IsSome);
        });
        Applied(b.T.Apply(new Command.Remove(new Selector.InPlan(plan))));
        Assert.Equal(second.Added.Select(static g => g.Id).ToHashSet(), b.T.Board.Index.Ghosts.Keys.ToHashSet());
    }

    [Fact]
    public void A_template_is_placed_turned_about_its_origin()
    {
        var b = new TransformBench();
        var corner = b.T.Catalogue.Named("StructureCableCorner").Key;
        var template = new Template(Pump, ValueList.Of(Draft(new Intent.Build(new Placement(corner, new Cell(20, 0, 5), Turn.None), Option<Finish>.None))));
        var turn = b.Sending(Direction.XPlus, Direction.ZMinus);

        var placed = Applied(b.Run(new Command.PlaceTemplate(template, new Cell(10, 10, 10), turn, NewPlan("Turned"))));

        var target = ((Intent.Build)Assert.Single(placed.Added).Intent).Target;
        Assert.Equal(new Placement(corner, new Cell(10, 10, 10).Plus(b.T.Grid.Apply(turn, new CellOffset(20, 0, 5))), turn), target);
    }

    [Fact]
    public void A_plan_kept_as_a_template_places_back_onto_the_same_cells()
    {
        var b = new TransformBench();
        var plan = Applied(b.T.Apply(new Command.CreatePlan(NewPlan("Room")))).PlansOpened[0].Id;
        b.Build("StructureFrame", new Cell(30, 10, 10), plan: Option.Some(plan));
        b.Build("StructureCableCorner", new Cell(35, 15, 5), b.Sending(Direction.XMinus, Direction.ZPlus), Option.Some(plan));
        var kept = b.Placed();

        var template = TestSelect.Ok(b.Plans.AsTemplate(plan, Pump));
        Applied(b.T.Apply(new Command.Remove(new Selector.InPlan(plan))));
        Applied(b.Run(new Command.PlaceTemplate(template, new Cell(30, 10, -10), Turn.None, NewPlan("Again"))));

        Assert.Equal(2, template.Ghosts.Count);
        Assert.All(template.Ghosts, static d => Assert.False(d.Plan.IsSome));
        Assert.Equal(kept, b.Placed());
    }

    [Fact]
    public void A_template_keeps_swaps_as_builds_of_their_new_piece_and_leaves_removals_out()
    {
        var b = new TransformBench();
        var frame = b.T.Catalogue.Named("StructureFrame").Key;
        var iron = b.T.Catalogue.Named("StructureFrameIron").Key;
        var standing = new StructureRef(new ReferenceId(5), frame, new Cell(10, 10, 10));
        b.T.Placed(
            Draft(new Intent.Swap(standing, new Placement(iron, new Cell(10, 10, 10), Turn.None), Option<Finish>.None)),
            Draft(new Intent.Remove(new StructureRef(new ReferenceId(6), frame, new Cell(30, 10, 10)))));

        var template = TestSelect.Ok(b.Plans.AsTemplate(b.Plans.Default, Pump));

        var only = Assert.Single(template.Ghosts);
        Assert.Equal(new Intent.Build(new Placement(iron, new Cell(0, 0, 0), Turn.None), Option<Finish>.None), only.Intent);
    }

    [Fact]
    public void A_plan_that_is_not_active_is_read_from_saving_which_is_missing_here()
    {
        var b = new TransformBench();

        Assert.True(b.Plans.AsTemplate(new PlanId(99), Pump).TryGetError(out var why));
        Assert.Equal(ReasonCodes.ModuleMissing, why.Code);
    }

    [Fact]
    public void A_template_placed_where_its_pieces_leave_their_grid_is_refused_naming_the_drafts()
    {
        var b = new TransformBench();
        var template = new Template(Pump, ValueList.From(new[]
        {
            b.T.Draft("StructureCableStraight", new Cell(0, 0, 0)),
            b.T.Draft("StructureFrame", new Cell(0, 0, 0)),
        }));

        var why = Refusal(b.Run(new Command.PlaceTemplate(template, new Cell(5, 5, 5), Turn.None, NewPlan("Off"))));

        Assert.Equal("off-grid", why.Facts["problem"]);
        Assert.Equal("1", why.Facts["drafts"]);
        Assert.Empty(b.T.Board.Index.Ghosts);
        Assert.Single(b.T.Board.Index.Plans);
    }

    [Fact]
    public void A_template_draft_about_a_standing_structure_is_refused()
    {
        var b = new TransformBench();
        var frame = b.T.Catalogue.Named("StructureFrame").Key;
        var template = new Template(Pump, ValueList.Of(Draft(new Intent.Remove(new StructureRef(new ReferenceId(6), frame, new Cell(10, 10, 10))))));

        var why = Refusal(b.Run(new Command.PlaceTemplate(template, new Cell(10, 10, 10), Turn.None, NewPlan("Bad"))));

        Assert.Equal("targets-a-structure", why.Facts["problem"]);
        Assert.Equal("0", why.Facts["drafts"]);
    }

    [Fact]
    public void A_placed_template_makes_one_plan_owned_by_the_actor()
    {
        var b = new TransformBench();
        var template = new Template(Pump, ValueList.Of(b.T.Draft("StructureFrame", new Cell(0, 0, 0))));

        var placed = Applied(b.Run(new Command.PlaceTemplate(template, new Cell(10, 10, 10), Turn.None, NewPlan("Mine")), Bob));

        var plan = Assert.Single(placed.PlansOpened);
        Assert.Equal(Bob, plan.Owner);
        Assert.Equal("Mine", plan.Name.Value);
        Assert.Equal(Bob, Assert.Single(placed.Added).PlacedBy);
    }
}
