using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Openings;

/// <summary>Which pieces are doors or windows, found by type so a modded subclass is the same.</summary>
internal interface IOpeningFacts
{
    Option<OpeningKind> Of(PrefabKey piece);
}

/// <summary>Opening kinds as the catalogue read them from each piece's type (<see cref="PieceFacts.Opening"/>); none for an unknown piece.</summary>
internal sealed class CatalogueOpeningFacts(ICatalogue catalogue) : IOpeningFacts
{
    public Option<OpeningKind> Of(PrefabKey piece) =>
        catalogue.Piece(piece).Match(static facts => facts.Opening, static _ => Option<OpeningKind>.None);
}

/// <summary>No piece is a door or a window: what Lattice knows until the catalogue reads opening kinds.</summary>
internal sealed class NoOpeningFacts : IOpeningFacts
{
    public Option<OpeningKind> Of(PrefabKey piece) => Option<OpeningKind>.None;
}

/// <summary>
/// How far from a door's or window's face plane the space in front of it reaches, each side, in decimetres. Lattice's
/// layout choice, not a game rule: a door keeps 1 m clear to walk through, a window 0.5 m clear to see through.
/// </summary>
internal sealed record OpeningBands(int Door, int Window)
{
    /// <summary>A door 10 dm (1 m) each side, a window 5 dm (0.5 m).</summary>
    public static OpeningBands Default => new(Door: 10, Window: 5);

    public int Of(OpeningKind kind) => kind == OpeningKind.Door ? Door : Window;
}

/// <summary>A door or window whose keep-out holds a cell.</summary>
internal sealed record OpeningHit(StructureRef Opening, OpeningKind Kind);

/// <summary>
/// The keep-outs in front of doors and windows, one definition for every module that keeps them clear or warns about
/// them (route planning, build safety). A keep-out is the opening's face rectangle (each large face its footprint
/// takes) on its face plane, widened to <see cref="OpeningBands"/> either side; edges count. A window counts only on a
/// vertical face: a transparent piece on a floor or ceiling face (floor gratings, which are <c>WallTransparent</c>
/// too) is walked on, not looked through. A door counts on any face (hatches). The small cells where a piece joining
/// one of a door's own ports stands, and the cells in front of the port out to the edge of the door's keep-out, are left out, so a powered door can still be wired. Reads the world it is given
/// (the planned world for a plan); openings are found from their own cell up to <see cref="Reach"/> large cells away,
/// each large cell read once per instance.
/// </summary>
internal sealed class OpeningKeepOuts(IWorldQuery world, Shapes shapes, IOpeningFacts facts, OpeningBands bands)
{
    /// <summary>How many large cells from its own cell an opening's footprint is looked for.</summary>
    public const int Reach = 1;

    private const int Large = 20;
    private const int Half = 10;
    private const int SmallStep = 5;
    private const int MostReleased = 16;

    private readonly Dictionary<Cell, List<Face>> _faces = [];
    private readonly HashSet<Cell> _read = [];
    private readonly HashSet<Cell> _around = [];
    private readonly HashSet<ReferenceId> _seen = [];
    private readonly HashSet<Cell> _ports = [];

    /// <summary>
    /// The door or window whose keep-out holds the small cell, if any (the first found). Cost: the large cells around
    /// it, once; then constant.
    /// </summary>
    public Option<OpeningHit> At(Cell cell)
    {
        var reach = Half + System.Math.Max(bands.Door, bands.Window);
        for (var x = FirstCentre(cell.X - reach); x <= cell.X + reach; x += Large)
            for (var y = FirstCentre(cell.Y - reach); y <= cell.Y + reach; y += Large)
                for (var z = FirstCentre(cell.Z - reach); z <= cell.Z + reach; z += Large)
                    foreach (var face in FacesOf(new Cell(x, y, z)))
                        if (face.Holds(cell, bands.Of(face.Kind)) && !_ports.Contains(cell))
                            return Option.Some(new OpeningHit(face.Opening, face.Kind));
        return Option<OpeningHit>.None;
    }

    /// <summary>The faces of openings keyed at a large cell (a face spot is keyed by the cell on its negative side).</summary>
    private List<Face> FacesOf(Cell large)
    {
        if (_around.Add(large))
            ReadAround(large);
        return _faces.TryGetValue(large, out var faces) ? faces : [];
    }

    private void ReadAround(Cell large)
    {
        for (var dx = -Reach; dx <= Reach; dx++)
            for (var dy = -Reach; dy <= Reach; dy++)
                for (var dz = -Reach; dz <= Reach; dz++)
                    Read(new Cell(large.X + dx * Large, large.Y + dy * Large, large.Z + dz * Large));
    }

    private void Read(Cell large)
    {
        if (!_read.Add(large))
            return;
        foreach (var info in world.At(large))
        {
            if (!_seen.Add(info.Ref.Id) || !facts.Of(info.Ref.Piece).TryGet(out var kind)
                || !shapes.Of(new Placement(info.Ref.Piece, info.Ref.At, info.Turn)).TryGet(out var shape))
                continue;
            var mine = new List<Face>(shape.Spots.Count);
            foreach (var spot in shape.Spots)
                if (Normal(spot.Slot) is { } normal && (kind == OpeningKind.Door || normal != Axis.Y))
                {
                    var key = NegativeSide(spot, normal);
                    var face = new Face(info.Ref, kind, key, normal);
                    mine.Add(face);
                    if (!_faces.TryGetValue(key, out var here))
                        _faces[key] = here = new List<Face>(1);
                    here.Add(face);
                }

            if (kind == OpeningKind.Door)
                foreach (var end in shape.Ends)
                    Release(SmallCellOf(end.Cell), end.Facing, mine);
        }
    }

    /// <summary>Releases the port's cell and the cells it faces, out to the edge of the door's own keep-out.</summary>
    private void Release(Cell port, Direction facing, List<Face> door)
    {
        var step = facing.Unit().Times(SmallStep);
        var cell = port;
        _ports.Add(cell);
        for (var taken = 0; taken < MostReleased; taken++)
        {
            cell = cell.Plus(step);
            if (!HeldBy(door, cell))
                return;
            _ports.Add(cell);
        }
    }

    private bool HeldBy(List<Face> door, Cell cell)
    {
        foreach (var face in door)
            if (face.Holds(cell, bands.Door))
                return true;
        return false;
    }

    private static Cell SmallCellOf(Cell point) => new(Round(point.X), Round(point.Y), Round(point.Z));

    private static int Round(int dm) => (int)System.Math.Round(dm / (double)SmallStep, System.MidpointRounding.AwayFromZero) * SmallStep;

    /// <summary>The first large-cell centre (odd metres: 10 mod 20 decimetres) at or above the value.</summary>
    private static int FirstCentre(int dm)
    {
        var offset = ((dm - Half) % Large + Large) % Large;
        return offset == 0 ? dm : dm + Large - offset;
    }

    /// <summary>The large cell on a face spot's negative side: the spot's own cell for a positive face, else the one behind it.</summary>
    private static Cell NegativeSide(SpotKey spot, Axis normal) => spot.Slot switch
    {
        Slot.FaceXMinus or Slot.FaceYMinus or Slot.FaceZMinus => normal switch
        {
            Axis.X => spot.Cell with { X = spot.Cell.X - Large },
            Axis.Y => spot.Cell with { Y = spot.Cell.Y - Large },
            _ => spot.Cell with { Z = spot.Cell.Z - Large },
        },
        _ => spot.Cell,
    };

    private static Axis? Normal(Slot slot) => slot switch
    {
        Slot.FaceXPlus or Slot.FaceXMinus => Axis.X,
        Slot.FaceYPlus or Slot.FaceYMinus => Axis.Y,
        Slot.FaceZPlus or Slot.FaceZMinus => Axis.Z,
        _ => null,
    };

    /// <summary>One large face of an opening: keyed at the cell on its negative side, so its plane is that cell's positive face.</summary>
    private sealed record Face(StructureRef Opening, OpeningKind Kind, Cell Key, Axis Normal)
    {
        public bool Holds(Cell cell, int band) => Normal switch
        {
            Axis.X => Near(cell.X, Key.X + Half, band) && Within(cell.Y, Key.Y) && Within(cell.Z, Key.Z),
            Axis.Y => Near(cell.Y, Key.Y + Half, band) && Within(cell.X, Key.X) && Within(cell.Z, Key.Z),
            _ => Near(cell.Z, Key.Z + Half, band) && Within(cell.X, Key.X) && Within(cell.Y, Key.Y),
        };

        private static bool Near(int value, int plane, int band) => System.Math.Abs(value - plane) <= band;

        private static bool Within(int value, int centre) => System.Math.Abs(value - centre) <= Half;
    }
}
