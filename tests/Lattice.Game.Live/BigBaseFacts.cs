using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

/// <summary>One ground sample: where the ray went down and the height it hit, <c>null</c> where it hit nothing.</summary>
internal readonly record struct GroundSample(float X, float Z, float? Y)
{
    public string Where => $"x {X.ToString("0.##", CultureInfo.InvariantCulture)} z {Z.ToString("0.##", CultureInfo.InvariantCulture)}";
}

/// <summary>The ground check under a base: raycast heights near the stand spot against the base's lowest piece.</summary>
internal readonly record struct GroundVerdict(bool Ok, float? Min, float? Max, IReadOnlyList<string> Misses, float Lowest)
{
    /// <summary>A hit above the lowest piece's y minus this many metres means the base would cut into the terrain.</summary>
    public const float Clearance = 1f;

    /// <summary>Terrain colliders exist only near the body: only points this close (metres, on the ground plane) to the stand spot are sampled.</summary>
    public const float LoadedRadius = 60f;

    /// <summary>The candidate points that lie within <see cref="LoadedRadius"/> of the stand spot.</summary>
    public static List<(float X, float Z)> Within(IEnumerable<(float X, float Z)> candidates, float standX, float standZ)
    {
        var near = new List<(float X, float Z)>();
        foreach (var c in candidates)
        {
            var (dx, dz) = (c.X - standX, c.Z - standZ);
            if (dx * dx + dz * dz <= LoadedRadius * LoadedRadius)
                near.Add(c);
        }

        return near;
    }

    /// <summary>The verdict over the samples. A ray that hit nothing fails, naming its point; so does having no sample.</summary>
    public static GroundVerdict Judge(IReadOnlyList<GroundSample> samples, float lowestPiece)
    {
        float? min = null, max = null;
        var misses = new List<string>();
        var ok = samples.Count > 0;
        foreach (var sample in samples)
        {
            if (sample.Y is not { } y)
            {
                misses.Add(sample.Where);
                ok = false;
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
        return Misses.Count > 0 ? text + $"; no ground found at {string.Join(", ", Misses)}" : text;
    }
}

/// <summary>A step of the build that can end the whole build: its FAIL line is logged once and the build checks <see cref="Failed"/>.</summary>
internal sealed class StepGate
{
    public bool Failed { get; private set; }

    public void Fail(Action<string> line, string text)
    {
        Failed = true;
        line("FAIL bigbase: " + text);
    }
}

/// <summary>Due once for each full interval of a long wait, so the log shows it is alive.</summary>
internal sealed class WaitProgress(double everySeconds = WaitProgress.DefaultEvery)
{
    public const double DefaultEvery = 60;
    private int _logged;

    public bool Due(double elapsedSeconds)
    {
        var intervals = (int)(elapsedSeconds / everySeconds);
        if (intervals <= _logged)
            return false;
        _logged = intervals;
        return true;
    }
}

/// <summary>When the held body is put back at the stand spot, and when it counts as having stayed.</summary>
internal static class BodyHold
{
    public const float RestandBeyond = 0.5f;
    public const float StayedWithin = 1f;

    public static bool NeedsRestand(float metresFromStand) => metresFromStand > RestandBeyond;

    public static bool Stayed(float metresFromStand) => metresFromStand <= StayedWithin;
}

/// <summary>The run's <c>world.txt</c>: one <c>key value</c> line per fact.</summary>
internal static class WorldFile
{
    /// <summary>The facts as lines; a fact with no value (<c>null</c>) is left out, never written as a placeholder.</summary>
    public static string Text(IEnumerable<KeyValuePair<string, string?>> facts)
    {
        var text = new StringBuilder();
        foreach (var fact in facts)
            if (fact.Value is not null)
                text.Append(fact.Key).Append(' ').Append(fact.Value.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
        return text.ToString();
    }

    public static string Metres(float x, float y, float z) =>
        string.Join(" ", x.ToString("0.##", CultureInfo.InvariantCulture), y.ToString("0.##", CultureInfo.InvariantCulture), z.ToString("0.##", CultureInfo.InvariantCulture));
}

/// <summary>Whether the world's landing is over: no lander capsule descending or venting, and the body out of any capsule.</summary>
internal readonly record struct Landing(int Descending, int Venting, bool BodyInCapsule)
{
    public bool Landed => Descending == 0 && Venting == 0 && !BodyInCapsule;

    /// <summary>What is still pending, for the log and the FAIL line; empty when landed.</summary>
    public string Pending => Landed
        ? ""
        : string.Join(", ", new[]
        {
            Descending > 0 ? $"{Descending} lander capsule(s) descending" : "",
            Venting > 0 ? $"{Venting} lander capsule(s) venting" : "",
            BodyInCapsule ? "the body is still in a capsule" : "",
        }.Where(static p => p.Length > 0));
}
