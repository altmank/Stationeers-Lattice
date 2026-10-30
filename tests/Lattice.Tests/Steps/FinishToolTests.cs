using System.Linq;
using Lattice.Api;
using Lattice.Tests.Board;
using Lattice.Tests.Catalogue;
using Lattice.Tests.Matching;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Steps.StepsBench;

namespace Lattice.Tests.Steps;

/// <summary>
/// Paint, name and setting steps name what the act holds. Paint: the colour's spray can in the active hand, which the
/// act uses up part of (ISprayer.DoSpray, SprayCan.OnUseItem); name and setting: a labeller, on and charged, nothing used
/// up (Labeller.Rename, Labeller.Set). The bill counts whole cans for the paint acts and the labeller as a tool. The hand
/// catalogue's cans (one per colour, 10 acts each) are test values; the game's labeller is ItemLabeller.
/// </summary>
public sealed class FinishToolTests
{
    private static readonly PaintColour Three = TestKitIds.Must(PaintColour.Create(3));
    private static readonly PaintColour Four = TestKitIds.Must(PaintColour.Create(4));
    private static readonly ThingName Name = TestKitIds.Must(ThingName.Create("Main bus"));
    private static readonly Setting Two = TestKitIds.Must(Setting.Create(2));

    private static Finish FinishOf(Option<PaintColour> colour = default, Option<ThingName> name = default, Option<Setting> setting = default) =>
        TestKitIds.Must(Finish.Create(colour, name, setting));

    private static Ghost Painted(StepsBench bench, int index, PaintColour colour) =>
        bench.W.Build("StructureCableStraight", TestWorld.Small with { X = TestWorld.Small.X + 5 * index }, finish: Option.Some(FinishOf(Option.Some(colour))));

    private static StepsBench Bare()
    {
        var hand = FakeCatalogue.Hand();
        return Over(new FakeCatalogue(hand.Pieces, hand.BrokenToolNames));
    }

    [Fact]
    public void A_paint_step_names_the_colours_can_and_the_acts_a_full_can_gives()
    {
        var bench = new StepsBench();
        var ghost = Painted(bench, 0, Three);
        bench.Run();

        var paint = Assert.IsType<Step.Paint>(bench.Steps(ghost.Id).Steps[^1]);

        Assert.Equal(new Step.Paint(PlacedBy(0), Three, Item("TestSprayCan3"), 10), paint);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(21, 3)]
    public void A_bill_counts_whole_cans_for_its_paint_acts(int acts, int cans)
    {
        var bench = new StepsBench();
        var ghosts = Enumerable.Range(0, acts).Select(i => Painted(bench, i, Three).Id).ToArray();
        bench.Run();

        var bill = bench.Bill(TestBoard.Ids(ghosts));

        Assert.Contains(Of("TestSprayCan3", cans), bill.Needed);
        Assert.Contains(Of("ItemCableCoil", acts), bill.Needed);
        Assert.DoesNotContain(Item("TestSprayCan3"), bill.Tools);
    }

    [Fact]
    public void Two_colours_take_a_can_each()
    {
        var bench = new StepsBench();
        var red = Painted(bench, 0, Three);
        var blue = Painted(bench, 1, Four);
        bench.Run();

        var bill = bench.Bill(TestBoard.Ids(red.Id, blue.Id));

        Assert.Contains(Of("TestSprayCan3", 1), bill.Needed);
        Assert.Contains(Of("TestSprayCan4", 1), bill.Needed);
    }

    [Fact]
    public void Paint_with_no_can_of_the_colour_is_refused_no_tool()
    {
        var bench = Bare();
        var ghost = Painted(bench, 0, Three);
        bench.Run();

        var why = bench.Refusal(ghost.Id);

        Assert.Equal(ReasonCodes.NoTool, why.Code);
        Assert.Equal("paint", why.Facts["way"]);
        Assert.Equal("3", why.Facts["colour"]);
    }

    [Fact]
    public void Name_and_setting_steps_name_the_labeller_and_the_bill_has_it_as_a_tool()
    {
        var bench = new StepsBench();
        var target = bench.W.Placement("StructureCableStraight", TestWorld.Small);
        var ghost = bench.W.Place(new Intent.Build(target, Option.Some(FinishOf(name: Option.Some(Name), setting: Option.Some(Two)))));
        bench.Run();

        var steps = bench.Steps(ghost.Id).Steps;
        var bill = bench.Bill(TestBoard.Ids(ghost.Id));

        Assert.Equal(new Step.Rename(PlacedBy(0), Name, Item("ItemLabeller")), steps[^2]);
        Assert.Equal(new Step.SetValue(PlacedBy(0), Two, Item("ItemLabeller")), steps[^1]);
        Assert.Contains(Item("ItemLabeller"), bill.Tools);
        Assert.DoesNotContain(bill.Needed, quantity => quantity.Item == Item("ItemLabeller"));
    }

    [Fact]
    public void A_name_on_a_standing_structure_names_the_labeller()
    {
        var bench = new StepsBench();
        var built = bench.W.Structure("StructureCableStraight", TestWorld.Small);
        bench.W.World.Load(built);
        var ghost = bench.W.Place(new Intent.FinishOnly(built.Ref, FinishOf(name: Option.Some(Name))));
        bench.Run();

        Assert.Equal(ValueList.From<Step>([new Step.Rename(Standing(built.Ref), Name, Item("ItemLabeller"))]), bench.Steps(ghost.Id).Steps);
        Assert.Equal(ValueSet.Of(Item("ItemLabeller")), bench.Bill(TestBoard.Ids(ghost.Id)).Tools);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_name_or_setting_with_no_labeller_is_refused_no_tool(bool name)
    {
        var bench = Bare();
        var finish = name ? FinishOf(name: Option.Some(Name)) : FinishOf(setting: Option.Some(Two));
        var ghost = bench.W.Place(new Intent.Build(bench.W.Placement("StructureCableStraight", TestWorld.Small), Option.Some(finish)));
        bench.Run();

        var why = bench.Refusal(ghost.Id);

        Assert.Equal(ReasonCodes.NoTool, why.Code);
        Assert.Equal("label", why.Facts["way"]);
    }

    [GameCatalogueFact]
    public void The_game_catalogue_holds_a_can_for_every_paint_colour_and_the_labeller()
    {
        var catalogue = GameCatalogue.Load();

        Assert.Contains(Item("ItemLabeller"), catalogue.Labellers);
        Assert.Equal(16, catalogue.PaintCans.Count);
        Assert.Equal(catalogue.PaintCans.Count, catalogue.PaintCans.Select(static can => can.Colour).Distinct().Count());
        Assert.All(catalogue.PaintCans, static can => Assert.True(can.ActsPerCan >= 1));
        Assert.Contains(catalogue.PaintCans, static can => can.Can == Item("ItemSprayCanRed"));
    }
}
