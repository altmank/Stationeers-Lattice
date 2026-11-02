using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.TestKit.BigBase;

/// <summary>One structure of a base file: its prefab, where it stands (cell and turn) and its build state index.</summary>
internal sealed record BasePiece(string Prefab, Cell At, Turn Turn, int State);

/// <summary>A base file read back: its comment lines and its structures.</summary>
internal sealed record BaseList(ValueList<string> Comments, ValueList<BasePiece> Pieces)
{
    /// <summary>The save name a <c># station &lt;name&gt;</c> line gives; none without one.</summary>
    public Option<string> Station
    {
        get
        {
            foreach (var comment in Comments)
                if (comment.StartsWith(BaseFile.StationLine, StringComparison.Ordinal))
                    return Option.Some(comment.Substring(BaseFile.StationLine.Length).Trim());
            return Option<string>.None;
        }
    }
}

/// <summary>
/// The base file format the live big-base build spawns (<c>tests/Lattice.Game.Live/BigBaseRun.cs</c>): one structure a
/// line, tab separated, <c>prefab x y z qx qy qz qw state</c>, the registered position in metres and the rotation as
/// Unity's quaternion; <c>#</c> lines are comments. A cell is written as its point (<see cref="Cell.ToWorld"/>) and read
/// back as the game's Grid3 key, round(metres * 10); a turn is written as its quaternion and read back by the test
/// kit's turn table, as the save loader reads a save.
/// </summary>
internal static class BaseFile
{
    public const string StationLine = "# station ";
    public const string ColumnsLine = "# prefab\tx\ty\tz\tqx\tqy\tqz\tqw\tstate";

    private static readonly CubeGrid Grid = new();

    public static void Write(TextWriter writer, IReadOnlyList<string> comments, IReadOnlyList<BasePiece> pieces)
    {
        foreach (var comment in comments)
            writer.Write("# " + comment + "\n");
        writer.Write(ColumnsLine + "\n");
        foreach (var piece in pieces)
        {
            var (x, y, z, w) = Quaternion(piece.Turn);
            writer.Write(string.Join("\t",
                piece.Prefab, Metres(piece.At.X), Metres(piece.At.Y), Metres(piece.At.Z),
                Number(x), Number(y), Number(z), Number(w), piece.State.ToString(CultureInfo.InvariantCulture)) + "\n");
        }
    }

    public static Result<BaseList, FileError> Read(TextReader reader, string fileName)
    {
        var comments = new List<string>();
        var pieces = new List<BasePiece>(40000);
        var number = 0;
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            number++;
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                comments.Add(line);
                continue;
            }

            var f = line.Split('\t');
            if (f.Length != 9 || !Numbers(f, out var v) || !int.TryParse(f[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var state))
                return Result<BaseList, FileError>.Error(new FileError(fileName, number, "not a base line"));
            if (!Lattice.TestKit.Grid.TurnTable.FromQuaternion(v[3], v[4], v[5], v[6]).TryGet(out var turn))
                return Result<BaseList, FileError>.Error(new FileError(fileName, number, "rotation is not a cube turn"));
            pieces.Add(new BasePiece(f[0], new Cell(Key(v[0]), Key(v[1]), Key(v[2])), turn, state));
        }

        return Result<BaseList, FileError>.Ok(new BaseList(ValueList.From(comments), ValueList.From(pieces)));
    }

    public static Result<BaseList, FileError> Load(string path)
    {
        if (!File.Exists(path))
            return Result<BaseList, FileError>.Error(new FileError(path, 0, "no such file"));
        using var reader = new StreamReader(path);
        return Read(reader, path);
    }

    /// <summary>
    /// The unit quaternion (Unity's x, y, z, w) of the turn as Lattice's grid applies it: the rotation whose columns are
    /// the images of +X, +Y and +Z.
    /// </summary>
    public static (double X, double Y, double Z, double W) Quaternion(Turn turn)
    {
        var (m00, m10, m20) = Column(turn, Direction.XPlus);
        var (m01, m11, m21) = Column(turn, Direction.YPlus);
        var (m02, m12, m22) = Column(turn, Direction.ZPlus);
        var trace = m00 + m11 + m22;
        double x, y, z, w;
        if (trace > 0)
        {
            var s = Math.Sqrt(trace + 1.0) * 2;
            (w, x, y, z) = (0.25 * s, (m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s);
        }
        else if (m00 > m11 && m00 > m22)
        {
            var s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
            (w, x, y, z) = ((m21 - m12) / s, 0.25 * s, (m01 + m10) / s, (m02 + m20) / s);
        }
        else if (m11 > m22)
        {
            var s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
            (w, x, y, z) = ((m02 - m20) / s, (m01 + m10) / s, 0.25 * s, (m12 + m21) / s);
        }
        else
        {
            var s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
            (w, x, y, z) = ((m10 - m01) / s, (m02 + m20) / s, (m12 + m21) / s, 0.25 * s);
        }

        return (x, y, z, w);
    }

    private static (int X, int Y, int Z) Column(Turn turn, Direction axis)
    {
        var image = Grid.Apply(turn, axis).Unit();
        return (image.X, image.Y, image.Z);
    }

    private static string Metres(int key) => (key / 10m).ToString(CultureInfo.InvariantCulture);

    private static string Number(double value) =>
        (Math.Abs(value) < 1e-9 ? 0.0 : value).ToString("R", CultureInfo.InvariantCulture);

    private static bool Numbers(string[] f, out double[] values)
    {
        values = new double[7];
        for (var i = 0; i < 7; i++)
            if (!double.TryParse(f[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        return true;
    }

    private static int Key(double metres) => (int)Math.Round(metres * 10, MidpointRounding.AwayFromZero);
}
