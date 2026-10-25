using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Lattice.Tests.Consequences;

/// <summary>The consequence prediction stands on Lattice alone and on catalogue facts: only System and Lattice namespaces, no base-game prefab name.</summary>
public sealed class ConsequenceSourceTests
{
    // A using directive of a namespace other than System or Lattice.
    private static readonly Regex OtherNamespace = new(@"^\s*using\s+(static\s+)?(\w+\s*=\s*)?(?!System\b|Lattice\b)[\w.]+\s*;", RegexOptions.Multiline);

    // A base-game prefab name: Structure or Item followed by a capitalised word, other than Lattice's own Structure types.
    private static readonly Regex PrefabName = new(@"""(Structure|Item)[A-Z]\w*""");

    [Fact]
    public void Only_system_and_lattice_namespaces_are_used() =>
        Assert.DoesNotContain(Sources(), file => OtherNamespace.IsMatch(File.ReadAllText(file)));

    [Fact]
    public void No_base_game_prefab_name_is_checked() =>
        Assert.DoesNotContain(Sources(), file => PrefabName.IsMatch(File.ReadAllText(file)));

    private static string[] Sources() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Lattice.Core", "Consequences"), "*.cs", SearchOption.AllDirectories);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
                return dir.FullName;
        throw new InvalidOperationException("Lattice.sln not found above the test output folder.");
    }
}
