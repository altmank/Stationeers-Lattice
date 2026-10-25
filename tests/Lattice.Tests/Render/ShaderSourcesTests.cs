using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Lattice.Tests.Render;

/// <summary>
/// The shipped shader bundle matches its sources and the game's Unity: <c>lattice.shaders.sha</c> names
/// the hash of the sources it was built from (the rule in <c>build-shaders.ps1</c>), Unity 2022.3.62f3 and the Windows
/// player target. A shader edited without a rebuild, or a bundle from another editor, fails here.
/// </summary>
public sealed class ShaderSourcesTests
{
    /// <summary>The game's Unity version: only a bundle built with it loads in the game's player.</summary>
    private const string GameUnity = "2022.3.62f3";

    private static string RenderDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Lattice.Render");
    }

    private static string Manifest(string name)
    {
        var lines = File.ReadAllText(Path.Combine(RenderDir(), "Bundle", "lattice.shaders.sha")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = Assert.Single(lines, l => l.StartsWith(name + " ", StringComparison.Ordinal));
        return line.Substring(name.Length + 1);
    }

    private static string SourcesHash()
    {
        var sources = Path.Combine(RenderDir(), "Shaders");
        var files = Directory.GetFiles(sources, "*", SearchOption.AllDirectories)
            .Where(static f => Path.GetExtension(f) is ".shader" or ".cginc" or ".cs")
            .Select(f => (Rel: Path.GetRelativePath(sources, f).Replace('\\', '/'), Full: f))
            .OrderBy(static f => f.Rel, StringComparer.Ordinal);
        var utf8 = new UTF8Encoding(false);
        using var stream = new MemoryStream();
        foreach (var (rel, full) in files)
        {
            var text = File.ReadAllText(full, utf8).Replace("\r\n", "\n");
            var bytes = utf8.GetBytes(rel + "\n" + text + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    [Fact]
    public void The_bundle_was_built_from_the_sources_as_they_are()
    {
        Assert.Equal(SourcesHash(), Manifest("sources"));
    }

    [Fact]
    public void The_bundle_was_built_with_the_games_unity_for_the_windows_player()
    {
        Assert.Equal(GameUnity, Manifest("unity"));
        Assert.Equal("StandaloneWindows64", Manifest("target"));
    }

    [Fact]
    public void The_bundle_ships_beside_its_manifest()
    {
        Assert.True(new FileInfo(Path.Combine(RenderDir(), "Bundle", "lattice.shaders")).Length > 0);
    }
}
