using Lattice.Api;
using Lattice.Testing.Stubs;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A builder against the contract alone: where to put a kit down, why it waits in Lattice's words, and the name of the
/// item it lacks from a stubbed catalogue.
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

    private static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static v => v, static why => throw new System.InvalidOperationException(why.ToString()));
}
