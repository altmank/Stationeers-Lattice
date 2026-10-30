using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.21: each piece's allowed turns and the turn-not-allowed code, documented as added in 0.21.</summary>
public sealed class ContractAllowedTurnsTests
{
    private const string Added = "Added in contract 0.21.";

    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void The_contract_is_zero_twenty_one()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(21, ApiVersion.ContractMinor);
    }

    [Theory]
    [InlineData("P:Lattice.Api.PieceEntry.AllowedTurns")]
    [InlineData("T:Lattice.Api.TurnSet")]
    [InlineData("F:Lattice.Api.ReasonCodes.TurnNotAllowed")]
    public void Each_added_member_says_when_it_was_added(string member) => Assert.Contains(Added, Doc(member));
}
