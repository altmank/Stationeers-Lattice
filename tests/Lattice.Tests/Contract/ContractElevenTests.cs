using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.11: a see-through look's own opacity, documented as added in 0.11.</summary>
public sealed class ContractElevenTests
{
    [Fact]
    public void A_see_through_looks_own_opacity_says_it_was_added_in_zero_eleven()
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == "P:Lattice.Api.SeeThroughLook.Through.Opacity");

        Assert.Contains("Added in contract 0.11.", entry.Value);
    }
}
