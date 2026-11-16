using Lattice.Render;

namespace Lattice.Tests.Render;

public sealed class RenderLogTests
{
    [Fact]
    public void Own_shaders_are_one_line_with_the_counts_device_and_Unity_version() =>
        Assert.Equal(
            "Lattice render: drawing with Lattice's shaders (12 materials, 0 unsupported) on Direct3D11, Unity 2022.3.9f1.",
            new RenderSummary(true, 12, 0, "Direct3D11", "2022.3.9f1").Line());

    [Fact]
    public void An_unsupported_shader_is_counted_in_the_line() =>
        Assert.Contains("(12 materials, 1 unsupported)", new RenderSummary(true, 12, 1, "Vulkan", "2022.3.9f1").Line());

    [Fact]
    public void The_stand_in_is_named_in_the_line() =>
        Assert.Contains("drawing with the stand-in (3 materials", new RenderSummary(false, 3, 0, "Direct3D11", "2022.3.9f1").Line());

    [Fact]
    public void The_line_is_a_single_line()
    {
        var line = new RenderSummary(true, 12, 0, "Direct3D11", "2022.3.9f1").Line();
        Assert.DoesNotContain('\n', line);
    }
}
