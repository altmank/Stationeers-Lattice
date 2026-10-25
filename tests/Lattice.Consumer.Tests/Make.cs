using System;
using Lattice.Api;
using Xunit.Sdk;

namespace Lattice.Consumer.Tests;

/// <summary>Builds contract values for the consumer tests; a refused value fails the test.</summary>
internal static class Make
{
    public static T Ok<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(value => value, reason => throw new XunitException($"refused: {reason.Code}"));

    public static ModId Mod(string id) => Ok(ModId.Create(id));

    public static Maker Maker(string mod, string name) => Ok(Api.Maker.Create(Mod(mod), name));

    public static Actor.Player Player() => new(new ClientId(76561198000000001));

    public static Actor.Agent Foreman() => new(Ok(AgentId.Create("foreman")));

    public static Ghost Ghost(long id, Intent intent, Maker maker) =>
        new(new GhostId(id),
            new PlanId(1),
            new Revision(1),
            intent,
            new GhostState.Planned(),
            Player(),
            maker,
            DateTimeOffset.UnixEpoch,
            new Visibility.Everyone(),
            Option<Label>.None,
            Option<Tint>.None,
            ValueSet<GroupName>.Empty,
            ValueMap<AttachmentKey, Attachment>.Empty);

    public static Intent.Build Build(int prefab, int x) =>
        new(new Placement(new PrefabKey(prefab), new Cell(x, 0, 0), Turn.None), Option<Finish>.None);

    public static ChangeSet Changes(CommandId command, Actor by, ValueList<Ghost> added) =>
        new(new ChangeSetId(1),
            command,
            by,
            new Revision(2),
            DateTimeOffset.UnixEpoch,
            added,
            ValueList<GhostChange>.Empty,
            ValueList<GhostRemoval>.Empty,
            ValueList<Plan>.Empty,
            ValueList<PlanChange>.Empty,
            ValueList<PlanId>.Empty);

    public static Bill EmptyBill() =>
        new(ValueList<ItemQuantity>.Empty, ValueList<ItemQuantity>.Empty, ValueList<ItemDelta>.Empty, ValueSet<ItemKey>.Empty);

    public static AnalysisReport NoEffects() =>
        new(ValueList<NetworkEffect>.Empty, ValueList<RoomEffect>.Empty, ValueList<GasEffect>.Empty, ValueList<AnalyzerFact>.Empty);
}
