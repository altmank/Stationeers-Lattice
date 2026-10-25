using System.Linq;
using Lattice.Api;
using Lattice.Core.EntryPoint;

namespace Lattice.Tests.EntryPoint;

public sealed class AssemblyCopiesTests
{
    private static readonly ValueMap<string, string> Own = ValueMap.Of("Lattice.Api", @"C:\mods\Lattice\Lattice.Api.dll")
        .With("Lattice.Core", @"C:\mods\Lattice\Lattice.Core.dll")
        .With("Lattice.Game", @"C:\mods\Lattice\Lattice.Game.dll");

    [Fact]
    public void Each_of_lattices_own_copies_is_named_with_its_file()
    {
        var loaded = ValueList.From(new[]
        {
            new LoadedAssembly("Assembly-CSharp", "0.0.0.0", @"C:\game\Assembly-CSharp.dll"),
            new LoadedAssembly("Lattice.Api", "0.0.0.0", @"C:\mods\Lattice\Lattice.Api.dll"),
            new LoadedAssembly("Lattice.Core", "0.0.0.0", @"C:\mods\Lattice\Lattice.Core.dll"),
            new LoadedAssembly("Lattice.Game", "0.0.0.0", @"C:\mods\Lattice\Lattice.Game.dll"),
        });

        var lines = AssemblyCopies.Report(loaded, Own);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.DoesNotContain("second copy", line));
        Assert.Contains(@"C:\mods\Lattice\Lattice.Api.dll", lines[0]);
    }

    [Fact]
    public void A_second_copy_shipped_by_another_mod_is_called_out()
    {
        var loaded = ValueList.From(new[]
        {
            new LoadedAssembly("Lattice.Api", "0.0.0.0", @"C:\mods\Lattice\Lattice.Api.dll"),
            new LoadedAssembly("Lattice.Api", "0.0.0.0", @"C:\mods\Planner\Lattice.Api.dll"),
        });

        var lines = AssemblyCopies.Report(loaded, Own);

        var second = Assert.Single(lines, line => line.Contains("second copy"));
        Assert.Contains(@"C:\mods\Planner\Lattice.Api.dll", second);
        Assert.Contains("Private=\"false\"", second);
        Assert.Equal(2, lines.Count(line => line.Contains("Lattice.Api")));
    }
}
