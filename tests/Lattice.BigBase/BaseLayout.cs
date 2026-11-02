using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;

namespace Lattice.BigBase;

/// <summary>
/// Where a generated base stands and how its rooms run: the large cell (indices, a cell's centre at 20 * i + 10 dm) of
/// its first corner, room columns per corridor, the room sizes in large cells, the room heights, and the storeys the
/// corridor rows are dealt over.
/// </summary>
internal sealed record BaseLayout(int OriginX, int OriginY, int OriginZ)
{
    /// <summary>A base 601 m east and north and 301 m up, inside the area a new world's base is spawned into.</summary>
    public static BaseLayout Default => new(300, 150, 300);

    public int Columns { get; init; } = 8;

    public int ColumnPitch { get; init; } = 10;

    public int RoomMin { get; init; } = 5;

    public int RoomMax { get; init; } = 10;

    public int HeightMin { get; init; } = 2;

    public int HeightMax { get; init; } = 5;

    /// <summary>How many floors the corridor rows are stacked over.</summary>
    public int Storeys { get; init; } = 4;

    /// <summary>The large cells of void between a storey's tallest ceiling and the floor above.</summary>
    public int StoreyGap { get; init; } = 2;

    /// <summary>Up to this many more large cells between two storeys, drawn per storey.</summary>
    public int StoreyJitter { get; init; } = 2;
}

/// <summary>
/// A generated base: its structures in file order, the counts per catalogue family, the rooms and storeys, the networks
/// laid per family (each one connected piece), the run cells no kit piece could take.
/// </summary>
internal sealed record GeneratedBase(
    ValueList<BasePiece> Pieces,
    ValueMap<string, int> Counts,
    int Rooms,
    int Storeys,
    ValueMap<string, int> Networks,

    int DroppedRuns);

/// <summary>A partition wall across a room: the large-cell row it stands before, and the column of its door.</summary>
internal readonly record struct Partition(int At, int DoorAt);

/// <summary>
/// A room: its first large cell (x, z), size, height of air over its floor frames at large index <see cref="Y0"/>, the
/// column of its door to the corridor and the side it opens on (+1 toward +z, -1 toward -z).
/// </summary>
internal sealed record Room(int X0, int Z0, int W, int D, int H, int Y0, int DoorX, int Facing, Option<Partition> Partition, Corridor Corridor, bool Factory);

/// <summary>
/// A corridor of floor frames two large cells deep at <see cref="Zc"/> on the storey whose floor is at large index
/// <see cref="Y0"/>, with the rooms either side and their networks. Corridors at one <see cref="Stack"/> lie one above
/// another and share a gas network through a riser.
/// </summary>
internal sealed class Corridor(int zc, int y0, int storey, int stack)
{
    public int Zc { get; } = zc;

    public int Y0 { get; } = y0;

    public int Storey { get; } = storey;

    public int Stack { get; } = stack;

    public List<Room> Rooms { get; } = new();

    public Net? Pipe { get; set; }

    public Net? Cable { get; set; }
}
