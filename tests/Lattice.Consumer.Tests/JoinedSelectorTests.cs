using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A planner widening a planned run to its whole network, against the contract alone: the joined selector from the
/// clicked cell, and the same drag swapping the network's pieces.
/// </summary>
public sealed class JoinedSelectorTests
{
    /// <summary>What a planner's tool tip says for the selection it is about to send.</summary>
    private static string Describe(Selector selector) => selector switch
    {
        Selector.Joined { Cell: var at } => $"the planned network at {at.X},{at.Y},{at.Z}",
        Selector.RunThrough { Cell: var at } => $"the planned run at {at.X},{at.Y},{at.Z}",
        _ => "a selection this version does not know",
    };

    [Fact]
    public void A_click_on_a_planned_run_widens_to_its_network()
    {
        var clicked = new Cell(10, 0, -5);
        Selector run = new Selector.RunThrough(clicked);
        Selector widened = run is Selector.RunThrough through ? new Selector.Joined(through.Cell) : run;

        Assert.Equal("the planned network at 10,0,-5", Describe(widened));
        Assert.Equal(new Selector.Joined(clicked), widened);
    }

    [Fact]
    public void The_whole_planned_network_is_swapped_by_one_change()
    {
        var to = new SwapTo.SameShapeFrom(new KitKey(77));
        var change = new Command.Change(new Selector.Joined(new Cell(0, 0, 0)), new GhostEdit.Swap(to), Option.Some(new Revision(9)));

        Assert.Equal("the planned network at 0,0,0", Describe(change.Which));
    }
}
