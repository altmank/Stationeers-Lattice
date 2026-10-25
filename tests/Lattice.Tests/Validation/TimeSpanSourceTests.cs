using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lattice.Tests.Validation;

/// <summary>
/// The game's Mono runs the .NET Framework base class library, where <see cref="TimeSpan.FromMilliseconds"/> and the
/// other <c>From</c> factories round to whole milliseconds: a sub-millisecond literal there is zero in the game though
/// it holds on .NET 8. Shipped code builds such spans from ticks.
/// </summary>
public sealed class TimeSpanSourceTests
{
    private static readonly Regex Fractional =
        new(@"TimeSpan\.FromMilliseconds\(\s*\d*\.\d+|TimeSpan\.From(Seconds|Minutes)\(\s*0*\.\d{0,2}[1-9]", RegexOptions.Compiled);

    [Fact]
    public void Shipped_sources_build_no_sub_millisecond_time_span_from_a_rounding_factory()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, line, i)))
            .Where(x => Fractional.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
                return dir.FullName;
        throw new InvalidOperationException("Lattice.sln not found above the test output folder.");
    }
}
