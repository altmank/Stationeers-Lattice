using Lattice.Api;
using Lattice.Render;
using Lattice.Render.Materials;

namespace Lattice.Tests.Render;

/// <summary>
/// Materials by key: every key resolves to two instances, one at
/// its draw order and one at the overlays' order with the depth offset; overrides stack and are copied, never
/// written; a material without instancing is refused at the call; ending the world destroys every copy Lattice made.
/// </summary>
public sealed class MaterialTableTests
{
    private static MaterialTable Loaded(out FakeMaterial bundle)
    {
        var table = new MaterialTable(RenderOrder.Default);
        var loaded = new FakeMaterial("bundle");
        table.Load(_ => Option.Some<Lattice.Render.Gpu.IGpuMaterial>(loaded));
        bundle = loaded;
        return table;
    }

    [Fact]
    public void Every_key_resolves_to_a_placement_and_an_overlay_instance()
    {
        var table = Loaded(out _);

        foreach (var key in MaterialTable.LatticeKeys)
        {
            var entry = table.Entry(key);
            var placement = Assert.IsType<FakeMaterial>(entry.Placement);
            var overlay = Assert.IsType<FakeMaterial>(entry.Overlay);
            Assert.NotSame(placement, overlay);
            Assert.False(placement.Overlay);
            Assert.True(overlay.Overlay);
        }
    }

    [Fact]
    public void Queues_follow_the_draw_order()
    {
        var table = Loaded(out _);
        var order = RenderOrder.Default;

        Assert.Equal(order.Ghosts, ((FakeMaterial)table.Entry(MaterialKeys.Ghost).Placement!).Queue);
        Assert.Equal(order.Overlays, ((FakeMaterial)table.Entry(MaterialKeys.Ghost).Overlay!).Queue);
        Assert.Equal(order.Overlays, ((FakeMaterial)table.Entry(MaterialKeys.Edge).Overlay!).Queue);
        Assert.Equal(order.SeeThrough, ((FakeMaterial)table.Entry(MaterialKeys.SeeThrough).Placement!).Queue);
        Assert.Equal(order.FadedWorld, ((FakeMaterial)table.Entry(MaterialKeys.Faded).Placement!).Queue);
        Assert.Equal(order.Labels, ((FakeMaterial)table.Entry(MaterialKeys.Label).Placement!).Queue);
        Assert.True(order.FadedWorld < order.Ghosts && order.Ghosts < order.Overlays && order.Overlays < order.SeeThrough && order.SeeThrough < order.Labels);
    }

    [Fact]
    public void An_override_draws_from_copies_and_the_consumers_material_is_never_written()
    {
        var table = Loaded(out _);
        var mine = new FakeMaterial("planner-ghost");

        var installed = Assert.IsAssignableFrom<System.IDisposable>(table.Override(MaterialKeys.Ghost, mine).Ok());
        var entry = table.Entry(MaterialKeys.Ghost);

        Assert.Same(mine, ((FakeMaterial)entry.Placement!).CopyOf);
        Assert.Same(mine, ((FakeMaterial)entry.Overlay!).CopyOf);
        Assert.Equal(RenderOrder.Default.Overlays, ((FakeMaterial)entry.Overlay!).Queue);
        Assert.Same(mine, entry.Exposed);

        installed.Dispose();

        Assert.Equal(0, mine.Writes);
        Assert.False(mine.Destroyed);
        Assert.All(mine.Copies, static copy => Assert.True(copy.Destroyed));
    }

    [Fact]
    public void An_override_that_sets_its_own_queue_draws_as_given_and_its_overlay_is_still_a_copy()
    {
        var table = Loaded(out _);
        var mine = new FakeMaterial("planner-ghost", ownQueue: 3005);

        table.Override(MaterialKeys.Ghost, mine);

        Assert.Same(mine, table.Entry(MaterialKeys.Ghost).Placement);
        Assert.Same(mine, ((FakeMaterial)table.Entry(MaterialKeys.Ghost).Overlay!).CopyOf);
    }

    [Fact]
    public void Overrides_stack_and_disposing_reveals_the_one_under_it()
    {
        var table = Loaded(out _);
        var own = table.Entry(MaterialKeys.Edge).Placement;
        var first = new FakeMaterial("first");
        var second = new FakeMaterial("second");

        var a = table.Override(MaterialKeys.Edge, first).Ok();
        var b = table.Override(MaterialKeys.Edge, second).Ok();
        Assert.Same(second, ((FakeMaterial)table.Entry(MaterialKeys.Edge).Placement!).CopyOf);

        b.Dispose();
        Assert.Same(first, ((FakeMaterial)table.Entry(MaterialKeys.Edge).Placement!).CopyOf);

        a.Dispose();
        Assert.Same(own, table.Entry(MaterialKeys.Edge).Placement);
    }

    [Fact]
    public void A_material_without_instancing_is_refused_at_the_call()
    {
        var table = Loaded(out _);
        var revision = table.Revision;

        var refused = table.Override(MaterialKeys.Ghost, new FakeMaterial("plain", instanced: false));

        Assert.Equal(ReasonCodes.InvalidValue, refused.Refusal().Code);
        Assert.Equal(revision, table.Revision);
    }

    [Fact]
    public void A_key_with_no_material_is_not_found()
    {
        var table = new MaterialTable(RenderOrder.Default);

        Assert.Equal(ReasonCodes.NotFound, table.Exposed(MaterialKeys.Wire).Refusal().Code);
    }

    [Fact]
    public void Ending_destroys_every_material_render_made_and_none_of_the_consumers()
    {
        var table = Loaded(out var bundle);
        var mine = new FakeMaterial("planner-faded");
        table.Override(MaterialKeys.Faded, mine);

        table.End();

        Assert.All(bundle.Copies, static copy => Assert.True(copy.Destroyed));
        Assert.All(mine.Copies, static copy => Assert.True(copy.Destroyed));
        Assert.False(bundle.Destroyed);
        Assert.False(mine.Destroyed);
        Assert.False(table.Any);
    }
}
