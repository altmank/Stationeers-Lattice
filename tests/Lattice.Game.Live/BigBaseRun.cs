using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
/// <see cref="LatticeWorld.Frame"/> (test instance only): the last start's wall time and, while recording, each frame's.
/// </summary>
internal static class LatticeTiming
{
    private static readonly Stopwatch StartWatch = new();
    private static readonly Stopwatch FrameWatch = new();
    private static bool _installed;

    public static double LastStartMs { get; private set; } = -1;

    public static int Starts { get; private set; }

    public static bool Recording { get; set; }

    public static List<double> FrameMs { get; } = new(4096);

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        var harmony = new Harmony("lattice.live.timing");
        harmony.Patch(AccessTools.Method(typeof(LatticeWorld), nameof(LatticeWorld.Start)),
            prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(StartBegins)), postfix: new HarmonyMethod(typeof(LatticeTiming), nameof(StartEnds)));
        harmony.Patch(AccessTools.Method(typeof(LatticeWorld), nameof(LatticeWorld.Frame)),
            prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(FrameBegins)), postfix: new HarmonyMethod(typeof(LatticeTiming), nameof(FrameEnds)));
    }

    private static void StartBegins() => StartWatch.Restart();

    private static void StartEnds()
    {
        StartWatch.Stop();
        LastStartMs = StartWatch.Elapsed.TotalMilliseconds;
        Starts++;
    }

    private static void FrameBegins() => FrameWatch.Restart();

    private static void FrameEnds()
    {
        FrameWatch.Stop();
        if (Recording)
            FrameMs.Add(FrameWatch.Elapsed.TotalMilliseconds);
    }
}

/// <summary>One structure of the base file: prefab, registered position and rotation, build state index.</summary>
internal sealed class BaseStructure(string prefab, Vector3 position, Quaternion rotation, int state)
{
    public string Prefab { get; } = prefab;

    public Vector3 Position { get; } = position;

    public Quaternion Rotation { get; } = rotation;

    public int State { get; } = state;
}

/// <summary>
/// A large real base on the test instance. <c>bigbase-build</c>, a new world: Lattice places a plan of
/// <see cref="Held"/> ghosts at the placements of the first base structures the game holds (held back, not built),
/// then every other structure of the base file is spawned at its registered position, rotation and build state, the
/// modded rest skipped and counted; saved as <c>lattice-big</c>. <c>bigbase-load</c>, that save loaded: the load's
/// matching pass, the held-back structures spawned (live completion), a head save, a 5,000-ghost new plan checked,
/// steady frames, an autosave, the autosave loaded (plans equal), then the older head save loaded while the world runs
/// (plans as at the head save; the new plan hidden, not offered, since the autosave holds its lines). The base file is
/// the structures of a Vulcan2_4 save copy, extracted outside the repo.
/// </summary>
internal static class BigBaseRun
{
    public const string Station = "lattice-big";
    private const int Held = 5000;
    private const int SpawnPerFrame = 500;
    private static readonly Maker LiveMaker = Maker.Create(LatticeIdentity.Mod, "live")
        .Match(static m => m, static r => throw new InvalidOperationException(r.ToString()));

    public static IEnumerator Build(Action<string> line)
    {
        if (!Lattice(line, out var world))
            yield break;
        var structures = ReadBase(line);
        var (held, rest, unknown) = Split(structures);
        line($"bigbase: {structures.Count} structures in the base file, {unknown} of prefabs the game lacks (modded), {held.Count} held back, {rest.Count} to spawn");

        var watch = Stopwatch.StartNew();
        var outcome = Submit(world, PlanOf("Held back", held));
        line($"bigbase: plan of {held.Count} held-back ghosts: {outcome} in {watch.Elapsed.TotalMilliseconds:F0} ms");

        watch.Restart();
        var spawned = 0;
        var failed = 0;
        for (var i = 0; i < rest.Count; i++)
        {
            var s = rest[i];
            if (Scene.Spawn(s.Prefab, s.Position, s.Rotation, s.State) != null)
                spawned++;
            else
                failed++;
            if (i % SpawnPerFrame == SpawnPerFrame - 1)
                yield return null;
        }

        line($"bigbase: spawned {spawned}, failed {failed}, in {watch.Elapsed.TotalSeconds:F1} s");
        yield return Settle(world, line, "after the spawn", 240f);
        SaveResult? saved = null;
        yield return SaveHelper.NewSave(Station, CancellationToken.None).ToCoroutine(r => saved = r);
        line($"bigbase: save {Station}: {(saved is { } result ? $"success {result.Success} '{result.Message}'" : "no result")}");
        line($"bigbase: board {Board(world)}");
    }

    public static IEnumerator Load(string outDir, float sinceRunning, Action<string> line)
    {
        line($"bigbase: world running at {Time.realtimeSinceStartup - sinceRunning:F1} s after process start; Lattice start {LatticeTiming.LastStartMs:F0} ms (starts so far {LatticeTiming.Starts})");
        if (!Lattice(line, out var world))
            yield break;
        line($"bigbase: load match {world.Match.Map(static m => m.ToString()).Reduce("none")}");
        line($"bigbase: board {Board(world)}; structures in the world {Scene.AllStructures().Count}");
        var (held, _, _) = Split(ReadBase(line));

        LatticeTiming.Recording = true;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < held.Count; i++)
        {
            Scene.Spawn(held[i].Prefab, held[i].Position, held[i].Rotation, held[i].State);
            if (i % SpawnPerFrame == SpawnPerFrame - 1)
                yield return null;
        }

        line($"bigbase: held-back structures spawned in {watch.Elapsed.TotalSeconds:F1} s");
        yield return Settle(world, line, "completion of the held-back plan", 300f);
        line($"bigbase: board {Board(world)}");

        yield return Save(SaveHelper.Save(Station, CancellationToken.None), "head save (older)", line);
        var atHead = Dump(world);
        File.WriteAllText(Path.Combine(outDir, "bigbase-plans-head.txt"), atHead);

        var fresh = FreshCells(held, Held);
        watch.Restart();
        var outcome = Submit(world, PlanOf("New wing", fresh));
        line($"bigbase: new plan of {fresh.Count} ghosts beside the base: {outcome} in {watch.Elapsed.TotalMilliseconds:F0} ms");
        yield return Settle(world, line, "validation of the new plan", 600f);
        yield return Frames(world, line, "steady", 600);

        yield return Save(SaveHelper.AutoSave(Station, CancellationToken.None), "autosave (newer)", line);
        var before = Dump(world);
        File.WriteAllText(Path.Combine(outDir, "bigbase-plans-before.txt"), before);
        var auto = Newest(Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, Station, "autosave"));

        // GameManager.LeaveGame starts with CameraController.Instance, which a headless server lacks
        // (Assets.Scripts/GameManager.cs:1006-1009): a dedicated server has no quit to the menu.
        line("NOT RUN bigbase: quit to the menu, which a dedicated server lacks; the two-player client covers it");

        var starts = LatticeTiming.Starts;
        yield return LoadAndWait(auto, starts, line, "the autosave while the world runs");
        if (!Lattice(line, out world))
            yield break;
        var after = Dump(world);
        File.WriteAllText(Path.Combine(outDir, "bigbase-plans-after.txt"), after);
        line($"{(after == before ? "PASS" : "FAIL")} plans after the autosave reload equal the plans before ({Lines(before)} plan lines)");
        line($"bigbase: Lattice start on reload {LatticeTiming.LastStartMs:F0} ms; load match {world.Match.Map(static m => m.ToString()).Reduce("none")}");

        var head = Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, Station, Station + ".save");
        starts = LatticeTiming.Starts;
        yield return LoadAndWait(head, starts, line, "the older head save while the world runs");
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

    private static IEnumerator LoadAndWait(string path, int starts, Action<string> line, string what)
    {
        line($"bigbase: loading {what}: {path}");
        var watch = Stopwatch.StartNew();
        LoadHelper.LoadGame(path, Station);
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

    // Frames until validation has been idle for 3 frames, recording game and Lattice frame costs.
    private static IEnumerator Settle(LatticeWorld world, Action<string> line, string what, float limit)
    {
        LatticeTiming.FrameMs.Clear();
        var game = new List<double>(4096);
        var start = Time.realtimeSinceStartup;
        var idle = 0;
        while (Time.realtimeSinceStartup - start < limit && idle < 3)
        {
            yield return null;
            game.Add(Time.unscaledDeltaTime * 1000.0);
            idle = world.ValidationModule is { Busy: true } ? 0 : idle + 1;
        }

        line($"bigbase: {what}: {(idle >= 3 ? "settled" : "NOT settled")} after {Time.realtimeSinceStartup - start:F1} s; game frames {Stats(game)}; Lattice frames {Stats(LatticeTiming.FrameMs)}");
    }

    private static IEnumerator Frames(LatticeWorld world, Action<string> line, string what, int count)
    {
        LatticeTiming.FrameMs.Clear();
        var game = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            yield return null;
            game.Add(Time.unscaledDeltaTime * 1000.0);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        world.Frame();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        line($"bigbase: {what}: game frames {Stats(game)}; Lattice frames {Stats(LatticeTiming.FrameMs)}; one Lattice frame now allocates {allocated} bytes");
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

    private static (List<BaseStructure> Held, List<BaseStructure> Others, int Unknown) Split(List<BaseStructure> all)
    {
        var held = new List<BaseStructure>(Held);
        var rest = new List<BaseStructure>(all.Count);
        var unknown = 0;
        foreach (var s in all)
        {
            if (Prefab.Find<Structure>(s.Prefab) == null)
                unknown++;
            else if (held.Count < Held && s.State >= 0)
                held.Add(s);
            else
                rest.Add(s);
        }

        return (held, rest, unknown);
    }

    private static List<BaseStructure> ReadBase(Action<string> line)
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, "-lattice-bigbase");
        var path = at >= 0 && at + 1 < args.Length ? args[at + 1] : "";
        var list = new List<BaseStructure>(40000);
        if (!File.Exists(path))
        {
            line($"FAIL bigbase: no base file '{path}' (-lattice-bigbase <tsv>)");
            return list;
        }

        foreach (var text in File.ReadLines(path))
        {
            if (text.StartsWith("#", StringComparison.Ordinal))
                continue;
            var f = text.Split('\t');
            float F(int i) => float.Parse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture);
            list.Add(new BaseStructure(f[0], new Vector3(F(1), F(2), F(3)), new Quaternion(F(4), F(5), F(6), F(7)), int.Parse(f[8], CultureInfo.InvariantCulture)));
        }

        return list;
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
