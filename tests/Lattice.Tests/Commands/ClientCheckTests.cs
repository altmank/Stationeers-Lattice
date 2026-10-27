using System.IO;
using Lattice.Api;
using Lattice.Core.Commands;
using Lattice.Core.Board;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Commands;

/// <summary>The host's size and sanity checks on commands from clients: each limit refuses with its own code.</summary>
public sealed class ClientCheckTests
{
    private static readonly Cell At = new(100, 100, 100);
    private static readonly AttachmentKey Tag = TestKitIds.Must(AttachmentKey.Create(TestKitIds.Mod, "tag"));

    private const string ModdedText = """
        lattice-catalogue 3
        piece ModSuperCableStraight kit=ItemModSuperCoil option=0 grid=small
          step material=ItemModSuperCoil*1 exit=ItemWireCutters air=no
          end 0 0 0 z+ power,data
          end 0 0 0 z- power,data
        """;

    private static NamedPiece Modded() =>
        Assert.Single(TestKitIds.MustRead(CatalogueFile.Read(new StringReader(ModdedText), "modded.lcat")).Pieces);

    private static Command Place(TestCommands c, params Cell[] cells)
    {
        var drafts = new GhostDraft[cells.Length];
        for (var i = 0; i < cells.Length; i++)
            drafts[i] = c.T.Draft("StructureCableStraight", cells[i]);
        return new Command.Place(ValueList.From(drafts));
    }

    private static Selector Nested(int depth)
    {
        Selector selector = new Selector.Ids(ValueList.Of(new GhostId(1)));
        for (var i = 1; i < depth; i++)
            selector = new Selector.Any(ValueList.Of(selector));
        return selector;
    }

    private static Command NestedBatch(int depth, Command inner)
    {
        var command = inner;
        for (var i = 0; i < depth; i++)
            command = new Command.Batch(ValueList.Of(command));
        return command;
    }

    private static ReasonCode Code(Outcome outcome) => Refused(outcome).Why.Code;

    [Fact]
    public void Too_many_ghosts_in_one_command_is_refused_too_many_ghosts()
    {
        var c = new TestCommands(TestCommands.Limits(maxGhosts: 2));

        Assert.Equal(ReasonCodes.TooManyGhosts, Code(c.FromClient(new Command.Batch(ValueList.From([
            Place(c, At, new Cell(200, 100, 100)),
            Place(c, new Cell(300, 100, 100)),
        ])))));
        Assert.IsType<Outcome.Applied>(c.FromClient(Place(c, At, new Cell(200, 100, 100))));
    }

    [Fact]
    public void Ghosts_in_a_new_plan_and_moves_count_as_ghosts_too()
    {
        var c = new TestCommands(TestCommands.Limits(maxGhosts: 1));
        var drafts = ((Command.Place)Place(c, At, new Cell(200, 100, 100))).Ghosts;
        var plan = new PlanDraft(TestKitIds.Must(PlanName.Create("Hall")), Lattice.Tests.Board.TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);
        var placement = ((Intent.Build)drafts[0].Intent).Target;
        var moves = new Command.Move(ValueMap<GhostId, Placement>.From(new System.Collections.Generic.Dictionary<GhostId, Placement>
        {
            [new GhostId(1)] = placement,
            [new GhostId(2)] = placement,
        }), Option<Revision>.None);

        Assert.Equal(ReasonCodes.TooManyGhosts, Code(c.FromClient(new Command.CreatePlan(plan) { Ghosts = drafts })));
        Assert.Equal(ReasonCodes.TooManyGhosts, Code(c.FromClient(moves)));
    }

    [Fact]
    public void Ids_named_count_as_ghosts_too()
    {
        var c = new TestCommands(TestCommands.Limits(maxGhosts: 2));

        var outcome = c.FromClient(new Command.Remove(new Selector.Ids(ValueList.From([new GhostId(1), new GhostId(2), new GhostId(3)]))));

        Assert.Equal(ReasonCodes.TooManyGhosts, Code(outcome));
    }

    [Fact]
    public void A_selector_nested_too_deep_is_refused_selector_too_deep()
    {
        var c = new TestCommands(TestCommands.Limits(selectorDepth: 3));

        Assert.Equal(ReasonCodes.SelectorTooDeep, Code(c.FromClient(new Command.Remove(Nested(4)))));
        Assert.Equal(ReasonCodes.NotFound, Code(c.FromClient(new Command.Remove(Nested(3)))));
    }

    [Fact]
    public void A_structure_selector_nested_too_deep_is_refused_selector_too_deep()
    {
        var c = new TestCommands(TestCommands.Limits(selectorDepth: 2));
        StructureSelector deep = new StructureSelector.All(ValueList.Of<StructureSelector>(
            new StructureSelector.Any(ValueList.Of<StructureSelector>(new StructureSelector.RunThrough(At)))));

        Assert.Equal(ReasonCodes.SelectorTooDeep, Code(c.FromClient(new Command.MarkRemoval(deep, Option<PlanId>.None, Lattice.Tests.Board.TestBoard.TestMaker))));
    }

    [Fact]
    public void Batches_nested_too_deep_are_refused_batch_too_deep()
    {
        var c = new TestCommands(TestCommands.Limits(batchDepth: 2));

        Assert.Equal(ReasonCodes.BatchTooDeep, Code(c.FromClient(NestedBatch(3, Place(c, At)))));
        Assert.IsType<Outcome.Applied>(c.FromClient(NestedBatch(2, Place(c, At))));
    }

    [Fact]
    public void Attached_text_past_its_declared_length_is_refused_text_too_long()
    {
        var c = new TestCommands();
        Assert.True(c.Attachments.Register(new AttachmentDescriptor(Tag, TestKitIds.Must(DisplayName.Create("Tag"))) { MaxLength = 4 }).IsOk);
        var draft = c.T.Draft("StructureCableStraight", At) with
        {
            Attached = ValueMap<AttachmentKey, Attachment>.Empty.With(Tag, TestKitIds.Must(Attachment.Create("12345"))),
        };

        var refusal = Refused(c.FromClient(new Command.Place(ValueList.Of(draft))));

        Assert.Equal(ReasonCodes.TextTooLong, refusal.Why.Code);
        Assert.True(refusal.Why.Facts.TryGetValue("limit", out var limit) && limit == "4");
    }

    [Fact]
    public void Undeclared_attached_text_gets_the_default_length()
    {
        var c = new TestCommands();
        var edit = new GhostEdit.Attach(Tag, TestKitIds.Must(Attachment.Create(new string('x', 1025))));

        Assert.Equal(ReasonCodes.TextTooLong, Code(c.FromClient(new Command.Change(Ids(new GhostId(1)), edit, Option<Revision>.None))));
    }

    [Fact]
    public void Plan_data_past_its_length_is_refused_text_too_long()
    {
        var c = new TestCommands();
        var data = ValueMap<AttachmentKey, Attachment>.Empty.With(Tag, TestKitIds.Must(Attachment.Create(new string('x', 2000))));
        var draft = new PlanDraft(TestKitIds.Must(PlanName.Create("Hydro")), TestMaker, new Visibility.Everyone(), data);

        Assert.Equal(ReasonCodes.TextTooLong, Code(c.FromClient(new Command.CreatePlan(draft))));
    }

    [Fact]
    public void A_cell_out_of_range_is_refused_out_of_range()
    {
        var c = new TestCommands(TestCommands.Limits(min: new Cell(-1000, -1000, -1000), max: new Cell(1000, 1000, 1000)));

        Assert.Equal(ReasonCodes.OutOfRange, Code(c.FromClient(Place(c, new Cell(1005, 0, 0)))));
        Assert.Equal(ReasonCodes.OutOfRange, Code(c.FromClient(new Command.Remove(new Selector.Box(At, new Cell(0, -2000, 0))))));
        Assert.Equal(ReasonCodes.OutOfRange, Code(c.FromClient(new Command.Remove(new Selector.Joined(new Cell(0, 2000, 0))))));
        Assert.Equal(ReasonCodes.OutOfRange, Code(c.FromClient(new Command.PlanRun(
            new RunRequest(default, At, ValueList.Of(new Cell(0, 0, 5000)), At, Option<PlanId>.None, TestMaker)))));
    }

    [Fact]
    public void A_piece_the_catalogue_does_not_know_is_refused_unknown_piece()
    {
        var c = new TestCommands();
        var unknown = new Placement(new PrefabKey(123456789), At, default);

        var outcome = c.FromClient(new Command.Place(ValueList.Of(Draft(new Intent.Build(unknown, Option<Finish>.None)))));

        Assert.Equal(ReasonCodes.UnknownPiece, Code(outcome));
    }

    [Fact]
    public void A_kit_the_catalogue_does_not_know_is_refused_unknown_piece()
    {
        var c = new TestCommands();

        var outcome = c.FromClient(new Command.PlanRun(new RunRequest(new KitKey(987654), At, ValueList<Cell>.Empty, At, Option<PlanId>.None, TestMaker)));

        Assert.Equal(ReasonCodes.UnknownPiece, Code(outcome));
    }

    [Fact]
    public void A_modded_piece_the_catalogue_knows_passes()
    {
        var modded = Modded();
        var c = new TestCommands(null, modded);

        var outcome = c.FromClient(new Command.Place(ValueList.Of(c.T.Draft(modded.Name, At))));

        Assert.IsType<Outcome.Applied>(outcome);
    }

    [Fact]
    public void A_refused_client_command_is_remembered_by_its_id()
    {
        var c = new TestCommands(TestCommands.Limits(maxGhosts: 1));
        var id = c.NextId();
        var place = Place(c, At, new Cell(200, 100, 100));

        var first = TestCommands.Answer(c.Service.SubmitFromClient(id, place, Bob));

        Assert.Same(first, TestCommands.Answer(c.Service.Submit(id, place, Bob)));
        Assert.Empty(c.T.View.At(At));
    }

    [Fact]
    public void Limits_that_are_not_positive_are_refused()
    {
        Assert.Equal(ReasonCodes.OutOfRange, CommandLimits.Create(0, 8, 4, new Cell(-1, -1, -1), new Cell(1, 1, 1)).ErrorOf().Code);
        Assert.Equal(ReasonCodes.OutOfRange, CommandLimits.Create(10, 8, 4, new Cell(1, 1, 1), new Cell(-1, -1, -1)).ErrorOf().Code);
        Assert.True(CommandLimits.Create(10, 8, 4, new Cell(-1, -1, -1), new Cell(1, 1, 1)).IsOk);
    }
}
