using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Consequences;

namespace Lattice.Tests.Consequences;

/// <summary>Builders for consequence models, in raw cell units (connectivity does not care about the step).</summary>
internal static class Models
{
    public static readonly NetworkTypes Power = new(4);
    public static readonly NetworkTypes Data = new(2);
    public static readonly NetworkTypes PowerAndData = new(6);

    public static readonly Cell Origin = new(0, 0, 0);

    public static Cell At(int x, int y, int z) => new(x, y, z);

    public static ReferenceId Id(long value) => new(value);

    public static NetworkRef Net(long value) => new(value);

    public static HashSet<ReferenceId> Ids(params long[] values)
    {
        var set = new HashSet<ReferenceId>();
        foreach (var value in values)
            set.Add(new ReferenceId(value));
        return set;
    }

    public static Link Link(long from, long to) => new(new ReferenceId(from), new ReferenceId(to));

    /// <summary>An end of a piece in <paramref name="own"/> reaching one step along (dx, dy, dz).</summary>
    public static PieceEnd End(Cell own, int dx, int dy, int dz, NetworkTypes? types = null) =>
        new(new Cell(own.X + dx, own.Y + dy, own.Z + dz), own, types ?? PowerAndData, ConnectionRole.None);

    public static PieceModel Piece(long id, Cell cell, params PieceEnd[] ends) =>
        new(new ReferenceId(id), [cell], ends, Option<PipeContent>.None);

    public static PieceModel Piece(long id, Cell[] cells, PieceEnd[] ends, PipeContent? content = null) =>
        new(new ReferenceId(id), cells, ends, content is null ? Option<PipeContent>.None : Option.Some(content));

    /// <summary>A one-cell piece at <paramref name="cell"/> with ends towards each named direction (+x, -z, ...).</summary>
    public static PieceModel Run(long id, Cell cell, params string[] directions)
    {
        var ends = new PieceEnd[directions.Length];
        for (var index = 0; index < directions.Length; index++)
        {
            var (dx, dy, dz) = Step(directions[index]);
            ends[index] = End(cell, dx, dy, dz);
        }

        return Piece(id, cell, ends);
    }

    public static (int, int, int) Step(string direction) => direction switch
    {
        "+x" => (1, 0, 0),
        "-x" => (-1, 0, 0),
        "+y" => (0, 1, 0),
        "-y" => (0, -1, 0),
        "+z" => (0, 0, 1),
        "-z" => (0, 0, -1),
        _ => throw new KeyNotFoundException(direction),
    };

    public static string NameOf(PieceEnd end) => (end.Into.X - end.From.X, end.Into.Y - end.From.Y, end.Into.Z - end.From.Z) switch
    {
        (1, 0, 0) => "+x",
        (-1, 0, 0) => "-x",
        (0, 1, 0) => "+y",
        (0, -1, 0) => "-y",
        (0, 0, 1) => "+z",
        (0, 0, -1) => "-z",
        var other => other.ToString(),
    };

    public static List<string> Directions(IEnumerable<PieceEnd> ends)
    {
        var names = new List<string>();
        foreach (var end in ends)
            names.Add(NameOf(end));
        return names;
    }
}
