using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lattice.Tests.Build;

/// <summary>
/// BannedSymbols.txt fails the build on any LINQ call in a shipped assembly; this catches the
/// <c>using System.Linq</c> that would invite one. Lattice also reads no keys: a key a mod reads fires while the player
/// types in a game text field (the Stationpedia search, chat, a labeller), so a first key read must come with a guard
/// on the game's text-focus state, and this scan fails until it does.
/// </summary>
public sealed class ShippedSourceTests
{
    private static readonly string[] ShippedProjects = ["Lattice.Api", "Lattice.Core", "Lattice.Game"];

    private static readonly Regex KeyRead =
        new(@"\b(KeyCode|KeyManager|UnityEngine\.Input\b|Input\s*\.\s*(GetKey|GetButton|anyKey|inputString)|Keyboard\.current|Event\.current|ImGui\.IsKey)");

    private static readonly Regex LinqUsing =
        new(@"^\s*(global\s+)?using\s+(static\s+)?System\.Linq\b", RegexOptions.Multiline);

    [Fact]
    public void Shipped_sources_have_no_linq_using()
    {
        var offenders = ShippedProjects
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .Where(file => LinqUsing.IsMatch(File.ReadAllText(file)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_source_reads_a_key()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Where(file => KeyRead.IsMatch(File.ReadAllText(file)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("if (Input.GetKeyDown(KeyCode.F)) Toggle();")]
    [InlineData("if (KeyManager.GetButtonDown(bind)) Toggle();")]
    [InlineData("var e = Event.current;")]
    [InlineData("UnityEngine.Input.anyKey")]
    public void The_key_scan_sees_a_key_read(string line) => Assert.Matches(KeyRead, line);

    [Theory]
    [InlineData("case WorkInput.Ghosts ghosts:")]
    [InlineData("var input = support.Input with { World = world };")]
    public void The_key_scan_passes_lattice_input_records(string line) => Assert.DoesNotMatch(KeyRead, line);

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
                return dir.FullName;
        throw new InvalidOperationException("Lattice.sln not found above the test output folder.");
    }
}
