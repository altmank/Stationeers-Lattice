using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.7: the window family, <c>StyleMatch.Facing</c>, and the catalogue's family tree, each documented as added in 0.7.</summary>
public sealed class ContractSevenTests
{
    [Fact]
    public void The_contract_is_zero_seven()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(7, ApiVersion.ContractMinor);
    }

    [Theory]
    [InlineData("F:Lattice.Api.PieceFamilies.Window")]
    [InlineData("T:Lattice.Api.StyleMatch.Facing")]
    [InlineData("M:Lattice.Api.IPieceCatalogue.FamiliesOf(Lattice.Api.PrefabKey)")]
    [InlineData("M:Lattice.Api.IPieceCatalogue.ParentOf(Lattice.Api.PieceFamily)")]
    public void Every_new_member_says_it_was_added_in_zero_seven(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);

        Assert.Contains("Added in contract 0.7.", entry.Value);
    }

    [Fact]
    public void FamilyOf_says_it_is_the_top_of_the_chain()
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == "M:Lattice.Api.IPieceCatalogue.FamilyOf(Lattice.Api.PrefabKey)");

        Assert.Contains("top", entry.Value);
    }
}
