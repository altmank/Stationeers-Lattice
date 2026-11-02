using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Lattice.Api;

namespace Lattice.TestKit.BigBase;

/// <summary>A prefab or kit name drawn by weight.</summary>
internal readonly record struct Weighted(string Name, int Weight);

/// <summary>
/// The target mix of a generated base, read from a mix file outside the repo: how many structures of each catalogue
/// family (<see cref="Groups"/>), the pieces frames, walls and windows are drawn from (<see cref="Pieces"/>), the kits
/// pipe, cable and chute networks are built from (<see cref="Kits"/>), and the pieces that sit on a straight pipe with
/// the share of the devices they take (<see cref="OnPipe"/>). Names are the catalogue's; the code holds none.
/// </summary>
internal sealed record BaseMix(
    ValueMap<string, int> Groups,
    ValueMap<string, ValueList<Weighted>> Pieces,
    ValueMap<string, ValueList<Weighted>> Kits,
    ValueList<Weighted> OnPipe)
{
    /// <summary>The target count of the family, 0 for one the mix does not name.</summary>
    public int Target(PieceFamily family) => Groups.TryGetValue(family.Value, out var count) ? count : 0;

    /// <summary>The weighted names for the family, empty for one the mix does not name.</summary>
    public static ValueList<Weighted> Of(ValueMap<string, ValueList<Weighted>> table, PieceFamily family) =>
        table.TryGetValue(family.Value, out var list) ? list : ValueList<Weighted>.Empty;

    /// <summary>
    /// Reads a mix file: <c>group &lt;family&gt; &lt;count&gt;</c>, <c>piece &lt;family&gt; &lt;prefab&gt; &lt;weight&gt;</c>,
    /// <c>kit &lt;family&gt; &lt;kit&gt; &lt;weight&gt;</c>, <c>on-pipe &lt;prefab&gt; &lt;percent&gt;</c>; <c>structures</c>,
    /// blank and <c>#</c> lines are skipped.
    /// </summary>
    public static Result<BaseMix, FileError> Read(TextReader reader, string fileName)
    {
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        var pieces = new Dictionary<string, List<Weighted>>(StringComparer.Ordinal);
        var kits = new Dictionary<string, List<Weighted>>(StringComparer.Ordinal);
        var onPipe = new List<Weighted>();
        var number = 0;
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            number++;
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal))
                continue;
            var f = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var ok = (f[0], f.Length) switch
            {
                ("structures", 2) => int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                ("group", 3) => Count(f[2], out var count) && groups.TryAdd(f[1], count),
                ("piece", 4) => Add(pieces, f[1], f[2], f[3]),
                ("kit", 4) => Add(kits, f[1], f[2], f[3]),
                ("on-pipe", 3) => Count(f[2], out var share) && Added(onPipe, new Weighted(f[1], share)),
                _ => false,
            };
            if (!ok)
                return Result<BaseMix, FileError>.Error(new FileError(fileName, number, "not a mix line: " + text));
        }

        return groups.Count == 0
            ? Result<BaseMix, FileError>.Error(new FileError(fileName, 0, "no group lines"))
            : Result<BaseMix, FileError>.Ok(new BaseMix(ValueMap.From(groups), Freeze(pieces), Freeze(kits), ValueList.From(onPipe)));
    }

    public static Result<BaseMix, FileError> Load(string path)
    {
        if (!File.Exists(path))
            return Result<BaseMix, FileError>.Error(new FileError(path, 0, "no such file"));
        using var reader = new StreamReader(path);
        return Read(reader, path);
    }

    private static bool Count(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;

    private static bool Add(Dictionary<string, List<Weighted>> table, string family, string name, string weight)
    {
        if (!Count(weight, out var w))
            return false;
        if (!table.TryGetValue(family, out var list))
            table[family] = list = new List<Weighted>();
        list.Add(new Weighted(name, w));
        return true;
    }

    private static bool Added(List<Weighted> list, Weighted item)
    {
        list.Add(item);
        return true;
    }

    private static ValueMap<string, ValueList<Weighted>> Freeze(Dictionary<string, List<Weighted>> table)
    {
        var frozen = new Dictionary<string, ValueList<Weighted>>(StringComparer.Ordinal);
        foreach (var entry in table)
            frozen[entry.Key] = ValueList.From(entry.Value);
        return ValueMap.From(frozen);
    }
}
