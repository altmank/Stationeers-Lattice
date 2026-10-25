using System;
using Lattice.Api;
using Lattice.Core.Slices;

namespace Lattice.Tests.Slices;

public sealed class SliceValueTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_budget_that_is_not_positive_is_refused(int ticks)
    {
        Assert.True(SliceBudget.Create(TimeSpan.FromTicks(ticks)).TryGetError(out var reason));
        Assert.Equal(ReasonCodes.InvalidValue, reason.Code);
    }

    [Fact]
    public void A_positive_budget_keeps_its_length()
    {
        Assert.True(SliceBudget.Create(TimeSpan.FromMilliseconds(3)).TryGet(out var budget));
        Assert.Equal(TimeSpan.FromMilliseconds(3), budget.Length);
    }

    [Fact]
    public void Priorities_order_by_rank_and_default_is_normal()
    {
        Assert.True(Priority.High.Rank > Priority.Normal.Rank);
        Assert.True(Priority.Normal.Rank > Priority.Low.Rank);
        Assert.Equal(Priority.Normal, default);
    }
}
