using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.EntryPoint;
using Lattice.Core.Executors;
using Lattice.Core.Logging;
using Lattice.Core.Modules;
using Lattice.Core.Styles;
using Lattice.Core.World;
using Lattice.Render.Gpu;
using Lattice.Tests.Board;
using Lattice.Tests.EntryPoint;
using Lattice.Tests.Render;
using Lattice.Tests.Styles;
using Lattice.Tests.Styles.Library;
using Renderer = Lattice.Render.Renderer;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Logging;

internal enum Level
{
    Info,
    Warning,
    Error,
}

/// <summary>A <see cref="LogSink"/> that keeps each line with the level it came in at, and passes it on to <c>also</c>.</summary>
internal sealed class RecordedLog
{
    public RecordedLog(Action<string>? also = null)
    {
        Sink = new LogSink(Write(Level.Info, also), Write(Level.Warning, also), Write(Level.Error, also));
    }

    public LogSink Sink { get; }

    public List<(Level Level, string Text)> Lines { get; } = new();

    public IReadOnlyList<string> At(Level level) => Lines.Where(line => line.Level == level).Select(line => line.Text).ToList();

    private Action<string> Write(Level level, Action<string>? also) => text =>
    {
        Lines.Add((level, text));
        also?.Invoke(text);
    };
}

/// <summary>
/// What a reader of the BepInEx log sees: each line at the level its cause deserves, with its text unchanged. Lines the
/// game's code writes (the plugin, the watcher, the save hooks) are judged by the source scan at the end.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class LogLevelTests : IDisposable
{
    private readonly WorldBench _bench = new();

    public void Dispose() => _bench.Dispose();

    [Fact]
    public void A_single_action_receives_every_level()
    {
        var seen = new List<string>();
        var sink = LogSink.To(seen.Add);

        sink.Info("a");
        sink.Warning("b");
        sink.Error("c");

        Assert.Equal(new[] { "a", "b", "c" }, seen);
    }

    [Fact]
    public void A_single_action_converts_to_a_sink_that_takes_every_level()
    {
        var seen = new List<string>();
        Action<string> one = seen.Add;
        LogSink sink = one;

        sink.Warning("w");
        sink.Error("e");

        Assert.Equal(new[] { "w", "e" }, seen);
    }

    [Fact]
    public void A_start_that_fails_is_an_error_with_its_text_unchanged()
    {
        _bench.Scan = new ThrowingScan();

        _bench.TryStart();

        var line = Assert.Single(_bench.Recorded.At(Level.Error));
        Assert.StartsWith("Lattice isn't running in this world: starting mirror failed. System.InvalidOperationException: scan failed", line);
    }

    [Fact]
    public void Without_the_catalogue_the_refusal_is_an_error_and_the_module_off_line_a_warning()
    {
        _bench.TryStart(WorldSeat.Host, CatalogueManifest.Module.Uses[0]);

        Assert.Equal("Lattice isn't running in this world: it can't read this game's building pieces.", Assert.Single(_bench.Recorded.At(Level.Error)));
        Assert.Contains(_bench.Recorded.At(Level.Warning), line => line.StartsWith("Lattice: module catalogue is off: ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_module_that_is_off_is_a_warning_with_its_text_unchanged()
    {
        _bench.Start(WorldSeat.Host, WorldManifest.SetCustomColor);

        var line = Assert.Single(_bench.Recorded.At(Level.Warning), static l => l.Contains("module validation is off", StringComparison.Ordinal));
        Assert.StartsWith("Lattice: module validation is off: ", line);
        Assert.Empty(_bench.Recorded.At(Level.Error));
    }

    [Fact]
    public void A_module_that_throws_is_an_error_with_its_text_unchanged()
    {
        var world = _bench.Start();

        world.Faults.Run(LatticeModules.Validation, () => throw new InvalidOperationException("validation bug"));

        var line = Assert.Single(_bench.Recorded.At(Level.Error));
        Assert.StartsWith("Lattice: validation failed and goes on; later failures are counted. System.InvalidOperationException: validation bug", line);
    }

    [Fact]
    public void A_clean_start_writes_nothing_above_info()
    {
        _bench.Start();

        Assert.Empty(_bench.Recorded.At(Level.Warning));
        Assert.Empty(_bench.Recorded.At(Level.Error));
    }

    [Fact]
    public void An_icon_that_is_not_registered_is_a_warning()
    {
        var rig = Drawn(Patterned(Tiled()));

        var line = Assert.Single(rig.Recorded.At(Level.Warning));
        Assert.StartsWith("Lattice render: no icon registered as ", line);
        Assert.EndsWith("spray-can; its icon pattern is not drawn.", line);
    }

    [Fact]
    public void A_fill_material_without_the_icon_keyword_is_a_warning()
    {
        var planner = new FakeMaterial("planner-ghost");

        var rig = Drawn(Patterned(Tiled()), before: r => RenderResults.Ok(r.Materials.Override(MaterialKeys.Ghost, planner)), icon: new FakeTexture("spray"));

        var line = Assert.Single(rig.Recorded.At(Level.Warning));
        Assert.EndsWith(" has no LATTICE_ICON_PATTERN keyword; its icon patterns are not drawn.", line);
    }

    [Fact]
    public void A_piece_with_no_meshes_is_a_warning()
    {
        var rig = Drawn(new Style(), noMeshes: true);

        var line = Assert.Single(rig.Recorded.At(Level.Warning));
        Assert.StartsWith("Lattice render: no meshes for piece ", line);
        Assert.EndsWith("; its ghosts are not drawn.", line);
    }

    [Theory]
    [InlineData("Lattice.Game/EntryPoint/WorldWatcher.cs", "_log.Info($\"Lattice: started for this world (")]
    [InlineData("Lattice.Game/EntryPoint/WorldWatcher.cs", "_log.Info(\"Lattice: ended for this world.\");")]
    [InlineData("Lattice.Game/EntryPoint/WorldWatcher.cs", "_log.Error($\"Lattice isn't running in this world: it failed while starting. {error}\");")]
    [InlineData("Lattice.Game/EntryPoint/LatticePlugin.cs", "var log = new LogSink(Logger.LogInfo, Logger.LogWarning, Logger.LogError);")]
    [InlineData("Lattice.Game/EntryPoint/LatticePlugin.cs", "PluginLog.Sink = log;")]
    public void The_game_side_writes_its_lines_at_the_level_the_log_reader_expects(string file, string line) =>
        Assert.Contains(line, File.ReadAllText(Path.Combine(SourceRoot(), file)));

    [Theory]
    [InlineData("Lattice.Game/Saving/SaveHooks.cs")]
    [InlineData("Lattice.Game/World/WorldPatches.cs")]
    public void A_failing_patch_is_an_error_through_the_plugin_source_not_unitys_log(string file)
    {
        var text = File.ReadAllText(Path.Combine(SourceRoot(), file));

        Assert.DoesNotContain("Debug.Log", text, StringComparison.Ordinal);
        Assert.Contains("PluginLog.Sink.Error(", text, StringComparison.Ordinal);
    }

    private static readonly IconKey Spray = TestKitIdsOf(IconKey.Create(Mod, "spray-can"));

    private static T TestKitIdsOf<T>(Result<T, Reason> result) where T : notnull => Lattice.TestKit.TestKitIds.Must(result);

    private static IconPattern.Tiled Tiled() => new(Spray, new Paint.FinishColour(LibraryPalette.Planned));

    private static Style Patterned(IconPattern pattern) => new() { PatternIcon = Option.Some(pattern) };

    /// <summary>A ghost asking for finish paint 2, drawn once under one layer of <paramref name="style"/>; the rig holds what was logged.</summary>
    private static RenderRig Drawn(Style style, Action<Renderer>? before = null, FakeTexture? icon = null, bool noMeshes = false)
    {
        var bench = new StyleBench(true, (Lattice.Core.Styles.Library.StarterLibrary?)null, (IPaintColours?)null);
        var rig = new RenderRig();
        if (noMeshes)
            rig.Pieces.Fallback = null;
        bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), style)));
        var placement = new Placement(bench.T.Catalogue.Named("StructureFrame").Key, new Cell(10, 10, 10), default);
        bench.Place(TestBoard.Draft(new Intent.Build(placement, Option.Some(LibraryResolveTests.Painted(2)))));
        var renderer = rig.Over(bench);
        if (icon is not null)
            RenderResults.Ok(renderer.Materials.Icons.Register(Spray, icon));
        before?.Invoke(renderer);
        for (var frame = 0; frame < 2; frame++)
        {
            RenderRig.Settle(bench, renderer);
            rig.Gpu.NewFrame();
            renderer.Frame(0.1f);
        }

        return rig;
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
                return Path.Combine(dir.FullName, "src");
        throw new InvalidOperationException("Lattice.sln not found above the test output folder.");
    }
}
