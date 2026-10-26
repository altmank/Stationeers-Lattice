using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.13: the swap edit, documented as added in 0.13, and the ghost edit union open to later cases.</summary>
public sealed class ContractThirteenTests
{
    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void The_contract_is_zero_thirteen()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(13, ApiVersion.ContractMinor);
    }

    [Fact]
    public void The_swap_edit_says_it_was_added_in_zero_thirteen() =>
        Assert.Contains("Added in contract 0.13.", Doc("T:Lattice.Api.GhostEdit.Swap"));

    [Fact]
    public void The_ghost_edit_union_says_a_later_minor_may_add_cases() =>
        Assert.Contains("A later minor may add cases", Doc("T:Lattice.Api.GhostEdit"));
}
