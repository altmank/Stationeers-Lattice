using Lattice.Api;
using Lattice.Core.EntryPoint;

namespace Lattice.Tests.EntryPoint;

public sealed class LoadLineTests
{
    [Fact]
    public void A_normal_load_names_the_version_saving_sync_and_both_libraries() =>
        Assert.Equal(
            "Lattice 0.22.21 loaded: saving hooks in (3 patches), multiplayer sync on, LaunchPadBooster 0.4.1, Harmony 2.3.3.",
            new LoadLine("0.22.21", Option.Some(3), true, "0.4.1", "2.3.3").Text);

    [Fact]
    public void Failed_saving_hooks_read_as_not_in() =>
        Assert.Contains("saving hooks not in,", new LoadLine("0.22.21", Option.None<int>(), true, "0.4.1", "2.3.3").Text);

    [Fact]
    public void Sync_that_is_off_reads_as_off() =>
        Assert.Contains("multiplayer sync off,", new LoadLine("0.22.21", Option.Some(3), false, "0.4.1", "2.3.3").Text);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_version_reads_as_unknown(string missing) =>
        Assert.Equal(
            "Lattice unknown loaded: saving hooks in (3 patches), multiplayer sync on, LaunchPadBooster unknown, Harmony unknown.",
            new LoadLine(missing, Option.Some(3), true, missing, missing).Text);
}
