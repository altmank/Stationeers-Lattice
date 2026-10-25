using Lattice.Api;

namespace Lattice.Tests.Build;

public sealed class ApiRecordTests
{
    [Fact]
    public void Api_record_has_value_equality_in_a_consumer() =>
        Assert.Equal(new ApiVersion(1, 2), new ApiVersion(1, 2));

    [Fact]
    public void Api_record_supports_with_in_a_consumer() =>
        Assert.Equal(new ApiVersion(1, 3), new ApiVersion(1, 2) with { Minor = 3 });
}
