using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.10: the template form in public, the plan record on every access ask, and where finished plans live.</summary>
public sealed class ContractTenTests
{
    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var text = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member).Value;
        return string.Join(" ", text.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("M:Lattice.Api.IPlans.WriteTemplate(Lattice.Api.Template)")]
    [InlineData("M:Lattice.Api.IPlans.ReadTemplate(System.String)")]
    [InlineData("P:Lattice.Api.AccessRequest.PlanRecord")]
    public void Every_new_member_says_it_was_added_in_zero_ten(string member)
    {
        Assert.Contains("Added in contract 0.10.", Doc(member));
    }

    [Fact]
    public void The_finished_plans_docs_say_they_live_on_the_host()
    {
        Assert.Contains("empty on a client in this version", Doc("P:Lattice.Api.IPlans.Finished"));
        Assert.Contains("module=saving", Doc("M:Lattice.Api.IPlans.AsTemplate(Lattice.Api.PlanId,Lattice.Api.TemplateName)"));
    }
}
