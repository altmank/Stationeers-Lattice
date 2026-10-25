using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.World;

namespace Lattice.TestKit.Scenarios;

/// <summary>A built scenario: the world, the ghosts in placing order, and the catalogue both use.</summary>
internal sealed record BuiltScenario(FakeWorld World, ValueList<Ghost> Ghosts, FakeCatalogue Catalogue);

/// <summary>
/// A few lines place pieces and ghosts for a test:
/// <code>
/// var s = Scenario.With(FakeCatalogue.Hand())
///     .Built("StructureFrameIron", new Cell(10, 10, 10), out var frame)
///     .Built("StructureCableStraight", new Cell(5, 5, 5), turn: 1)
///     .Ghost("StructureCableCorner", new Cell(5, 5, 10))
///     .Removal(frame)
///     .Build();
/// </code>
/// Structure ids count from 1, ghost ids from 1, in call order. Every ghost is planned, in plan 1, placed by player 1,
/// made by <c>testkit/scenario</c>, seen by everyone. Unknown prefab names are a bug in the test and throw.
/// </summary>
internal sealed class Scenario
{
    public static readonly PlanId Plan = new(1);
    public static readonly Actor Placer = new Actor.Player(new ClientId(1));
    public static readonly Maker Maker = TestKitIds.Must(Maker.Create(TestKitIds.Mod, "scenario"));
    public static readonly DateTimeOffset PlacedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeCatalogue _catalogue;
    private readonly List<StructureInfo> _structures = [];
    private readonly List<Ghost> _ghosts = [];
    private readonly List<RoomInfo> _rooms = [];

    private Scenario(FakeCatalogue catalogue) => _catalogue = catalogue;

    public static Scenario With(FakeCatalogue catalogue) => new(catalogue);

    /// <summary>A structure of the piece; at its last build step unless <paramref name="step"/> says otherwise.</summary>
    public Scenario Built(string piece, Cell at, int turn = 0, int? step = null, int? colour = null, string? name = null) =>
        Built(piece, at, out _, turn, step, colour, name);

    public Scenario Built(string piece, Cell at, out StructureRef built, int turn = 0, int? step = null, int? colour = null, string? name = null)
    {
        var facts = _catalogue.Named(piece);
        var last = facts.Steps.Count - 1;
        var stepIndex = step ?? last;
        built = new StructureRef(new ReferenceId(_structures.Count + 1), facts.Key, at);
        _structures.Add(new StructureInfo(
            built,
            TestKitIds.Must(Turn.Create(turn)),
            BuildStates.At(TestKitIds.Must(StepIndex.Create(stepIndex)), Option.Some(facts)),
            colour is { } c ? Option.Some(TestKitIds.Must(PaintColour.Create(c))) : Option<PaintColour>.None,
            name is { } n ? Option.Some(TestKitIds.Must(ThingName.Create(n))) : Option<ThingName>.None,
            Option<Setting>.None));
        return this;
    }

    /// <summary>A ghost asking for the piece to be built here.</summary>
    public Scenario Ghost(string piece, Cell at, int turn = 0) => Ghost(piece, at, out _, turn);

    public Scenario Ghost(string piece, Cell at, out GhostId ghost, int turn = 0)
    {
        var placement = new Placement(_catalogue.Named(piece).Key, at, TestKitIds.Must(Turn.Create(turn)));
        return Add(new Intent.Build(placement, Option<Finish>.None), out ghost);
    }

    /// <summary>A ghost asking for a structure to be removed.</summary>
    public Scenario Removal(StructureRef target) => Removal(target, out _);

    public Scenario Removal(StructureRef target, out GhostId ghost) => Add(new Intent.Remove(target), out ghost);

    /// <summary>A room over the given large cells.</summary>
    public Scenario Room(long id, bool sealedRoom, params Cell[] cells)
    {
        _rooms.Add(new RoomInfo(new RoomRef(id), ValueList.From(cells), sealedRoom, Option<ValueList<GasHeld>>.None));
        return this;
    }

    public BuiltScenario Build()
    {
        var world = new FakeWorld(_catalogue);
        foreach (var structure in _structures)
            world.Load(structure);
        foreach (var room in _rooms)
            world.SetRoom(room);
        return new BuiltScenario(world, ValueList.From(_ghosts), _catalogue);
    }

    private Scenario Add(Intent intent, out GhostId ghost)
    {
        ghost = new GhostId(_ghosts.Count + 1);
        _ghosts.Add(new Ghost(
            ghost,
            Plan,
            new Revision(_ghosts.Count + 1),
            intent,
            new GhostState.Planned(),
            Placer,
            Maker,
            PlacedAt,
            new Visibility.Everyone(),
            Option<Label>.None,
            Option<Tint>.None,
            ValueSet<GroupName>.Empty,
            ValueMap<AttachmentKey, Attachment>.Empty));
        return this;
    }
}
