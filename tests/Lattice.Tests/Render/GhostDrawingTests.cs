using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Lattice.Api;
using Lattice.Render;
using Lattice.Render.Drawing;
using Lattice.Render.Gpu;
using Lattice.Render.Labels;
using Lattice.Tests.Styles;
using UnityEngine;
using Renderer = Lattice.Render.Renderer;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Render;

/// <summary>Engine objects as identities only: no engine call is ever made on them.</summary>
internal static class UnityStub
{
    public static T Make<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}

/// <summary>Maps stub engine objects to Render's fakes and back, by reference.</summary>
internal sealed class FakeObjects : IUnityObjects
{
    private readonly Dictionary<object, IGpuMaterial> _materials = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, IGpuMesh> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IGpuMaterial, Material> _back = new(ReferenceEqualityComparer.Instance);

    public Material Material(FakeMaterial fake)
    {
        var material = UnityStub.Make<Material>();
        _materials[material] = fake;
        _back[fake] = material;
        return material;
    }

    public Mesh Mesh(FakeMesh fake)
    {
        var mesh = UnityStub.Make<Mesh>();
        _meshes[mesh] = fake;
        return mesh;
    }

    IGpuMaterial IUnityObjects.Material(Material material) => _materials[material];

    IGpuMesh IUnityObjects.Mesh(Mesh mesh) => _meshes[mesh];

    public Material Unwrap(IGpuMaterial material)
    {
        if (!_back.TryGetValue(material, out var held))
            _back[material] = held = UnityStub.Make<Material>();
        return held;
    }

    public IGpuMaterial Behind(Material material) => _materials.TryGetValue(material, out var m) ? m : _back.Single(p => ReferenceEquals(p.Value, material)).Key;
}

/// <summary>
/// <c>IGhostDrawing</c> over Render: a key's material is Lattice's own
/// until an override is installed and again after it is disposed; an override without instancing is refused; a piece
/// look draws its piece in place of the prefab's parts and disposing it gives them back; a registered key is held once;
/// text styles reach the labels.
/// </summary>
public sealed class GhostDrawingTests
{
    private static ModId Planner => RenderResults.Ok(ModId.Create("planner"));

    private sealed class Rig
    {
        public StyleBench Bench { get; } = new();
        public RenderRig Render { get; } = new();
        public FakeObjects Objects { get; } = new();
        public Renderer Renderer { get; private set; } = null!;
        public GhostDrawing Drawing { get; private set; } = null!;

        public Rig Open(bool labels = false)
        {
            var ports = labels ? new LabelPorts(new FakeLabelSurface(), NoLabelFacts.Instance, new TextStyle(null!, 20f, new Rgba(255, 255, 255, 255)), Bench.Clock) : null;
            Renderer = RenderResults.Ok(Renderer.Over(Bench.Styles.Plan, Render.Ports, Bench.Scheduler, Bench.Styles.Watch, ports));
            Drawing = new GhostDrawing(Renderer, Objects, static () => Option<Camera>.None);
            RenderRig.Settle(Bench, Renderer);
            return this;
        }

        public void Settle() => RenderRig.Settle(Bench, Renderer);

        public List<string> DrawnMeshes()
        {
            Render.Gpu.NewFrame();
            Renderer.Frame(0f);
            return Render.Gpu.Draws.Select(static d => d.Mesh.ToString()!).ToList();
        }
    }

    [Fact]
    public void A_keys_material_is_lattices_own_then_the_top_override_then_its_own_again()
    {
        var rig = new Rig().Open();
        var own = rig.Objects.Behind(RenderResults.Ok(rig.Drawing.Material(MaterialKeys.Ghost)));
        var planner = rig.Objects.Material(new FakeMaterial("planner-ghost"));

        var installed = RenderResults.Ok(rig.Drawing.Override(MaterialKeys.Ghost, planner));
        Assert.Same(planner, RenderResults.Ok(rig.Drawing.Material(MaterialKeys.Ghost)));

        installed.Dispose();
        Assert.Same(own, rig.Objects.Behind(RenderResults.Ok(rig.Drawing.Material(MaterialKeys.Ghost))));
        Assert.Equal("ghost", ((FakeMaterial)own).CopyOf!.Name);
    }

    [Fact]
    public void A_key_with_no_material_is_refused_not_found()
    {
        var rig = new Rig().Open();

        var why = rig.Drawing.Material(RenderResults.Ok(MaterialKey.Create(Planner, "cursor"))).Refusal();

        Assert.Equal(ReasonCodes.NotFound, why.Code);
    }

    [Fact]
    public void An_override_without_instancing_is_refused_and_lattice_keeps_its_own()
    {
        var rig = new Rig().Open();
        var flat = rig.Objects.Material(new FakeMaterial("flat", instanced: false));

        var why = rig.Drawing.Override(MaterialKeys.Ghost, flat).Refusal();

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.NotSame(flat, RenderResults.Ok(rig.Drawing.Material(MaterialKeys.Ghost)));
    }

    [Fact]
    public void A_piece_look_draws_its_piece_from_the_next_sort_and_disposing_it_gives_the_prefab_parts_back()
    {
        var rig = new Rig();
        rig.Bench.Place(rig.Bench.Draft(Modded, new Cell(10, 10, 10)), rig.Bench.Draft("StructureFrame", new Cell(30, 10, 10)));
        rig.Open();
        var modded = rig.Bench.T.Catalogue.Named(Modded).Key;
        var look = new PieceLook(modded, ValueList.Of(new PieceMesh(rig.Objects.Mesh(new FakeMesh("planner-conduit")), Matrix4x4.identity)));

        var registered = RenderResults.Ok(rig.Drawing.Register(look));
        rig.Settle();
        Assert.Equal(new[] { "cube", "planner-conduit" }, rig.DrawnMeshes().OrderBy(static m => m));

        registered.Dispose();
        rig.Settle();
        Assert.Equal(new[] { "cube", "cube" }, rig.DrawnMeshes());
    }

    [Fact]
    public void A_second_look_for_a_piece_is_refused_until_the_first_is_disposed()
    {
        var rig = new Rig().Open();
        var piece = rig.Bench.T.Catalogue.Named("StructureFrame").Key;
        PieceLook Look() => new(piece, ValueList.Of(new PieceMesh(rig.Objects.Mesh(new FakeMesh("m")), Matrix4x4.identity)));
        var first = RenderResults.Ok(rig.Drawing.Register(Look()));

        Assert.Equal(ReasonCodes.AlreadyRegistered, rig.Drawing.Register(Look()).Refusal().Code);

        first.Dispose();
        Assert.True(rig.Drawing.Register(Look()).IsOk);
    }

    [Fact]
    public void An_icon_and_a_decoration_key_are_held_once_and_freed_by_disposing()
    {
        var rig = new Rig().Open();
        var icon = RenderResults.Ok(IconKey.Create(Planner, "pin"));
        var mark = RenderResults.Ok(DecorationKey.Create(Planner, "arrow"));
        var first = RenderResults.Ok(rig.Drawing.Register(icon, UnityStub.Make<Texture2D>()));
        var decoration = RenderResults.Ok(rig.Drawing.Register(mark, rig.Objects.Mesh(new FakeMesh("arrow")), rig.Objects.Material(new FakeMaterial("arrow"))));

        Assert.Equal(ReasonCodes.AlreadyRegistered, rig.Drawing.Register(icon, UnityStub.Make<Texture2D>()).Refusal().Code);
        Assert.Equal(ReasonCodes.AlreadyRegistered, rig.Drawing.Register(mark, rig.Objects.Mesh(new FakeMesh("arrow")), rig.Objects.Material(new FakeMaterial("arrow"))).Refusal().Code);

        first.Dispose();
        decoration.Dispose();
        Assert.True(rig.Drawing.Register(icon, UnityStub.Make<Texture2D>()).IsOk);
        Assert.True(rig.Drawing.Register(mark, rig.Objects.Mesh(new FakeMesh("arrow")), rig.Objects.Material(new FakeMaterial("arrow"))).IsOk);
    }

    [Fact]
    public void A_text_style_registers_with_the_labels()
    {
        var rig = new Rig().Open(labels: true);
        var key = RenderResults.Ok(TextStyleKey.Create(Planner, "title"));
        var style = new TextStyle(null!, 30f, new Rgba(0, 0, 0, 255));

        RenderResults.Ok(rig.Drawing.Register(key, style));

        Assert.Same(style, rig.Renderer.Labels!.Styles.Of(key));
    }

    [Fact]
    public void Without_labels_a_text_style_is_refused_with_the_reason()
    {
        var rig = new Rig().Open();

        var why = rig.Drawing.Register(RenderResults.Ok(TextStyleKey.Create(Planner, "title")), new TextStyle(null!, 30f, new Rgba(0, 0, 0, 255))).Refusal();

        Assert.Equal(ReasonCodes.ModuleMissing, why.Code);
    }

    [Fact]
    public void The_draw_order_is_lattices_fixed_queues()
    {
        var rig = new Rig().Open();

        Assert.Equal(RenderOrder.Default, rig.Drawing.Order);
    }
}
