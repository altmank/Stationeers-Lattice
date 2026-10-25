using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Tests.EntryPoint;

/// <summary>
/// The mod folder the build stages (artifacts/&lt;Configuration&gt;/Lattice) holds exactly what ships, its
/// About.xml carries the assemblies' version and Lattice's fixed ModID, and the consumer guide names the same id.
/// </summary>
public sealed class PackagingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Configuration() =>
        new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name;

    private static string Staged() => Path.Combine(RepoRoot(), "artifacts", Configuration(), "Lattice");

    [Fact]
    public void The_staged_mod_folder_holds_the_about_file_and_images_lattices_five_assemblies_and_the_shader_bundle_only()
    {
        var root = Staged();
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/')).Order().ToArray();

        Assert.Equal(new[] { "About/About.xml", "About/Preview.png", "About/thumb.png", "Bundle/lattice.shaders", "Lattice.Api.dll", "Lattice.Api.Unity.dll", "Lattice.Api.Unity.xml", "Lattice.Api.xml", "Lattice.Core.dll", "Lattice.Game.dll", "Lattice.Render.dll" }, files);
    }

    [Fact]
    public void About_carries_the_assemblies_version_and_the_fixed_mod_id()
    {
        var about = XDocument.Load(Path.Combine(Staged(), "About", "About.xml")).Root!;
        var informational = typeof(LatticeSystem).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(informational.Split('+')[0], about.Element("Version")!.Value);
        Assert.Equal(LatticeIdentity.ModIdText, about.Element("ModID")!.Value);
        Assert.Equal(LatticeIdentity.ModIdText, LatticeIdentity.Mod.Value);
        Assert.Equal("Lattice", about.Element("Name")!.Value);
        Assert.Empty(about.Elements("DependsOn"));
        Assert.Equal($"{ApiVersion.ContractMajor}.{ApiVersion.ContractMinor}", string.Join(".", about.Element("Version")!.Value.Split('.').Take(2)));
    }

    [Fact]
    public void The_workshop_description_is_under_the_workshop_limit()
    {
        var about = XDocument.Load(Path.Combine(Staged(), "About", "About.xml")).Root!;

        Assert.InRange(about.Element("Description")!.Value.Length, 1, 7999);
    }

    [Fact]
    public void The_consumer_guide_depends_on_the_same_mod_id()
    {
        var guide = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "consumer-guide.md"));

        Assert.Contains($"<DependsOn ModID=\"{LatticeIdentity.ModIdText}\"", guide);
        Assert.Contains("Private=\"false\"", guide);
    }
}
