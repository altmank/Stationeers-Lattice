using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>
/// The contract's patch is readable (<see cref="ApiVersion.ContractPatch"/>, <see cref="ApiVersion.CurrentPatch"/>), is
/// Lattice's own patch number, and every member a patch of this minor added says which.
/// </summary>
public sealed class ContractPatchTests
{
    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void The_contract_patch_is_the_builds_patch_and_the_running_one_reads_the_same()
    {
        var file = typeof(ApiVersion).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
        var parts = file.Split('.');

        Assert.Equal(ApiVersion.ContractMajor.ToString(), parts[0]);
        Assert.Equal(ApiVersion.ContractMinor.ToString(), parts[1]);
        Assert.Equal(ApiVersion.ContractPatch.ToString(), parts[2]);
        Assert.Equal(6, ApiVersion.ContractPatch);
        Assert.Equal(ApiVersion.ContractPatch, ApiVersion.CurrentPatch);
        Assert.Equal(new ApiVersion(0, 22), ApiVersion.Current);
    }

    [Theory]
    [InlineData("P:Lattice.Api.PieceEntry.Placing", "Added in 0.22.1.")]
    [InlineData("T:Lattice.Api.PiecePlacing", "Added in 0.22.1.")]
    [InlineData("M:Lattice.Api.AxisSnap.OnLine(System.Int32)", "Added in 0.22.2.")]
    [InlineData("P:Lattice.Api.PieceSnap.OnFaces", "Added in 0.22.2.")]
    [InlineData("F:Lattice.Api.ApiVersion.ContractPatch", "Added in 0.22.4.")]
    [InlineData("P:Lattice.Api.ApiVersion.CurrentPatch", "Added in 0.22.4.")]
    [InlineData("F:Lattice.Api.ReasonCodes.FlowBlocked", "Added in 0.22.6.")]
    [InlineData("P:Lattice.Api.RunReport.FlowChosen", "Added in 0.22.6.")]
    [InlineData("T:Lattice.Api.FlowChoice", "Added in 0.22.6.")]
    public void Each_member_a_patch_added_names_its_patch(string member, string added) => Assert.Contains(added, Doc(member));

    [Fact]
    public void The_version_doc_says_what_a_minor_and_a_patch_may_do()
    {
        var doc = Doc("T:Lattice.Api.ApiVersion");

        Assert.Contains("a minor may change or remove members", doc);
        Assert.Contains("a patch only adds members or makes behaviour follow the game's own rule", doc);
    }
}
