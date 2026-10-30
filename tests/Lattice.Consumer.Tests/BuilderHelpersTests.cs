using Lattice.Api;
using Lattice.Testing.Stubs;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A builder against the contract alone: where to put a kit down, why it waits in Lattice's words, the name of the
/// item it lacks from a stubbed catalogue, and what to have in hand for a merge, a paint, a name or a setting.
/// </summary>
public sealed class BuilderHelpersTests
{
    private static readonly RuleId Orb = Must(RuleId.Create(Must(ModId.Create("builder")), "orb"));

    private sealed class Named : StubPieceCatalogue
    {
        public override Result<string, Reason> ItemName(ItemKey item) =>
            item.Hash == 11
                ? Result<string, Reason>.Ok("Iron Ingot")
                : Result<string, Reason>.Error(Reason.Of(ReasonCodes.NotFound, Orb));
    }

    [Fact]
    public void A_builder_puts_a_kit_down_at_its_cells_point()
    {
        var at = new Cell(15, -5, 2010);

        Assert.Equal(new WorldPoint(1.5f, -0.5f, 201f), at.ToWorld());
        Assert.Equal(at, Cell.FromWorld(at.ToWorld(), PieceGrid.Small));
    }

    [Fact]
    public void A_builder_says_what_it_lacks_and_shows_the_items_name()
    {
        IPieceCatalogue catalogue = new Named();

        var waits = Reason.NeedsItems(Orb, Must(ItemQuantity.Create(new ItemKey(11), 3)));
        var name = catalogue.ItemName(new ItemKey(int.Parse(waits.Facts["item"]))).Match(static n => n, static _ => "?");

        Assert.Equal(ReasonCodes.NeedsItems, waits.Code);
        Assert.Equal("Needs 3 Iron Ingot", $"Needs {waits.Facts["count"]} {name}");
        Assert.Equal("?", catalogue.ItemName(new ItemKey(12)).Match(static n => n, static _ => "?"));
    }

    [Fact]
    public void A_builder_words_reach_power_and_blocking_with_lattices_codes()
    {
        Assert.Equal(ReasonCodes.OutOfReach, Reason.OutOfReach(Orb, Must(Metres.Create(9f)), Must(Metres.Create(3f))).Code);
        Assert.Equal(ReasonCodes.NoPower, Reason.NoPower(Orb).Code);
        Assert.Equal(ReasonCodes.NoPower, Reason.NoPower(Orb, new ReferenceId(5)).Code);
        Assert.Equal("player", Reason.BlockedByPlayer(Orb, new Cell(10, 10, 10)).Facts["by"]);
        var byGhost = Reason.Blocked(Orb, new Cell(10, 10, 10), new GhostId(3));
        Assert.Equal(ReasonCodes.Blocked, byGhost.Code);
        Assert.Equal("ghost", byGhost.Facts["by"]);
        Assert.Equal("3", byGhost.Facts["ghost"]);
    }

    [Fact]
    public void A_builder_has_a_merges_held_stack_in_hand_and_gathers_needed_plus_held()
    {
        var coil = new ItemKey(21);
        var merge = new Step.Merge(new StructureRef(new ReferenceId(5), new PrefabKey(31), new Cell(0, 0, 0)), new PrefabKey(32),
            Must(ItemQuantity.Create(coil, 2)), new ItemKey(41), Option<ItemQuantity>.None);
        var bill = new Bill(ValueList<ItemQuantity>.Empty, ValueList<ItemQuantity>.Empty, ValueList<ItemDelta>.Empty, ValueSet.Of(new ItemKey(41)))
        {
            Held = ValueList.Of(Must(ItemQuantity.Create(coil, 2))),
        };

        var (_, _, held, tool, extra) = merge;

        Assert.Equal(coil, held.Item);
        Assert.Equal(2, held.Count);
        Assert.Equal(new ItemKey(41), tool);
        Assert.True(extra.IsNone);
        Assert.Equal(held, Assert.Single(bill.Held));
        Assert.Empty(new Bill(ValueList<ItemQuantity>.Empty, ValueList<ItemQuantity>.Empty, ValueList<ItemDelta>.Empty, ValueSet<ItemKey>.Empty).Held);
    }

    [Fact]
    public void A_builder_reads_the_can_a_paint_step_uses_and_the_labeller_a_name_or_setting_step_holds()
    {
        var on = new StepTarget.PlacedBy(0);
        var steps = new Step[]
        {
            new Step.Paint(on, Must(PaintColour.Create(3)), new ItemKey(51), 10),
            new Step.Rename(on, Must(ThingName.Create("Main bus")), new ItemKey(61)),
            new Step.SetValue(on, Must(Setting.Create(2)), new ItemKey(61)),
        };

        var held = new System.Collections.Generic.List<(ItemKey Item, int ActsPerCan)>();
        foreach (var step in steps)
            switch (step)
            {
                case Step.Paint(_, _, var can, var acts):
                    held.Add((can, acts));
                    break;
                case Step.Rename(_, _, var labeller):
                    held.Add((labeller, 0));
                    break;
                case Step.SetValue(_, _, var labeller):
                    held.Add((labeller, 0));
                    break;
            }

        Assert.Equal([(new ItemKey(51), 10), (new ItemKey(61), 0), (new ItemKey(61), 0)], held);
    }

    private static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static v => v, static why => throw new System.InvalidOperationException(why.ToString()));
}
