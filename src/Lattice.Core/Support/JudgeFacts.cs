using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Validation;

namespace Lattice.Core.Support;

/// <summary>
/// The built world as judging reads it: structures, and the facts only the main thread can read (rooms, networks, the
/// game's placement check, mods' main-thread rules). For each of those an outer none means "not known yet": the
/// judgement asks for it and is incomplete until it is answered.
/// </summary>
internal interface IJudgeFacts
{
    Option<StructureInfo> Structure(ReferenceId id);

    ValueList<StructureInfo> At(Cell cell);

    Option<Option<NetworkRef>> NetworkOf(ReferenceId id);

    Option<Option<RoomRef>> RoomAt(Cell cell);

    Option<GameAnswer> Game(Placement placement);

    Option<Guarded<SupportVerdict>> MainSupport(SupportRules rules, SupportRules.Entry entry, GhostId ghost, SupportInput input);

    Option<Guarded<RuleVerdict>> MainRule(ValidationRules rules, ValidationRules.Entry entry, RuleInput input);

    /// <summary>The ghost's epoch: a main-thread rule's answer counts only for the epoch it was asked at.</summary>
    int EpochOf(GhostId ghost);
}

/// <summary>
/// Facts read straight from a world view, with the game's answers from a map: for judging on the thread that owns the
/// view (the main thread over the game, or a test). Main-thread rules are called at once.
/// </summary>
internal sealed class DirectFacts(IWorldView world, GameChecks checks) : IJudgeFacts
{
    public Option<StructureInfo> Structure(ReferenceId id) => world.Structure(id);

    public ValueList<StructureInfo> At(Cell cell) => world.At(cell);

    public Option<Option<NetworkRef>> NetworkOf(ReferenceId id) => Option.Some(world.NetworkOf(id));

    public Option<Option<RoomRef>> RoomAt(Cell cell) => Option.Some(world.RoomAt(cell));

    public Option<GameAnswer> Game(Placement placement) => checks.For(placement);

    public Option<Guarded<SupportVerdict>> MainSupport(SupportRules rules, SupportRules.Entry entry, GhostId ghost, SupportInput input) =>
        Option.Some(rules.Check(entry, input));

    public Option<Guarded<RuleVerdict>> MainRule(ValidationRules rules, ValidationRules.Entry entry, RuleInput input) =>
        Option.Some(rules.Check(entry, input));

    public int EpochOf(GhostId ghost) => 0;
}

/// <summary>
/// The structures of judging facts as an <see cref="IWorldView"/>, for the consequence models that take one; rooms
/// and networks not known yet read as none, and the facts it does not carry read as empty.
/// </summary>
internal sealed class FactsWorld(IJudgeFacts facts) : IWorldView
{
    public Option<StructureInfo> Structure(ReferenceId id) => facts.Structure(id);

    public ValueList<StructureInfo> At(Cell cell) => facts.At(cell);

    public Option<NetworkRef> NetworkOf(ReferenceId id) =>
        facts.NetworkOf(id).TryGet(out var network) ? network : Option<NetworkRef>.None;

    public Option<NetworkInfo> Network(NetworkRef network) => Option<NetworkInfo>.None;

    public Option<RoomRef> RoomAt(Cell cell) => facts.RoomAt(cell).TryGet(out var room) ? room : Option<RoomRef>.None;

    public Option<RoomInfo> Room(RoomRef room) => Option<RoomInfo>.None;

    public Kilopascals PressureDifference(Cell cell, Direction face) => new(0);

    public bool Terrain(Cell large) => false;
}
