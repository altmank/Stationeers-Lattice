using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.12: one highlight over ghosts and built structures, documented as added in 0.12.</summary>
public sealed class ContractTwelveHighlightTests
{
    [Fact]
    public void The_contract_is_zero_twelve()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(12, ApiVersion.ContractMinor);
    }

    [Fact]
    public void Picked_and_built_says_it_was_added_in_zero_twelve()
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == "T:Lattice.Api.HighlightTargets.PickedAndBuilt");

        Assert.Contains("Added in contract 0.12.", string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries)));
    }
}
