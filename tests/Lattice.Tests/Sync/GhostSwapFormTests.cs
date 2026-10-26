using System;
using Lattice.Api;
using Lattice.Core.Sync;
using Lattice.TestKit.Catalogue;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Sync;

/// <summary>
/// The written form of the swap edit: a node kind of its own, so a reader that does not know it refuses the command
/// rather than reads it as another edit.
/// </summary>
public sealed class GhostSwapFormTests
{
    private static readonly CommandId Id = new(new Guid(43, 3, 9, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    private static readonly SwapTo Heavy = new SwapTo.SameShapeFrom(PrefabHash.Kit("ItemCableCoilHeavy"));

    private static string Written(Command command) =>
        CommandForm.Write(Id, command).Match(static text => text, static why => throw new InvalidOperationException(why.ToString()));

    private static Command.Change Swap(SwapTo to) =>
        new(new Selector.RunThrough(new Cell(5, 0, 10)), new GhostEdit.Swap(to), Option.Some(new Revision(17)));

    public static TheoryData<SwapTo> Targets() => new()
    {
        Heavy,
        new SwapTo.Exactly(new PrefabKey(-118)),
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void A_swap_edit_reads_back_as_written(SwapTo to)
    {
        var read = CommandForm.Read(Written(Swap(to)));

        Assert.True(read.TryGet(out var back), read.ToString());
        Assert.Equal(Swap(to), back.Command);
    }

    [Fact]
    public void A_swap_edit_in_a_batch_with_a_plan_swap_reads_back_as_written()
    {
        var batch = new Command.Batch(ValueList.From(new Command[]
        {
            new Command.PlanSwap(new StructureSelector.RunThrough(new Cell(5, 0, 10)), Heavy, Option<PlanId>.None, TestBoard.TestMaker),
            Swap(Heavy),
        }));

        Assert.True(CommandForm.Read(Written(batch)).TryGet(out var back));
        Assert.Equal(batch, back.Command);
    }

    [Fact]
    public void An_edit_kind_the_reader_does_not_know_refuses_the_command()
    {
        var text = Written(Swap(new SwapTo.Exactly(new PrefabKey(-118))));
        Assert.Contains("e.swap ", text);

        Assert.True(CommandForm.Read(text.Replace("e.swap ", "e.reshape ")).TryGetError(out _));
    }

    [Fact]
    public void A_swap_edit_with_no_target_is_refused()
    {
        var text = Written(Swap(new SwapTo.Exactly(new PrefabKey(-118))));

        Assert.True(CommandForm.Read(text.Replace(" to-piece=-118", "")).TryGetError(out var why));
        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
    }
}
