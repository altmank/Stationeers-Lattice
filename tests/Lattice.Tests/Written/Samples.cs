using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Written;
using Lattice.TestKit;

namespace Lattice.Tests.Written;

/// <summary>
/// Ghosts, plans, drafts and change lines covering every intent, state, actor and visibility, written text that needs
/// escaping, a modded piece (a prefab hash no base game has) and another mod's attached data.
/// </summary>
internal static class Samples
{
    public static readonly ModId DataMod = TestKitIds.Must(ModId.Create("builder"));
    public static readonly Actor Player = new Actor.Player(new ClientId(76561198000000001));
    public static readonly Actor Agent = new Actor.Agent(TestKitIds.Must(AgentId.Create("tool")));
    public static readonly Actor ModActor = new Actor.Mod(DataMod);
    public static readonly Maker RunTool = TestKitIds.Must(Maker.Create(TestKitIds.Must(ModId.Create("planner")), "run-tool"));
    public static readonly DateTimeOffset Monday = new(2026, 10, 19, 8, 30, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset MondayLater = new DateTimeOffset(2026, 10, 19, 8, 31, 5, TimeSpan.Zero).AddTicks(1234567);
    public static readonly CommandId Command = new(new Guid("6f9619ff-8b86-d011-b42d-00cf4fc964ff"));

    /// <summary>A piece from a mod: a prefab hash the base game does not have.</summary>
    public static readonly PrefabKey ModdedPiece = new(-1912345678);

    public static readonly PrefabKey Frame = new(1432509316);
    public static readonly PrefabKey Cable = new(-1529819532);

    public static readonly StructureRef BuiltCable = new(new ReferenceId(901234), Cable, new Cell(10, 20, -30));

    public static Ghost Ghost(long id, Intent intent, long plan = 3) => new(
        new GhostId(id), new PlanId(plan), new Revision(120), intent, new GhostState.Planned(), Player, RunTool, Monday,
        new Visibility.Everyone(), Option<Label>.None, Option<Tint>.None, ValueSet<GroupName>.Empty,
        ValueMap<AttachmentKey, Attachment>.Empty);

    public static Intent Build(PrefabKey piece, Cell at, int turn = 0) =>
        new Intent.Build(new Placement(piece, at, TestKitIds.Must(Turn.Create(turn))), Option<Finish>.None);

    public static Finish Finish() => TestKitIds.Must(Lattice.Api.Finish.Create(
        Option.Some(TestKitIds.Must(PaintColour.Create(4))),
        Option.Some(TestKitIds.Must(ThingName.Create("Main bus = 50%, east"))),
        Option.Some(TestKitIds.Must(Setting.Create(0.1)))));

    public static ValueMap<AttachmentKey, Attachment> Data(params (string Name, string Value)[] entries)
    {
        var data = new Dictionary<AttachmentKey, Attachment>();
        foreach (var (name, value) in entries)
            data[TestKitIds.Must(AttachmentKey.Create(DataMod, name))] = TestKitIds.Must(Attachment.Create(value));
        return ValueMap.From<AttachmentKey, Attachment>(data);
    }

    public static ValueSet<GroupName> Groups(params string[] names)
    {
        var groups = new List<GroupName>();
        foreach (var name in names)
            groups.Add(TestKitIds.Must(GroupName.Create(name)));
        return ValueSet.From(groups);
    }

    /// <summary>One ghost of each shape the form has to carry.</summary>
    public static IReadOnlyList<Ghost> EveryGhost() =>
    [
        Ghost(12, Build(Frame, new Cell(10, 20, 30))),
        Ghost(13, Build(ModdedPiece, new Cell(-5, 0, 15), 17)) with
        {
            State = new GhostState.Started(new StructureRef(new ReferenceId(77), ModdedPiece, new Cell(-5, 0, 15))),
            Attached = Data(("preferred-bay", "{\"bay\": 2, \"note\": \"left of door\"}"), ("job", "a=b c%d\nline two")),
        },
        Ghost(14, new Intent.Remove(BuiltCable)) with
        {
            PlacedBy = Agent,
            PlacedAt = MondayLater,
            Visibility = new Visibility.OwnerOnly(),
            Label = Option.Some(TestKitIds.Must(Label.Create("North store #2 (100% done)"))),
        },
        Ghost(15, new Intent.Swap(BuiltCable, new Placement(new PrefabKey(-1123456789), BuiltCable.At, TestKitIds.Must(Turn.Create(5))), Option.Some(Finish()))) with
        {
            State = new GhostState.Stale(new Reason(
                ReasonCodes.Different,
                ValueMap.Of("found", "901235").With("expected piece", "-1123456789"),
                Option.Some("The game says: no room"),
                TestKitIds.Must(RuleId.Create(DataMod, "matching")))),
            Tint = Option.Some(new Tint(255, 0, 128, 64)),
            Groups = Groups("Hydro power", "Air", "zone=b"),
        },
        Ghost(16, new Intent.FinishOnly(BuiltCable, Finish())) with
        {
            PlacedBy = ModActor,
            Visibility = new Visibility.Team(TestKitIds.Must(TeamName.Create("Night shift"))),
            Revision = new Revision(7),
        },
    ];

    public static Plan Plan(long id = 3) => new(
        new PlanId(id), new Revision(120), TestKitIds.Must(PlanName.Create("North store")), Player, RunTool,
        new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

    public static IReadOnlyList<Plan> EveryPlan() =>
    [
        Plan(),
        Plan(4) with
        {
            Name = TestKitIds.Must(PlanName.Create("Phase 2: hull & air")),
            Owner = Agent,
            Visibility = new Visibility.OwnerOnly(),
            HigherLayerData = Data(("approved", "yes"), ("draft", "")),
        },
    ];

    public static IReadOnlyList<GhostDraft> EveryDraft()
    {
        var drafts = new List<GhostDraft>();
        foreach (var ghost in EveryGhost())
            drafts.Add(new GhostDraft(ghost.Intent, Option<PlanId>.None, ghost.MadeBy, ghost.Visibility, ghost.Label, ghost.Tint, ghost.Groups, ghost.Attached));
        drafts[1] = drafts[1] with { Plan = Option.Some(new PlanId(9)) };
        return drafts;
    }

    /// <summary>The typical change: one player places one build ghost.</summary>
    public static ChangeLine TypicalLine() => new(
        new LineNumber(41), Option.Some(new LineNumber(40)), Command, Player, Monday, new ChangeSetId(88), new Revision(120),
        ValueList.Of<ChangeOp>(new ChangeOp.AddGhost(GhostRecord.Of(Ghost(12, Build(Frame, new Cell(10, 20, 30)))))));

    /// <summary>A line with every operation.</summary>
    public static ChangeLine EveryOpLine()
    {
        var ghosts = EveryGhost();
        var ops = new List<ChangeOp>
        {
            new ChangeOp.OpenPlan(PlanRecord.Of(Plan())),
            new ChangeOp.EditPlan(PlanRecord.Of(EveryPlan()[1])),
        };
        foreach (var ghost in ghosts)
            ops.Add(new ChangeOp.AddGhost(GhostRecord.Of(ghost)));
        ops.Add(new ChangeOp.ChangeGhost(GhostRecord.Of(ghosts[0] with { Plan = new PlanId(4) })));
        ops.Add(new ChangeOp.RemoveGhost(new GhostId(20), new PlanId(3), new RemovalCause.Removed()));
        ops.Add(new ChangeOp.RemoveGhost(new GhostId(21), new PlanId(3), new RemovalCause.Replaced(new GhostId(12))));
        ops.Add(new ChangeOp.RemoveGhost(new GhostId(22), new PlanId(3), new RemovalCause.Done(new Completion(Option.Some(BuiltCable), Monday))));
        ops.Add(new ChangeOp.RemoveGhost(new GhostId(23), new PlanId(3), new RemovalCause.Done(new Completion(Option<StructureRef>.None, MondayLater))));
        ops.Add(new ChangeOp.RemoveGhost(new GhostId(24), new PlanId(3), new RemovalCause.TargetGone()));
        ops.Add(new ChangeOp.FinishPlan(new PlanId(4)));
        return new ChangeLine(new LineNumber(1), Option<LineNumber>.None, Command, Agent, Monday, new ChangeSetId(1), new Revision(120), ValueList.From(ops));
    }
}
