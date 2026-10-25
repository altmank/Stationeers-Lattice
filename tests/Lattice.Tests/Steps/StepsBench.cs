using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;
using Lattice.Core.Steps;
using Lattice.Tests.Matching;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;

namespace Lattice.Tests.Steps;

/// <summary>
/// The step planner and the bills over a board, a fake world and its mirror (<see cref="TestWorld"/>), with the hand
/// catalogue plus the pieces given. Call <see cref="Run"/> after loading the world so the mirror and matching start.
/// </summary>
internal sealed class StepsBench
{
    public StepsBench(params NamedPiece[] extraPieces)
        : this(static catalogue => catalogue, extraPieces)
    {
    }

    private StepsBench(System.Func<FakeCatalogue, ICatalogue> plannerCatalogue, NamedPiece[] extraPieces)
    {
        W = new TestWorld(extraPieces);
        Planner = new StepPlanner(W.T.Board, W.Mirror, plannerCatalogue(W.T.Catalogue), new Shapes(W.T.Catalogue, W.T.Grid), Order);
        Bills = new BillMaker(W.T.Board, Planner, W.Mirror);
    }

    /// <summary>A bench whose planner reads a catalogue that names no tool for broken structures.</summary>
    public static StepsBench WithoutBrokenTools() => new(static catalogue => catalogue.WithBrokenTools(), []);

    public TestWorld W { get; }

    public SetOrder Order { get; } = new();

    public StepPlanner Planner { get; }

    public BillMaker Bills { get; }

    public FakeCatalogue Catalogue => W.T.Catalogue;

    public void Run() => W.Run();

    public StepPlan Steps(GhostId ghost) => TestKitIds.Must(Planner.For(ghost));

    public Reason Refusal(GhostId ghost) =>
        Planner.For(ghost).TryGetError(out var why) ? why : throw new System.InvalidOperationException("steps given");

    public Bill Bill(Selector which) => TestKitIds.Must(Bills.For(which));

    public KitKey Kit(string name) => PrefabHash.Kit(name);

    public static ItemKey Item(string name) => PrefabHash.Item(name);

    public static ItemQuantity Of(string item, int count) => TestKitIds.Must(ItemQuantity.Create(PrefabHash.Item(item), count));

    public static Option<ItemQuantity> Some(string item, int count) => Option.Some(Of(item, count));

    public static KitOption OptionOf(int index) => TestKitIds.Must(KitOption.Create(index));

    /// <summary>The structure a ghost's steps work on before the kit is placed: no id yet, found by piece and cell.</summary>
    /// <summary>The structure the step at this index of the same plan places.</summary>
    public static StepTarget PlacedBy(int step) => new StepTarget.PlacedBy(step);

    public static StepTarget Standing(StructureRef structure) => new StepTarget.Standing(structure);

    public static NamedPiece[] Pieces(string text)
    {
        var catalogue = TestKitIds.MustRead(CatalogueFile.Read(new StringReader("lattice-catalogue 3\n" + text), "inline.lcat"));
        var pieces = new NamedPiece[catalogue.Pieces.Count];
        for (var i = 0; i < pieces.Length; i++)
            pieces[i] = catalogue.Pieces[i];
        return pieces;
    }

    /// <summary>The pieces with merge facts: a kit of theirs placed on one of them merges, the given tool in the other hand.</summary>
    public static NamedPiece[] Merging(string offhandTool, params NamedPiece[] pieces)
    {
        var merging = new NamedPiece[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
            merging[i] = pieces[i] with { Facts = pieces[i].Facts with { Merge = Option.Some(new MergeFacts(Item(offhandTool))) } };
        return merging;
    }
}

/// <summary>A build order's facts set by hand: which ghosts wait for which, which join pipe networks.</summary>
internal sealed class SetOrder : IOrderFacts
{
    private readonly Dictionary<GhostId, ValueList<GhostId>> _waits = [];
    private readonly HashSet<GhostId> _joins = [];

    public void Waits(GhostId ghost, params GhostId[] others) => _waits[ghost] = ValueList.From(others);

    public void Joins(GhostId ghost) => _joins.Add(ghost);

    public ValueList<GhostId> WaitsFor(GhostId ghost) => _waits.TryGetValue(ghost, out var others) ? others : ValueList<GhostId>.Empty;

    public bool JoinsPipes(GhostId ghost) => _joins.Contains(ghost);
}
