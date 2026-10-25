using Lattice.Api;

namespace Lattice.Core.Ports;

/// <summary>A position in the world in metres, free of Unity types.</summary>
internal readonly record struct WorldPosition(float X, float Y, float Z);

/// <summary>Snapping, cell keys, the 24 cube turns, neighbours and mirroring.</summary>
internal interface IGrid
{
    Cell Snap(WorldPosition position, GridSize grid);

    WorldPosition CentreOf(Cell cell, GridSize grid);

    Cell Neighbour(Cell cell, Direction direction, GridSize grid);

    Turn Compose(Turn first, Turn then);

    Turn Invert(Turn turn);

    Direction Apply(Turn turn, Direction direction);

    CellOffset Apply(Turn turn, CellOffset offset);

    Direction Reflect(Direction direction, Axis normal);

    Cell Reflect(Cell cell, MirrorPlane plane);
}
