using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.EntryPoint;
using Lattice.Core.World;
using Lattice.Game.World;
using UnityEngine;
using Cell = Lattice.Api.Cell;

namespace Lattice.Game.Live;

/// <summary>
/// Lattice's own timings in the game, read by Harmony around <see cref="LatticeWorld.Start"/> and
/// <see cref="LatticeWorld.Frame"/> (test instance only): the last start's wall time and, while recording, each frame's wall
/// time and the main thread's CPU time over it (<c>QueryThreadCycleTime</c>, whose cycles are turned into milliseconds by the
/// rate measured while the thread spins at install: <c>GetThreadTimes</c> advances only in 15.6 ms scheduler ticks). A
/// process the host pauses, or a stall off the thread, shows in wall time only.
/// </summary>
internal static partial class LatticeTiming
{
    private static readonly Stopwatch StartWatch = new();
    private static readonly Stopwatch FrameWatch = new();
    private static bool _installed;
    private static double _frameCpuStart;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    private static double _cyclesPerMs;

    public static double CyclesPerMs => _cyclesPerMs;

    /// <summary>The calling thread's CPU time in milliseconds, from its cycle count; -1 when the rate is not measured.</summary>
    public static double ThreadCpuMs() =>
        _cyclesPerMs > 0 && QueryThreadCycleTime(GetCurrentThread(), out var cycles) ? cycles / _cyclesPerMs : -1;

    // The most cycles per millisecond over three 40 ms spins of this thread: a spin the host pauses counts fewer cycles, so
    // the largest rate is the true one.
    private static double MeasureCyclesPerMs()
    {
        var best = 0.0;
        for (var i = 0; i < 3; i++)
        {
            QueryThreadCycleTime(GetCurrentThread(), out var from);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalMilliseconds < 40)
            {
            }

            QueryThreadCycleTime(GetCurrentThread(), out var to);
            best = Math.Max(best, (to - from) / watch.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    public static double LastStartMs { get; private set; } = -1;

    public static int Starts { get; private set; }

    public static bool Recording { get; set; }

    public static List<double> FrameMs { get; } = new(4096);

    public static List<double> FrameCpuMs { get; } = new(4096);

    /// <summary>Clears every recorded frame, with each frame's module and step entries.</summary>
    public static void ClearFrames()
    {
        FrameMs.Clear();
        FrameCpuMs.Clear();
        ClearParts();
    }

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        _cyclesPerMs = MeasureCyclesPerMs();
        var harmony = new Harmony("lattice.live.timing");
        harmony.Patch(AccessTools.Method(typeof(LatticeWorld), nameof(LatticeWorld.Start)),
            prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(StartBegins)), postfix: new HarmonyMethod(typeof(LatticeTiming), nameof(StartEnds)));
        harmony.Patch(AccessTools.Method(typeof(LatticeWorld), nameof(LatticeWorld.Frame)),
            prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(FrameBegins)), postfix: new HarmonyMethod(typeof(LatticeTiming), nameof(FrameEnds)));
        InstallParts(harmony);
    }

    private static void StartBegins() => StartWatch.Restart();

    private static void StartEnds()
    {
        StartWatch.Stop();
        LastStartMs = StartWatch.Elapsed.TotalMilliseconds;
        Starts++;
    }

    private static void FrameBegins()
    {
        _frameCpuStart = ThreadCpuMs();
        FrameWatch.Restart();
        if (Recording)
            BeginParts();
    }

    private static void FrameEnds()
    {
        FrameWatch.Stop();
        if (!Recording)
            return;
        FrameMs.Add(FrameWatch.Elapsed.TotalMilliseconds);
        FrameCpuMs.Add(ThreadCpuMs() - _frameCpuStart);
        EndParts();
    }
}

/// <summary>One structure of the base file: prefab, registered position and rotation, build state index.</summary>
internal sealed class BaseStructure(string prefab, Vector3 position, Quaternion rotation, int state)
{
    public string Prefab { get; } = prefab;

    public Vector3 Position { get; } = position;

    public Quaternion Rotation { get; } = rotation;

    public int State { get; } = state;

    public BaseStructure Moved(Vector3 by) => new(Prefab, Position + by, Rotation, State);
}

/// <summary>
/// A large real base on the test instance. <c>bigbase-build</c>, a new world: Lattice places a plan of
/// <see cref="Held"/> ghosts at the placements of the first base structures the game holds (held back, not built),
/// then every other structure of the base file is spawned at its registered position, rotation and build state, the
/// rest the game refuses counted and listed; saved under the base file's station name (plus <c>-held</c> when
/// <c>-lattice-held</c> is above 0). A client with no <c>-lattice-save</c> first makes a new Lunar Creative world from the main menu,
/// stands the body at the file's <c># stand</c> spot and checks the ground under the base; its facts go to <c>world.txt</c>.
/// <c>bigbase-load</c>, that save loaded: the load's
/// matching pass, the held-back structures spawned (live completion), a head save, a 5,000-ghost new plan checked,
/// steady frames, an autosave, the autosave loaded (plans equal), then the older head save loaded while the world runs
/// (plans as at the head save; the new plan hidden, not offered, since the autosave holds its lines). The base file
/// (<c>-lattice-bigbase &lt;tsv&gt;</c>) is the generated big base (LatticeFixtures\bigbase\lattice-gen-big.tsv, whose
/// <c># station</c> line names the save, <c>lattice-gen-big</c>), outside the repo. Its optional <c># offset dx dy dz</c>
/// line is where the build shifted the base in the world loaded: <c>bigbase-load</c> moves every held-back structure by it
/// (no line, no move). A server builds in the new world it starts with; a
/// client, started from the main menu, loads the world <c>-lattice-save &lt;file&gt;</c> names, or makes a new one.
/// </summary>
internal static class BigBaseRun
{
    private const int DefaultHeld = 5000;
    private const int NewWing = 5000;
    private const int RefusedListed = 200;
    private const int SpawnPerFrame = 500;
    private const int ProgressEvery = 1000;
    private static readonly Maker LiveMaker = Maker.Create(LatticeIdentity.Mod, "live")
        .Match(static m => m, static r => throw new InvalidOperationException(r.ToString()));

    public static IEnumerator Build(string outDir, Action<string> line)
    {
        var (structures, header) = ReadBase(line);
        if (structures.Count == 0)
            yield break;
        var held = HeldCount();
        var station = header.SaveName(held);
        if (station is null)
        {
            line("FAIL bigbase: the base file has no station line");
            yield break;
        }

        var gate = new StepGate();
        if (GameManager.GameState != GameState.Running)
        {
            yield return Arg("-lattice-save") != "" ? OpenWorld(line, gate) : NewWorld(line, gate);
            if (gate.Failed)
                yield break;
        }

        if (!Lattice(line, out var world))
            yield break;
        yield return WaitLanded(line, gate);
        if (gate.Failed)
            yield break;

        if (header.Stand is not { } stand)
        {
            line("FAIL bigbase: the base file has no stand line");
            yield break;
        }

        var placed = new Measured();
        yield return PlaceOnGround(structures, stand, line, gate, placed);
        if (gate.Failed)
            yield break;
        var offset = placed.Offset;
        var by = new Vector3(offset.Dx, offset.Dy, offset.Dz);
        structures = structures.ConvertAll(s => s.Moved(by));
        stand = new StandSpot(stand.X + offset.Dx, stand.Y + offset.Dy, stand.Z + offset.Dz);

        var (heldBack, rest, unknown) = Split(structures, held);
        line($"bigbase: {structures.Count} structures in the base file, {unknown} of prefabs the game lacks (modded), {heldBack.Count} held back, {rest.Count} to spawn; saved as {station}");

        var watch = Stopwatch.StartNew();
        if (held > 0)
        {
            var outcome = Submit(world, PlanOf("Held back", heldBack));
            line($"bigbase: plan of {heldBack.Count} held-back ghosts: {outcome} in {watch.Elapsed.TotalMilliseconds:F0} ms");
        }

        watch.Restart();
        var spawned = 0;
        var refused = 0;
        var lacking = new HashSet<string>();
        for (var i = 0; i < rest.Count; i++)
        {
            var s = rest[i];
            if (Scene.Spawn(s.Prefab, s.Position, s.Rotation, s.State) != null)
                spawned++;
            else
            {
                refused++;
                if (refused <= RefusedListed)
                    line($"bigbase: refused {s.Prefab} at {WorldFile.Metres(s.Position.x, s.Position.y, s.Position.z)} turn {s.Rotation.x:0.###} {s.Rotation.y:0.###} {s.Rotation.z:0.###} {s.Rotation.w:0.###} state {s.State}");
                if (Prefab.Find<Structure>(s.Prefab) == null && lacking.Add(s.Prefab))
                    line($"bigbase: the game lacks prefab {s.Prefab}");
            }

            if ((i + 1) % ProgressEvery == 0)
                line($"bigbase: spawning: {i + 1} of {rest.Count} ({refused} refused) in {watch.Elapsed.TotalSeconds:F1} s");
            if (i % SpawnPerFrame == SpawnPerFrame - 1)
                yield return null;
        }

        line($"bigbase: spawned {spawned}, refused {refused} (prefabs the game lacks {unknown}), of {rest.Count} to spawn, in {watch.Elapsed.TotalSeconds:F1} s");

        // The floor exists once the spawn is done: the body stands on it now and is held there for the rest of the run.
        yield return StandBody(stand, line);
        var ground = GroundVerdict.Judge([.. placed.Samples, .. GroundSamples(structures, stand)], LowestPiece(structures));
        line("bigbase: " + ground.Describe());
        if (!ground.Ok)
        {
            line(ground.Misses.Count > 0
                ? $"FAIL bigbase: no ground found under the base near the stand spot at {string.Join(", ", ground.Misses)}"
                : $"FAIL bigbase: the base would cut into the terrain: the ground reaches y {ground.Max}, the lowest piece stands at y {ground.Lowest} (move the generator's origin)");
            yield break;
        }

        yield return Settle(world, line, "after the spawn", 240f, HoldBody);
        if (_standAt is { } stoodAt && Assets.Scripts.Inventory.InventoryManager.Parent is { } body)
        {
            var away = Vector3.Distance(body.ThingTransformPosition, stoodAt);
            line($"{(BodyHold.Stayed(away) ? "PASS" : "FAIL")} bigbase: the body ends {away:F2} m from the stand spot after the settle (limit {BodyHold.StayedWithin} m)");
        }

        var landing = ReadLanding();
        if (!landing.Landed)
        {
            line($"FAIL bigbase: the landing is not over at the save: {landing.Pending}");
            yield break;
        }

        SaveResult? saved = null;
        yield return SaveHelper.NewSave(station, CancellationToken.None).ToCoroutine(r => saved = r);
        line($"bigbase: save {station}: {(saved is { } result ? $"success {result.Success} '{result.Message}'" : "no result")}");
        line($"bigbase: board {Board(world)}");
        WriteWorld(outDir, header, station, held, spawned, refused, unknown, saved?.Success ?? false, ground, stand, world, landing, placed);
    }

    private static Vector3? _standAt;

    // Test saves are staged after landing: no lander capsule descending or venting and the body out of any capsule
    // (LanderCapsule.LanderMode AtRest/Descending/Venting, Human.ParentSlot.Parent is LanderCapsule).
    private static Landing ReadLanding()
    {
        int descending = 0, venting = 0;
        foreach (var capsule in UnityEngine.Object.FindObjectsOfType<LanderCapsule>())
        {
            if (capsule.LanderMode == LanderMode.Descending)
                descending++;
            else if (capsule.LanderMode == LanderMode.Venting)
                venting++;
        }

        var inCapsule = Assets.Scripts.Inventory.InventoryManager.ParentHuman?.ParentSlot?.Parent is LanderCapsule;
        return new Landing(descending, venting, inCapsule);
    }

    // Waits out any landing (at most 180 s, a progress line every 60 s) before anything is built.
    private static IEnumerator WaitLanded(Action<string> line, StepGate gate)
    {
        var watch = Stopwatch.StartNew();
        var progress = new WaitProgress();
        var landing = ReadLanding();
        while (!landing.Landed && watch.Elapsed.TotalSeconds < 180)
        {
            if (progress.Due(watch.Elapsed.TotalSeconds))
                line($"bigbase: waiting for the landing: {watch.Elapsed.TotalSeconds:F0} s of 180: {landing.Pending}");
            yield return null;
            landing = ReadLanding();
        }

        if (!landing.Landed)
        {
            gate.Fail(line, $"the landing did not finish in {watch.Elapsed.TotalSeconds:F0} s: {landing.Pending}");
            yield break;
        }

        line($"bigbase: landed (after {watch.Elapsed.TotalSeconds:F1} s)");
    }

    private static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : "";
    }

    private static int HeldCount() =>
        int.TryParse(Arg("-lattice-held"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : DefaultHeld;

    private static float LowestPiece(List<BaseStructure> all)
    {
        var lowest = float.MaxValue;
        foreach (var s in all)
            lowest = Math.Min(lowest, s.Position.y);
        return lowest;
    }

    // The ground cast again after the spawn, with the body at the stand spot: terrain colliders exist only near the body (only
    // LOD level 0 has colliders, TerrainSystem.Lods\LodManager.cs:162, and the LODs are centred on each requester,
    // LodManager.cs:713), so only the candidate points within the loaded radius of the stand spot. The verdict judges these
    // with every point measured over the footprint before the spawn. A ray that hits nothing is a sample with no height.
    private static List<GroundSample> GroundSamples(List<BaseStructure> all, StandSpot stand)
    {
        var mask = TerrainMask();
        var samples = new List<GroundSample>();
        foreach (var (x, z) in GroundVerdict.Within(GroundVerdict.Candidates(FootprintOf(all), stand.X, stand.Z), stand.X, stand.Z))
            samples.Add(new GroundSample(x, z, GroundAt(x, z, stand.Y, mask)));
        return samples;
    }

    private static Footprint FootprintOf(List<BaseStructure> all) =>
        Footprint.Of(all.ConvertAll(static s => (s.Position.x, s.Position.y, s.Position.z)))
        ?? throw new InvalidOperationException("a base with no pieces has no footprint");

    // The terrain's height at a point: a ray down at the terrain layer only, triggers ignored, from 1,000 m over a height
    // near the ground there and 2,000 m long, so neither a hill nor a hollow puts its start inside the ground or its end
    // above it; null where it hits nothing (no terrain colliders there yet).
    private static float? GroundAt(float x, float z, float near, int mask) =>
        mask != 0 && Physics.Raycast(new Vector3(x, near + 1000f, z), Vector3.down, out var hit, 2000f, mask, QueryTriggerInteraction.Ignore)
            ? hit.point.y
            : null;

    private sealed class Measured
    {
        public BaseOffset Offset { get; set; } = BaseOffset.Zero;

        /// <summary>The ground under the body where it landed.</summary>
        public Vector3? Landing { get; set; }

        /// <summary>Every candidate point's ground, measured before the spawn, in world coordinates.</summary>
        public List<GroundSample> Samples { get; } = [];
    }

    private const float MeasureLeast = 5f;
    private const float MeasureMost = 30f;

    // The base moves off the file's fixed coordinates to this world's ground beside the landing: across, by the landing and
    // its lander (BaseOffset.Beside); up, over the highest terrain measured at every point the ground is judged at, the
    // footprint's 8 m grid among them. Terrain colliders exist only near the body (LodManager.cs:162, :713), so the body is
    // stood over the stand spot, each footprint corner and the centre in turn and held there until every point nearest it
    // has ground (at least MeasureLeast, at most MeasureMost seconds).
    private static IEnumerator PlaceOnGround(List<BaseStructure> structures, StandSpot stand, Action<string> line, StepGate gate, Measured result)
    {
        if (Assets.Scripts.Inventory.InventoryManager.Parent is not { } body)
        {
            line("bigbase: no body (server): the base stays at the file's coordinates");
            yield break;
        }

        // A new world's terrain colliders come some seconds after the body (only LOD level 0 has colliders,
        // TerrainSystem.Lods\LodManager.cs:162, centred on each requester, LodManager.cs:713): the body is held where it
        // landed until a ray down from 100 m over it hits the terrain.
        var mask = TerrainMask();
        var at = body.ThingTransformPosition;
        _standAt = at;
        var waited = Stopwatch.StartNew();
        float? ground = null;
        while (ground is null && waited.Elapsed.TotalSeconds < MeasureMost)
        {
            yield return null;
            HoldBody();
            ground = mask != 0 && Physics.Raycast(at + Vector3.up * 100f, Vector3.down, out var down, 400f, mask, QueryTriggerInteraction.Ignore)
                ? down.point.y
                : null;
        }

        if (ground is not { } landingGround)
        {
            gate.Fail(line, $"no ground under the landing after {MeasureMost:F0} s: the body is at {WorldFile.Metres(at.x, at.y, at.z)} and a ray down from 100 m over it hits no terrain");
            yield break;
        }

        result.Landing = new Vector3(at.x, landingGround, at.z);
        Vector3? lander = null;
        foreach (var capsule in UnityEngine.Object.FindObjectsOfType<LanderCapsule>())
            if (lander is not { } l || Vector3.Distance(capsule.ThingTransformPosition, at) < Vector3.Distance(l, at))
                lander = capsule.ThingTransformPosition;

        var footprint = FootprintOf(structures);
        var (dx, dz) = BaseOffset.Beside(footprint, new LandingSpot(at.x, at.z, lander?.x, lander?.z));
        var plane = footprint.Moved(new BaseOffset(dx, 0f, dz));
        var (standX, standZ) = (stand.X + dx, stand.Z + dz);
        line($"bigbase: landing: body at {WorldFile.Metres(at.x, at.y, at.z)}, ground y {landingGround:0.##} after {waited.Elapsed.TotalSeconds:F1} s, lander {(lander is { } p ? WorldFile.Metres(p.x, p.y, p.z) : "none")}; " +
             $"the base goes to x {plane.MinX:0.##} to {plane.MaxX:0.##}, z {plane.MinZ:0.##} to {plane.MaxZ:0.##} (move x {dx:0.##} z {dz:0.##})");

        var samples = result.Samples;
        var near = landingGround;
        foreach (var (spot, points) in GroundPlacement.Visits([(standX, standZ), .. plane.Corners], GroundVerdict.Candidates(plane, standX, standZ)))
        {
            _standAt = new Vector3(spot.X, near + 2f, spot.Z);
            Restand();
            var watch = Stopwatch.StartNew();
            var heights = new float?[points.Count];
            do
            {
                yield return null;
                HoldBody();
                for (var i = 0; i < points.Count; i++)
                    heights[i] = GroundAt(points[i].X, points[i].Z, near, mask);
            }
            while (watch.Elapsed.TotalSeconds < MeasureLeast || (Array.IndexOf(heights, null) >= 0 && watch.Elapsed.TotalSeconds < MeasureMost));

            var found = new List<string>(points.Count);
            for (var i = 0; i < points.Count; i++)
            {
                samples.Add(new GroundSample(points[i].X, points[i].Z, heights[i]));
                found.Add(heights[i] is { } y ? y.ToString("0.##", CultureInfo.InvariantCulture) : "none");
            }

            line($"bigbase: ground near x {spot.X:0.##} z {spot.Z:0.##} after {watch.Elapsed.TotalSeconds:F1} s: {string.Join(" ", found)}");
            foreach (var y in heights)
                if (y is { } h)
                {
                    near = h;
                    break;
                }
        }

        var placement = GroundPlacement.Over((dx, dz), footprint.Lowest, samples);
        if (placement.Offset is not { } offset)
        {
            gate.Fail(line, $"no ground under the base after waiting {MeasureMost:F0} s at {string.Join(", ", placement.Misses)}");
            yield break;
        }

        result.Offset = offset;
        line($"bigbase: offset {offset.Text}: highest ground {placement.Highest:0.##}, lowest piece {footprint.Lowest:0.##} to {footprint.Lowest + offset.Dy:0.##}");
    }

    private static int TerrainMask()
    {
        var layer = LayerMask.NameToLayer("Terrain");
        return layer >= 0 ? 1 << layer : 0;
    }

    // The body stood at the stand spot as the game's teleport does (Human.ForceSetPosition: position, transform, rigidbody and
    // control position; a transform alone is put back by the body's own motion), then given 10 s for terrain to build around it.
    private static IEnumerator StandBody(StandSpot stand, Action<string> line)
    {
        if (Assets.Scripts.Inventory.InventoryManager.Parent == null)
        {
            line("bigbase: no body (server)");
            yield break;
        }

        _standAt = new Vector3(stand.X, stand.Y, stand.Z);
        Restand();
        var until = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < until)
        {
            HoldBody();
            yield return null;
        }

        line($"bigbase: body stands at {Assets.Scripts.Inventory.InventoryManager.Parent.ThingTransformPosition} (stand spot {WorldFile.Metres(stand.X, stand.Y, stand.Z)})");
    }

    private static void Restand()
    {
        if (_standAt is { } at)
            Assets.Scripts.Inventory.InventoryManager.ParentHuman?.ForceSetPosition(at);
    }

    // Called every frame after the stand: the body is put back when it slides or falls from the stand spot.
    private static void HoldBody()
    {
        if (_standAt is { } at && Assets.Scripts.Inventory.InventoryManager.Parent is { } body
            && BodyHold.NeedsRestand(Vector3.Distance(body.ThingTransformPosition, at)))
            Restand();
    }

    private static void WriteWorld(string outDir, BaseHeader header, string station, int held, int spawned, int refused, int unknown,
        bool saveSuccess, GroundVerdict ground, StandSpot stand, LatticeWorld world, Landing landing, Measured placed)
    {
        var path = Arg("-lattice-bigbase");
        string sha1;
        using (var sha = System.Security.Cryptography.SHA1.Create())
            sha1 = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
        string N(float? v) => v is { } y ? y.ToString("0.##", CultureInfo.InvariantCulture) : "none";
        var facts = new List<KeyValuePair<string, string?>>
        {
            new("station", station),
            new("tsv", Path.GetFileName(path)),
            new("tsv-sha1", sha1),
            new("seed", header.Seed),
            new("catalogue-sha1", header.CatalogueSha1),
            new("mix-sha1", header.MixSha1),
            new("world", WorldSetting.Current?.Id),
            new("difficulty", DifficultySetting.Current?.Id),
            new("start", WorldSetting.Current?.StartConditionData?.Id),
            new("held", held.ToString(CultureInfo.InvariantCulture)),
            new("spawned", spawned.ToString(CultureInfo.InvariantCulture)),
            new("refused", refused.ToString(CultureInfo.InvariantCulture)),
            new("unknown", unknown.ToString(CultureInfo.InvariantCulture)),
            new("structures", Scene.AllStructures().Count.ToString(CultureInfo.InvariantCulture)),
            new("plans", world.Ghosts.Index.Plans.Count.ToString(CultureInfo.InvariantCulture)),
            new("ghosts", world.Ghosts.Index.Ghosts.Count.ToString(CultureInfo.InvariantCulture)),
            new("landed", landing.Landed ? "yes" : "no"),
            new("offset", placed.Offset.Text),
            new("landing", placed.Landing is { } at ? WorldFile.Metres(at.x, at.y, at.z) : null),
            new("stand", WorldFile.Metres(stand.X, stand.Y, stand.Z)),
            new("ground-min", N(ground.Min)),
            new("ground-max", N(ground.Max)),
            new("lowest-piece", N(ground.Lowest)),
            new("save-success", saveSuccess ? "True" : "False"),
            new("lattice-version", typeof(LatticeWorld).Assembly.GetName().Version.ToString()),
        };
        File.WriteAllText(Path.Combine(outDir, "world.txt"), WorldFile.Text(facts));
    }

    // The main menu, up and visible with the save system set: a load or a new game started before that throws inside the
    // game (Assets.Scripts.Serialization/LoadHelper.cs LoadGameTask hides the menu first; Assets.Scripts.Objects/World.cs
    // StartNewWorld reads MainMenu.Instance). Not up after 300 s ends the build.
    private static IEnumerator MainMenu(Action<string> line, StepGate gate)
    {
        var watch = Stopwatch.StartNew();
        var progress = new WaitProgress();
        var menuBy = Time.realtimeSinceStartup + 300f;
        while ((Assets.Scripts.UI.MainMenu.Instance == null || !Assets.Scripts.UI.MainMenu.Instance.IsVisible || XmlSaveLoad.Instance == null)
               && Time.realtimeSinceStartup < menuBy)
        {
            if (progress.Due(watch.Elapsed.TotalSeconds))
                line($"bigbase: waiting for the main menu: {watch.Elapsed.TotalSeconds:F0} s of 300");
            yield return null;
        }

        if (Assets.Scripts.UI.MainMenu.Instance == null || XmlSaveLoad.Instance == null)
        {
            gate.Fail(line, $"the main menu did not come up in {watch.Elapsed.TotalSeconds:F0} s");
            yield break;
        }

        yield return new WaitForSecondsRealtime(2f);
        line($"bigbase: main menu up after {watch.Elapsed.TotalSeconds:F1} s");
    }

    // A new Lunar Creative world with the first default start, as the game's own newgame command makes one
    // (Util.Commands.NewGameCommand, Assets.Scripts.Objects/World.cs StartNewWorld); Creative, so a body standing through
    // the whole run keeps its air and food.
    private static IEnumerator NewWorld(Action<string> line, StepGate gate)
    {
        yield return MainMenu(line, gate);
        if (gate.Failed)
            yield break;
        var world = WorldSetting.Find("Lunar");
        var difficulty = DifficultySetting.Find("Creative");
        StartConditionData? start = null;
        if (world != null)
            foreach (var condition in world.Data.StartConditionDatas)
                if (condition.IsDefault)
                {
                    start = condition;
                    break;
                }

        if (world == null || difficulty == null || start == null)
        {
            gate.Fail(line, $"the game has no world Lunar ({world != null}), difficulty Creative ({difficulty != null}) or default start ({start != null})");
            yield break;
        }

        var watch = Stopwatch.StartNew();
        var progress = new WaitProgress();
        var starts = LatticeTiming.Starts;
        WorldSetting.SetCurrent(world, start);
        DifficultySetting.SetCurrent(difficulty);
        Assets.Scripts.UI.ImGuiLoadingScreen.WorldName = world.Id;
        Assets.Scripts.Objects.World.StartNewWorld(world.Id).Forget();
        var deadline = Time.realtimeSinceStartup + 600f;
        while ((GameManager.GameState != GameState.Running || Assets.Scripts.Inventory.InventoryManager.Parent == null || LatticeTiming.Starts == starts)
               && Time.realtimeSinceStartup < deadline)
        {
            if (progress.Due(watch.Elapsed.TotalSeconds))
                line($"bigbase: waiting for the new world: {watch.Elapsed.TotalSeconds:F0} s of 600 (state {GameManager.GameState}, body {Assets.Scripts.Inventory.InventoryManager.Parent != null})");
            yield return null;
        }

        if (GameManager.GameState != GameState.Running || Assets.Scripts.Inventory.InventoryManager.Parent == null || LatticeTiming.Starts == starts)
        {
            gate.Fail(line, $"the new world did not start (state {GameManager.GameState}, body {Assets.Scripts.Inventory.InventoryManager.Parent != null})");
            yield break;
        }

        line($"bigbase: new world {world.Id} {difficulty.Id} {start.Id}: running after {watch.Elapsed.TotalSeconds:F1} s");
    }

    // A client starts at the main menu: the world to build in is loaded from it, once the menu is up, and the build waits
    // for the world to run with Lattice started.
    private static IEnumerator OpenWorld(Action<string> line, StepGate gate)
    {
        var save = Arg("-lattice-save");
        if (!File.Exists(save))
        {
            gate.Fail(line, $"no world is running and no world to load: '{save}' (-lattice-save <file>)");
            yield break;
        }

        var watch = Stopwatch.StartNew();
        var progress = new WaitProgress();
        yield return MainMenu(line, gate);
        if (gate.Failed)
            yield break;
        line($"bigbase: loading {save}");

        var starts = LatticeTiming.Starts;
        LoadHelper.LoadGame(save, Path.GetFileNameWithoutExtension(save));
        var deadline = Time.realtimeSinceStartup + 600f;
        var refusedBy = Time.realtimeSinceStartup + 30f;
        while ((LatticeTiming.Starts == starts || GameManager.GameState != GameState.Running) && Time.realtimeSinceStartup < deadline)
        {
            // A refused load clears the world save the game was handed and leaves the state at None.
            if (Time.realtimeSinceStartup > refusedBy && XmlSaveLoad.Instance != null && XmlSaveLoad.Instance.CurrentWorldSave == null && GameManager.GameState == GameState.None)
            {
                gate.Fail(line, $"the game refused to load {save} (the client log has the game's error)");
                yield break;
            }

            if (progress.Due(watch.Elapsed.TotalSeconds))
                line($"bigbase: waiting for the loaded world: {watch.Elapsed.TotalSeconds:F0} s of 600 (state {GameManager.GameState})");
            yield return null;
        }

        yield return new WaitForSecondsRealtime(3f);
        line($"bigbase: world running after {watch.Elapsed.TotalSeconds:F1} s (Lattice starts {LatticeTiming.Starts}, state {GameManager.GameState})");
    }

    public static IEnumerator Load(string outDir, float sinceRunning, Action<string> line)
    {
        if (GameManager.GameState != GameState.Running && Arg("-lattice-save") != "")
        {
            var gate = new StepGate();
            yield return OpenWorld(line, gate);
            if (gate.Failed)
                yield break;
        }

        line($"bigbase: world running at {Time.realtimeSinceStartup - sinceRunning:F1} s after process start; Lattice start {LatticeTiming.LastStartMs:F0} ms (starts so far {LatticeTiming.Starts})");
        if (!Lattice(line, out var world))
            yield break;
        line($"bigbase: load match {world.Match.Map(static m => m.ToString()).Reduce("none")}");
        line($"bigbase: board {Board(world)}; structures in the world {Scene.AllStructures().Count}");
        var (structures, header) = ReadBase(line);
        var heldCount = HeldCount();
        var station = header.SaveName(heldCount);
        if (station is null)
        {
            line(structures.Count == 0 ? "FAIL bigbase: no base to load against" : "FAIL bigbase: the base file has no station line");
            yield break;
        }

        // The build shifted the base onto this world's ground; the file's own coordinates are 2 km off in the world.
        var offset = header.Offset;
        line($"bigbase: offset {offset.Text} ({(offset == BaseOffset.Zero ? "none in the base file: its own coordinates" : "from the base file's # offset line")})");
        var by = new Vector3(offset.Dx, offset.Dy, offset.Dz);
        structures = structures.ConvertAll(s => s.Moved(by));
        var footprint = FootprintOf(structures);

        var (held, _, _) = Split(structures, heldCount);

        LatticeTiming.Recording = true;
        var watch = Stopwatch.StartNew();
        var spawned = 0;
        Vector3? firstAt = null;
        for (var i = 0; i < held.Count; i++)
        {
            if (Scene.Spawn(held[i].Prefab, held[i].Position, held[i].Rotation, held[i].State) is { } made)
            {
                spawned++;
                firstAt ??= made.ThingTransformPosition;
            }

            if (i % SpawnPerFrame == SpawnPerFrame - 1)
                yield return null;
        }

        line($"bigbase: held-back structures spawned in {watch.Elapsed.TotalSeconds:F1} s: {spawned} of {held.Count}");
        if (firstAt is { } first)
        {
            var inside = footprint.DistanceTo(first.x, first.z) == 0f && first.y >= footprint.Lowest - BaseOffset.HalfGrid;
            line($"{(inside ? "PASS" : "FAIL")} the first held-back structure spawned at {WorldFile.Metres(first.x, first.y, first.z)}, " +
                 $"{(inside ? "inside" : "outside")} the base's footprint x {footprint.MinX:0.##} to {footprint.MaxX:0.##}, z {footprint.MinZ:0.##} to {footprint.MaxZ:0.##}");
        }
        else
            line("FAIL no held-back structure spawned");
        yield return Settle(world, line, "completion of the held-back plan", 300f);
        line($"bigbase: board {Board(world)}");

        yield return Save(SaveHelper.Save(station, CancellationToken.None), "head save (older)", line);
        var atHead = Dump(world);
        File.WriteAllText(Path.Combine(outDir, "bigbase-plans-head.txt"), atHead);

        var fresh = FreshCells(held, NewWing);
        watch.Restart();
        var outcome = Submit(world, PlanOf("New wing", fresh));
        line($"bigbase: new plan of {fresh.Count} ghosts beside the base: {outcome} in {watch.Elapsed.TotalMilliseconds:F0} ms");
        yield return Settle(world, line, "validation of the new plan", 600f);
        yield return Frames(world, line, "steady", 600);

        yield return Save(SaveHelper.AutoSave(station, CancellationToken.None), "autosave (newer)", line);
        var before = Dump(world);
        File.WriteAllText(Path.Combine(outDir, "bigbase-plans-before.txt"), before);
        var auto = Newest(Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, station, "autosave"));

        // GameManager.LeaveGame starts with CameraController.Instance, which a headless server lacks
        // (Assets.Scripts/GameManager.cs:1006-1009): a dedicated server has no quit to the menu.
        line("NOT RUN bigbase: quit to the menu, which a dedicated server lacks; the two-player client covers it");

        var starts = LatticeTiming.Starts;
        yield return LoadAndWait(auto, station, starts, line, "the autosave while the world runs");
        if (!Lattice(line, out world))
            yield break;
        var after = Dump(world);
        File.WriteAllText(Path.Combine(outDir, "bigbase-plans-after.txt"), after);
        line($"{(after == before ? "PASS" : "FAIL")} plans after the autosave reload equal the plans before ({Lines(before)} plan lines)");
        line($"bigbase: Lattice start on reload {LatticeTiming.LastStartMs:F0} ms; load match {world.Match.Map(static m => m.ToString()).Reduce("none")}");

        var head = Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, station, station + ".save");
        starts = LatticeTiming.Starts;
        yield return LoadAndWait(head, station, starts, line, "the older head save while the world runs");
        if (!Lattice(line, out world))
            yield break;
        var older = Dump(world);
        var newWing = PlanNamed(world, "New wing");
        var load = world.Plans.LastLoad;
        var found = load.Map(static l => l.Record is SaveRecord.Found).Reduce(false);
        var ahead = load.Map(static l => l.Ahead.Count).Reduce(-1);
        // A plan made after a save is hidden when that save loads; a later save holding its lines means no crash
        // to recover, so nothing is offered.
        line($"{(older == atHead && newWing.IsNone && found && ahead == 0 ? "PASS" : "FAIL")} the older save loads its plans as at the head save, the new plan hidden and not offered: plans equal {older == atHead} ({Lines(atHead)} plan lines), new plan on the board {newWing.IsSome}, record found {found}, plans ahead {ahead}");
        File.WriteAllText(Path.Combine(outDir, "bigbase-plans-older.txt"), older);
        LatticeTiming.Recording = false;
    }

    private static bool Lattice(Action<string> line, out LatticeWorld world)
    {
        if (LatticeSystem.Current.TryGet(out var system) && system is LatticeWorld found)
        {
            world = found;
            return true;
        }

        world = null!;
        line("FAIL bigbase: Lattice is not running in this world");
        return false;
    }

    private static IEnumerator LoadAndWait(string path, string station, int starts, Action<string> line, string what)
    {
        line($"bigbase: loading {what}: {path}");
        var watch = Stopwatch.StartNew();
        LoadHelper.LoadGame(path, station);
        var deadline = Time.realtimeSinceStartup + 300f;
        while ((LatticeTiming.Starts == starts || GameManager.GameState != GameState.Running) && Time.realtimeSinceStartup < deadline)
            yield return null;
        yield return new WaitForSecondsRealtime(2f);
        line($"{(LatticeTiming.Starts > starts && LatticeSystem.Current.IsSome ? "PASS" : "FAIL")} loading {what} restarts Lattice: {watch.Elapsed.TotalSeconds:F1} s to running, Lattice start {LatticeTiming.LastStartMs:F0} ms");
    }

    private static IEnumerator Save(UniTask<SaveResult> save, string what, Action<string> line)
    {
        var watch = Stopwatch.StartNew();
        SaveResult? result = null;
        yield return save.ToCoroutine(r => result = r);
        line($"bigbase: {what}: {(result is { } r ? $"success {r.Success}" : "no result")} in {watch.Elapsed.TotalSeconds:F1} s");
    }

    // Frames until validation is idle and every slice queue is drained (FrameScheduler.Idle) for 3 frames in a row, recording game and Lattice frame costs.
    private static IEnumerator Settle(LatticeWorld world, Action<string> line, string what, float limit, Action? tick = null)
    {
        LatticeTiming.Watching = world;
        LatticeTiming.ClearFrames();
        var game = new List<double>(4096);
        var start = Time.realtimeSinceStartup;
        var idle = 0;
        var progress = new WaitProgress();
        while (Time.realtimeSinceStartup - start < limit && idle < 3)
        {
            yield return null;
            tick?.Invoke();
            if (progress.Due(Time.realtimeSinceStartup - start))
                line($"bigbase: {what}: still validating or draining after {Time.realtimeSinceStartup - start:F0} s of {limit:F0}");
            game.Add(Time.unscaledDeltaTime * 1000.0);
            idle = world.ValidationModule is { Busy: true } || !world.Scheduler.Idle ? 0 : idle + 1;
        }

        line($"bigbase: {what}: {(idle >= 3 ? "settled" : "NOT settled")} after {Time.realtimeSinceStartup - start:F1} s; game frames {Stats(game)}; Lattice frames {Stats(LatticeTiming.FrameMs)}");
        foreach (var slow in LatticeTiming.Describe(what, 5, 0, int.MaxValue))
            line(slow);
        if (idle < 3)
        {
            var validating = world.ValidationModule is { Busy: true };
            line($"FAIL bigbase: {what} NOT settled after {Time.realtimeSinceStartup - start:F0} s (limit {limit:F0} s): validation busy {validating}, frame scheduler idle {world.Scheduler.Idle}");
        }
    }

    private static IEnumerator Frames(LatticeWorld world, Action<string> line, string what, int count)
    {
        LatticeTiming.Watching = world;
        LatticeTiming.ClearFrames();
        var (gc0, gc1, gc2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var game = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            yield return null;
            game.Add(Time.unscaledDeltaTime * 1000.0);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        world.Frame();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        line($"bigbase: {what}: game frames {Stats(game)}; Lattice frames (wall) {Stats(LatticeTiming.FrameMs)}; Lattice frames (cpu) {Stats(LatticeTiming.FrameCpuMs)}; " +
             $"gc gen0/1/2 {GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}; cpu rate {LatticeTiming.CyclesPerMs / 1e6:F2} GHz; one Lattice frame now allocates {allocated} bytes");
        foreach (var slow in LatticeTiming.Describe(what, 50, 6.0, count))
            line(slow);
    }

    private static string Stats(List<double> ms)
    {
        if (ms.Count == 0)
            return "none";
        var sorted = new List<double>(ms);
        sorted.Sort();
        double At(double p) => sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1))];
        var over = 0;
        foreach (var m in ms)
            if (m > 6.0)
                over++;
        return $"{ms.Count} median {At(0.5):F2} ms p99 {At(0.99):F2} ms max {sorted[sorted.Count - 1]:F2} ms, over 6 ms {over}";
    }

    private static Outcome Submit(LatticeWorld world, Command command) =>
        world.Commands.Submit(new CommandId(Guid.NewGuid()), command, LatticeIdentity.Actor) switch
        {
            Submission.Answered answered => answered.Outcome,
            var other => throw new InvalidOperationException("not answered at once: " + other),
        };

    private static Command PlanOf(string name, List<BaseStructure> structures)
    {
        var drafts = new List<GhostDraft>(structures.Count);
        foreach (var s in structures)
            if (PlacementOf(s).TryGet(out var placement))
                drafts.Add(Draft(placement));
        return PlanOf(name, drafts);
    }

    private static Command PlanOf(string name, List<Cell> cells)
    {
        var frame = new PrefabKey(Animator.StringToHash(Scene.FrameName));
        var drafts = new List<GhostDraft>(cells.Count);
        foreach (var cell in cells)
            drafts.Add(Draft(new Placement(frame, cell, Turn.None)));
        return PlanOf(name, drafts);
    }

    private static Command PlanOf(string name, List<GhostDraft> drafts) =>
        new Command.CreatePlan(new PlanDraft(
            PlanName.Create(name).Match(static n => n, static r => throw new InvalidOperationException(r.ToString())),
            LiveMaker, new Lattice.Api.Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty))
        {
            Ghosts = ValueList.From(drafts),
        };

    private static GhostDraft Draft(Placement placement) => new(
        new Intent.Build(placement, Option<Finish>.None), Option<PlanId>.None, LiveMaker, new Lattice.Api.Visibility.Everyone(),
        Option<Lattice.Api.Label>.None, Option<Tint>.None, ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty);

    private static Option<Placement> PlacementOf(BaseStructure s)
    {
        var prefab = Prefab.Find<Structure>(s.Prefab);
        if (prefab == null)
            return Option<Placement>.None;
        var game = new GameStructure(0, prefab.PrefabHash, s.Position.x, s.Position.y, s.Position.z, GameReads.Rotation(s.Rotation),
            s.State, prefab.BuildStates?.Count ?? 1, -1, "", Option<double>.None);
        return StructureReading.Read(game).TryGet(out var info)
            ? Option.Some(new Placement(info.Ref.Piece, info.Ref.At, info.Turn))
            : Option<Placement>.None;
    }

    // Large cells in a layer 20 m over the highest held-back structure, one large cell apart.
    private static List<Cell> FreshCells(List<BaseStructure> near, int count)
    {
        var minX = float.MaxValue;
        var minZ = float.MaxValue;
        var maxY = float.MinValue;
        foreach (var s in near)
        {
            minX = Math.Min(minX, s.Position.x);
            minZ = Math.Min(minZ, s.Position.z);
            maxY = Math.Max(maxY, s.Position.y);
        }

        static int Large(float metres) => ((int)Math.Floor(metres / 2f) * 2 + 1) * 10;
        var (x0, y, z0) = (Large(minX), Large(maxY + 20f), Large(minZ));
        var side = (int)Math.Ceiling(Math.Sqrt(count));
        var cells = new List<Cell>(count);
        for (var i = 0; i < count; i++)
            cells.Add(new Cell(x0 + 20 * (i % side), y, z0 + 20 * (i / side)));
        return cells;
    }

    // The first known structures with a build state are held back; every other row is to spawn, the rows of prefabs the
    // game lacks among them (the spawn refuses those, and the count tells them apart).
    private static (List<BaseStructure> Held, List<BaseStructure> Others, int Unknown) Split(List<BaseStructure> all, int heldCount)
    {
        var held = new List<BaseStructure>(heldCount);
        var rest = new List<BaseStructure>(all.Count);
        var unknown = 0;
        foreach (var s in all)
        {
            var known = Prefab.Find<Structure>(s.Prefab) != null;
            if (!known)
                unknown++;
            if (known && held.Count < heldCount && s.State >= 0)
                held.Add(s);
            else
                rest.Add(s);
        }

        return (held, rest, unknown);
    }

    // The base file's structures and its header; no structures (after a FAIL line) when the file is missing.
    private static (List<BaseStructure> Structures, BaseHeader Header) ReadBase(Action<string> line)
    {
        var path = Arg("-lattice-bigbase");
        var list = new List<BaseStructure>(40000);
        if (!File.Exists(path))
        {
            line($"FAIL bigbase: no base file '{path}' (-lattice-bigbase <tsv>)");
            return (list, BaseHeader.Read(Array.Empty<string>()));
        }

        var lines = File.ReadAllLines(path);
        foreach (var text in lines)
        {
            if (text.StartsWith("#", StringComparison.Ordinal))
                continue;
            var f = text.Split('\t');
            float F(int i) => float.Parse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture);
            list.Add(new BaseStructure(f[0], new Vector3(F(1), F(2), F(3)), new Quaternion(F(4), F(5), F(6), F(7)), int.Parse(f[8], CultureInfo.InvariantCulture)));
        }

        return (list, BaseHeader.Read(lines));
    }

    private static string Board(LatticeWorld world) =>
        $"{world.Ghosts.Index.Ghosts.Count} ghosts in {world.Ghosts.Index.Plans.Count} plans";

    private static Option<PlanId> PlanNamed(LatticeWorld world, string name)
    {
        foreach (var entry in world.Ghosts.Index.Plans)
            if (entry.Value.Plan.Name.Value == name)
                return Option.Some(entry.Key);
        return Option<PlanId>.None;
    }

    // Plans by id: name, finished, and their ghosts by id with piece and cell, sorted.
    private static string Dump(LatticeWorld world)
    {
        var lines = new List<string>(world.Ghosts.Index.Plans.Count + 1);
        foreach (var entry in world.Ghosts.Index.Plans)
        {
            var ghosts = new List<string>(entry.Value.Ghosts.Count);
            foreach (var id in entry.Value.Ghosts)
                if (world.Ghosts.Index.Ghosts.TryGetValue(id, out var ghost))
                    ghosts.Add($"{id.Value}:{ghost.Ghost.Intent}");
            ghosts.Sort(StringComparer.Ordinal);
            lines.Add($"plan {entry.Key.Value} '{entry.Value.Plan.Name.Value}' ghosts {ghosts.Count} {Hash(string.Join("|", ghosts))}");
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    private static string Hash(string text)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return BitConverter.ToString(bytes, 0, 6).Replace("-", "");
    }

    private static int Lines(string text) => text.Split('\n').Length - 1;

    private static string Newest(string folder)
    {
        var newest = "";
        var at = DateTime.MinValue;
        if (Directory.Exists(folder))
            foreach (var file in Directory.GetFiles(folder, "*.save"))
                if (File.GetLastWriteTimeUtc(file) > at)
                {
                    at = File.GetLastWriteTimeUtc(file);
                    newest = file;
                }

        return newest;
    }
}
