using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lattice.Api;
using Lattice.TestKit.Catalogue;

namespace Lattice.TestKit.BigBase;

/// <summary>
/// Writes a generated big base file (<see cref="BaseFile"/>) from a catalogue data file, a mix file and a seed, headed
/// by where it came from (file names and SHA1s, never paths) and the save name the live build gives the world.
/// </summary>
internal static class BigBaseTool
{
    public const string Station = "lattice-gen-big";

    /// <summary>The base's text and a report of its counts per family against the mix, or why it could not be made.</summary>
    public static Result<(string Text, string Report), string> Make(string cataloguePath, string mixPath, int seed)
    {
        if (!File.Exists(cataloguePath))
            return Result<(string, string), string>.Error("no catalogue at " + cataloguePath);
        Result<FakeCatalogue, FileError> read;
        using (var reader = new StreamReader(cataloguePath))
            read = CatalogueFile.Read(reader, cataloguePath);
        if (!read.TryGet(out var catalogue))
            return Result<(string, string), string>.Error(cataloguePath + " does not read as a catalogue");
        if (!BaseMix.Load(mixPath).TryGet(out var mix))
            return Result<(string, string), string>.Error(mixPath + " does not read as a mix");
        var made = new BaseGenerator(catalogue, mix, seed).Generate();
        if (!made.TryGet(out var generated))
            return Result<(string, string), string>.Error(made.Match(static _ => "", static why => why));

        var comments = new List<string>
        {
            $"{Station}: a generated big base for Lattice's tests, seed {seed}, {generated.Pieces.Count} structures in {generated.Rooms} rooms",
            $"catalogue {Path.GetFileName(cataloguePath)} SHA1 {Sha1(cataloguePath)}; mix {Path.GetFileName(mixPath)} SHA1 {Sha1(mixPath)}",
            "station " + Station,
        };
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        BaseFile.Write(text, comments, generated.Pieces);
        return Result<(string, string), string>.Ok((text.ToString(), Report(generated, mix)));
    }

    /// <summary>One line per family the mix or the base names: count, target, and the difference in per cent.</summary>
    public static string Report(GeneratedBase generated, BaseMix mix)
    {
        var families = new List<string>(mix.Groups.Keys);
        foreach (var family in generated.Counts.Keys)
            if (!mix.Groups.TryGetValue(family, out _))
                families.Add(family);
        var report = new StringBuilder();
        foreach (var family in families)
        {
            var count = generated.Counts.TryGetValue(family, out var c) ? c : 0;
            var target = mix.Groups.TryGetValue(family, out var t) ? t : 0;
            var off = target == 0 ? "no target" : Math.Round((count - target) * 100.0 / target, 1).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
            report.Append(FormattableString.Invariant($"{family,-8} {count,6} target {target,6} {off}\n"));
        }

        report.Append(FormattableString.Invariant($"total    {generated.Pieces.Count,6}\n"));
        return report.ToString();
    }

    private static string Sha1(string path)
    {
        using var sha = SHA1.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }
}
