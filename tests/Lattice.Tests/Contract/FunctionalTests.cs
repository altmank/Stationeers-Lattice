using System;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Option and Result.</summary>
public sealed class FunctionalTests
{
    [Fact]
    public void Default_option_is_none() => Assert.True(default(Option<string>).IsNone);

    [Fact]
    public void Option_maps_binds_and_reduces()
    {
        var some = Option.Some(2);

        Assert.Equal(Option.Some(3), some.Map(x => x + 1));
        Assert.Equal(Option<int>.None, some.Bind(_ => Option<int>.None));
        Assert.Equal(5, Option<int>.None.Reduce(5));
        Assert.Equal("2", some.Match(x => x.ToString(), () => "none"));
        Assert.Equal(Option<int>.None, some.Filter(x => x > 2));
    }

    [Fact]
    public void Option_of_null_is_none() => Assert.True(Option<string>.Some(null!).IsNone);

    [Fact]
    public void Options_compare_by_value() => Assert.Equal(Option.Some("a"), Option.Some("a"));

    [Fact]
    public void Result_maps_binds_and_translates_errors()
    {
        var ok = Result<int, string>.Ok(2);
        var error = Result<int, string>.Error("no");

        Assert.Equal(Result<int, string>.Ok(3), ok.Map(x => x + 1));
        Assert.Equal(Result<int, string>.Error("no"), ok.Bind(_ => error));
        Assert.Equal(Result<int, int>.Error(2), error.MapError(e => e.Length));
        Assert.Equal("no", error.Match(_ => "", e => e));
        Assert.True(ok.TryGet(out var value) && value == 2);
        Assert.True(error.TryGetError(out var why) && why == "no");
        Assert.Equal(Option<int>.None, error.ToOption());
    }

    [Fact]
    public void Reading_a_default_result_is_a_bug() =>
        Assert.Throws<InvalidOperationException>(() => default(Result<int, string>).IsOk);
}
