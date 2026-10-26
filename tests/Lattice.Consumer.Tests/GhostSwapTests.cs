using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A connect tool's upgrade drag as a consumer writes it against the contract alone: planned pieces swapped in place,
/// and a drag over built and planned pieces as one batch.
/// </summary>
public sealed class GhostSwapTests
{
    private static readonly Maker Maker = Make.Maker("connect", "upgrades");

    /// <summary>What a consumer shows for a ghost edit.</summary>
    private static string Describe(GhostEdit edit) => edit switch
    {
        GhostEdit.Swap { To: SwapTo.SameShapeFrom kit } => $"swap to kit {kit.Kit.Hash}",
        GhostEdit.Swap { To: SwapTo.Exactly piece } => $"swap to piece {piece.Piece.Hash}",
        GhostEdit.SetLabel => "label",
        _ => "an edit this version does not know",
    };

    [Fact]
    public void A_planned_run_is_swapped_in_place_by_a_change()
    {
        var to = new SwapTo.SameShapeFrom(new KitKey(77));
        var change = new Command.Change(new Selector.RunThrough(new Cell(0, 0, 0)), new GhostEdit.Swap(to), Option.Some(new Revision(9)));

        Assert.Equal("swap to kit 77", Describe(change.Edit));
    }

    [Fact]
    public void A_drag_over_built_and_planned_pieces_is_one_batch()
    {
        var to = new SwapTo.Exactly(new PrefabKey(-5));
        var cell = new Cell(10, 0, 0);
        var drag = new Command.Batch(ValueList.From(new Command[]
        {
            new Command.PlanSwap(new StructureSelector.RunThrough(cell), to, Option<PlanId>.None, Maker),
            new Command.Change(new Selector.RunThrough(cell), new GhostEdit.Swap(to), Option<Revision>.None),
        }));

        Assert.Equal(2, drag.Steps.Count);
        Assert.Equal("swap to piece -5", Describe(((Command.Change)drag.Steps[1]).Edit));
    }
}
