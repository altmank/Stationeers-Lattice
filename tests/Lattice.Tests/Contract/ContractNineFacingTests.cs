using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.9: the catalogue answers the axis a placed face piece faces, documented as added in 0.9.</summary>
public sealed class ContractNineFacingTests
{
    [Fact]
    public void The_contract_is_zero_nine()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(9, ApiVersion.ContractMinor);
    }

    [Fact]
    public void FacingOf_says_it_was_added_in_zero_nine()
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == "M:Lattice.Api.IPieceCatalogue.FacingOf(Lattice.Api.Placement)");

        Assert.Contains("Added in contract 0.9.", entry.Value);
    }
}
