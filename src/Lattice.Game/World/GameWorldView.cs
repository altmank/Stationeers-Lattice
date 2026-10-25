using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.World;
using Networks;
using TerrainSystem;
using Cell = Lattice.Api.Cell;
using Direction = Lattice.Core.Ports.Direction;
using NetworkType = Lattice.Core.Ports.NetworkType;

namespace Lattice.Game.World;

/// <summary>
/// The built world read live from the game: every structure the game holds, modded ones included, read through the
/// same base members. Main thread only; each call reads the game directly, so cost is per call: a structure or a room by
/// id is a lookup, a cell is linear in what stands in it, a network or room is linear in its members.
/// <list type="bullet">
/// <item>A cell holds what the game's small cell, large cell and face lookup at its Grid3 key hold.</item>
/// <item>A structure's network: a cable's, a pipe's or chute's (and any other networked structure's), else a device's
/// power network, else its data network.</item>
/// <item>A network's kinds: power and data for cables; gas or liquid for pipes by the network's content type; chute;
/// any other game network type as <see cref="NetworkType.Unknown"/> with its name and value.</item>
/// <item>The scan is every structure in the game's structure pool, once per world.</item>
/// <item>A room is the game's room (only sealed spaces are rooms), its cells, air and air temperature.</item>
/// <item>A pipe network's temperature is its gas mixture's; a network with no gas, or a room with none, has none.</item>
/// <item>Terrain fills a large cell when any of its eight one-metre voxels is denser than the game's solid threshold
/// (<c>VoxelTerrain.GetDensityWorldSpace</c>; decompile TerrainSystem/VoxelTerrain.cs
/// <c>UpdateCanAirPass</c>, Assets.Scripts.Objects/SmallGrid.cs <c>HasVoxelBelow</c>). A server holds the voxels of
/// the whole world; a world without terrain reads as clear.</item>
/// <item>A face's pressure difference is this cell's pressure minus its neighbour's across the face.</item>
/// </list>
/// </summary>
internal sealed class GameWorldView : IWorldView, IWorldScan
{
    private const int LargeCell = 20;

    // The game's density above which a voxel is solid (VoxelTerrain.UpdateCanAirPass, SmallGrid.HasVoxelBelow).
    private const float SolidDensity = 0.49803922f;

    // Half a voxel, in metres: the eight voxels of a 2 m cell lie half a metre each way from its centre.
    private const float VoxelHalf = 0.5f;

    private static readonly ValueSet<NetworkType> CableKinds = ValueSet.From(new NetworkType[] { new NetworkType.Power(), new NetworkType.Data() });

    private readonly Chemistry.GasType[] _gases;
    private readonly GasKind[] _gasKinds;

    public GameWorldView()
    {
        var all = (Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType));
        var gases = new List<Chemistry.GasType>(all.Length);
        var kinds = new List<GasKind>(all.Length);
        foreach (var gas in all)
            if (gas != Chemistry.GasType.Undefined && GasKind.Create(GameText.Token(gas.ToString())).TryGet(out var kind))
            {
                gases.Add(gas);
                kinds.Add(kind);
            }

        _gases = gases.ToArray();
        _gasKinds = kinds.ToArray();
    }

    public Option<StructureInfo> Structure(ReferenceId id)
    {
        var structure = Find(id);
        return structure != null && GameReads.Info(structure).TryGet(out var info) ? Option.Some(info) : Option<StructureInfo>.None;
    }

    public ValueList<StructureInfo> At(Cell cell)
    {
        var world = GridController.World;
        if (world == null)
            return ValueList<StructureInfo>.Empty;
        var grid = GameReads.Grid(cell);
        var found = new List<StructureInfo>(4);
        var seen = new HashSet<long>();
        if (world.GetSmallCell(grid) is { } small)
        {
            Add(small.Cable, found, seen);
            Add(small.Pipe, found, seen);
            Add(small.Chute, found, seen);
            Add(small.Device, found, seen);
            Add(small.Other, found, seen);
            Add(small.Rail as Structure, found, seen);
        }

        if (world.GetCell(grid) is { } large)
            foreach (var structure in large.AllStructures)
                Add(structure, found, seen);
        foreach (var structure in world.GetFaceStructures(grid))
            Add(structure, found, seen);
        return found.Count == 0 ? ValueList<StructureInfo>.Empty : ValueList.From(found);
    }

    /// <summary>
    /// Every structure the game holds (<c>GridController.AllStructuresPool</c>), cursors left out. Called once when a
    /// world runs, after the bridge's signals are subscribed; linear in the world's structures.
    /// </summary>
    public ValueList<StructureInfo> Structures()
    {
        var found = new List<StructureInfo>(GridController.AllStructuresPool.ActiveCount);
        GridController.AllStructuresPool.ForEach(structure =>
        {
            if (structure != null && !structure.IsCursor && GameReads.Info(structure).TryGet(out var info))
                found.Add(info);
        });
        return ValueList.From(found);
    }

    public Option<NetworkRef> NetworkOf(ReferenceId id)
    {
        var structure = Find(id);
        if (structure == null)
            return Option<NetworkRef>.None;
        if (structure is Cable cable)
            return Of(cable.CableNetwork);
        if (structure is INetworkedStructure { StructureNetwork: not null } networked)
            return Option.Some(new NetworkRef(networked.StructureNetwork.ReferenceId));
        if (structure is Device device)
            return device.PowerCableNetwork != null ? Of(device.PowerCableNetwork) : Of(device.DataCableNetwork);
        return Option<NetworkRef>.None;
    }

    public Option<NetworkInfo> Network(NetworkRef network) =>
        Referencable.Find(network.Value) switch
        {
            CableNetwork cables => Option.Some(Read(cables)),
            StructureNetwork structures => Option.Some(Read(structures)),
            _ => Option<NetworkInfo>.None,
        };

    public Option<RoomRef> RoomAt(Cell cell) =>
        RoomController.World?.GetRoom(GameReads.Grid(cell)) is { } room
            ? Option.Some(new RoomRef(room.RoomId))
            : Option<RoomRef>.None;

    public Option<RoomInfo> Room(RoomRef room)
    {
        if (RoomController.Get(room.Value) is not { } found)
            return Option<RoomInfo>.None;
        var cells = new List<Cell>(found.Grids.Count);
        foreach (var grid in found.Grids)
            cells.Add(GameReads.Cell(grid.Value));
        return Option.Some(new RoomInfo(room, ValueList.From(cells), true, Option.Some(Contents(found.GasMixture)))
        {
            Temperature = TemperatureOf(found.GasMixture),
        });
    }

    /// <summary>Whether voxel terrain fills part of the large cell: eight voxel reads.</summary>
    public bool Terrain(Cell large)
    {
        var centre = GameReads.Position(large);
        for (var corner = 0; corner < 8; corner++)
        {
            var at = new UnityEngine.Vector3(
                centre.x + ((corner & 1) == 0 ? -VoxelHalf : VoxelHalf),
                centre.y + ((corner & 2) == 0 ? -VoxelHalf : VoxelHalf),
                centre.z + ((corner & 4) == 0 ? -VoxelHalf : VoxelHalf));
            if (VoxelTerrain.GetDensityWorldSpace(at) > SolidDensity)
                return true;
        }

        return false;
    }

    public Kilopascals PressureDifference(Cell cell, Direction face)
    {
        var beyond = cell.Plus(face.Unit().Times(LargeCell));
        return new Kilopascals(PressureAt(cell) - PressureAt(beyond));
    }

    private static Structure? Find(ReferenceId id) => Referencable.Find<Structure>(id.Value);

    private static Option<NetworkRef> Of(CableNetwork? network) =>
        network == null ? Option<NetworkRef>.None : Option.Some(new NetworkRef(network.ReferenceId));

    private static void Add(Structure? structure, List<StructureInfo> found, HashSet<long> seen)
    {
        if (structure == null || structure.IsCursor || !seen.Add(structure.ReferenceId))
            return;
        if (GameReads.Info(structure).TryGet(out var info))
            found.Add(info);
    }

    private static NetworkInfo Read(CableNetwork network)
    {
        var pieces = new List<ReferenceId>(network.CableList.Count);
        var cells = new List<Cell>(network.CableList.Count);
        foreach (var cable in network.CableList)
            if (cable != null)
            {
                pieces.Add(new ReferenceId(cable.ReferenceId));
                cells.Add(GameReads.Ref(cable).At);
            }

        var devices = new List<ReferenceId>(network.DeviceList.Count);
        foreach (var device in network.DeviceList)
            if (device != null)
                devices.Add(new ReferenceId(device.ReferenceId));
        return new NetworkInfo(
            new NetworkRef(network.ReferenceId),
            CableKinds,
            ValueList.From(pieces),
            ValueList.From(devices),
            ValueList.From(cells),
            Option.Some(new Watts(network.CurrentLoad)),
            Option<Watts>.None,
            Option<Litres>.None,
            Option<Kilopascals>.None,
            Option<ValueList<GasHeld>>.None);
    }

    private NetworkInfo Read(StructureNetwork network)
    {
        var pieces = new List<ReferenceId>(network.StructureList.Count);
        var cells = new List<Cell>(network.StructureList.Count);
        foreach (var member in network.StructureList)
            if (member is Structure structure && structure != null)
            {
                pieces.Add(new ReferenceId(structure.ReferenceId));
                cells.Add(GameReads.Ref(structure).At);
            }

        var devices = new List<ReferenceId>();
        if (network is PipeNetwork pipes)
            foreach (var device in pipes.DeviceList)
                if (device != null)
                    devices.Add(new ReferenceId(device.ReferenceId));

        var atmosphere = (network as AtmosphericsNetwork)?.Atmosphere;
        return new NetworkInfo(
            new NetworkRef(network.ReferenceId),
            ValueSet.Of(Kind(network)),
            ValueList.From(pieces),
            ValueList.From(devices),
            ValueList.From(cells),
            Option<Watts>.None,
            Option<Watts>.None,
            atmosphere == null ? Option<Litres>.None : Option.Some(new Litres(atmosphere.Volume.ToDouble())),
            atmosphere == null ? Option<Kilopascals>.None : Option.Some(new Kilopascals(atmosphere.PressureGassesAndLiquids.ToDouble())),
            atmosphere == null ? Option<ValueList<GasHeld>>.None : Option.Some(Contents(atmosphere.GasMixture)))
        {
            Temperature = atmosphere == null ? Option<Kelvin>.None : TemperatureOf(atmosphere.GasMixture),
        };
    }

    // GasMixture.Temperature is zero when the mixture holds no heat (nothing in it): no temperature then.
    private static Option<Kelvin> TemperatureOf(GasMixture mixture)
    {
        var kelvin = mixture.Temperature.ToDouble();
        return kelvin > 0 ? Option.Some(new Kelvin(kelvin)) : Option<Kelvin>.None;
    }

    private static NetworkType Kind(StructureNetwork network) =>
        network switch
        {
            AtmosphericsNetwork pipes => pipes.NetworkContentType == Pipe.ContentType.Liquid
                ? new NetworkType.LiquidPipe()
                : new NetworkType.GasPipe(),
            ChuteNetwork => new NetworkType.Chute(),
            _ => new NetworkType.Unknown(network.GetType().FullName ?? network.GetType().Name, (int)network.NetworkType),
        };

    private ValueList<GasHeld> Contents(GasMixture mixture)
    {
        var held = new List<GasHeld>(4);
        for (var i = 0; i < _gases.Length; i++)
        {
            var mole = mixture.GetMoleValue(_gases[i]);
            var moles = mole.Quantity.ToDouble();
            if (moles > 0)
                held.Add(new GasHeld(_gasKinds[i], new Moles(moles), new Joules(mole.Energy.ToDouble())));
        }

        return held.Count == 0 ? ValueList<GasHeld>.Empty : ValueList.From(held);
    }

    private static double PressureAt(Cell cell)
    {
        var atmosphere = GridController.World?.AtmosphericsController?.SampleGlobalAtmosphere(new WorldGrid(cell.X, cell.Y, cell.Z));
        return atmosphere == null ? 0 : atmosphere.PressureGassesAndLiquids.ToDouble();
    }
}
