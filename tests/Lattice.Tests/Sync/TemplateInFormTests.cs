using System;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Sync;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Commands;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Sync;

/// <summary>
/// <see cref="Command.PlaceTemplateIn"/> on the wire: the <c>place-template-in</c> word with the same draft lines as
/// <c>place-template</c>, and a client's command held to the limits <see cref="Command.PlaceTemplate"/> is held to.
/// </summary>
public sealed class TemplateInFormTests
{
    private static readonly CommandId Id = new(new Guid(41, 3, 9, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    private static readonly TemplateName Name = TestKitIds.Must(TemplateName.Create("Pump room"));
    private static readonly Turn Quarter = TestKitIds.Must(Turn.Create(1));

    private static GhostDraft Draft(int x) =>
        TestBoard.Draft(new Intent.Build(new Placement(PrefabHash.Piece("ModFancyCableStraight"), new Cell(x, 10, 15), Quarter), Option<Finish>.None));

    private static Template Two => new(Name, ValueList.From(new[] { Draft(5), Draft(10) }));

    private static string Written(Command command) =>
        CommandForm.Write(Id, command).Match(static text => text, static why => throw new InvalidOperationException(why.ToString()));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_template_placed_into_a_plan_reads_back_as_written(bool named)
    {
        var command = new Command.PlaceTemplateIn(Two, new Cell(100, 0, 100), Quarter, named ? Option.Some(new PlanId(6)) : Option<PlanId>.None);

        var read = CommandForm.Read(Written(command));

        Assert.True(read.TryGet(out var back), read.ToString());
        Assert.Equal(command, back.Command);
    }

    [Fact]
    public void It_is_written_with_its_own_word_the_plan_and_the_same_draft_lines_as_a_new_plans_template()
    {
        var into = Written(new Command.PlaceTemplateIn(Two, new Cell(100, 0, 100), Quarter, Option.Some(new PlanId(6))));
        var asNew = Written(new Command.PlaceTemplate(Two, new Cell(100, 0, 100), Quarter,
            new PlanDraft(TestKitIds.Must(PlanName.Create("New")), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty)));

        Assert.Contains("place-template-in name=Pump%20room origin=100,0,100 turn=1 into=6 ghost=#", into);
        Assert.Equal(DraftLines(asNew), DraftLines(into));
    }

    private static string[] DraftLines(string text) => text.Split('\n').Where(l => l.StartsWith("draft ", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void A_clients_template_is_held_to_the_limits_of_a_new_plans_template()
    {
        var c = new TestCommands(TestCommands.Limits(maxGhosts: 1, min: new Cell(-1000, -1000, -1000), max: new Cell(1000, 1000, 1000)));
        var plan = new PlanDraft(TestKitIds.Must(PlanName.Create("New")), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);
        var far = new Cell(5000, 0, 0);
        var near = new Cell(100, 0, 100);

        Assert.Equal(ReasonCodes.OutOfRange, Code(c.FromClient(new Command.PlaceTemplateIn(Two, far, Turn.None, Option<PlanId>.None))));
        Assert.Equal(Code(c.FromClient(new Command.PlaceTemplate(Two, far, Turn.None, plan))), Code(c.FromClient(new Command.PlaceTemplateIn(Two, far, Turn.None, Option<PlanId>.None))));
        Assert.Equal(ReasonCodes.TooManyGhosts, Code(c.FromClient(new Command.PlaceTemplateIn(Two, near, Turn.None, Option<PlanId>.None))));
        Assert.Equal(Code(c.FromClient(new Command.PlaceTemplate(Two, near, Turn.None, plan))), Code(c.FromClient(new Command.PlaceTemplateIn(Two, near, Turn.None, Option<PlanId>.None))));
    }

    private static ReasonCode Code(Outcome outcome) => TestBoard.Refused(outcome).Why.Code;
}
