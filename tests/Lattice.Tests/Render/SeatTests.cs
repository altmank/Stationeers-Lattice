using Lattice.Api;
using Lattice.Core.EntryPoint;
using Lattice.Core.Executors;
using Lattice.Core.World;
using Lattice.Render;
using Lattice.Tests.EntryPoint;

namespace Lattice.Tests.Render;

/// <summary>
/// Seats: a seat in batch mode (a dedicated server, a batch-mode client) runs styles but not
/// render, with the reason <c>batch-mode</c>; nothing draws and the GPU is never called. A seat with graphics draws.
/// </summary>
public sealed class SeatTests
{
    private static string Fact(Reason why, string name) => why.Facts.TryGetValue(name, out var value) ? value : "";

    private static LatticeWorld Start(bool drawing, WorldSeat seat = WorldSeat.Host)
    {
        var bench = new WorldBench { Drawing = drawing };
        return bench.Start(seat);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void In_batch_mode_render_is_off_with_the_reason_and_styles_still_run(bool host)
    {
        var world = Start(drawing: false, host ? WorldSeat.Host : WorldSeat.Client);

        var render = Assert.Single(world.Modules, static m => m.Name == LatticeModules.Render);
        var off = Assert.IsType<ModuleState.Off>(render.State);
        Assert.Equal(ReasonCodes.ModuleMissing, off.Why.Code);
        Assert.Equal("batch-mode", Fact(off.Why, "seat"));
        Assert.IsType<ModuleState.On>(Assert.Single(world.Modules, static m => m.Name == LatticeModules.Styles).State);
    }

    [Fact]
    public void In_batch_mode_nothing_opens_and_the_gpu_is_never_called()
    {
        var world = Start(drawing: false);
        var rig = new RenderRig();

        var opened = Renderer.Open(world, rig.Ports);

        Assert.False(opened.IsOk);
        Assert.Equal(0, rig.Gpu.Calls);
        Assert.False(LatticeDrawing.Current.IsSome);
    }

    [Fact]
    public void A_seat_with_graphics_opens_drawing()
    {
        var world = Start(drawing: true);
        var rig = new RenderRig();

        Assert.True(Renderer.Open(world, rig.Ports).IsOk);
    }

    [Fact]
    public void With_no_material_at_all_render_does_not_open_and_names_why()
    {
        var world = Start(drawing: true);
        var rig = new RenderRig(bundle: false);

        var why = Renderer.Open(world, rig.Ports).Refusal();

        Assert.Equal("materials", Fact(why, "missing"));
    }
}
