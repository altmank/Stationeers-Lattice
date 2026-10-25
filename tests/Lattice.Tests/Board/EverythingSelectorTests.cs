using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

public sealed class EverythingSelectorTests
{
    private static readonly Cell At = new(100, 100, 100);
    private static readonly Cell Next = new(120, 100, 100);

    private static Command OtherPlan() => new Command.CreatePlan(new PlanDraft(
        TestKitIds.Must(PlanName.Create("Bob's sketch")), TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty));

    [Fact]
    public void Everything_picks_every_ghost_in_every_plan()
    {
        var t = new TestBoard();
        var first = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var plan = Assert.Single(Applied(t.Apply(OtherPlan(), Bob)).PlansOpened);
        var second = Assert.Single(Applied(t.Apply(new Command.Place(ValueList.Of(t.Draft("StructureCableStraight", Next, plan: Option.Some(plan.Id)))), Bob)).Added);

        Assert.True(t.View.Select(new Selector.Everything()).TryGet(out var all));

        Assert.Equal(new[] { first.Id, second.Id }, all.Select(g => g.Id).OrderBy(i => i.Value));
    }

    [Fact]
    public void Everything_on_an_empty_board_is_refused_as_a_command_selection_like_any_empty_selection()
    {
        var t = new TestBoard();

        Assert.True(t.View.Select(new Selector.Everything()).TryGet(out var none));
        Assert.Empty(none);
        Assert.Equal(ReasonCodes.NotFound, Refused(t.Apply(new Command.Change(new Selector.Everything(), new GhostEdit.SetLabel(Option<Label>.None), Option<Revision>.None))).Why.Code);
    }
}
