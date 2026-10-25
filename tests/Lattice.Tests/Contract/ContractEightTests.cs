using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.8: a placed piece's ports, and run ends at a ghost's port, each documented as added in 0.8.</summary>
public sealed class ContractEightTests
{
    [Fact]
    public void The_contract_is_zero_eight()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(8, ApiVersion.ContractMinor);
    }

    [Theory]
    [InlineData("M:Lattice.Api.IPieceCatalogue.PortsAt(Lattice.Api.Placement)")]
    [InlineData("T:Lattice.Api.Port")]
    [InlineData("T:Lattice.Api.PortIndex")]
    [InlineData("T:Lattice.Api.PortKind")]
    [InlineData("T:Lattice.Api.PortKinds")]
    [InlineData("T:Lattice.Api.PortRole")]
    [InlineData("T:Lattice.Api.PortEnd")]
    [InlineData("T:Lattice.Api.PortEnd.OnGhost")]
    [InlineData("T:Lattice.Api.PortJoin")]
    [InlineData("P:Lattice.Api.RunRequest.FromPort")]
    [InlineData("P:Lattice.Api.RunRequest.ToPort")]
    [InlineData("P:Lattice.Api.RouteRequest.FromPort")]
    [InlineData("P:Lattice.Api.RouteRequest.ToPort")]
    [InlineData("P:Lattice.Api.RunReport.PortEnds")]
    [InlineData("F:Lattice.Api.ReasonCodes.PortMismatch")]
    public void Every_new_member_says_it_was_added_in_zero_eight(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);

        Assert.Contains("Added in contract 0.8.", entry.Value);
    }

    [Fact]
    public void The_window_family_says_windows_are_the_faces_that_seal_air()
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == "F:Lattice.Api.PieceFamilies.Window");

        Assert.Contains("seal air", entry.Value);
        Assert.DoesNotContain("glass", entry.Value);
    }
}
