using System;
using System.Linq;
using Lattice.Core.Catalogue;
using Lattice.Core.Consequences;
using Lattice.Core.EntryPoint;
using Lattice.Core.Openings;
using Lattice.Core.Safety;
using Lattice.Core.Selectors;
using Lattice.Core.Steps;

namespace Lattice.Tests.EntryPoint;

public sealed class WarmNamespacesTests
{
    public static TheoryData<Type> ArrivalTypes => new()
    {
        typeof(BuildSafety), typeof(OpeningKeepOuts), typeof(CatalogueAdditions),
        typeof(BillMaker), typeof(GhostSelectors), typeof(Connectivity),
    };

    [Theory]
    [MemberData(nameof(ArrivalTypes))]
    public void The_warm_set_holds_the_namespace_of_each_type_the_arrival_paths_first_steps_run(Type type) =>
        Assert.Contains(type.Namespace, LatticeWorld.WarmNamespaces);

    [Fact]
    public void Every_warm_namespace_holds_a_type_of_Core()
    {
        var spaces = typeof(LatticeWorld).Assembly.GetTypes().Select(t => t.Namespace).ToHashSet();

        var missing = LatticeWorld.WarmNamespaces.Where(n => !spaces.Contains(n)).ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void The_warm_set_lists_no_namespace_twice() =>
        Assert.Equal(LatticeWorld.WarmNamespaces.Length, LatticeWorld.WarmNamespaces.Distinct().Count());
}
