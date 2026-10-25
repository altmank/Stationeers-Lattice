using Lattice.Api;
using Lattice.Core.Slices;

namespace Lattice.Tests.Slices;

public sealed class PlanSizeLimitTests
{
    [Fact]
    public void With_no_limit_any_size_is_admitted()
    {
        Assert.True(new PlanSizeLimit().Admit(10_000_000).IsOk);
    }

    [Fact]
    public void Past_the_limit_is_refused_as_too_large_with_both_numbers()
    {
        var limit = new PlanSizeLimit();
        Assert.True(limit.Set(100).IsOk);

        Assert.True(limit.Admit(100).IsOk);
        Assert.True(limit.Admit(101).TryGetError(out var reason));
        Assert.Equal(ReasonCodes.TooLarge, reason.Code);
        Assert.True(reason.Facts.TryGetValue("limit", out var max));
        Assert.True(reason.Facts.TryGetValue("size", out var size));
        Assert.Equal(("100", "101"), (max, size));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_limit_that_is_not_positive_is_refused(int max)
    {
        Assert.True(new PlanSizeLimit().Set(max).TryGetError(out var reason));
        Assert.Equal(ReasonCodes.InvalidValue, reason.Code);
    }

    [Fact]
    public void A_second_limit_is_refused_while_one_is_set()
    {
        var limit = new PlanSizeLimit();
        Assert.True(limit.Set(100).IsOk);

        Assert.True(limit.Set(50).TryGetError(out var reason));
        Assert.Equal(ReasonCodes.AlreadyRegistered, reason.Code);
        Assert.True(limit.Admit(100).IsOk);
    }

    [Fact]
    public void Disposing_removes_the_limit_and_a_stale_dispose_leaves_a_newer_limit_alone()
    {
        var limit = new PlanSizeLimit();
        Assert.True(limit.Set(100).TryGet(out var first));
        first.Dispose();
        Assert.True(limit.Admit(1_000).IsOk);

        Assert.True(limit.Set(10).IsOk);
        first.Dispose();

        Assert.True(limit.Admit(11).IsError);
    }
}
