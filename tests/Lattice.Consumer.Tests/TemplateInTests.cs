using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>A stamp tool placing a template into the plan the player has open, against the contract alone.</summary>
public sealed class TemplateInTests
{
    [Fact]
    public void A_stamp_names_the_open_plan_or_none_for_the_default_plan()
    {
        var template = new Template(Make.Ok(TemplateName.Create("Airlock")), ValueList<GhostDraft>.Empty);

        var intoOpen = new Command.PlaceTemplateIn(template, new Cell(10, 10, 10), Turn.None, Option.Some(new PlanId(4)));
        var intoDefault = intoOpen with { Into = Option<PlanId>.None };

        Assert.NotEqual<Command>(intoOpen, intoDefault);
        Assert.True(intoDefault.Into.IsNone);
    }
}
