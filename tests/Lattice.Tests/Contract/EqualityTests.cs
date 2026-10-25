using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Records holding collections compare by content.</summary>
public sealed class EqualityTests
{
    [Fact]
    public void Value_lists_compare_by_elements_in_order()
    {
        AssertEqualPair(ValueList.From(new[] { 1, 2, 3 }), ValueList.From(new List<int> { 1, 2, 3 }));
        Assert.NotEqual(ValueList.From(new[] { 1, 2, 3 }), ValueList.From(new[] { 3, 2, 1 }));
    }

    [Fact]
    public void Value_sets_compare_by_elements_in_any_order()
    {
        AssertEqualPair(ValueSet.From(new[] { "a", "b" }), ValueSet.From(new[] { "b", "a", "a" }));
        Assert.NotEqual(ValueSet.From(new[] { "a" }), ValueSet.From(new[] { "a", "b" }));
    }

    [Fact]
    public void Value_maps_compare_by_entries()
    {
        AssertEqualPair(ValueMap<string, string>.Empty.With("a", "1").With("b", "2"), ValueMap<string, string>.Empty.With("b", "2").With("a", "1"));
        Assert.NotEqual(ValueMap.Of("a", "1"), ValueMap.Of("a", "2"));
    }

    [Fact]
    public void A_value_list_copies_its_input()
    {
        var source = new List<int> { 1 };
        var list = ValueList.From(source);

        source.Add(2);

        Assert.Single(list);
    }

    [Fact]
    public void Selectors_with_lists_compare_by_content()
    {
        AssertEqualPair(new Selector.Ids(Ids(1, 2)), new Selector.Ids(Ids(1, 2)));
        AssertEqualPair(
            new Selector.All(ValueList.From(new Selector[] { new Selector.InPlan(new PlanId(1)), new Selector.Ids(Ids(3)) })),
            new Selector.All(ValueList.From(new Selector[] { new Selector.InPlan(new PlanId(1)), new Selector.Ids(Ids(3)) })));
    }

    [Fact]
    public void Catalogue_entries_compare_by_content()
    {
        static KitPlace Place(int option) => new(new KitKey(7), KitOption.Create(option).Match(static o => o, static _ => default));

        AssertEqualPair(
            new PieceEntry(new PrefabKey(1), "Piece", "Piece", PieceGrid.Small, ValueList.From(new[] { Place(0), Place(2) })),
            new PieceEntry(new PrefabKey(1), "Piece", "Piece", PieceGrid.Small, ValueList.From(new List<KitPlace> { Place(0), Place(2) })));
        AssertEqualPair(
            new KitEntry(new KitKey(7), "Kit", "Kit", ValueList.From(new[] { new PrefabKey(1), new PrefabKey(2) })),
            new KitEntry(new KitKey(7), "Kit", "Kit", ValueList.From(new List<PrefabKey> { new(1), new(2) })));
        Assert.NotEqual(
            new KitEntry(new KitKey(7), "Kit", "Kit", ValueList.From(new[] { new PrefabKey(1), new PrefabKey(2) })),
            new KitEntry(new KitKey(7), "Kit", "Kit", ValueList.From(new[] { new PrefabKey(2), new PrefabKey(1) })));
    }

    [Fact]
    public void Ghosts_compare_by_content()
    {
        AssertEqualPair(Ghost(), Ghost());
        Assert.NotEqual(Ghost(), Ghost() with { Groups = ValueSet<GroupName>.Empty });
    }

    [Fact]
    public void Reasons_compare_by_their_facts() =>
        AssertEqualPair(Error(Turn.Create(30)), Error(Turn.Create(30)));

    [Fact]
    public void Change_sets_commands_and_step_plans_compare_by_content()
    {
        var id = CommandId.New();
        AssertEqualPair(Changes(id), Changes(id));
        AssertEqualPair(new Command.Place(ValueList.Of(Draft())), new Command.Place(ValueList.Of(Draft())));
        AssertEqualPair(new Command.Batch(ValueList.Of<Command>(new Command.Remove(new Selector.Ids(Ids(1))))),
            new Command.Batch(ValueList.Of<Command>(new Command.Remove(new Selector.Ids(Ids(1))))));
        AssertEqualPair(StepPlan(), StepPlan());
        AssertEqualPair(new Outcome.Refused(ValueList.Of(new Refusal(Error(Turn.Create(30)), Ids(1)))),
            new Outcome.Refused(ValueList.Of(new Refusal(Error(Turn.Create(30)), Ids(1)))));
    }

    [Fact]
    public void Plans_and_bills_compare_by_content()
    {
        AssertEqualPair(Plan(), Plan());
        AssertEqualPair(Bill(), Bill());
    }

    [Fact]
    public void Answers_load_reports_and_module_lists_compare_by_content()
    {
        AssertEqualPair(new Answer<Bill>.Ready(Bill(), new Revision(3)), new Answer<Bill>.Ready(Bill(), new Revision(3)));
        Assert.NotEqual<Answer<Bill>>(new Answer<Bill>.Pending(new Revision(3)), new Answer<Bill>.Ready(Bill(), new Revision(3)));
        AssertEqualPair(
            new LoadReport(new SaveRecord.Missing(), ValueList.Of(new PlanAhead(new PlanId(1), 4))),
            new LoadReport(new SaveRecord.Missing(), ValueList.Of(new PlanAhead(new PlanId(1), 4))));
    }

    [Fact]
    public void Value_sets_and_maps_walk_every_element()
    {
        var seen = 0;
        foreach (var item in ValueSet.From(new[] { 1, 2, 3 }))
            seen += item;
        foreach (var entry in ValueMap.Of("a", 4))
            seen += entry.Value;

        Assert.Equal(10, seen);
    }

    private static void AssertEqualPair<T>(T a, T b)
    {
        Assert.False(ReferenceEquals(a, b));
        Assert.Equal(a, b);
        Assert.Equal(a!.GetHashCode(), b!.GetHashCode());
    }

    private static ValueList<GhostId> Ids(params long[] ids)
    {
        var list = new List<GhostId>();
        foreach (var id in ids)
            list.Add(new GhostId(id));
        return ValueList.From(list);
    }

    private static T Ok<T>(Result<T, Reason> result) where T : notnull => result.Match(v => v, r => throw new InvalidOperationException(r.Code.Value));

    private static Reason Error<T>(Result<T, Reason> result) where T : notnull => result.Match(_ => throw new InvalidOperationException(), r => r);

    private static ModId Mod => Ok(ModId.Create("mymod"));

    private static Ghost Ghost() =>
        new(new GhostId(1), new PlanId(1), new Revision(1),
            new Intent.Build(new Placement(new PrefabKey(10), new Cell(1, 2, 3), Turn.None), Option<Finish>.None),
            new GhostState.Planned(), new Actor.Mod(Mod), Ok(Maker.Create(Mod, "tool")), DateTimeOffset.UnixEpoch,
            new Visibility.Everyone(), Option.Some(Ok(Label.Create("a"))), Option<Tint>.None,
            ValueSet.From(new[] { Ok(GroupName.Create("Hydro power")) }),
            ValueMap.Of(Ok(AttachmentKey.Create(Mod, "job")), Ok(Attachment.Create("412"))));

    private static GhostDraft Draft() =>
        new(new Intent.Remove(new StructureRef(new ReferenceId(5), new PrefabKey(10), new Cell(0, 0, 0))), Option<PlanId>.None,
            Ok(Maker.Create(Mod, "tool")), new Visibility.OwnerOnly(), Option<Label>.None, Option<Tint>.None,
            ValueSet.Of(Ok(GroupName.Create("g"))), ValueMap<AttachmentKey, Attachment>.Empty);

    private static ChangeSet Changes(CommandId id) =>
        new(new ChangeSetId(1), id, new Actor.Mod(Mod), new Revision(2), DateTimeOffset.UnixEpoch,
            ValueList.Of(Ghost()), ValueList.Of(new GhostChange(Ghost(), Ghost())),
            ValueList.Of(new GhostRemoval(Ghost(), new RemovalCause.Replaced(new GhostId(2)))),
            ValueList.Of(Plan()), ValueList<PlanChange>.Empty, ValueList<PlanId>.Empty);

    private static StepPlan StepPlan() =>
        new(new GhostId(1),
            ValueList.Of<Step>(new Step.TakeBack(new StructureRef(new ReferenceId(5), new PrefabKey(10), new Cell(0, 0, 0)), new ItemKey(3),
                ValueList.Of(Ok(ItemQuantity.Create(new ItemKey(4), 2))))),
            ValueList.Of<Constraint>(new Constraint.After(new GhostId(9))));

    private static Plan Plan() =>
        new(new PlanId(1), new Revision(1), Ok(PlanName.Create("North store")), new Actor.Mod(Mod), Ok(Maker.Create(Mod, "tool")),
            new Visibility.Team(Ok(TeamName.Create("Crew"))), ValueMap.Of(Ok(AttachmentKey.Create(Mod, "state")), Ok(Attachment.Create("draft"))));

    private static Bill Bill() =>
        new(ValueList.Of(Ok(ItemQuantity.Create(new ItemKey(1), 3))), ValueList<ItemQuantity>.Empty,
            ValueList.Of(new ItemDelta(new ItemKey(1), 3)), ValueSet.Of(new ItemKey(7)));
}
