using System;
using Lattice.Api;
using Lattice.Core.Sync;

namespace Lattice.Tests.Sync;

/// <summary>
/// The written form of <see cref="Selector.Joined"/>: a node <c>s.joined</c> with its cell. A reader without that kind
/// (Lattice 0.14 and earlier) answers <c>unknown kind</c> for the node and refuses the whole command, so it never picks
/// something else in its place.
/// </summary>
public sealed class JoinedFormTests
{
    private static readonly CommandId Id = new(new Guid(41, 3, 9, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

    private static string Written(Command command) =>
        CommandForm.Write(Id, command).Match(static text => text, static why => throw new InvalidOperationException(why.ToString()));

    private static Command Swap(Selector which) =>
        new Command.Change(which, new GhostEdit.Swap(new SwapTo.Exactly(new PrefabKey(1234))), Option.Some(new Revision(4)));

    public static TheoryData<Command> Commands() => new()
    {
        new Command.Remove(new Selector.Joined(new Cell(5, -10, 15))),
        Swap(new Selector.Joined(new Cell(0, 0, 0))),
        new Command.Remove(new Selector.Any(ValueList.From<Selector>([
            new Selector.RunThrough(new Cell(5, 0, 0)),
            new Selector.All(ValueList.From<Selector>([new Selector.Joined(new Cell(-5, 0, 0)), new Selector.InPlan(new PlanId(3))])),
        ]))),
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void A_joined_selector_reads_back_as_written(Command command)
    {
        var read = CommandForm.Read(Written(command));

        Assert.True(read.TryGet(out var back), read.ToString());
        Assert.Equal(command, back.Command);
    }

    [Fact]
    public void It_is_written_as_its_own_kind_with_its_cell()
    {
        var text = Written(new Command.Remove(new Selector.Joined(new Cell(5, -10, 15))));

        Assert.Contains("s.joined cell=5,-10,15", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s.run", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reader_that_does_not_know_the_selector_kind_refuses_the_command()
    {
        var text = Written(Swap(new Selector.Any(ValueList.From<Selector>([new Selector.Joined(new Cell(0, 0, 0)), new Selector.InPlan(new PlanId(3))]))));
        var unknown = text.Replace("s.joined", "s.later", StringComparison.Ordinal);

        var read = CommandForm.Read(unknown);

        Assert.True(read.TryGetError(out var why));
        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Contains("unknown kind 's.later'", why.Facts["problem"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_joined_node_without_its_cell_is_refused()
    {
        var text = Written(new Command.Remove(new Selector.Joined(new Cell(5, -10, 15)))).Replace(" cell=5,-10,15", "", StringComparison.Ordinal);

        Assert.True(CommandForm.Read(text).TryGetError(out var why));
        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
    }

    [Fact]
    public void A_run_through_selector_writes_as_it_did()
    {
        var text = Written(new Command.Remove(new Selector.RunThrough(new Cell(5, -10, 15))));

        Assert.Contains("s.run cell=5,-10,15", text, StringComparison.Ordinal);
        Assert.Equal(new Command.Remove(new Selector.RunThrough(new Cell(5, -10, 15))), CommandForm.Read(text).Match(static r => r.Command, static _ => null!));
    }
}
