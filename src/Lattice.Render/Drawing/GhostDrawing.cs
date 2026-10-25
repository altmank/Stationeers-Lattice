using System;
using Lattice.Api;
using Lattice.Render.Gpu;
using Lattice.Render.Labels;
using Lattice.Render.Materials;
using UnityEngine;

namespace Lattice.Render.Drawing;

/// <summary>
/// The engine objects a consumer hands Lattice's drawing or reads from it, as Render's seams see them. The Unity host
/// wraps the real objects; Lattice never writes a consumer's material (it copies it).
/// </summary>
internal interface IUnityObjects
{
    IGpuMaterial Material(Material material);

    IGpuMesh Mesh(Mesh mesh);

    /// <summary>The engine material behind one of Render's: Lattice's own copy, or the consumer's material as given.</summary>
    Material Unwrap(IGpuMaterial material);
}

/// <summary>A registered decoration mesh and its material (<see cref="Style.Mark"/>).</summary>
internal sealed record Decoration(IGpuMesh Mesh, IGpuMaterial Material);

/// <summary>
/// <see cref="IGhostDrawing"/> over one world's <see cref="Renderer"/>: every member is a plain call into Render's
/// tables (Lattice never checks or guards a consumer beyond the argument checks the contract names). Main thread.
/// </summary>
internal sealed class GhostDrawing : IGhostDrawing
{
    private readonly Renderer _renderer;
    private readonly IUnityObjects _objects;
    private readonly Func<Option<Camera>> _camera;

    public GhostDrawing(Renderer renderer, IUnityObjects objects, Func<Option<Camera>> camera)
    {
        _renderer = renderer;
        _objects = objects;
        _camera = camera;
        Icons = new Registry<IconKey, Texture2D>("icon", static key => key.Value);
        Decorations = new Registry<DecorationKey, Decoration>("decoration", static key => key.Value);
    }

    /// <summary>Registered image icons.</summary>
    public Registry<IconKey, Texture2D> Icons { get; }

    /// <summary>Registered decoration meshes.</summary>
    public Registry<DecorationKey, Decoration> Decorations { get; }

    public DrawOrder Order => _renderer.Order;

    public Option<Camera> Camera => _camera();

    public Result<Material, Reason> Material(MaterialKey key) => _renderer.Materials.Exposed(key).Map(_objects.Unwrap);

    public Result<IDisposable, Reason> Override(MaterialKey key, Material material)
    {
        if (material is null)
            throw new ArgumentNullException(nameof(material));
        return _renderer.Materials.Override(key, _objects.Material(material));
    }

    public Result<IDisposable, Reason> Register(TextStyleKey key, TextStyle style)
    {
        if (_renderer.Labels is not { } labels)
            return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.ModuleMissing, RenderCodes.Rule, ValueMap.Of("module", "render").With("missing", "labels")));
        return labels.Styles.Register(key, style);
    }

    public Result<IDisposable, Reason> Register(IconKey key, Texture2D icon)
    {
        if (icon is null)
            throw new ArgumentNullException(nameof(icon));
        return Icons.Register(key, icon);
    }

    public Result<IDisposable, Reason> Register(DecorationKey key, Mesh mesh, Material material)
    {
        if (mesh is null)
            throw new ArgumentNullException(nameof(mesh));
        if (material is null)
            throw new ArgumentNullException(nameof(material));
        return Decorations.Register(key, new Decoration(_objects.Mesh(mesh), _objects.Material(material)));
    }

    public Result<IDisposable, Reason> Register(PieceLook look)
    {
        if (look is null)
            throw new ArgumentNullException(nameof(look));
        var parts = new RenderPart[look.Meshes.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            var mesh = look.Meshes[i];
            parts[i] = new RenderPart(_objects.Mesh(mesh.Mesh), mesh.Local, PartShows.Every);
        }

        return _renderer.Look(look.Piece, parts);
    }

    public Result<IDisposable, Reason> Claim(RendererClaimDescriptor claim) => _renderer.Claim(claim);

    /// <summary>Drops the icon and decoration registrations (world end; the renderer ends its own).</summary>
    public void End()
    {
        Icons.End();
        Decorations.End();
    }
}
