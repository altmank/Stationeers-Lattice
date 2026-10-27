using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>
/// Contract 0.15: <see cref="Selector.Joined"/> picks a planned network from one of its pieces, documented as added in
/// 0.15.
/// </summary>
public sealed class ContractFifteenTests
{
    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void The_contract_is_zero_fifteen()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(15, ApiVersion.ContractMinor);
    }

    [Fact]
    public void The_joined_selector_says_it_was_added_in_zero_fifteen() =>
        Assert.Contains("Added in contract 0.15.", Doc("T:Lattice.Api.Selector.Joined"));

    [Fact]
    public void The_joined_selector_is_a_value_of_its_cell()
    {
        var at = new Cell(5, 0, 10);

        Assert.Equal(new Selector.Joined(at), new Selector.Joined(at));
        Assert.NotEqual<Selector>(new Selector.Joined(at), new Selector.RunThrough(at));
    }
}
