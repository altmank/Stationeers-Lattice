using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.TestKit;
using Lattice.TestKit.Allocations;
using static Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// <see cref="Paint.PlanColour"/>: a ghost drawn in its plan's colour, the fallback token for a plan with none, a draft
/// and a structure; a plan recoloured or a ghost moved to another plan restyles the plan's ghosts once, with no ghost
/// write; an unchanged board allocates nothing.
/// </summary>
public sealed class PlanColourTests
{
    private static readonly Tint Teal = new(10, 120, 130, 255);
    private static readonly Tint Amber = new(230, 160, 20, 255);
    private static readonly Paint InPlanColour = new Paint.PlanColour(LibraryPalette.Warn);

    private static StyleLayerDescriptor Filled() => LayerOf("plan-fill", StyleBand.Theme, 0,
        Rule(new StyleMatch.Everything(), new Style { FillPaint = Option.Some(InPlanColour), FillOpacity = Option.Some(Fraction.One) }));

    private static Plan OpenPlan(StyleBench bench, string name, Option<Tint> colour) =>
        Assert.Single(Applied(bench.T.Apply(new Command.CreatePlan(new PlanDraft(
            TestKitIds.Must(PlanName.Create(name)), TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty) { Colour = colour }))).PlansOpened);

    private static Ghost PlaceIn(StyleBench bench, Plan plan, int x) =>
        Assert.Single(bench.Place(bench.T.Draft("StructureFrame", new Cell(x, 10, 10), plan: Option.Some(plan.Id))).Added);

    private static (byte, byte, byte) Rgb(Rgba c) => (c.R, c.G, c.B);

    private static (byte, byte, byte) Rgb(Tint t) => (t.R, t.G, t.B);

    private static (byte, byte, byte) Fallback(StyleBench bench) => Rgb(System.Linq.Enumerable.Single(DefaultPalette.Colours, static p => p.Key == LibraryPalette.Warn).Value);

    private static Thing Overlay(StyleBench bench)
    {
        for (var b = 0; b < bench.Styles.Plan.BatchCount; b++)
        {
            var batch = bench.Styles.Plan.BatchAt(b);
            for (var i = 0; i < batch.Count; i++)
                if (batch[i].Thing.Kind == ThingKind.Structure)
                    return batch[i].Thing;
        }

        throw new Xunit.Sdk.XunitException("no overlay drawn");
    }

    [Fact]
    public void A_ghost_is_filled_with_its_plans_colour()
    {
        var bench = new StyleBench();
        bench.Add(Filled());
        var ghost = PlaceIn(bench, OpenPlan(bench, "power", Option.Some(Teal)), 10);
        bench.Settle();

        Assert.Equal(Rgb(Teal), Rgb(bench.Look(ghost.Id).Fill));
    }

    [Fact]
    public void A_plan_with_no_colour_fills_with_the_fallback_token()
    {
        var bench = new StyleBench();
        bench.Add(Filled());
        var ghost = PlaceIn(bench, OpenPlan(bench, "plain", Option<Tint>.None), 10);
        bench.Settle();

        Assert.Equal(Fallback(bench), Rgb(bench.Look(ghost.Id).Fill));
    }

    [Fact]
    public void A_draft_fills_with_the_fallback_token_even_when_it_names_a_coloured_plan()
    {
        var bench = new StyleBench();
        bench.Add(Filled());
        var plan = OpenPlan(bench, "power", Option.Some(Teal));
        var handle = (DraftsHandle)Must(bench.Styles.Drafts(Must(DraftsId.Create(Mod, "cursor"))));
        handle.Show(ValueList.Of(bench.T.Draft("StructureFrame", new Cell(10, 10, 10), plan: Option.Some(plan.Id))));

        var draft = Assert.Single(handle.Shown);
        Assert.Equal(Fallback(bench), Rgb(bench.Styles.Look(draft.Signature).Fill));
    }

    [Fact]
    public void A_structure_overlay_fills_with_the_fallback_token()
    {
        var bench = new StyleBench();
        bench.Add(Filled());
        OpenPlan(bench, "power", Option.Some(Teal));
        var frame = new StructureRef(new ReferenceId(1), bench.T.Catalogue.Named("StructureFrame").Key, new Cell(10, 10, 10));
        Must(bench.Styles.StructureSet(SetId("inspected"))).Put(frame);

        var overlay = Overlay(bench);
        Assert.Equal(Fallback(bench), Rgb(bench.Styles.Look(overlay.Signature).Fill));
    }

    [Fact]
    public void A_modded_piece_in_a_coloured_plan_is_filled_with_the_plans_colour()
    {
        var bench = new StyleBench();
        bench.Add(Filled());
        var plan = OpenPlan(bench, "conduits", Option.Some(Teal));
        var ghost = Assert.Single(bench.Place(bench.T.Draft(Modded, new Cell(10, 10, 10), plan: Option.Some(plan.Id))).Added);
        bench.Settle();

        Assert.Equal(Rgb(Teal), Rgb(bench.Look(ghost.Id).Fill));
    }

    [Fact]
    public void Recolouring_a_plan_restyles_its_ghosts_once_and_leaves_other_plans_and_the_ghosts_alone()
    {
        var bench = new StyleBench();
        bench.Add(Filled());
        var power = OpenPlan(bench, "power", Option.Some(Teal));
        var other = OpenPlan(bench, "other", Option.Some(Teal));
        var a = PlaceIn(bench, power, 10);
        var b = PlaceIn(bench, power, 30);
        var c = PlaceIn(bench, other, 50);
        bench.Settle();
        var before = bench.Styles.Counters.Resignatures;
        var revision = bench.T.Board.Index.Ghosts[a.Id].Ghost.Revision;

        Applied(bench.T.Apply(new Command.EditPlan(power.Id, new PlanEdit.Recolour(Option.Some(Amber)), Option<Revision>.None)));
        bench.Settle();

        Assert.Equal(2, bench.Styles.Counters.Resignatures - before);
        Assert.Equal(Rgb(Amber), Rgb(bench.Look(a.Id).Fill));
        Assert.Equal(Rgb(Amber), Rgb(bench.Look(b.Id).Fill));
        Assert.Equal(Rgb(Teal), Rgb(bench.Look(c.Id).Fill));
        Assert.Equal(revision, bench.T.Board.Index.Ghosts[a.Id].Ghost.Revision);
    }

    [Fact]
    public void Clearing_a_plans_colour_restyles_its_ghosts_to_the_fallback()
    {
        var bench = new StyleBench();
        bench.Add(Filled());
        var power = OpenPlan(bench, "power", Option.Some(Teal));
        var ghost = PlaceIn(bench, power, 10);
        bench.Settle();

        Applied(bench.T.Apply(new Command.EditPlan(power.Id, new PlanEdit.Recolour(Option<Tint>.None), Option<Revision>.None)));
        bench.Settle();

        Assert.Equal(Fallback(bench), Rgb(bench.Look(ghost.Id).Fill));
    }

    [Fact]
    public void Moving_a_ghost_to_another_plan_restyles_it_once_in_the_new_plans_colour()
    {
        var bench = new StyleBench();
        bench.Add(Filled());
        var power = OpenPlan(bench, "power", Option.Some(Teal));
        var water = OpenPlan(bench, "water", Option.Some(Amber));
        var ghost = PlaceIn(bench, power, 10);
        PlaceIn(bench, power, 30);
        bench.Settle();
        var before = bench.Styles.Counters.Resignatures;

        Applied(bench.T.Apply(new Command.MoveToPlan(Ids(ghost.Id), water.Id)));
        bench.Settle();

        Assert.Equal(1, bench.Styles.Counters.Resignatures - before);
        Assert.Equal(Rgb(Amber), Rgb(bench.Look(ghost.Id).Fill));
    }

    [Fact]
    public void Without_a_plan_colour_rule_a_recolour_restyles_nothing()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("edges", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Edge(LineStyle.Solid, LibraryPalette.Fail))));
        var power = OpenPlan(bench, "power", Option.Some(Teal));
        PlaceIn(bench, power, 10);
        bench.Settle();
        var before = bench.Styles.Counters.Resignatures;

        Applied(bench.T.Apply(new Command.EditPlan(power.Id, new PlanEdit.Recolour(Option.Some(Amber)), Option<Revision>.None)));
        bench.Settle();

        Assert.Equal(0, bench.Styles.Counters.Resignatures - before);
    }

    [Fact]
    public void An_under_line_in_plan_colour_carries_the_plan_colour_too()
    {
        var bench = new StyleBench();
        var line = new EdgeLook.Line(LineStyle.Dashed, Must(Pixels.Create(3f)), new Paint.Token(LibraryPalette.Select))
        {
            Under = Option.Some(new UnderLine(Must(Pixels.Create(5f)), InPlanColour)),
        };
        bench.Add(LayerOf("ants", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), new Style { Edge = Option.Some<EdgeLook>(line) })));
        var ghost = PlaceIn(bench, OpenPlan(bench, "power", Option.Some(Teal)), 10);
        bench.Settle();

        Assert.Equal(Rgb(Teal), Rgb(bench.Look(ghost.Id).Under));
    }

    [Fact]
    public void Frames_on_an_unchanged_board_with_plan_colours_allocate_nothing()
    {
        var bench = new StyleBench();
        bench.Add(Filled());
        var power = OpenPlan(bench, "power", Option.Some(Teal));
        for (var i = 0; i < 20; i++)
            PlaceIn(bench, power, 10 + 20 * i);
        bench.Settle();

        Allocations.AssertNone(bench.Frame, "a frame on an unchanged board");
    }
}
