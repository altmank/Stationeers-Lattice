using System;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Safety;


/// <summary>
/// The facts build safety reads that neither the catalogue nor the world view gives yet. Catalogue facts are found by
/// the piece's type, so a modded subclass is the same; world facts are read on the main thread like the world view's.
/// </summary>
internal interface ISafetyFacts
{
    /// <summary>What the device keeps going; <see cref="Ports.Keeps.None"/> for anything else.</summary>
    Keeps Keeps(PrefabKey piece);

    /// <summary>Whether the piece feeds power into the networks it sits on (generators, batteries, power controls).</summary>
    bool Supplies(PrefabKey piece);

    /// <summary>The side the piece's controls face, unturned; none for a piece with no control a player uses.</summary>
    Option<Direction> ControlFace(PrefabKey piece);

    /// <summary>Whether the piece opens its pipe network to the air of its cell (a passive vent).</summary>
    bool Vents(PrefabKey piece);

    /// <summary>The room's air temperature; none when not read.</summary>
    Option<Kelvin> Temperature(RoomRef room);

    /// <summary>The pipe network's gas temperature; none when not read.</summary>
    Option<Kelvin> Temperature(NetworkRef network);

    /// <summary>Whether terrain fills any part of the large cell.</summary>
    bool Terrain(Cell large);
}

/// <summary>
/// Nothing known: what Lattice has until the catalogue and the world view read these facts. Every warning that
/// needs one of them stays silent.
/// </summary>
internal sealed class NoSafetyFacts : ISafetyFacts
{
    public Keeps Keeps(PrefabKey piece) => Ports.Keeps.None;

    public bool Supplies(PrefabKey piece) => false;

    public Option<Direction> ControlFace(PrefabKey piece) => Option<Direction>.None;

    public bool Vents(PrefabKey piece) => false;

    public Option<Kelvin> Temperature(RoomRef room) => Option<Kelvin>.None;

    public Option<Kelvin> Temperature(NetworkRef network) => Option<Kelvin>.None;

    public bool Terrain(Cell large) => false;
}

/// <summary>
/// The facts as the catalogue and the world view read them: the piece facts <see cref="PieceFacts.Keeps"/>,
/// <see cref="PieceFacts.Supplies"/>, <see cref="PieceFacts.ControlFace"/> and <see cref="PieceFacts.Vents"/> (none or
/// false for a piece the catalogue does not hold), and the room, network and terrain reads of the world view. Main
/// thread; each call is one catalogue or world view read.
/// </summary>
internal sealed class CatalogueSafetyFacts(ICatalogue catalogue, IWorldView world) : ISafetyFacts
{
    public Keeps Keeps(PrefabKey piece) =>
        catalogue.Piece(piece).Match(static facts => facts.Keeps, static _ => Ports.Keeps.None);

    public bool Supplies(PrefabKey piece) =>
        catalogue.Piece(piece).Match(static facts => facts.Supplies, static _ => false);

    public Option<Direction> ControlFace(PrefabKey piece) =>
        catalogue.Piece(piece).Match(static facts => facts.ControlFace, static _ => Option<Direction>.None);

    public bool Vents(PrefabKey piece) =>
        catalogue.Piece(piece).Match(static facts => facts.Vents, static _ => false);

    public Option<Kelvin> Temperature(RoomRef room) =>
        world.Room(room).TryGet(out var info) ? info.Temperature : Option<Kelvin>.None;

    public Option<Kelvin> Temperature(NetworkRef network) =>
        world.Network(network).TryGet(out var info) ? info.Temperature : Option<Kelvin>.None;

    public bool Terrain(Cell large) => world.Terrain(large);
}

/// <summary>Where build safety draws its lines. Lattice's choice, not a game rule.</summary>
/// <param name="HotGas">Gas at or above this temperature counts as hot: 50 °C (323.15 K) by default.</param>
internal sealed record SafetyLimits(Kelvin HotGas)
{
    public static SafetyLimits Default => new(new Kelvin(323.15));
}
