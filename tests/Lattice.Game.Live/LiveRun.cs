using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Cysharp.Threading.Tasks;
using Lattice.Api;
using Lattice.Game.World;
using TerrainSystem;
using UnityEngine;
using Cell = Lattice.Api.Cell;

namespace Lattice.Game.Live;

/// <summary>
/// The world bridge's live run on the test instance. Started once the world runs (by the live-check plugin, by
/// reflection) with an output folder and a mode: <c>new</c> builds the known scene, checks the removal, broken and move
/// signals, writes the bridge's reads, saves the world as <c>lattice-known</c>, checks placement verdicts and their
/// cost; <c>load</c> (that save loaded) writes the reads again. Both then run the port suites and write
/// <c>done.txt</c>. <c>safety</c> reads build safety's game facts back (<see cref="SafetyRun"/>). <c>looks</c> reads every piece's look (<see cref="LooksRun"/>). <c>cells</c> checks the contract's world-to-cell snap against the game's (<see cref="CellsRun"/>). Every line goes to <c>live-bridge-&lt;mode&gt;.log</c>.
/// </summary>
public static class LiveRun
{
    public const string SaveName = "lattice-known";

    /// <summary>Whether the two-player client's cookie paths point into the test folder (<see cref="ClientCookie"/>).</summary>
    public static bool CookieReady { get; private set; }

    /// <summary>The client cookie's paths after the patch, or why it did not land.</summary>
    public static string CookiePaths { get; private set; } = "not patched";

    /// <summary>
    /// Before any world loads: the saving hooks go in, so a load's save file is seen, and Lattice's timing; for the
    /// two-player client, the cookie patch (<c>-lattice-client-data &lt;folder&gt;</c>).
    /// </summary>
    public static void Prepare()
    {
        Lattice.Game.Saving.SaveHooks.Install();
        LatticeTiming.Install();
        var args = Environment.GetCommandLineArgs();
        var live = Array.IndexOf(args, "-lattice-live");
        if (live < 0 || live + 1 >= args.Length || args[live + 1] != "two-player-client")
            return;
        var at = Array.IndexOf(args, "-lattice-client-data");
        var folder = at >= 0 && at + 1 < args.Length ? args[at + 1] : "";
        var patched = ClientCookie.Install(folder);
        CookieReady = patched.IsOk;
        CookiePaths = patched.Match(static p => p, static e => e);
        UnityEngine.Debug.Log("LIVE client cookie: " + CookiePaths);
    }

    public static void Start(string outDir, string mode)
    {
        var host = new GameObject("Lattice.Game.Live");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<LiveRunner>().Begin(outDir, mode);
    }
}

internal sealed class LiveRunner : MonoBehaviour
{
    private readonly List<(long Id, string What)> _heard = new();
    private string _outDir = ".";
    private string _mode = "new";
    private StreamWriter? _log;
    private string _step = "start";
    private float _deadline;
    private bool _finished;

    public void Begin(string outDir, string mode)
    {
        _outDir = outDir;
        _mode = mode;
        Directory.CreateDirectory(outDir);
        _log = new StreamWriter(Path.Combine(outDir, $"live-bridge-{mode}.log"), append: false) { AutoFlush = true };
        _limit = mode.StartsWith("bigbase", StringComparison.Ordinal) || mode.StartsWith("two-player", StringComparison.Ordinal) ? LongLimitSeconds : TimeLimitSeconds;
        _deadline = Time.realtimeSinceStartup + _limit;
        StartCoroutine(Guarded(Run()));
    }

    // Steps the run with its nested coroutines flattened, so a step that throws ends the run with a FAIL line at once
    // instead of leaving it to the time limit.
    private IEnumerator Guarded(IEnumerator run)
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(run);
        while (stack.Count > 0)
        {
            bool more;
            try
            {
                more = stack.Peek().MoveNext();
            }
            catch (Exception e)
            {
                Line($"FAIL live run threw at step '{_step}': {e}");
                Finish();
                yield break;
            }

            if (!more)
            {
                stack.Pop();
                continue;
            }

            if (stack.Peek().Current is IEnumerator nested)
                stack.Push(nested);
            else
                yield return stack.Peek().Current;
        }
    }

    /// <summary>The whole run's limit in real time: a loaded world starts paused (time scale 0), so nothing here waits on game time.</summary>
    private const float TimeLimitSeconds = 240f;

    /// <summary>The limit of the base-sized and two-player runs, which load and save a large real base more than once.</summary>
    private const float LongLimitSeconds = 1800f;

    private float _limit = TimeLimitSeconds;

    private void Update()
    {
        if (_finished || Time.realtimeSinceStartup < _deadline)
            return;
        Line($"FAIL live run timed out after {_limit} s at step '{_step}'");
        StopAllCoroutines();
        Finish();
    }

    private void Line(string text)
    {
        _log?.WriteLine("LIVE " + text);
        UnityEngine.Debug.Log("LIVE " + text);
    }

    private IEnumerator Run()
    {
        Line($"mode {_mode}; time scale {Time.timeScale}; members missing: {GameMembers.Missing(Core.World.WorldManifest.Module).Count}");
        if (_mode.StartsWith("saving", StringComparison.Ordinal))
        {
            _step = "saving";
            yield return new WaitForSecondsRealtime(5f);
            yield return SavingRun.Run(_mode, Line);
            Finish();
            yield break;
        }

        if (_mode == "cells")
        {
            _step = "cells";
            yield return CellsRun.Run(Line);
            Finish();
            yield break;
        }

        if (_mode == "paints")
        {
            _step = "paints";
            yield return PaintsRun.Run(Line);
            Finish();
            yield break;
        }

        if (_mode == "safety")
        {
            _step = "safety";
            yield return SafetyRun.Run(_outDir, Line);
            Finish();
            yield break;
        }

        if (_mode == "render-timing")
        {
            _step = "render-timing";
            yield return DrawTimingRun.Run(Path.Combine(_outDir, "lattice.shaders"), Line);
            Finish();
            yield break;
        }

        if (_mode == "looks")
        {
            _step = "looks";
            yield return LooksRun.Run(Line);
            Finish();
            yield break;
        }

        if (_mode.StartsWith("two-player", StringComparison.Ordinal))
        {
            _step = _mode;
            yield return _mode == "two-player-host" ? TwoPlayerRun.Host(_outDir, Line) : TwoPlayerRun.Client(_outDir, Line);
            Finish();
            yield break;
        }

        if (_mode.StartsWith("bigbase", StringComparison.Ordinal))
        {
            _step = _mode;
            yield return _mode == "bigbase-build" ? BigBaseRun.Build(Line) : BigBaseRun.Load(_outDir, 10f, Line);
            Finish();
            yield break;
        }

        if (!WorldBridge.Start().TryGet(out var bridge))
        {
            Line("FAIL world bridge did not start");
            Finish();
            yield break;
        }

        Listen(bridge);
        if (_mode == "new")
        {
            _step = "scene";
            foreach (var built in Scene.Build())
                Line("built " + built);
            yield return new WaitForSecondsRealtime(5f);
            _step = "hand removal";
            yield return HandRemoval();
            _step = "broken";
            yield return BrokenByDamage();
            _step = "move";
            yield return Move();
            _step = "setting";
            yield return LogicSetting();
            _step = "dump and save";
            Line($"dump new: {WorldDump.Write(Path.Combine(_outDir, "world-dump-new.txt"))} lines");
            SaveResult? saved = null;
            yield return SaveHelper.NewSave(LiveRun.SaveName, CancellationToken.None).ToCoroutine(r => saved = r);
            Line($"save {LiveRun.SaveName}: {(saved is { } result ? $"success {result.Success} '{result.Message}'" : "no result")}");
            _step = "placement";
            Placement();
            _step = "terrain";
            Terrain();
        }
        else
        {
            _step = "load wait";
            yield return new WaitForSecondsRealtime(5f);
            _step = "dump load";
            Line($"dump load: {WorldDump.Write(Path.Combine(_outDir, "world-dump-load.txt"))} lines");
        }

        bridge.End();
        _step = "suites";
        SuiteRunner.RunAll(Line);
        Finish();
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        Line("done");
        // The run script waits for done.txt from the server; the two-player client and the render modes end on their own file.
        var done = _mode == "two-player-client" ? "done-client.txt" : _mode.StartsWith("render", StringComparison.Ordinal) ? "done-render.txt" : "done.txt";
        File.WriteAllText(Path.Combine(_outDir, done), DateTime.Now.ToString("O"));
        _log?.Dispose();
        _log = null;
    }

    private void Listen(WorldBridge bridge)
    {
        bridge.Changes.Registered += info => _heard.Add((info.Ref.Id.Value, "registered"));
        bridge.Changes.StateChanged += (s, state) => _heard.Add((s.Id.Value, "state " + state.GetType().Name));
        bridge.Changes.Removed += s => _heard.Add((s.Id.Value, "removed"));
        bridge.Changes.Painted += (s, c) => _heard.Add((s.Id.Value, "painted " + c.Index));
        bridge.Changes.Renamed += (s, n) => _heard.Add((s.Id.Value, "renamed " + n.Value));
        bridge.Changes.SettingChanged += (s, v) => _heard.Add((s.Id.Value, "setting " + v.Value));
        bridge.Changes.Moved += moves =>
        {
            foreach (var move in moves)
                _heard.Add((move.From.Id.Value, $"moved {move.From.At.X},{move.From.At.Y},{move.From.At.Z} to {move.To.X},{move.To.Y},{move.To.Z}"));
        };
    }

    private List<string> HeardFor(long id)
    {
        var heard = new List<string>();
        foreach (var (each, what) in _heard)
            if (each == id)
                heard.Add(what);
        return heard;
    }

    private void Expect(string check, long id, string expected)
    {
        var heard = string.Join("; ", HeardFor(id));
        Line($"{(heard == expected ? "PASS" : "FAIL")} {check}: heard [{heard}], expected [{expected}]");
    }

    private static IEnumerator Frames(int count)
    {
        for (var i = 0; i < count; i++)
            yield return null;
    }

    /// <summary>The game's last take-apart step: index below 0, then destroyed (<c>Structure.cs:1578-1591</c>).</summary>
    private IEnumerator HandRemoval()
    {
        var frame = Scene.Spawn(Scene.FrameName, new Vector3(301, Scene.Y, 301), Quaternion.identity, int.MaxValue);
        yield return Frames(3);
        if (frame == null)
        {
            Line("FAIL hand removal: no frame");
            yield break;
        }

        var id = frame.ReferenceId;
        _heard.Clear();
        frame.CurrentBuildStateIndex--;
        while (frame.CurrentBuildStateIndex >= 0)
            frame.CurrentBuildStateIndex--;
        OnServer.Destroy(frame);
        yield return Frames(4);
        Expect("hand removal gives only removed", id, "removed");
    }

    /// <summary>Damage's broken state: the index set below 0 while the structure stands (<c>ThingDamageState.Destroy</c>).</summary>
    private IEnumerator BrokenByDamage()
    {
        var frame = Scene.Spawn(Scene.FrameName, new Vector3(305, Scene.Y, 301), Quaternion.identity, int.MaxValue);
        yield return Frames(3);
        if (frame == null)
        {
            Line("FAIL broken: no frame");
            yield break;
        }

        _heard.Clear();
        frame.UpdateBuildStateAndVisualizer(-1);
        yield return Frames(3);
        Expect("broken by damage gives state broken", frame.ReferenceId, "state Broken");
    }

    /// <summary>A rocket part's path: off the grid, moved, back on (<c>GridController.Detatch</c>, <c>Attach</c>).</summary>
    private IEnumerator Move()
    {
        var frame = Scene.Spawn(Scene.FrameName, new Vector3(309, Scene.Y, 301), Quaternion.identity, int.MaxValue);
        yield return Frames(3);
        if (frame == null)
        {
            Line("FAIL move: no frame");
            yield break;
        }

        _heard.Clear();
        GridController.World.Detatch(frame);
        yield return Frames(2);
        frame.ThingTransformPosition = new Vector3(309, Scene.Y + 4, 301);
        GridController.World.Attach(frame);
        yield return Frames(2);
        Expect("detach and attach give one move", frame.ReferenceId, "moved 3090,810,3010 to 3090,850,3010");
    }

    /// <summary>The labeller's and logic's path: <c>SetLogicValue(LogicType.Setting)</c> (<c>Labeller.InputSetting</c>).</summary>
    private IEnumerator LogicSetting()
    {
        var memory = Scene.Spawn(Scene.MemoryName, new Vector3(313, Scene.Y, 301), Quaternion.identity, int.MaxValue);
        yield return Frames(3);
        if (memory is not Assets.Scripts.Objects.Items.ISetable setable)
        {
            Line("FAIL setting: no logic memory");
            yield break;
        }

        _heard.Clear();
        setable.SetLogicValue(Assets.Scripts.Objects.Motherboards.LogicType.Setting, 7);
        yield return Frames(2);
        Expect("a logic setting gives setting changed", memory.ReferenceId, "setting 7");
    }

    private void Placement()
    {
        var check = new GamePlacementCheck();
        void Verdict(string what, string prefab, Cell at, int turn)
        {
            var piece = new PrefabKey(Animator.StringToHash(prefab));
            var placement = new Placement(piece, at, Turn.Create(turn).TryGet(out var t) ? t : Turn.None);
            var answer = check.Check(placement);
            Line($"placement {what}: " + (answer.TryGetError(out var why)
                ? $"refused {why.Code.Value} '{why.GameMessage.Reduce("")}'"
                : "accepted"));
        }

        Verdict("frame in free air", Scene.FrameName, Scene.FreeAir, 0);
        Verdict("frame in a frame's cell", Scene.FrameName, Scene.Occupied, 0);
        Verdict("frame on a frame", Scene.FrameName, new Cell(90, 870, 90), 0);
        Verdict("area power control with no wall", "StructureAreaPowerControl", new Cell(410, 850, 410), 0);
        Verdict("pipe on the cable's axis, same cell", "StructurePipeStraight", new Cell(210, 810, 110), 0);
        Verdict("pipe turned a quarter, same cell", "StructurePipeStraight", new Cell(210, 810, 110), 1);
        Verdict("matterlink capacitor in free air", "StructureMatterlinkCapacitor", new Cell(415, 810, 415), 0);
        Verdict("a piece with no cursor", "LatticeNoSuchPiece", Scene.FreeAir, 0);

        const int Count = 200;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Count; i++)
            check.Check(new Placement(Scene.Frame, new Cell(410 + 20 * (i % 10), 810, 410 + 20 * (i / 10)), Turn.None));
        watch.Stop();
        Line($"placement cost: {Count} frame checks in {watch.Elapsed.TotalMilliseconds:F2} ms, {watch.Elapsed.TotalMilliseconds * 1000 / Count:F1} us each");
        watch.Restart();
        for (var i = 0; i < Count; i++)
            check.Check(new Placement(new PrefabKey(Animator.StringToHash("StructurePipeStraight")), new Cell(410 + 5 * (i % 10), 810, 410 + 5 * (i / 10)), Turn.None));
        watch.Stop();
        Line($"placement cost: {Count} pipe checks in {watch.Elapsed.TotalMilliseconds:F2} ms, {watch.Elapsed.TotalMilliseconds * 1000 / Count:F1} us each");
    }

    /// <summary>
    /// Does the game refuse ground pieces where no player has loaded the terrain? The checks read voxel
    /// data (<c>SmallGrid.HasVoxelBelow</c>, <c>VoxelTerrain.GetDensityWorldSpace</c>), not colliders.
    /// </summary>
    private void Terrain()
    {
        const float X = 101f;
        const float Z = 101f;
        var surface = float.NaN;
        for (var y = 400f; y > -100f; y -= 0.5f)
            if (VoxelTerrain.GetDensityWorldSpace(new Vector3(X, y, Z)) > 0.5f)
            {
                surface = y;
                break;
            }

        if (float.IsNaN(surface))
        {
            Line("terrain: no voxel below (101, 101)");
            return;
        }

        var collider = Physics.Raycast(new Vector3(X, surface + 20f, Z), Vector3.down, 40f);
        Line($"terrain: surface voxel at y {surface} under ({X}, {Z}); a collider there: {collider}");
        var check = new GamePlacementCheck();
        var smallY = (int)Math.Round((Math.Floor(surface / 0.5) * 0.5 + 0.5) * 10);
        foreach (var (what, prefab, cell) in new[]
                 {
                     ("small flag on the terrain", "StructureFlagSmall", new Cell(1010, smallY, 1010)),
                     ("frame on the terrain", Scene.FrameName, new Cell(1010, (int)(Math.Floor(surface / 2) * 2 + 1) * 10 + 20, 1010)),
                     ("small flag in free air", "StructureFlagSmall", new Cell(1010, smallY + 200, 1010)),
                 })
        {
            var answer = check.Check(new Placement(new PrefabKey(Animator.StringToHash(prefab)), cell, Turn.None));
            Line($"terrain placement {what} at {cell.X},{cell.Y},{cell.Z}: " + (answer.TryGetError(out var why)
                ? $"refused {why.Code.Value} '{why.GameMessage.Reduce("")}'"
                : "accepted"));
        }
    }
}
