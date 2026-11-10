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

    /// <summary>How far the ring of points around the stand spot lies from it, in metres.</summary>
    public const float Ring = 40f;

    /// <summary>
    /// Every point the ground is judged at: the stand spot, the footprint's corners and centre, and a ring of eight points
    /// <see cref="Ring"/> metres around the stand spot. The build measures them all before it spawns; the verdict after
    /// the spawn judges those <see cref="Within"/> the loaded radius.
    /// </summary>
    public static List<(float X, float Z)> Candidates(Footprint footprint, float standX, float standZ)
    {
        var points = new List<(float X, float Z)> { (standX, standZ) };
        points.AddRange(footprint.Corners);
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            points.Add((standX + (float)(Ring * Math.Cos(angle)), standZ + (float)(Ring * Math.Sin(angle))));
        }

        return points;
    }

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

/// <summary>
/// The ground the base covers: the piece centres' bounds on the ground plane, grown by half a large cell on each side
/// (a large piece reaches <see cref="BaseOffset.HalfGrid"/> past its centre), and the lowest piece's centre height.
/// </summary>
internal readonly record struct Footprint(float MinX, float MinZ, float MaxX, float MaxZ, float Lowest)
{
    /// <summary>The footprint of the piece centres; none for no pieces.</summary>
    public static Footprint? Of(IEnumerable<(float X, float Y, float Z)> centres)
    {
        Footprint? found = null;
        foreach (var (x, y, z) in centres)
            found = found is { } f
                ? new Footprint(Math.Min(f.MinX, x - BaseOffset.HalfGrid), Math.Min(f.MinZ, z - BaseOffset.HalfGrid),
                    Math.Max(f.MaxX, x + BaseOffset.HalfGrid), Math.Max(f.MaxZ, z + BaseOffset.HalfGrid), Math.Min(f.Lowest, y))
                : new Footprint(x - BaseOffset.HalfGrid, z - BaseOffset.HalfGrid, x + BaseOffset.HalfGrid, z + BaseOffset.HalfGrid, y);
        return found;
    }

    public (float X, float Z) Centre => ((MinX + MaxX) / 2f, (MinZ + MaxZ) / 2f);

    /// <summary>The four corners and the centre: where the ground under the base is measured.</summary>
    public IReadOnlyList<(float X, float Z)> Corners => [(MinX, MinZ), (MinX, MaxZ), (MaxX, MinZ), (MaxX, MaxZ), Centre];

    public Footprint Moved(BaseOffset by) => new(MinX + by.Dx, MinZ + by.Dz, MaxX + by.Dx, MaxZ + by.Dz, Lowest + by.Dy);

    /// <summary>The shortest distance on the ground plane from a point to the footprint; 0 inside it.</summary>
    public float DistanceTo(float x, float z)
    {
        var dx = Math.Max(0f, Math.Max(MinX - x, x - MaxX));
        var dz = Math.Max(0f, Math.Max(MinZ - z, z - MaxZ));
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>Where the landing left the body and, when the start has one, its lander capsule, on the ground plane.</summary>
internal readonly record struct LandingSpot(float BodyX, float BodyZ, float? LanderX, float? LanderZ);

/// <summary>
/// How far the whole base moves from the base file's fixed coordinates to stand on this world's ground beside the landing.
/// Every component is a whole number of large cells (<c>RocketGrid.GridSize</c> = 2 m,
/// Assembly-CSharp\Assets.Scripts.GridSystem\RocketGrid.cs:221), so every piece keeps its place on both grids.
/// </summary>
internal readonly record struct BaseOffset(float Dx, float Dy, float Dz)
{
    /// <summary>A large cell, <c>RocketGrid.GridSize</c>.</summary>
    public const float Grid = 2f;

    /// <summary>Half a large cell, <c>RocketGrid.HalfGridSize</c> (RocketGrid.cs:219): how far a large piece reaches past its centre.</summary>
    public const float HalfGrid = 1f;

    /// <summary>
    /// The least distance from the body's landing spot to the base's footprint: five large cells, so the body, and the
    /// lander capsule behind it, stand on open ground outside the base.
    /// </summary>
    public const float Gap = 10f;

    /// <summary>
    /// How far the lowest piece's underside sits above the highest ground measured, at least: one large frame's height,
    /// for terrain rising between the measured points. It is more than <see cref="GroundVerdict.Clearance"/>, so the
    /// verdict's check holds at every measured point.
    /// </summary>
    public const float Rise = Grid;

    public static BaseOffset Zero => new(0f, 0f, 0f);

    /// <summary>The offset as world.txt writes it: <c>dx dy dz</c> in metres.</summary>
    public string Text => WorldFile.Metres(Dx, Dy, Dz);

    /// <summary>
    /// The ground-plane move that puts the footprint <see cref="Gap"/> or more from the body, on the side away from the
    /// lander, centred on the body across that side. The side follows the larger component of lander-to-body; with no
    /// lander, or one under the body, the base goes to +x.
    /// </summary>
    public static (float Dx, float Dz) Beside(Footprint footprint, LandingSpot landing)
    {
        var (ax, az) = landing is { LanderX: { } lx, LanderZ: { } lz } ? (landing.BodyX - lx, landing.BodyZ - lz) : (0f, 0f);
        if (ax * ax + az * az < 0.01f)
            (ax, az) = (1f, 0f);
        var (cx, cz) = footprint.Centre;
        return Math.Abs(ax) >= Math.Abs(az)
            ? (Along(ax, landing.BodyX, footprint.MinX, footprint.MaxX), Cells(landing.BodyZ - cz, Math.Round))
            : (Cells(landing.BodyX - cx, Math.Round), Along(az, landing.BodyZ, footprint.MinZ, footprint.MaxZ));
    }

    /// <summary>
    /// The vertical move that puts the lowest piece's underside (its centre less <see cref="HalfGrid"/>) <see cref="Rise"/>
    /// or more above the highest ground, rounded up to whole cells: the underside ends 2 to 4 m over it.
    /// </summary>
    public static float Up(float lowestPiece, float highestGround) =>
        Cells(highestGround + Rise + HalfGrid - lowestPiece, Math.Ceiling);

    // A positive direction moves the footprint's near edge (its min) to body + Gap or beyond; a negative one its far edge
    // (its max) to body - Gap or below.
    private static float Along(float direction, float body, float min, float max) =>
        direction > 0 ? Cells(body + Gap - min, Math.Ceiling) : Cells(body - Gap - max, Math.Floor);

    private static float Cells(float metres, Func<double, double> round) => (float)(round(metres / Grid) * Grid);
}

/// <summary>
/// The ground measured under the moved footprint before anything is built: the offset when every point has ground, or
/// the points with none.
/// </summary>
internal readonly record struct GroundPlacement(BaseOffset? Offset, float? Highest, IReadOnlyList<string> Misses)
{
    /// <summary>
    /// Where the body goes to measure: each spot in turn, with the points nearest it. Terrain colliders exist only near the
    /// body, so each point is measured from the closest spot; a spot nearest to no point is left out.
    /// </summary>
    public static List<((float X, float Z) Spot, List<(float X, float Z)> Points)> Visits(
        IReadOnlyList<(float X, float Z)> spots, IEnumerable<(float X, float Z)> points)
    {
        var visits = spots.Select(static s => (Spot: s, Points: new List<(float X, float Z)>())).ToList();
        foreach (var p in points)
            visits.OrderBy(v => (v.Spot.X - p.X) * (v.Spot.X - p.X) + (v.Spot.Z - p.Z) * (v.Spot.Z - p.Z)).First().Points.Add(p);
        return visits.Where(static v => v.Points.Count > 0).ToList();
    }

    /// <summary>The full offset: the ground-plane move already chosen, and the rise over the highest of the samples.</summary>
    public static GroundPlacement Over((float Dx, float Dz) beside, float lowestPiece, IReadOnlyList<GroundSample> samples)
    {
        var misses = samples.Where(static s => s.Y is null).Select(static s => s.Where).ToList();
        if (samples.Count == 0 || misses.Count > 0)
            return new GroundPlacement(null, null, misses);
        var highest = samples.Max(static s => s.Y!.Value);
        return new GroundPlacement(new BaseOffset(beside.Dx, BaseOffset.Up(lowestPiece, highest), beside.Dz), highest, misses);
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
