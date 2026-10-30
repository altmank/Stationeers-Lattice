using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>
/// Contract 0.22, the builders contract: builders describe themselves through their handles, plans carry a colour and
/// a set of assigned builders, completions name their claim holder; each addition documented as added in 0.22, and
/// <see cref="PlanEdit"/> says a later minor may add cases.
/// </summary>
public sealed class ContractTwentyTwoTests
{
    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void The_contract_is_zero_twenty_two()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(22, ApiVersion.ContractMinor);
    }

    [Theory]
    [InlineData("T:Lattice.Api.BuildScope")]
    [InlineData("T:Lattice.Api.BuilderArea")]
    [InlineData("T:Lattice.Api.BuilderWord")]
    [InlineData("T:Lattice.Api.SupplyReport")]
    [InlineData("T:Lattice.Api.AssignmentAnswer")]
    [InlineData("T:Lattice.Api.BuilderState")]
    [InlineData("T:Lattice.Api.Supply")]
    [InlineData("T:Lattice.Api.Builder")]
    [InlineData("T:Lattice.Api.Assignment")]
    [InlineData("T:Lattice.Api.BuilderProgress")]
    [InlineData("P:Lattice.Api.ExecutorInfo.Owner")]
    [InlineData("P:Lattice.Api.ExecutorInfo.Builds")]
    [InlineData("P:Lattice.Api.ExecutorId.Mod")]
    [InlineData("P:Lattice.Api.Plan.Colour")]
    [InlineData("P:Lattice.Api.Plan.BuildBy")]
    [InlineData("P:Lattice.Api.PlanDraft.Colour")]
    [InlineData("T:Lattice.Api.PlanEdit.Recolour")]
    [InlineData("T:Lattice.Api.PlanEdit.Assign")]
    [InlineData("T:Lattice.Api.PlanEdit.Unassign")]
    [InlineData("P:Lattice.Api.Completion.By")]
    [InlineData("E:Lattice.Api.IGhostEvents.BuilderChanged")]
    [InlineData("P:Lattice.Api.IExecutors.All")]
    [InlineData("M:Lattice.Api.IExecutors.Find(Lattice.Api.ExecutorId)")]
    [InlineData("M:Lattice.Api.IExecutors.AssignedTo(Lattice.Api.PlanId)")]
    [InlineData("M:Lattice.Api.IExecutors.PlansOf(Lattice.Api.ExecutorId)")]
    [InlineData("M:Lattice.Api.IExecutors.ProgressOf(Lattice.Api.PlanId,Lattice.Api.ExecutorId)")]
    [InlineData("M:Lattice.Api.IExecutors.ProgressOf(Lattice.Api.PlanId)")]
    [InlineData("M:Lattice.Api.IExecutors.SupplyToward(Lattice.Api.PlanId)")]
    [InlineData("M:Lattice.Api.IExecutors.UnclaimedIn(Lattice.Api.PlanId)")]
    [InlineData("M:Lattice.Api.IExecutorHandle.Locate(Lattice.Api.Option{Lattice.Api.BuilderArea})")]
    [InlineData("M:Lattice.Api.IExecutorHandle.Say(Lattice.Api.BuilderWord)")]
    [InlineData("M:Lattice.Api.IExecutorHandle.Offer(Lattice.Api.SupplyReport)")]
    [InlineData("M:Lattice.Api.IExecutorHandle.Answer(Lattice.Api.PlanId,Lattice.Api.AssignmentAnswer)")]
    public void Each_addition_says_it_was_added_in_zero_twenty_two(string member) =>
        Assert.Contains("Added in contract 0.22.", Doc(member));

    [Fact]
    public void Plan_edit_says_a_later_minor_may_add_cases() =>
        Assert.Contains("A later minor may add cases", Doc("T:Lattice.Api.PlanEdit"));

    [Fact]
    public void The_additions_default_to_what_came_before()
    {
        var info = new ExecutorInfo(default, default);
        var plan = new Plan(default, default, default, new Actor.Player(new ClientId(1)), default, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

        Assert.True(info.Owner.IsNone);
        Assert.IsType<BuildScope.Any>(info.Builds);
        Assert.True(plan.Colour.IsNone);
        Assert.Empty(plan.BuildBy);
        Assert.True(new Completion(Option<StructureRef>.None, default).By.IsNone);
    }
}
