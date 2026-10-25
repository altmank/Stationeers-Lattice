using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.10: a template placed into a plan that is already there, documented as added in 0.10.</summary>
public sealed class ContractTenTemplateTests
{
    [Fact]
    public void The_contract_is_zero_ten()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(10, ApiVersion.ContractMinor);
    }

    [Fact]
    public void Placing_a_template_into_a_plan_says_it_was_added_in_zero_ten()
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == "T:Lattice.Api.Command.PlaceTemplateIn");

        Assert.Contains("Added in contract 0.10.", entry.Value);
    }
}
