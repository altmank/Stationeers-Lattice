using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Sync;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Sync;

public sealed class CommandFormTests
{
    private static readonly CommandId Id = new(new Guid(41, 3, 9, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    private static readonly AttachmentKey Tag = TestKitIds.Must(AttachmentKey.Create(TestKitIds.Mod, "tag"));
    private static readonly Attachment Spaced = TestKitIds.Must(Attachment.Create("two words, 100% #1"));
    private static readonly Maker Maker = TestBoard.TestMaker;
    private static readonly PrefabKey Modded = PrefabHash.Piece("ModFancyCableStraight");
    private static readonly KitKey ModdedKit = PrefabHash.Kit("ModKitFancyCable");
    private static readonly StructureRef Standing = new(new ReferenceId(77), Modded, new Cell(5, 0, -5));
    private static readonly Turn Quarter = TestKitIds.Must(Turn.Create(1));

    private static ValueList<T> L<T>(params T[] items) => ValueList.From(items);

    private static GhostDraft Draft(string label) => TestBoard.Draft(new Intent.Build(new Placement(Modded, new Cell(5, 10, 15), Quarter), Option<Finish>.None)) with
    {
        Label = Option.Some(TestKitIds.Must(Label.Create(label))),
        Attached = ValueMap<AttachmentKey, Attachment>.Empty.With(Tag, Spaced),
    };

    private static PlanDraft Plan(string name) =>
        new(TestKitIds.Must(PlanName.Create(name)), Maker, new Visibility.OwnerOnly(), ValueMap<AttachmentKey, Attachment>.Empty.With(Tag, Spaced));

    private static Finish Painted => TestKitIds.Must(Finish.Create(Option.Some(TestKitIds.Must(PaintColour.Create(3))), Option.Some(TestKitIds.Must(ThingName.Create("Main bus"))), Option<Setting>.None));

    private static Selector Nested => new Selector.All(L<Selector>(
        new Selector.Everything(),
        new Selector.Ids(L(new GhostId(4), new GhostId(9))),
        new Selector.Any(L<Selector>(
            new Selector.Box(new Cell(-10, 0, 0), new Cell(10, 20, 30)),
            new Selector.RunThrough(new Cell(0, 5, 0)),
            new Selector.Network(new NetworkRef(12)),
            new Selector.Room(new RoomRef(13)),
            new Selector.Group(TestKitIds.Must(GroupName.Create("east wing"))),
            new Selector.Owner(TestBoard.Bob),
            new Selector.MadeBy(Maker),
            new Selector.InPlan(new PlanId(2)),
            new Selector.Tagged(Tag, Option.Some(Spaced)),
            new Selector.Tagged(Tag, Option<Attachment>.None),
            new Selector.OfPiece(Modded)))));

    private static StructureSelector Structures => new StructureSelector.Any(L<StructureSelector>(
        new StructureSelector.Refs(L(Standing)),
        new StructureSelector.Box(new Cell(0, 0, 0), new Cell(20, 20, 20)),
        new StructureSelector.RunThrough(new Cell(5, 0, -5)),
        new StructureSelector.Network(new NetworkRef(1)),
        new StructureSelector.Room(new RoomRef(2)),
        new StructureSelector.All(L<StructureSelector>(new StructureSelector.OfPiece(Modded)))));

    /// <summary>One of every command kind, edit and selector this version has; the piece is modded.</summary>
    public static IEnumerable<Command> EveryKind()
    {
        yield return new Command.Place(L(Draft("first one"), Draft("second")));
        yield return new Command.Remove(Nested);
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.SetLabel(Option.Some(TestKitIds.Must(Label.Create("new label")))), Option.Some(new Revision(8)));
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.SetLabel(Option<Label>.None), Option<Revision>.None);
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.SetTint(Option.Some(new Tint(1, 2, 3, 4))), Option<Revision>.None);
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.SetTint(Option<Tint>.None), Option<Revision>.None);
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.JoinGroup(TestKitIds.Must(GroupName.Create("g"))), Option<Revision>.None);
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.LeaveGroup(TestKitIds.Must(GroupName.Create("g"))), Option<Revision>.None);
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.SetFinish(Option.Some(Painted)), Option<Revision>.None);
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.SetFinish(Option<Finish>.None), Option<Revision>.None);
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.SetVisibility(new Visibility.Team(TestKitIds.Must(TeamName.Create("blue")))), Option<Revision>.None);
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.Attach(Tag, Spaced), Option<Revision>.None);
        yield return new Command.Change(TestBoard.Ids(new GhostId(1)), new GhostEdit.Detach(Tag), Option<Revision>.None);
        yield return new Command.CreatePlan(Plan("Night shift"));
        yield return new Command.CreatePlan(Plan("Night shift")) { Ghosts = L(Draft("c1"), Draft("c2")) };
        yield return new Command.EditPlan(new PlanId(3), new PlanEdit.Rename(TestKitIds.Must(PlanName.Create("Day shift"))), Option.Some(new Revision(2)));
        yield return new Command.EditPlan(new PlanId(3), new PlanEdit.ChangeOwner(TestBoard.Alice), Option<Revision>.None);
        yield return new Command.EditPlan(new PlanId(3), new PlanEdit.ChangeVisibility(new Visibility.Everyone()), Option<Revision>.None);
        yield return new Command.EditPlan(new PlanId(3), new PlanEdit.SetData(Tag, Spaced), Option<Revision>.None);
        yield return new Command.EditPlan(new PlanId(3), new PlanEdit.ClearData(Tag), Option<Revision>.None);
        yield return new Command.MoveToPlan(TestBoard.Ids(new GhostId(5)), new PlanId(4));
        yield return new Command.Revert(new ChangeSetId(17));
        yield return new Command.Batch(L<Command>(
            new Command.Place(L(Draft("in a batch"))),
            new Command.Batch(L<Command>(new Command.Remove(TestBoard.Ids(new GhostId(2)))))));
        yield return new Command.FinishPlan(new PlanId(5));
        yield return new Command.CatchUp(new PlanId(6));
        yield return new Command.MarkRemoval(Structures, Option.Some(new PlanId(7)), Maker);
        yield return new Command.PlanSwap(Structures, new SwapTo.Exactly(Modded), Option<PlanId>.None, Maker);
        yield return new Command.PlanSwap(Structures, new SwapTo.SameShapeFrom(ModdedKit), Option.Some(new PlanId(2)), Maker);
        yield return new Command.PlanFinish(Structures, Painted, Option<PlanId>.None, Maker);
        yield return new Command.MarkRemoval(Structures, Option<PlanId>.None, Maker);
        yield return new Command.PlanFinish(Structures, Painted, Option.Some(new PlanId(3)), Maker);
        yield return new Command.Move(ValueMap.Of(new GhostId(3), new Placement(Modded, new Cell(0, 0, 10), Quarter)), Option<Revision>.None);
        yield return new Command.Move(
            ValueMap.Of(new GhostId(3), new Placement(Modded, new Cell(0, 0, 10), Quarter)).With(new GhostId(4), new Placement(Modded, new Cell(20, 0, 10), Turn.None)),
            Option.Some(new Revision(12)));
        yield return new Command.PlanRun(new RunRequest(ModdedKit, new Cell(0, 0, 0), L(new Cell(0, 0, 50), new Cell(50, 0, 50)), new Cell(50, 10, 50), Option.Some(new PlanId(2)), Maker));
        yield return new Command.PlanRoute(new RouteRequest(ModdedKit, new Cell(0, 0, 0), new Cell(100, 0, 0), new RouteToggles(true, false, true, false, true), Option<PlanId>.None, Maker) { SearchBound = Option.Some(5000) });
        yield return PortRun;
        yield return PortRoute;
        yield return new Command.Transform(Nested, new Transformation.Move(new CellOffset(20, 0, -20)), new TransformMode.Move());
        yield return new Command.Transform(Nested, new Transformation.Rotate(new Cell(0, 0, 0), Quarter), new TransformMode.Copy(Maker));
        yield return new Command.Transform(Nested, new Transformation.Mirror(new MirrorPlane(Axis.Z, 10)), new TransformMode.Copy(Maker));
        yield return new Command.PlaceTemplate(new Template(TestKitIds.Must(TemplateName.Create("Pump room")), L(Draft("t1"), Draft("t2"))), new Cell(100, 0, 100), Quarter, Plan("From a template"));
        yield return new Command.Repair(new PlanId(8), Maker);
        yield return new Command.TakeDown(new PlanId(9), Maker);
        yield return new Command.Reopen(new PlanId(10));
    }

    private static readonly Command.PlanRun PortRun = new(new RunRequest(ModdedKit, new Cell(0, 0, 0), ValueList<Cell>.Empty, new Cell(50, 0, 0), Option<PlanId>.None, Maker)
    {
        ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(new GhostId(12), TestKitIds.Must(PortIndex.Create(3)))),
    });

    private static readonly Command.PlanRoute PortRoute = new(new RouteRequest(ModdedKit, new Cell(0, 0, 0), new Cell(100, 0, 0), new RouteToggles(false, false, false, true, false), Option.Some(new PlanId(4)), Maker)
    {
        FromPort = Option.Some<PortEnd>(new PortEnd.OnGhost(new GhostId(7), TestKitIds.Must(PortIndex.Create(0)))),
        ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(new GhostId(8), TestKitIds.Must(PortIndex.Create(2)))),
    });

    [Fact]
    public void A_run_or_route_without_port_ends_writes_as_before_ports_were_added()
    {
        var run = new Command.PlanRun(new RunRequest(ModdedKit, new Cell(0, 0, 0), L(new Cell(0, 0, 50), new Cell(50, 0, 50)), new Cell(50, 10, 50), Option.Some(new PlanId(2)), Maker));
        var route = new Command.PlanRoute(new RouteRequest(ModdedKit, new Cell(0, 0, 0), new Cell(100, 0, 0), new RouteToggles(true, false, true, false, true), Option<PlanId>.None, Maker) { SearchBound = Option.Some(5000) });

        Assert.Equal(
            "lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\nplan-run kit=1101555311 from=0,0,0 via=0,0,50 via=50,0,50 to=50,10,50 made=testkit/board-test into=2\n",
            Written(run));
        Assert.Equal(
            "lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\nplan-route kit=1101555311 from=0,0,0 to=100,0,0 made=testkit/board-test frames=1 bends=0 doors=1 ports=0 no-bridge=1 bound=5000\n",
            Written(route));
    }

    [Fact]
    public void Port_ends_are_written_as_the_ghost_and_its_port_index()
    {
        Assert.Contains(" to-port=12:3", Written(PortRun));
        Assert.Contains(" from-port=7:0 to-port=8:2", Written(PortRoute));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("12:")]
    [InlineData(":3")]
    [InlineData("12:-1")]
    [InlineData("x:3")]
    public void A_port_end_that_does_not_read_is_refused(string text)
    {
        var why = Problem($"lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\nplan-run kit=1101555311 from=0,0,0 to=50,0,0 made=testkit/board-test to-port={text}\n");

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
    }

    private static string Written(Command command) =>
        CommandForm.Write(Id, command).Match(static text => text, static why => throw new InvalidOperationException(why.ToString()));

    private static Reason Problem(string text) =>
        CommandForm.Read(text).Match(static ok => throw new InvalidOperationException($"read: {ok.Command}"), static why => why);

    [Fact]
    public void Every_command_kind_reads_back_as_written()
    {
        foreach (var command in EveryKind())
        {
            var read = CommandForm.Read(Written(command));

            Assert.True(read.TryGet(out var back), $"{command.GetType().Name}: {read}");
            Assert.Equal(Id, back.Id);
            Assert.Equal(command, back.Command);
        }
    }

    public static TheoryData<string, Command> CarryingGhosts() => new()
    {
        { "place", new Command.Place(L(Draft("p1"), Draft("p2"))) },
        { "create-plan", new Command.CreatePlan(Plan("Client wing")) { Ghosts = L(Draft("c1"), Draft("c2"), Draft("c3")) } },
        { "place-template", new Command.PlaceTemplate(new Template(TestKitIds.Must(TemplateName.Create("Pump room")), L(Draft("t1"), Draft("t2"))), new Cell(100, 0, 100), Quarter, Plan("From a template")) },
        { "batch", new Command.Batch(L<Command>(new Command.CreatePlan(Plan("In a batch")) { Ghosts = L(Draft("b1")) })) },
    };

    [Theory]
    [MemberData(nameof(CarryingGhosts))]
    public void A_command_that_carries_ghosts_reads_back_with_every_ghost(string kind, Command command)
    {
        var read = CommandForm.Read(Written(command));

        Assert.True(read.TryGet(out var back), $"{kind}: {read}");
        Assert.NotEmpty(Ghosts(back.Command));
        Assert.Equal(Ghosts(command), Ghosts(back.Command));
    }

    private static List<GhostDraft> Ghosts(Command command) => command switch
    {
        Command.Place place => [.. place.Ghosts],
        Command.CreatePlan create => [.. create.Ghosts],
        Command.PlaceTemplate template => [.. template.Template.Ghosts],
        Command.Batch batch => batch.Steps.SelectMany(Ghosts).ToList(),
        _ => [],
    };

    [Fact]
    public void Every_line_is_one_node_and_the_command_comes_last()
    {
        var lines = Written(new Command.Remove(TestBoard.Ids(new GhostId(3)))).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(new[] { "lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA", "s.ids id=3", "remove which=#0" }, lines);
    }

    [Fact]
    public void A_reference_to_a_later_node_is_refused()
    {
        var why = Problem("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\nremove which=#1\ns.ids id=3\n");

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Contains("bad reference", why.Facts["problem"]);
    }

    [Fact]
    public void A_node_used_twice_is_refused()
    {
        var why = Problem("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\ns.ids id=3\ns.all of=#0 of=#0\nremove which=#1\n");

        Assert.Contains("used twice", why.Facts["problem"]);
    }

    [Fact]
    public void A_node_nobody_uses_is_refused()
    {
        var why = Problem("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\ns.ids id=3\ns.ids id=4\nremove which=#1\n");

        Assert.Contains("not used", why.Facts["problem"]);
    }

    [Fact]
    public void A_node_of_the_wrong_kind_is_refused()
    {
        var why = Problem("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\ne.detach key=testkit/tag\nremove which=#0\n");

        Assert.Contains("is not a Selector", why.Facts["problem"]);
    }

    [Theory]
    [InlineData("not a command at all")]
    [InlineData("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\n")]
    [InlineData("lattice-command 1 id=short\nrevert set=1\n")]
    [InlineData("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\nexplode now=1\n")]
    [InlineData("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\nrevert set=1 extra=2\n")]
    [InlineData("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\ns.ids id=3\n")]
    [InlineData("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\nrevert set=%ZZ\n")]
    [InlineData("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\ntransform which=#0 how=spin mode=move\n")]
    [InlineData("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\ns.ids id=3\ntransform which=#0 how=move by=0,0,0 mode=copy\n")]
    [InlineData("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\nrepair plan=3\n")]
    [InlineData("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\nm.to ghost=3 at=1@0,0,0\nm.to ghost=3 at=1@0,0,0\nmove to=#0 to=#1\n")]
    public void Garbled_text_is_a_problem_never_an_exception(string text)
    {
        Assert.Equal(ReasonCodes.InvalidValue, Problem(text).Code);
    }

    [Fact]
    public void A_deep_chain_reads_without_recursion()
    {
        var text = new System.Text.StringBuilder("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\ns.ids id=1\n");
        for (var i = 0; i < 20_000; i++)
            text.Append("s.all of=#").Append(i).Append('\n');
        text.Append("remove which=#20000\n");

        Assert.True(CommandForm.Read(text.ToString()).TryGet(out _));
    }
}
