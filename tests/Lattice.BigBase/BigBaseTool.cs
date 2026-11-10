using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;

namespace Lattice.BigBase;

/// <summary>
/// Writes a generated big base file (<see cref="BaseFile"/>) from a catalogue data file, a mix file and a seed, headed
/// by where it came from (file names and SHA1s, never paths) and the save name the live build gives the world.
/// </summary>
internal static class BigBaseTool
{
    public const string Station = "lattice-gen-big";

    /// <summary>
    /// The base's text and a report of its counts per family against the mix, or why it could not be made. Every piece
    /// the catalogue names must be in the vanilla catalogue (the base game's alone): a base is designed against the base game.
    /// </summary>
    public static Result<(string Text, string Report), string> Make(string cataloguePath, string vanillaPath, string mixPath, int seed)
    {
        var readCatalogue = ReadCatalogue(cataloguePath);
        if (!readCatalogue.TryGet(out var catalogue))
            return Result<(string, string), string>.Error(readCatalogue.Match(static _ => "", static why => why));
        var readVanilla = ReadCatalogue(vanillaPath);
        if (!readVanilla.TryGet(out var vanilla))
            return Result<(string, string), string>.Error(readVanilla.Match(static _ => "", static why => why));
        var modded = NotInVanilla(catalogue, vanilla);
        if (modded.Count > 0)
            return Result<(string, string), string>.Error(
                $"{Path.GetFileName(cataloguePath)} names {modded.Count} pieces the vanilla catalogue {Path.GetFileName(vanillaPath)} lacks: {string.Join(", ", modded)}");
        if (!BaseMix.Load(mixPath).TryGet(out var mix))
            return Result<(string, string), string>.Error(mixPath + " does not read as a mix");
        var made = new BaseGenerator(catalogue, mix, seed).Generate();
        if (!made.TryGet(out var generated))
            return Result<(string, string), string>.Error(made.Match(static _ => "", static why => why));

        var comments = new List<string>
        {
            $"{Station}: a generated big base for Lattice's tests, seed {seed}, {generated.Pieces.Count} structures in {generated.Rooms} rooms on {generated.Storeys} storeys",
            $"catalogue {Path.GetFileName(cataloguePath)} SHA1 {Sha1(cataloguePath)}; vanilla {Path.GetFileName(vanillaPath)} SHA1 {Sha1(vanillaPath)}; mix {Path.GetFileName(mixPath)} SHA1 {Sha1(mixPath)}",
            "station " + Station,
        };
        if (generated.Stand.TryGet(out var stand))
            comments.Add(BaseFile.StandComment(stand));
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        BaseFile.Write(text, comments, generated.Pieces);
        return Result<(string, string), string>.Ok((text.ToString(), Report(generated, mix)));
    }

    /// <summary>The prefab names of the pieces <paramref name="catalogue"/> holds and <paramref name="vanilla"/> does not, in order.</summary>
    internal static IReadOnlyList<string> NotInVanilla(FakeCatalogue catalogue, FakeCatalogue vanilla)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in vanilla.Pieces)
            known.Add(piece.Name);
        var unknown = new List<string>();
        foreach (var piece in catalogue.Pieces)
            if (!known.Contains(piece.Name))
                unknown.Add(piece.Name);
        unknown.Sort(StringComparer.Ordinal);
        return unknown;
    }

    private static Result<FakeCatalogue, string> ReadCatalogue(string path)
    {
        if (!File.Exists(path))
            return Result<FakeCatalogue, string>.Error("no catalogue at " + path);
        using var reader = new StreamReader(path);
        return CatalogueFile.Read(reader, path).TryGet(out var catalogue)
            ? Result<FakeCatalogue, string>.Ok(catalogue)
            : Result<FakeCatalogue, string>.Error(path + " does not read as a catalogue");
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
        report.Append(FormattableString.Invariant(
            $"storeys {generated.Storeys}, distinct heights {DistinctHeights(generated)}, run cells dropped {generated.DroppedRuns}\n"));
        foreach (var network in generated.Networks)
            report.Append(FormattableString.Invariant($"networks {network.Key,-8} {network.Value}\n"));
        return report.ToString();
    }

    /// <summary>How many different heights (y of a piece's position) the base's pieces stand at.</summary>
    public static int DistinctHeights(GeneratedBase generated)
    {
        var heights = new HashSet<int>();
        foreach (var piece in generated.Pieces)
            heights.Add(piece.At.Y);
        return heights.Count;
    }

    private static string Sha1(string path)
    {
        using var sha = SHA1.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }
}
