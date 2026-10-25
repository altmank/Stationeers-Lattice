using Lattice.Core.Catalogue;

namespace Lattice.Core.Ports;

/// <summary>A box in a piece's own space, unturned, in metres: its centre and its full size along each axis.</summary>
internal readonly record struct PieceBox(Vector Centre, Vector Size)
{
    /// <summary>One grid cell of the given edge, centred on the piece's pivot.</summary>
    public static PieceBox Cell(double metres) => new(new Vector(0, 0, 0), new Vector(metres, metres, metres));
}

/// <summary>
/// Where a piece's ghost mesh comes from, best first: the prefab's own
/// renderers and build-state meshes, else its blueprint mesh, else its bounds box, else one grid cell. Read from
/// whatever prefab the game holds, so a modded piece has a source like any other.
/// </summary>
internal abstract record LookSource
{
    private LookSource() { }

    /// <summary>
    /// The prefab's meshes: <see cref="Count"/> in all, <see cref="Readable"/> of them with CPU-readable data, and
    /// <see cref="ByStep"/> of them shown only at some build steps (a build state's own renderer or draw data).
    /// </summary>
    public sealed record Meshes(int Count, int Readable, int ByStep) : LookSource;

    /// <summary>No renderer mesh; the prefab's blueprint (<c>Thing.Blueprint</c>) holds one, CPU-readable or not.</summary>
    public sealed record Blueprint(bool Readable) : LookSource;

    /// <summary>No mesh at all; drawn as the prefab's bounds box.</summary>
    public sealed record Box : LookSource;

    /// <summary>No mesh and no bounds; drawn as one grid cell.</summary>
    public sealed record Cell : LookSource;
}

/// <summary>How a piece is drawn as a ghost: the box it fills in its own space and where its mesh comes from.</summary>
internal sealed record LookFacts(PieceBox Bounds, LookSource Source);
