using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.12: run and route ends on a built structure's port and a route's via cells, documented as added in 0.12.</summary>
public sealed class ContractTwelveConnectTests
{
    [Theory]
    [InlineData("T:Lattice.Api.PortEnd.OnStructure")]
    [InlineData("P:Lattice.Api.RouteRequest.Via")]
    public void Each_new_member_says_it_was_added_in_zero_twelve(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);

        Assert.Contains("Added in contract 0.12.", entry.Value.Replace("\n", " ").Replace("  ", " "));
    }
}
