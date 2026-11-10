using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.EntryPoint;
using Lattice.Core.Styles;
using Lattice.Game.Looks;
using Lattice.Game.World;
using Lattice.Render.Labels;
using Lattice.Render.Unity;
using Unity.Profiling;
using UnityEngine;
using Cell = Lattice.Api.Cell;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Lattice.Game.Live;

/// <summary>
/// Lattice's drawing in the game with graphics, on a client with a desktop session. From the main menu it loads
/// the base-sized save (<c>-lattice-save &lt;file&gt;</c>), waits for Lattice and its drawing, and logs the device, the
/// bundle and every material's shader support. Then by mode:
/// <list type="bullet">
/// <item><c>render</c>: the look grid (every library state, the library and the linked candidate themes side by side) and the
/// overlay proofs on a real part of the base (a Remove overlay behind a Build ghost, a ghost behind a
/// ghost, a label inside its ghost, an outline over a fill; overlays over a hidden and over a faded original), each a
/// screenshot; a clip plane moved every frame with the globals read back at the end of every frame; Render's calls,
/// its own main-thread time and GC per frame; then the save loaded again (two worlds: the bundle loads again, the
/// material count holds).</item>
/// <item><c>render-look</c>: the same scenes, then a camera tour of them on the desktop until <c>stop-render.txt</c>
/// appears in the output folder.</item>
/// <item><c>render-perf</c>: 20,000 ghosts in the base's own shape, lifted above it, at a 30 fps cap; frame, main-thread, GPU
/// span and Lattice GPU time (<see cref="FrameClock"/>) with and without Lattice's drawing (a claim of everything that
/// draws nothing), each pair interleaved; first a control pair that proves both GPU columns follow GPU load.</item>
/// </list>
/// Screenshots go to <c>-lattice-shots &lt;folder&gt;</c>.
/// </summary>
internal static class RenderRun
{
    private const int PerfGhosts = 20000;
    private const int SmallGhosts = 5000;
    private const int Lift = 600;

    // A pair: 12 blocks of 50 recorded frames (300 a side), each after 15 frames for the switch to take hold.
    private const int PairBlocks = 12;
    private const int BlockFrames = 50;
    private const int SwitchFrames = 15;
    private const int CappedFrameRate = 30;

    public static IEnumerator Run(string mode, string outDir, Action<string> line)
    {
        var shots = Arg("-lattice-shots", Path.Combine(outDir, "render-shots"));
        Directory.CreateDirectory(shots);
        var save = Arg("-lattice-save", "");
        line($"render: unity {Application.unityVersion}; device {SystemInfo.graphicsDeviceType} '{SystemInfo.graphicsDeviceName}' ({SystemInfo.graphicsDeviceVendor}, {SystemInfo.graphicsMemorySize} MB); " +
             $"instancing {SystemInfo.supportsInstancing}; compute {SystemInfo.supportsComputeShaders}; screen {Screen.width}x{Screen.height}; save '{save}'");
        if (!File.Exists(save))
        {
            line($"FAIL render: no save file '{save}' (-lattice-save <file>)");
            yield break;
        }

        yield return MainMenuUp(line);
        var starts = LatticeTiming.Starts;
        yield return LoadWorld(save, starts, line, "first world");
        if (!Opened(line, out var world, out var host))
            yield break;
        yield return Settle(world, line, "after the load", 300f);
        Shaders(host, line);
        GlyphCoverage(line);

        var rig = LiveCamera.Make();
        var region = Region.Of(Scene.AllStructures(), line);
        if (mode == "render-perf")
        {
            yield return Perf(world, host, rig, region, shots, line);
            yield break;
        }

        var scenes = new Scenes(world, host, rig, region, line);
        yield return scenes.Build();
        yield return Shoot(rig, scenes, shots, line);
        if (mode == "render-look")
        {
            yield return scenes.Tour(Path.Combine(outDir, "stop-render.txt"));
            scenes.End();
            yield break;
        }

        yield return Clip(world, host, rig, scenes, shots, line);
        yield return RenderCost(host, Option<FrameClock>.None, line);
        scenes.End();
        yield return SecondWorld(save, host, line);
    }

    // The game's load from the menu hides the main menu first (Assets.Scripts.Serialization/LoadHelper.cs, LoadGameTask),
    // so a load started before the menu exists throws inside the game and no world comes.
    private static IEnumerator MainMenuUp(Action<string> line)
    {
        var watch = Stopwatch.StartNew();
        var deadline = Time.realtimeSinceStartup + 300f;
        while ((Assets.Scripts.UI.MainMenu.Instance == null || !Assets.Scripts.UI.MainMenu.Instance.IsVisible || XmlSaveLoad.Instance == null)
               && Time.realtimeSinceStartup < deadline)
            yield return null;
        yield return new WaitForSecondsRealtime(2f);
        line($"render: main menu {(Assets.Scripts.UI.MainMenu.Instance != null ? "up" : "NOT up")} after {watch.Elapsed.TotalSeconds:F1} s");
    }

    private static IEnumerator LoadWorld(string save, int starts, Action<string> line, string what)
    {
        line($"render: loading {what} from {save}");
        var watch = Stopwatch.StartNew();
        LoadHelper.LoadGame(save, Path.GetFileNameWithoutExtension(save));
        var deadline = Time.realtimeSinceStartup + 600f;
        var refusedBy = Time.realtimeSinceStartup + 30f;
        while ((LatticeTiming.Starts == starts || GameManager.GameState != GameState.Running || Object.FindObjectOfType<RenderHost>() == null)
               && Time.realtimeSinceStartup < deadline)
        {
            // A refused load clears the world save the game was handed and leaves the state at None.
            if (Time.realtimeSinceStartup > refusedBy && XmlSaveLoad.Instance.CurrentWorldSave == null && GameManager.GameState == GameState.None)
            {
                line($"FAIL render: the game refused to load {what} (the client log has the game's error)");
                yield break;
            }

            yield return null;
        }
        yield return new WaitForSecondsRealtime(3f);
        line($"render: {what} running after {watch.Elapsed.TotalSeconds:F1} s (Lattice starts {LatticeTiming.Starts}, start {LatticeTiming.LastStartMs:F0} ms, state {GameManager.GameState})");
    }

    private static bool Opened(Action<string> line, out LatticeWorld world, out RenderHost host)
    {
        world = null!;
        host = Object.FindObjectOfType<RenderHost>();
        if (!LatticeSystem.Current.TryGet(out var system) || system is not LatticeWorld found)
        {
            line("FAIL render: Lattice is not running in this world");
            return false;
        }

        world = found;
        if (host == null)
        {
            line("FAIL render: Lattice's drawing did not open (no RenderHost; the client log says why)");
            return false;
        }

        line($"render: world open, {world.Ghosts.Index.Ghosts.Count} ghosts in {world.Ghosts.Index.Plans.Count} plans; drawing open");
        return true;
    }

    private static void Shaders(RenderHost host, Action<string> line)
    {
        var (own, count) = host.Shaders;
        line($"{(own ? "PASS" : "FAIL")} the bundle loaded: {(own ? "Lattice's shaders" : "the stand-in")}, {count} material keys");
        foreach (var key in new[] { MaterialKeys.Ghost, MaterialKeys.Solid, MaterialKeys.Wire, MaterialKeys.Edge, MaterialKeys.SeeThrough, MaterialKeys.Glow, MaterialKeys.Faded })
        {
            var material = host.Drawing.Material(key);
            line(material.TryGet(out var m)
                ? $"{(m.shader.isSupported ? "PASS" : "FAIL")} material {key.Value}: shader {m.shader.name} supported {m.shader.isSupported}, queue {m.renderQueue}, instancing {m.enableInstancing}"
                : $"FAIL material {key.Value}: {material}");
        }
    }

    // Which library icons the game's text font draws: a missing glyph shows Render's plain stand-in.
    private static void GlyphCoverage(Action<string> line)
    {
        var font = GameText.Font();
        if (font == null)
        {
            line("render: glyphs: the game's text font is not found (labels use TextMeshPro's default)");
            return;
        }

        var has = new List<string>(Glyphs.Count);
        var lacks = new List<string>(Glyphs.Count);
        for (var i = 0; i < Glyphs.Count; i++)
        {
            var glyph = Glyphs.GlyphAt(i);
            var entry = $"{Glyphs.KeyAt(i).Value} '{glyph}' U+{(int)glyph:X4}";
            (font.HasCharacter(glyph, searchFallbacks: true) ? has : lacks).Add(entry);
        }

        line($"render: glyphs in the game's font {font.name}: has {has.Count} ({string.Join(", ", has)}); lacks {lacks.Count} ({string.Join(", ", lacks)})");
    }

    // Validation idle for 3 frames and Render's rebuilds done.
    private static IEnumerator Settle(LatticeWorld world, Action<string> line, string what, float limit)
    {
        var start = Time.realtimeSinceStartup;
        var next = start + 20f;
        var idle = 0;
        while (Time.realtimeSinceStartup - start < limit && idle < 3)
        {
            yield return null;
            if (Time.realtimeSinceStartup >= next)
            {
                next += 20f;
                line($"render: {what}: still settling after {Time.realtimeSinceStartup - start:F0} s");
            }

            var host = Object.FindObjectOfType<RenderHost>();
            idle = world.ValidationModule is { Busy: true } || world.StyleModule is { Busy: true } || host != null && host.Renderer.Rebuilding ? 0 : idle + 1;
        }

        line($"render: {what}: {(idle >= 3 ? "settled" : "NOT settled")} after {Time.realtimeSinceStartup - start:F1} s");
    }

    private static IEnumerator Shoot(LiveCamera rig, Scenes scenes, string shots, Action<string> line)
    {
        foreach (var view in scenes.Views)
        {
            rig.Show(view);
            yield return Frames(20);
            yield return new WaitForSecondsRealtime(2f);
            var file = Path.Combine(shots, view.Name + ".png");
            yield return rig.Capture(file);
            line($"render: screenshot {view.Name}: {view.Says} -> {file} ({(File.Exists(file) ? new FileInfo(file).Length : 0)} bytes)");
        }
    }

    // A plane across the look grid, moved every frame for 600 frames; at the end of every frame the clip globals must
    // equal the plane set before that frame's cameras (set once a frame before any camera, never reset).
    private static IEnumerator Clip(LatticeWorld world, RenderHost host, LiveCamera rig, Scenes scenes, string shots, Action<string> line)
    {
        if (world.StyleModule is not { } styles)
        {
            line("FAIL clip: no styles");
            yield break;
        }

        rig.Show(scenes.Views[0]);
        var (min, max) = scenes.GridSpan;
        // Ghosts beyond the plane are hidden while it moves, so the screenshots show the cut.
        var beyond = new StyleLayerDescriptor(
            StyleLayerId.Create(LatticeIdentity.Mod, "live-clip-beyond").Match(static l => l, static r => throw new InvalidOperationException(r.ToString())),
            StyleBand.Highlight, 30, ValueList.From(new[] { new StyleRule(new StyleMatch.Everything(), new Style { BeyondClip = Option.Some<BeyondClipLook>(new BeyondClipLook.Hidden()) }) }));
        if (!styles.Add(beyond).TryGet(out var beyondLayer))
        {
            line("FAIL clip: the beyond-hidden layer was refused");
            yield break;
        }

        if (!Plane(min, 1f).TryGet(out var first) || !styles.Clip(first).TryGet(out var clip))
        {
            beyondLayer.Dispose();
            line("FAIL clip: the plane was refused");
            yield break;
        }

        var plane = Shader.PropertyToID("_LatticeClipPlane");
        var modeId = Shader.PropertyToID("_LatticeClipMode");
        const int frames = 600;
        var equal = 0;
        var lagging = 0;
        var off = 0;
        var previous = float.NaN;
        var end = new WaitForEndOfFrame();
        for (var f = 0; f < frames; f++)
        {
            var x = min + (max - min) * (0.5f + 0.5f * Mathf.Sin(f * 0.02f));
            clip.Move(Plane(x, 1f).Match(static p => (ClipShape)p, static r => throw new InvalidOperationException(r.ToString())));
            if (f is 150 or 300 or 450)
            {
                var file = Path.Combine(shots, $"clip-{f}.png");
                yield return rig.Capture(file);
                line($"render: screenshot clip plane at x {x:F1} (frame {f}) -> {file}");
                continue;
            }

            yield return end;
            var global = Shader.GetGlobalVector(plane);
            // The shaders declare the mode a float; read it as one, as they do.
            var mode = Shader.GetGlobalFloat(modeId);
            if (Mathf.Abs(mode - 1f) > 0.01f)
                off++;
            else if (Mathf.Abs(global.w - x) < 0.01f)
                equal++;
            else if (Mathf.Abs(global.w - previous) < 0.01f)
                lagging++;
            previous = x;
        }

        clip.Dispose();
        beyondLayer.Dispose();
        yield return null;
        yield return end;
        var cleared = Shader.GetGlobalFloat(modeId) == 0f;
        var checkedFrames = frames - 3;
        line($"{(equal == checkedFrames ? "PASS" : lagging + equal == checkedFrames && off == 0 ? "WARN" : "FAIL")} clip moved every frame: globals equal the frame's plane at the end of {equal} of {checkedFrames} frames, one frame late in {lagging}, clip mode off in {off}; after Dispose the mode reads off: {cleared}");
    }

    private static Result<ClipShape.Plane, Reason> Plane(float x, float normalX) =>
        ClipShape.Plane.Create(new WorldPoint(x, 0f, 0f), new WorldVector(normalX, 0f, 0f));

    // Render's own frame, called once more after the host's (steady world, nothing changing): its main-thread time and
    // its allocations; the frame's calls and instances; the player's GC counter over 300 frames.
    private static IEnumerator RenderCost(RenderHost host, Option<FrameClock> clock, Action<string> line)
    {
        yield return Frames(30);
        var times = new List<double>(120);
        long allocated = 0;
        var watch = new Stopwatch();
        for (var i = 0; i < 120; i++)
        {
            yield return null;
            var before = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart();
            host.Renderer.Frame(Time.unscaledTime);
            watch.Stop();
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        var counts = host.Renderer.Counts;
        line($"render: Render's own frame (main thread, steady world): {Stats(times)}; allocated {allocated} B over 120 frames; " +
             $"calls {counts.Calls}, instances {counts.Instances}, groups {counts.Groups}, visible batches {counts.Visible} of {counts.Batches}, skipped layers {counts.SkippedLayers}, unknown pieces {counts.UnknownPieces}");
        var steady = new Side("");
        using (var calls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1))
            yield return Block(clock, steady, 300, calls);
        Describe("steady world", steady, clock, line);
    }

    // One block of frames into a side: the player's whole frame (unscaled delta), and, with a clock, the main thread's
    // part, the GPU's span and the GPU's time in the transparent pass of the same frames, read once they have come back
    // from the GPU. A frame with no span or no transparent pass counts as missing.
    private static IEnumerator Block(Option<FrameClock> clock, Side side, int frames, ProfilerRecorder calls)
    {
        var deltas = new List<double>(frames);
        var ids = new List<int>(frames);
        var hasClock = clock.TryGet(out var c);
        for (var i = 0; i < frames; i++)
        {
            yield return null;
            deltas.Add(Time.unscaledDeltaTime * 1000.0);
            if (hasClock)
                ids.Add(c.Frame);
        }

        var times = new List<FrameTimes>(frames);
        var missing = 0;
        if (hasClock)
        {
            for (var wait = 0; wait < 60 && !c.Settled(ids[ids.Count - 1]); wait++)
                yield return null;
            foreach (var id in ids)
            {
                if (c.Read(id).TryGet(out var t) && t.SpanMs > 0 && !double.IsNaN(t.LatticeMs))
                    times.Add(t);
                else
                    missing++;
            }
        }

        side.Add(deltas, times, missing, calls.Valid ? calls.LastValue : 0);
    }

    // A with/without pair, interleaved in blocks A B B A A B B A ...: a drift that is linear over four blocks hits both
    // sides alike, and every pair of neighbouring blocks holds one of each.
    // A progress line per block, so a stall shows within seconds.
    private static IEnumerator Pair(Option<FrameClock> clock, Side on, Side off, Action enable, Action disable, string progress, Action<string> line)
    {
        using var calls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1);
        for (var b = 0; b < PairBlocks; b++)
        {
            var first = b % 4 is 0 or 3;
            (first ? enable : disable)();
            yield return Frames(SwitchFrames);
            yield return Block(clock, first ? on : off, BlockFrames, calls);
            line($"render-perf: {progress} block {b + 1}/{PairBlocks} ({(first ? on : off).Name}) recorded");
        }
    }

    private static void Describe(string what, Side side, Option<FrameClock> clock, Action<string> line)
    {
        var name = side.Name.Length == 0 ? what : $"{what} {side.Name}";
        var frames = side.Frame.Count;
        string Gpu(List<double> ms) => !clock.IsSome ? "not measured (no frame clock)"
            : side.Missing > 0 ? $"missing on {side.Missing} of {frames} frames"
            : Stats(ms);
        line($"render: {name}: frames {Stats(side.Frame)}; CPU (main thread) {(clock.IsSome ? Stats(side.Cpu) : "not measured")}; GPU span {Gpu(side.Gpu)}; Lattice GPU {Gpu(side.Lattice)}; " +
             $"median of {side.FrameBlocks.Count} block medians: frame {Median(side.FrameBlocks):F3} ms, CPU {Median(side.CpuBlocks):F3} ms, GPU span {Median(side.GpuBlocks):F3} ms, Lattice GPU {Median(side.LatticeBlocks):F3} ms; " +
             $"block medians frame {Span(side.FrameBlocks)} ms, GPU span {Span(side.GpuBlocks)} ms, Lattice GPU {Span(side.LatticeBlocks)} ms; " +
             $"transparent pass submit (render thread) median {Median(side.Submit):F3} ms; draw calls (player counter) {side.DrawCalls}");
        if (clock.IsSome && side.Missing > 0)
            line($"FAIL render: {name}: no GPU time on {side.Missing} of {frames} recorded frames");
    }

    // The cost of the 'on' side over the 'off' side: the median of the neighbouring blocks' differences.
    private static Costs Cost(Side on, Side off)
    {
        static List<double> Differences(List<double> a, List<double> b)
        {
            var count = Math.Min(a.Count, b.Count);
            var d = new List<double>(count);
            for (var i = 0; i < count; i++)
                d.Add(a[i] - b[i]);
            return d;
        }

        var gpu = Differences(on.GpuBlocks, off.GpuBlocks);
        var lattice = Differences(on.LatticeBlocks, off.LatticeBlocks);
        return new Costs(Median(Differences(on.FrameBlocks, off.FrameBlocks)), Median(Differences(on.CpuBlocks, off.CpuBlocks)),
            Median(gpu), Median(lattice), Span(gpu), Span(lattice));
    }

    private readonly record struct Costs(double Frame, double Cpu, double Gpu, double Lattice, string GpuSpread, string LatticeSpread)
    {
        public override string ToString() =>
            $"frame {Frame:+0.000;-0.000} ms, CPU (main thread) {Cpu:+0.000;-0.000} ms, GPU span {Gpu:+0.000;-0.000} ms, Lattice GPU {Lattice:+0.000;-0.000} ms " +
            $"(pair differences GPU span {GpuSpread} ms, Lattice GPU {LatticeSpread} ms)";
    }

    public static double Median(List<double> values)
    {
        if (values.Count == 0)
            return double.NaN;
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }

    private static string Span(List<double> values)
    {
        if (values.Count == 0)
            return "none";
        var sorted = new List<double>(values);
        sorted.Sort();
        return $"{sorted[0]:F3}..{sorted[sorted.Count - 1]:F3}";
    }

    // The same save loaded while the world runs: Lattice ends and starts again, the bundle loads again.
    private static IEnumerator SecondWorld(string save, RenderHost firstHost, Action<string> line)
    {
        // Destroy takes effect at the end of the frame: the scenes' own material copy goes before the count.
        yield return null;
        var before = LatticeMaterials();
        var starts = LatticeTiming.Starts;
        yield return LoadWorld(save, starts, line, "second world");
        var host = Object.FindObjectOfType<RenderHost>();
        if (host == null || host == firstHost)
        {
            line("FAIL two worlds: no new drawing after the second load");
            yield break;
        }

        yield return Frames(60);
        var (own, count) = host.Shaders;
        var after = LatticeMaterials();
        line($"{(own && after == before ? "PASS" : "FAIL")} two worlds: the second world draws with {(own ? "Lattice's shaders" : "the stand-in")} ({count} keys); Lattice materials loaded {before} in the first world, {after} in the second");
    }

    private static int LatticeMaterials()
    {
        var count = 0;
        foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            if (material != null && material.shader != null && material.shader.name.StartsWith("Lattice/", StringComparison.Ordinal))
                count++;
        return count;
    }

    // The base's shape lifted 60 m, in the planned look: 5,000 ghosts, then 20,000, then the 20,000 with a dashed edge
    // still, the same edge moving, and the moving edge over an under-line. Each scene from two views: the whole lifted
    // base, and from inside it facing ghosts. Each view is an interleaved pair (see Pair): frame, main-thread, GPU span
    // and Lattice GPU times with Lattice drawing it and with a claim of everything that draws nothing (Lattice's
    // submission off, all else equal). Before any ghost, a control pair proves the GPU columns follow GPU load.
    // Vsync off and the frame rate capped at 30 for the whole run: every frame has slack, so the main thread does not
    // wait on the GPU inside its own span. Settings, the clock and every claim and layer are restored on every exit,
    // a throw included.
    private static IEnumerator Perf(LatticeWorld world, RenderHost host, LiveCamera rig, Region region, string shots, Action<string> line)
    {
        var drafts = new List<GhostDraft>(PerfGhosts);
        var lifted = new List<Vector3>(PerfGhosts);
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var structure in region.All)
        {
            if (drafts.Count == PerfGhosts)
                break;
            if (!GameReads.Info(structure).TryGet(out var info))
                continue;
            var at = new Cell(info.Ref.At.X, info.Ref.At.Y + Lift, info.Ref.At.Z);
            drafts.Add(Scenes.Draft(new Intent.Build(new Placement(info.Ref.Piece, at, info.Turn), Option<Finish>.None)));
            var p = structure.ThingTransformPosition + Vector3.up * (Lift / 10f);
            lifted.Add(p);
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }

        var small = drafts.GetRange(0, Math.Min(SmallGhosts, drafts.Count));
        var rest = drafts.GetRange(small.Count, drafts.Count - small.Count);
        var vsync = QualitySettings.vSyncCount;
        var rate = Application.targetFrameRate;
        var clock = Option<FrameClock>.None;
        try
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = CappedFrameRate;
            var opened = Result<FrameClock, string>.Error("not opened");
            yield return FrameClock.Open(rig.Camera, r => opened = r);
            clock = opened.ToOption();
            line(opened.Match(
                static _ => $"render-perf: frame clock open: D3D11 timestamp queries on the render thread (frame span; the camera's transparent pass as Lattice GPU); vsync off, {CappedFrameRate} fps cap",
                static why => $"FAIL render-perf: the frame clock did not open: {why}"));
            yield return PerfScenes(world, host, rig, drafts, lifted, small, rest, lo, hi, clock, shots, line);
        }
        finally
        {
            if (clock.TryGet(out var open))
                open.Dispose();
            QualitySettings.vSyncCount = vsync;
            Application.targetFrameRate = rate;
        }
    }

    private static IEnumerator PerfScenes(LatticeWorld world, RenderHost host, LiveCamera rig, List<GhostDraft> drafts, List<Vector3> lifted,
        List<GhostDraft> small, List<GhostDraft> rest, Vector3 lo, Vector3 hi, Option<FrameClock> clock, string shots, Action<string> line)
    {
        var centre = (lo + hi) * 0.5f;
        var size = (hi - lo).magnitude;
        var overview = new View("perf-overview", centre + new Vector3(-0.45f, 0.55f, -0.45f) * size, centre, "the whole lifted base in view");
        yield return Control(clock, host, rig, overview, line);

        yield return PerfPlan(world, "Perf lifted", small, $"bounds of all {drafts.Count}: {lo} .. {hi}", line);
        var inside = Option<View>.None;
        yield return Inside(lifted.GetRange(0, small.Count), host, rig, v => inside = v, line);
        var views = inside.TryGet(out var inView) ? new[] { overview, inView } : new[] { overview };
        yield return PerfScene($"{small.Count} ghosts", "scene 1/5", views, host, rig, clock, shots, line);
        yield return PerfPlan(world, "Perf lifted rest", rest, $"{drafts.Count} in all", line);
        yield return PerfScene($"{drafts.Count} ghosts", "scene 2/5", views, host, rig, clock, shots, line);

        var planned = new Paint.Token(LibraryPalette.Planned);
        var dashed = new EdgeLook.Line(LineStyle.Dashed, Must(Pixels.Create(2f)), planned);
        var moving = dashed with { Speed = Must(Speed.Create(0.5f)) };
        var under = moving with { Under = Option.Some(new UnderLine(Must(Pixels.Create(5f)), new Paint.Fixed(new Rgba(20, 20, 20, 200)))) };
        var edges = new (string Name, EdgeLook Edge)[] { ("dashed still", dashed), ("dashed moving", moving), ("under-line", under) };
        for (var e = 0; e < edges.Length; e++)
        {
            var (name, edge) = edges[e];
            if (world.StyleModule is not { } styles)
            {
                line("FAIL render-perf: no styles module");
                break;
            }

            var id = Must(StyleLayerId.Create(LatticeIdentity.Mod, "live-perf-edge"));
            var added = styles.Add(new StyleLayerDescriptor(id, StyleBand.Highlight, 30, ValueList.From(new[]
            {
                new StyleRule(new StyleMatch.Everything(), new Style { Edge = Option.Some(edge) }),
            })));
            if (!added.TryGet(out var layer))
            {
                line($"FAIL render-perf: the {name} layer was refused: {added}");
                continue;
            }

            try
            {
                yield return Settle(world, line, $"after the {name} edge", 600f);
                yield return PerfScene($"{drafts.Count} ghosts {name}", $"scene {e + 3}/5", views, host, rig, clock, shots, line);
            }
            finally
            {
                layer.Dispose();
            }
        }

        yield return Settle(world, line, "after the edge layers", 600f);
        yield return RenderCost(host, clock, line);
        if (clock.TryGet(out var used))
            line($"render-perf: frame clock: {used.Dropped} frame(s) dropped (queries still in flight), {used.DisjointFrames} disjoint");
    }

    private static IEnumerator PerfPlan(LatticeWorld world, string name, List<GhostDraft> drafts, string note, Action<string> line)
    {
        var watch = Stopwatch.StartNew();
        var outcome = Scenes.Submit(world, Scenes.PlanOf(name, drafts));
        line($"render-perf: plan '{name}' of {drafts.Count} ghosts in the base's shape lifted {Lift / 10} m: {outcome} in {watch.Elapsed.TotalMilliseconds:F0} ms; {note}");
        yield return Settle(world, line, $"after the plan '{name}'", 600f);
    }

    // A view from inside the lifted base: at the ghost nearest the ghosts' median point, 1.5 m up, looking 15 degrees
    // down in the one of eight headings where Lattice draws the most instances. When no heading draws a Lattice batch
    // the view is a FAIL: it would compare nothing.
    private static IEnumerator Inside(List<Vector3> ghosts, RenderHost host, LiveCamera rig, Action<Option<View>> done, Action<string> line)
    {
        if (ghosts.Count == 0)
        {
            line("FAIL render-perf: perf-inside: no ghosts to stand among");
            yield break;
        }

        var xs = new List<float>(ghosts.Count);
        var ys = new List<float>(ghosts.Count);
        var zs = new List<float>(ghosts.Count);
        foreach (var g in ghosts)
        {
            xs.Add(g.x);
            ys.Add(g.y);
            zs.Add(g.z);
        }

        xs.Sort();
        ys.Sort();
        zs.Sort();
        var median = new Vector3(xs[xs.Count / 2], ys[ys.Count / 2], zs[zs.Count / 2]);
        var nearest = ghosts[0];
        foreach (var g in ghosts)
            if ((g - median).sqrMagnitude < (nearest - median).sqrMagnitude)
                nearest = g;

        var from = nearest + Vector3.up * 1.5f;
        var best = Option<View>.None;
        var bestInstances = 0;
        var bestCalls = 0;
        for (var heading = 0; heading < 360; heading += 45)
        {
            var view = new View("perf-inside", from, from + Quaternion.Euler(15f, heading, 0f) * Vector3.forward * 10f,
                $"inside the lifted base at {from}, heading {heading}: overdraw");
            rig.Show(view);
            yield return Frames(10);
            var counts = host.Renderer.Counts;
            if (counts.Calls <= 0 || counts.Instances <= bestInstances)
                continue;
            best = Option.Some(view);
            bestInstances = counts.Instances;
            bestCalls = counts.Calls;
        }

        line(best.TryGet(out var chosen)
            ? $"render-perf: perf-inside: {chosen.Says}, {bestCalls} calls, {bestInstances} instances (median ghost {median}, nearest {nearest})"
            : $"FAIL render-perf: perf-inside: no heading at {from} draws a Lattice batch");
        done(best);
    }

    // No ghosts, a fixed view, Lattice's drawing claimed away: the same frames with and without full-screen layers of
    // the ghost material (one instanced call per 1,023 layers: much GPU work, little CPU work, all in the transparent
    // pass). The layer count rises until the GPU span moves 4 ms in a short probe. The control passes when the GPU span
    // and Lattice GPU medians both rise by 2 ms or more and the main thread's by at most a quarter of the span's rise (or
    // 0.5 ms): then both GPU columns are the GPU's time. Runs under Perf's frame-rate cap.
    private static IEnumerator Control(Option<FrameClock> clock, RenderHost host, LiveCamera rig, View view, Action<string> line)
    {
        if (clock.IsNone)
        {
            line("FAIL render-perf: GPU control: no frame clock");
            yield break;
        }

        if (!host.Drawing.Material(MaterialKeys.Ghost).TryGet(out var ghost))
        {
            line("FAIL render-perf: GPU control: no ghost material");
            yield break;
        }

        var claim = host.Drawing.Claim(new RendererClaimDescriptor(Must(ClaimId.Create(LatticeIdentity.Mod, "live-perf-control")), new StyleMatch.Everything(), ClaimParts.Everything, static _ => { }));
        var overdraw = new Overdraw(ghost, rig.Camera);
        var on = new Side("with the overdraw layers");
        var off = new Side("without");
        var layers = 0;
        try
        {
            rig.Show(view);
            yield return Frames(30);
            overdraw.Aim();
            rig.EachFrame = overdraw.Draw;
            using var calls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1);

            var baseline = new Side("");
            yield return Block(clock, baseline, 40, calls);
            foreach (var tryLayers in new[] { 128, 256, 512, 1024, 2048 })
            {
                layers = tryLayers;
                overdraw.Layers = layers;
                yield return Frames(SwitchFrames);
                var probe = new Side("");
                yield return Block(clock, probe, 40, calls);
                overdraw.Layers = 0;
                line($"render-perf: GPU control probe: {layers} layers GPU span median {Median(probe.Gpu):F3} ms, Lattice GPU {Median(probe.Lattice):F3} ms; " +
                     $"no layers {Median(baseline.Gpu):F3} ms, {Median(baseline.Lattice):F3} ms");
                if (Median(probe.Gpu) - Median(baseline.Gpu) >= 4.0)
                    break;
            }

            yield return Pair(clock, on, off, () => overdraw.Layers = layers, () => overdraw.Layers = 0, "GPU control", line);
        }
        finally
        {
            rig.EachFrame = null;
            overdraw.Dispose();
            if (claim.TryGet(out var held))
                held.Dispose();
        }

        var what = $"GPU control {layers} full-screen layers";
        Describe(what, on, clock, line);
        Describe(what, off, clock, line);
        var cost = Cost(on, off);
        var pass = cost.Gpu >= 2.0 && cost.Lattice >= 2.0 && cost.Cpu <= Math.Max(0.5, 0.25 * cost.Gpu);
        line($"{(pass ? "PASS" : "FAIL")} render-perf: GPU control ({layers} full-screen ghost-material layers, no ghosts, {CappedFrameRate} fps cap): {cost}: " +
             (pass ? "both GPU columns follow GPU load" : "the GPU columns do not follow GPU load: their numbers are not GPU time"));
    }

    private static IEnumerator PerfScene(string scene, string progress, View[] views, RenderHost host, LiveCamera rig, Option<FrameClock> clock, string shots, Action<string> line)
    {
        var claimId = Must(ClaimId.Create(LatticeIdentity.Mod, "live-perf-off"));
        for (var v = 0; v < views.Length; v++)
        {
            var view = views[v];
            var what = $"{scene} {view.Name}";
            rig.Show(view);
            yield return Frames(60);
            var counts = host.Renderer.Counts;
            line($"render-perf: {what}: calls {counts.Calls}, instances {counts.Instances}, groups {counts.Groups}, visible batches {counts.Visible} of {counts.Batches}");
            if (counts.Calls <= 0)
            {
                line($"FAIL render-perf: {what}: the view draws no Lattice batches");
                continue;
            }

            yield return rig.Capture(Path.Combine(shots, what.Replace(' ', '-').Replace(",", "") + ".png"));
            IDisposable? held = null;
            var refused = "";
            var on = new Side("with Lattice drawing");
            var off = new Side("with Lattice's drawing claimed away");
            try
            {
                yield return Pair(clock, on, off,
                    () =>
                    {
                        held?.Dispose();
                        held = null;
                    },
                    () =>
                    {
                        if (held != null)
                            return;
                        var claim = host.Drawing.Claim(new RendererClaimDescriptor(claimId, new StyleMatch.Everything(), ClaimParts.Everything, static _ => { }));
                        if (claim.TryGet(out var got))
                            held = got;
                        else
                            refused = claim.ToString();
                    },
                    $"{progress} view {v + 1}/{views.Length} {view.Name}", line);
            }
            finally
            {
                held?.Dispose();
            }

            if (refused.Length > 0)
                line($"FAIL render-perf: {what}: the claim was refused: {refused}");
            Describe(what, on, clock, line);
            Describe(what, off, clock, line);
            line($"render-perf: {what}: Lattice's drawing costs {Cost(on, off)}; median of {Math.Min(on.FrameBlocks.Count, off.FrameBlocks.Count)} neighbouring block differences");
        }
    }

    private static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static v => v, static r => throw new InvalidOperationException(r.ToString()));

    public static IEnumerator Frames(int count)
    {
        for (var i = 0; i < count; i++)
            yield return null;
    }

    public static string Stats(List<double> ms)
    {
        if (ms.Count == 0)
            return "none";
        var sorted = new List<double>(ms);
        sorted.Sort();
        double At(double p) => sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1))];
        return $"{ms.Count} median {At(0.5):F3} ms p90 {At(0.9):F3} ms p95 {At(0.95):F3} ms max {sorted[sorted.Count - 1]:F3} ms";
    }

    private static string Arg(string name, string fallback)
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
    }
}

/// <summary>A camera view of a scene: where from, what at, what it shows.</summary>
internal sealed class View(string name, Vector3 from, Vector3 at, string says)
{
    public string Name { get; } = name;

    public Vector3 From { get; } = from;

    public Vector3 At { get; } = at;

    public string Says { get; } = says;
}

/// <summary>
/// A real part of the base: the structures around the base's middle (median position), and the top of that part,
/// where the scenes go. Every structure, sorted by id, for the perf plan.
/// </summary>
internal sealed class Region
{
    private Region(List<Structure> all, List<Structure> near, Vector3 middle, float top)
    {
        All = all;
        Near = near;
        Middle = middle;
        Top = top;
    }

    public List<Structure> All { get; }

    public List<Structure> Near { get; }

    public Vector3 Middle { get; }

    public float Top { get; }

    public static Region Of(List<Structure> all, Action<string> line)
    {
        all.Sort(static (a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
        var xs = new List<float>(all.Count);
        var zs = new List<float>(all.Count);
        foreach (var s in all)
        {
            xs.Add(s.ThingTransformPosition.x);
            zs.Add(s.ThingTransformPosition.z);
        }

        xs.Sort();
        zs.Sort();
        var middle = all.Count == 0 ? Vector3.zero : new Vector3(xs[xs.Count / 2], 0f, zs[zs.Count / 2]);
        var near = new List<Structure>(1024);
        var top = float.MinValue;
        foreach (var s in all)
        {
            var p = s.ThingTransformPosition;
            if ((new Vector2(p.x, p.z) - new Vector2(middle.x, middle.z)).sqrMagnitude > 30f * 30f)
                continue;
            near.Add(s);
            top = Math.Max(top, p.y);
        }

        if (near.Count == 0)
            top = 100f;
        middle.y = top;
        line($"render: region: {all.Count} structures in the world, {near.Count} within 30 m of the base's middle {middle}, top at {top:F1} m");
        return new Region(all, near, middle, top);
    }
}

/// <summary>
/// The test-only camera rig: its own camera tagged MainCamera (the game's main cameras untagged and off, so Render's
/// camera port finds this one), a light, names drawn beside the scene's ghosts, and screenshots. The game shows a
/// structure only within its render distance of the player, or of the game's main camera in cinematic mode
/// (Assets.Scripts.Objects/Thing.cs, SetOcclusion; Assets.Scripts.Inventory/InventoryManager.cs, WorldPosition), so
/// the rig keeps cinematic mode on and the game's main camera where it is.
/// </summary>
internal sealed class LiveCamera : MonoBehaviour
{
    private readonly List<(Vector3 At, string Text)> _names = new(128);
    private GUIStyle? _style;
    private float _nextSweep;

    public Camera Camera { get; private set; } = null!;

    public string Title { get; set; } = "";

    /// <summary>Drawing the scenes add each frame, after the game's update (the faded original).</summary>
    public Action? EachFrame { get; set; }

    public static LiveCamera Make()
    {
        var holder = new GameObject("Lattice.Live.Camera");
        DontDestroyOnLoad(holder);
        var rig = holder.AddComponent<LiveCamera>();
        rig.Camera = holder.AddComponent<Camera>();
        rig.Camera.depth = 60f;
        rig.Camera.nearClipPlane = 0.1f;
        rig.Camera.farClipPlane = 3000f;
        rig.Camera.fieldOfView = 60f;
        holder.tag = "MainCamera";
        var light = new GameObject("Lattice.Live.Light").AddComponent<Light>();
        light.transform.SetParent(holder.transform, false);
        light.type = LightType.Directional;
        light.intensity = 0.9f;
        light.shadows = LightShadows.None;
        CameraController.CinematicMode = true;
        rig.Sweep();
        return rig;
    }

    public void Show(View view)
    {
        transform.SetPositionAndRotation(view.From, Quaternion.LookRotation(view.At - view.From, Vector3.up));
        Title = view.Name + ": " + view.Says;
    }

    public void Name(Vector3 at, string text) => _names.Add((at, text));

    public IEnumerator Capture(string file)
    {
        ScreenCapture.CaptureScreenshot(file);
        yield return RenderRun.Frames(3);
    }

    private void LateUpdate()
    {
        if (CameraController.Instance != null && CameraController.Instance.MainCameraTransform != null)
            CameraController.Instance.MainCameraTransform.SetPositionAndRotation(transform.position, transform.rotation);
        if (Time.realtimeSinceStartup >= _nextSweep)
            Sweep();
        EachFrame?.Invoke();
    }

    // The game's cameras tagged MainCamera lose the tag and stop rendering, so this camera is Camera.main; the game's
    // HUD stays hidden (Assets.Scripts.Inventory/InventoryManager.cs, SetUiState), so screenshots show the scene only.
    private void Sweep()
    {
        _nextSweep = Time.realtimeSinceStartup + 1f;
        if (InventoryManager.Instance != null && InventoryManager.ShowUi)
            InventoryManager.SetUiState(false);
        foreach (var other in Camera.allCameras)
        {
            if (other == Camera || !other.CompareTag("MainCamera"))
                continue;
            other.tag = "Untagged";
            other.enabled = false;
        }
    }

    private void OnGUI()
    {
        _style ??= new GUIStyle(GUI.skin.label) { fontSize = 15, normal = { textColor = Color.white } };
        GUI.Label(new Rect(12, 8, Screen.width - 24, 26), Title, _style);
        foreach (var (at, text) in _names)
        {
            var p = Camera.WorldToScreenPoint(at);
            if (p.z <= 0f)
                continue;
            var size = _style.CalcSize(new GUIContent(text));
            GUI.Label(new Rect(p.x - size.x / 2f, Screen.height - p.y, size.x, size.y), text, _style);
        }
    }

    private void OnDestroy()
    {
        CameraController.CinematicMode = false;
        if (InventoryManager.Instance != null && !InventoryManager.ShowUi)
            InventoryManager.SetUiState(true);
    }
}

/// <summary>A built structure hidden from the game's drawing, and drawn faded with Lattice's <c>faded</c> material when asked (a consumer's fade, drawn here by the test).</summary>
internal sealed class Original
{
    private readonly Structure _structure;
    private readonly List<MeshRenderer> _hidden = new(8);
    private readonly bool _batched;
    private readonly IReadOnlyList<PiecePart> _parts;

    public Original(Structure structure, IReadOnlyList<PiecePart> parts)
    {
        _structure = structure;
        _parts = parts;
        _batched = structure.structureRenderMode == StructureRenderMode.Batched;
    }

    /// <summary>
    /// Takes the structure out of the game's drawing: a batched structure leaves its render batch (the game registers
    /// its build state's renderer instance, decompile Assets.Scripts.Objects/Structure.cs:966-968 and 1167-1170), a
    /// standard one has its mesh renderers switched off.
    /// </summary>
    public void Hide()
    {
        if (_batched)
        {
            if (_structure.CurrentBuildState?.RendererInstance is { } instance)
                RenderBatch.Deregister(instance);
            return;
        }

        foreach (var renderer in _structure.GetComponentsInChildren<MeshRenderer>())
            if (renderer.enabled)
            {
                renderer.enabled = false;
                _hidden.Add(renderer);
            }
    }

    public void Show()
    {
        if (_batched)
        {
            if (_structure.CurrentBuildState?.RendererInstance is { } instance)
                RenderBatch.Register(instance);
            return;
        }

        foreach (var renderer in _hidden)
            if (renderer != null)
                renderer.enabled = true;
        _hidden.Clear();
    }

    /// <summary>Draws the structure's meshes as built with the faded material this frame.</summary>
    public void DrawFaded(Material faded, Camera camera)
    {
        var step = _structure.CurrentBuildStateIndex;
        var root = _structure.ThingTransform.localToWorldMatrix;
        foreach (var part in _parts)
            if (part.Shows.At(step))
                for (var sub = 0; sub < part.Mesh.subMeshCount; sub++)
                    Graphics.DrawMesh(part.Mesh, root * part.Local, faded, 0, camera, sub);
    }
}
