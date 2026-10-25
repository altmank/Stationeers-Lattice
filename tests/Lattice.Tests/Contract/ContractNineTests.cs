using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.9: route toggles, branches, how pieces are chosen, and splitting long straights, each documented as added in 0.9.</summary>
public sealed class ContractNineTests
{
    [Fact]
    public void The_contract_is_zero_nine()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(9, ApiVersion.ContractMinor);
    }

    [Theory]
    [InlineData("T:Lattice.Api.FirstLeg")]
    [InlineData("P:Lattice.Api.RouteToggles.First")]
    [InlineData("P:Lattice.Api.RouteToggles.HugFramesAndWalls")]
    [InlineData("T:Lattice.Api.LayOptions")]
    [InlineData("P:Lattice.Api.RunRequest.Lay")]
    [InlineData("P:Lattice.Api.RouteRequest.Lay")]
    [InlineData("P:Lattice.Api.RouteRequest.Branches")]
    [InlineData("T:Lattice.Api.RouteBranch")]
    [InlineData("P:Lattice.Api.RunReport.LaidWith")]
    [InlineData("T:Lattice.Api.Command.PlanSplit")]
    [InlineData("T:Lattice.Api.SplitTargets")]
    public void Every_new_member_says_it_was_added_in_zero_nine(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);

        Assert.Contains("Added in contract 0.9.", entry.Value);
    }

    [Fact]
    public void A_toggles_value_built_as_in_zero_eight_equals_itself_rebuilt_and_keeps_the_zero_eight_defaults()
    {
        var built = new RouteToggles(true, false, true, false, true);
        var rebuilt = new RouteToggles(true, false, true, false, true);

        Assert.Equal(built, rebuilt);
        Assert.Equal(built.GetHashCode(), rebuilt.GetHashCode());
        Assert.True(built.First.IsNone);
        Assert.False(built.HugFramesAndWalls);
        var (frames, bends, doors, ports, bridging) = built;
        Assert.Equal((true, false, true, false, true), (frames, bends, doors, ports, bridging));
    }

    [Fact]
    public void A_run_and_a_route_built_as_in_zero_eight_lay_with_the_default_options_and_no_branches()
    {
        var run = new RunRequest(default, default, ValueList<Cell>.Empty, default, Option<PlanId>.None, default);
        var route = new RouteRequest(default, default, default, new RouteToggles(false, false, false, false, false), Option<PlanId>.None, default);

        Assert.Equal(LayOptions.Default, run.Lay);
        Assert.Equal(LayOptions.Default, route.Lay);
        Assert.Empty(route.Branches);
        Assert.Equal(new LayOptions(false, false), LayOptions.Default);
    }
}
