using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lattice.Tests.Build;

/// <summary>
/// BannedSymbols.txt fails the build on any LINQ call in a shipped assembly; this catches the
/// <c>using System.Linq</c> that would invite one.
/// </summary>
public sealed class ShippedSourceTests
{
    private static readonly string[] ShippedProjects = ["Lattice.Api", "Lattice.Core", "Lattice.Game"];

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
