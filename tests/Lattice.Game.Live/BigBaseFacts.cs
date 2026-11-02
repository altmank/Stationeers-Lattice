using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lattice.Game.Live;

/// <summary>Where the body stands in the base file's <c># stand x y z</c> line, in metres.</summary>
internal readonly record struct StandSpot(float X, float Y, float Z);

/// <summary>What the big-base file's comment lines say about the base (<c># station</c>, <c># stand</c>, the seed and the SHA1s).</summary>
internal sealed record BaseHeader(string? Station, StandSpot? Stand, string Seed, string CatalogueSha1, string MixSha1)
{
    private static readonly Regex SeedPattern = new(@"\bseed (\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex CataloguePattern = new(@"\bcatalogue \S+ SHA1 ([0-9A-Fa-f]+)", RegexOptions.CultureInvariant);
    private static readonly Regex MixPattern = new(@"\bmix \S+ SHA1 ([0-9A-Fa-f]+)", RegexOptions.CultureInvariant);

    /// <summary>The header read from the file's lines; only the <c>#</c> lines count. Missing facts are <c>null</c>, or <c>unknown</c> for the texts.</summary>
    public static BaseHeader Read(IEnumerable<string> lines)
    {
        string? station = null;
        StandSpot? stand = null;
        string seed = "unknown", catalogue = "unknown", mix = "unknown";
        foreach (var text in lines)
        {
            if (!text.StartsWith("#", StringComparison.Ordinal))
                continue;
            if (text.StartsWith("# station ", StringComparison.Ordinal))
                station = text.Substring("# station ".Length).Trim();
            else if (text.StartsWith("# stand ", StringComparison.Ordinal))
                stand = ParseStand(text.Substring("# stand ".Length)) ?? stand;
            else
            {
                if (SeedPattern.Match(text) is { Success: true } s)
                    seed = s.Groups[1].Value;
                if (CataloguePattern.Match(text) is { Success: true } c)
                    catalogue = c.Groups[1].Value;
                if (MixPattern.Match(text) is { Success: true } m)
                    mix = m.Groups[1].Value;
            }
        }

        return new BaseHeader(station, stand, seed, catalogue, mix);
    }

    /// <summary>The save the build makes: the station, plus <c>-held</c> when anything is held back; none without a station line.</summary>
    public string? SaveName(int held) => Station is null ? null : held > 0 ? Station + "-held" : Station;

    private static StandSpot? ParseStand(string text)
    {
        var f = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 3
            || !float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return null;
        return new StandSpot(x, y, z);
    }
}

/// <summary>The ground check under a base: raycast heights at its corners and centre against its lowest piece.</summary>
internal readonly record struct GroundVerdict(bool Ok, float? Min, float? Max, int Misses, float Lowest)
{
    /// <summary>A hit above the lowest piece's y minus this many metres means the base would cut into the terrain.</summary>
    public const float Clearance = 1f;

    /// <summary>The verdict over the ground heights (<c>null</c> where the ray hit nothing).</summary>
    public static GroundVerdict Judge(IReadOnlyList<float?> hits, float lowestPiece)
    {
        float? min = null, max = null;
        var misses = 0;
        var ok = true;
        foreach (var hit in hits)
        {
            if (hit is not { } y)
            {
                misses++;
                continue;
            }

            min = min is { } a ? Math.Min(a, y) : y;
            max = max is { } b ? Math.Max(b, y) : y;
            if (y > lowestPiece - Clearance)
                ok = false;
        }

        return new GroundVerdict(ok, min, max, misses, lowestPiece);
    }

    /// <summary>The line to log: <c>ground under the base: min Y max Y; lowest piece Y</c>, with the points that hit nothing.</summary>
    public string Describe()
    {
        static string N(float? v) => v is { } y ? y.ToString("0.##", CultureInfo.InvariantCulture) : "none";
        var text = $"ground under the base: min {N(Min)} max {N(Max)}; lowest piece {N(Lowest)}";
        return Misses > 0 ? text + $" ({Misses} points hit nothing)" : text;
    }
}

/// <summary>The run's <c>world.txt</c>: one <c>key value</c> line per fact.</summary>
internal static class WorldFile
{
    public static string Text(IEnumerable<KeyValuePair<string, string>> facts)
    {
        var text = new StringBuilder();
        foreach (var fact in facts)
            text.Append(fact.Key).Append(' ').Append(fact.Value.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
        return text.ToString();
    }

    public static string Metres(float x, float y, float z) =>
        string.Join(" ", x.ToString("0.##", CultureInfo.InvariantCulture), y.ToString("0.##", CultureInfo.InvariantCulture), z.ToString("0.##", CultureInfo.InvariantCulture));
}
