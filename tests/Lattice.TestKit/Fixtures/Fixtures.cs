using System;
using System.IO;
using Lattice.Api;
using Lattice.TestKit.Catalogue;

namespace Lattice.TestKit.Fixtures;

/// <summary>
/// Where generated fixtures live: <c>tests/fixtures/generated/</c> in the repo (git-ignored), filled by
/// <c>tests/fixtures/make-fixtures.ps1</c>. Saves there are verbatim copies, never written.
/// </summary>
internal static class Fixtures
{
    public const string CatalogueFileName = "catalogue.lcat";

    public static string Generated { get; } = Path.Combine(RepoRoot(), "tests", "fixtures", "generated");

    /// <summary>The path of a generated save, if the fixtures script has copied it.</summary>
    public static Option<string> Save(string fileName)
    {
        var path = Path.Combine(Generated, fileName);
        return File.Exists(path) ? Option.Some(path) : Option<string>.None;
    }

    /// <summary>The generated catalogue data file if there is one, else the hand-written catalogue.</summary>
    public static FakeCatalogue Catalogue()
    {
        var path = Path.Combine(Generated, CatalogueFileName);
        if (!File.Exists(path))
            return FakeCatalogue.Hand();
        using var reader = new StreamReader(path);
        return TestKitIds.MustRead(CatalogueFile.Read(reader, path));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
                return dir.FullName;
        throw new InvalidOperationException($"No Lattice.sln above {AppContext.BaseDirectory}.");
    }
}
