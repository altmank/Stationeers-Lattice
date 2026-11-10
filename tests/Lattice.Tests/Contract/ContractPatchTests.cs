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
        Assert.Equal(20, ApiVersion.ContractPatch);
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
    [InlineData("T:Lattice.Api.BuildMode", "Added in 0.22.8.")]
    [InlineData("T:Lattice.Api.BuildModeAccess", "Added in 0.22.8.")]
    [InlineData("T:Lattice.Api.SkippedGhost", "Added in 0.22.8.")]
    [InlineData("T:Lattice.Api.Command.SetBuildMode", "Added in 0.22.8.")]
    [InlineData("T:Lattice.Api.Outcome.Accepted", "Added in 0.22.8.")]
    [InlineData("F:Lattice.Api.ReasonCodes.NotSupported", "Added in 0.22.8.")]
    [InlineData("P:Lattice.Api.Builder.ControlledBy", "Added in 0.22.8.")]
    [InlineData("P:Lattice.Api.Builder.Mode", "Added in 0.22.8.")]
    [InlineData("P:Lattice.Api.Builder.BatchLeft", "Added in 0.22.8.")]
    [InlineData("P:Lattice.Api.Builder.BatchSkipped", "Added in 0.22.8.")]
    [InlineData("M:Lattice.Api.IExecutorHandle.AcceptModes(Lattice.Api.BuildModeAccess,System.Func{Lattice.Api.Actor,Lattice.Api.BuildMode,Lattice.Api.Result{Lattice.Api.Unit,Lattice.Api.Reason}})", "Added in 0.22.8.")]
    [InlineData("M:Lattice.Api.IExecutorHandle.SayMode(Lattice.Api.BuildMode,Lattice.Api.ValueList{Lattice.Api.SkippedGhost})", "Added in 0.22.8.")]
    [InlineData("M:Lattice.Api.IExecutorHandle.SetBatch(Lattice.Api.ValueSet{Lattice.Api.GhostId})", "Added in 0.22.8.")]
    [InlineData("P:Lattice.Api.IExecutorHandle.Batch", "Added in 0.22.8.")]
    [InlineData("T:Lattice.Api.Paint.PlanColour", "Added in 0.22.9.")]
    [InlineData("P:Lattice.Api.EdgeLook.Line.Dash", "Added in 0.22.9.")]
    [InlineData("P:Lattice.Api.EdgeLook.Line.Speed", "Added in 0.22.9.")]
    [InlineData("P:Lattice.Api.EdgeLook.Line.Under", "Added in 0.22.9.")]
    [InlineData("T:Lattice.Api.UnderLine", "Added in 0.22.9.")]
    [InlineData("T:Lattice.Api.Speed", "Added in 0.22.9.")]
    [InlineData("F:Lattice.Api.MaterialKeys.Ants", "Added in 0.22.9.")]
    [InlineData("T:Lattice.Api.IconPattern", "Added in 0.22.10.")]
    [InlineData("T:Lattice.Api.IconPattern.None", "Added in 0.22.10.")]
    [InlineData("T:Lattice.Api.IconPattern.Tiled", "Added in 0.22.10.")]
    [InlineData("P:Lattice.Api.IconPattern.Tiled.Size", "Added in 0.22.10.")]
    [InlineData("P:Lattice.Api.IconPattern.Tiled.SizeOrDefault", "Added in 0.22.10.")]
    [InlineData("P:Lattice.Api.IconPattern.Tiled.DefaultSize", "Added in 0.22.10.")]
    [InlineData("P:Lattice.Api.Style.PatternIcon", "Added in 0.22.10.")]
    [InlineData("F:Lattice.Api.StyleProperty.PatternIcon", "Added in 0.22.10.")]
    [InlineData("M:Lattice.Api.IPlans.BuildableNext(Lattice.Api.Selector)", "Added in 0.22.11.")]
    [InlineData("T:Lattice.Api.IconPattern.Alternating", "Added in 0.22.12.")]
    [InlineData("M:Lattice.Api.IconPattern.Alternating.#ctor(Lattice.Api.IconPattern.Tiled,Lattice.Api.IconPattern.Tiled)", "Added in 0.22.12.")]
    [InlineData("T:Lattice.Api.StyleMatch.Painted", "Added in 0.22.12.")]
    [InlineData("T:Lattice.Api.StyleMatch.Named", "Added in 0.22.12.")]
    public void Each_member_a_patch_added_names_its_patch(string member, string added) => Assert.Contains(added, Doc(member));

    [Fact]
    public void The_version_doc_says_what_a_minor_and_a_patch_may_do()
    {
        var doc = Doc("T:Lattice.Api.ApiVersion");

        Assert.Contains("a minor may change or remove members", doc);
        Assert.Contains("a patch only adds members or makes behaviour follow the game's own rule", doc);
    }
}
