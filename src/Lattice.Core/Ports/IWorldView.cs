using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Ports;

/// <summary>A volume in litres.</summary>
internal readonly record struct Litres(double Value);

/// <summary>An amount of energy in joules.</summary>
internal readonly record struct Joules(double Value);

/// <summary>One gas held in a network or a room: its moles and the energy they carry (conservation checks both).</summary>
internal sealed record GasHeld(GasKind Gas, Moles Amount, Joules Energy);

/// <summary>
/// A network: the kinds it carries (a cable network carries power and data; a modded network its
/// <see cref="NetworkType.Unknown"/> kind), its pieces and devices, the cells its pieces fill, load and rating, and for a pipe network its volume,
/// pressure and contents gas by gas. A fact the world did not read is none; a network that holds nothing has empty
/// contents.
/// </summary>
internal sealed record NetworkInfo(
    NetworkRef Ref,
    ValueSet<NetworkType> Kinds,
    ValueList<ReferenceId> Pieces,
    ValueList<ReferenceId> Devices,
    ValueList<Cell> Cells,
    Option<Watts> Load,
    Option<Watts> Rating,
    Option<Litres> Volume,
    Option<Kilopascals> Pressure,
    Option<ValueList<GasHeld>> Contents)
{
    /// <summary>The network's gas temperature (the game's <c>Atmosphere.Temperature</c>); none when not read.</summary>
    public Option<Kelvin> Temperature { get; init; }
}

/// <summary>A room: its large cells, whether it is sealed, and its air gas by gas (none when not read).</summary>
internal sealed record RoomInfo(RoomRef Ref, ValueList<Cell> Cells, bool Sealed, Option<ValueList<GasHeld>> Air)
{
    /// <summary>The room's air temperature (the game's <c>Atmosphere.Temperature</c>); none when not read.</summary>
    public Option<Kelvin> Temperature { get; init; }
}

/// <summary>The built world: cells, structures, networks, faces, rooms. Main thread over the game; faked in tests.</summary>
internal interface IWorldView
{
    Option<StructureInfo> Structure(ReferenceId id);

    ValueList<StructureInfo> At(Cell cell);

    Option<NetworkRef> NetworkOf(ReferenceId id);

    Option<NetworkInfo> Network(NetworkRef network);

    Option<RoomRef> RoomAt(Cell cell);

    Option<RoomInfo> Room(RoomRef room);

    /// <summary>The pressure difference across a large cell's face.</summary>
    Kilopascals PressureDifference(Cell cell, Direction face);

    /// <summary>Whether voxel terrain fills any part of the large cell (a server holds terrain for the whole world).</summary>
    bool Terrain(Cell large);
}

/// <summary>Would the game's construction cursor accept this piece here; its own message if not.</summary>
internal interface IPlacementCheck
{
    Result<Unit, Reason> Check(Placement placement);
}

/// <summary>
/// Signals from the game the completion watcher and add-ons need. <see cref="StateChanged"/> carries a structure's new
/// build state, broken included; a structure taken apart by hand passes through broken on its way out, and the world
/// reports that as <see cref="Removed"/> only.
/// </summary>
internal interface IWorldChanges
{
    event Action<StructureInfo> Registered;

    event Action<StructureRef, BuildState> StateChanged;

    event Action<StructureRef> Removed;

    event Action<StructureRef, PaintColour> Painted;

    event Action<StructureRef, ThingName> Renamed;

    /// <summary>A structure's labeller setting changed (<see cref="Setting"/>, the one value a labeller sets).</summary>
    event Action<StructureRef, Setting> SettingChanged;

    event Action<ValueList<StructureMove>> Moved;
}
