using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.EntryPoint;
using Lattice.Core.Slices;
using Lattice.Core.Styles;
using Lattice.Render.Claims;
using Lattice.Render.Drawing;
using Lattice.Render.Gpu;
using Lattice.Render.Labels;
using Lattice.Render.Marks;
using Lattice.Render.Materials;
using UnityEngine;

namespace Lattice.Render;

/// <summary>The render queues Lattice draws at: the game's cursor shader is Transparent+1 (3001).</summary>
internal static class RenderOrder
{
    public static DrawOrder Default { get; } = new(FadedWorld: 2990, Ghosts: 3000, Overlays: 3010, SeeThrough: 3020, Labels: 3030);
}

/// <summary>
/// What Game hands Render: the engine seams, and the piece meshes only Game can read. <paramref name="Facts"/> answers
/// ports, joins, flow and waited-for ghosts for <see cref="Style.Ports"/>, <see cref="Style.OpenEnds"/>,
/// <see cref="Style.FlowArrows"/> and <see cref="Style.WaitsLinks"/>; without it no marks are drawn.
/// </summary>
internal sealed record RenderPorts(IGpu Gpu, ICameraView Camera, IPieceParts Pieces, Func<MaterialKey, Option<IGpuMaterial>> Materials, Action<string> Log, IMarkFacts? Facts = null);

/// <summary>
/// The clip as shader globals (material contract): set once a frame before any camera renders and never reset, so
/// another mod's shader reads them all frame. Writes nothing when the clip did not change.
/// </summary>
internal sealed class ClipGlobals(IGpu gpu)
{
    private ClipShape? _last;
    private bool _set;

    public void Apply(Option<ClipShape> clip)
    {
        var shape = clip.TryGet(out var s) ? s : null;
        if (_set && ReferenceEquals(shape, _last))
            return;
        _set = true;
        _last = shape;
        switch (shape)
        {
            case ClipShape.Plane plane:
                var n = new Vector3(plane.Normal.X, plane.Normal.Y, plane.Normal.Z);
                var w = n.x * plane.Point.X + n.y * plane.Point.Y + n.z * plane.Point.Z;
                gpu.SetGlobal(ShaderProp.ClipPlane, new Vector4(n.x, n.y, n.z, w));
                gpu.SetGlobal(ShaderProp.ClipMode, 1f);
                break;
            case ClipShape.Box box:
                gpu.SetGlobal(ShaderProp.ClipMin, new Vector4(box.Min.X, box.Min.Y, box.Min.Z, 0f));
                gpu.SetGlobal(ShaderProp.ClipMax, new Vector4(box.Max.X, box.Max.Y, box.Max.Z, 0f));
                gpu.SetGlobal(ShaderProp.ClipMode, 2f);
                break;
            default:
                gpu.SetGlobal(ShaderProp.ClipMode, 0f);
                break;
        }

        // The viewer's default beyond look for shaders that read only the globals: drawn (Lattice's default
        // BeyondClipLook); Lattice's own batches set their style's per draw.
        gpu.SetGlobal(ShaderProp.Beyond, new Vector4(0f, 1f, 0f, 0f));
    }
}

/// <summary>
/// Lattice's drawing for one world, over Core's draw plan: materials, the clip globals, instanced submission, claims.
/// Plain C# over the engine seams (<see cref="IGpu"/>, <see cref="ICameraView"/>), so it runs on .NET 8 in tests; the
/// Unity host calls <see cref="BeforeCameras"/> from <c>Application.onBeforeRender</c> and <see cref="Frame"/> from its
/// late update. Main thread.
/// </summary>
internal sealed class Renderer
{
    private readonly IDrawPlan _plan;
    private readonly RenderPorts _ports;
    private readonly ClipGlobals _clip;
    private readonly Submission _submission;
    private readonly Rebuilds _rebuilds;
    private readonly DrawState _state;
    private readonly ClaimTable _claims;
    private readonly PieceLooks _looks;

    private Renderer(IDrawPlan plan, RenderPorts ports, MaterialTable materials, FrameScheduler scheduler, Func<StyleMatch, IMatchWatch> watch, LabelPorts? labels)
    {
        Labels = labels is null ? null : new LabelDraw(plan, ports.Camera, labels);
        _plan = plan;
        Materials = materials;
        _clip = new ClipGlobals(ports.Gpu);
        _claims = new ClaimTable(watch);
        DrawState? held = null;
        _looks = new PieceLooks(ports.Pieces, key => held?.Repiece(key));
        ports = ports with { Pieces = _looks };
        _ports = ports;
        var state = new DrawState(ports);
        held = state;
        _rebuilds = new Rebuilds(plan, ports.Camera, state, _claims);
        _submission = new Submission(plan, ports, materials, state, _rebuilds);
        _state = state;
        scheduler.Schedule(WorkKind.Drawing, _rebuilds, Priority.Normal);
    }

    public MaterialTable Materials { get; }

    /// <summary>Labels and glyph icons; none on a seat without a text surface.</summary>
    public LabelDraw? Labels { get; }

    public DrawOrder Order => RenderOrder.Default;

    public SubmissionCounts Counts => _submission.Counts;

    /// <summary>Batch rebuilds are still under way in the drawing budget.</summary>
    public bool Rebuilding => _rebuilds.Busy;

    /// <summary>
    /// Opens drawing for a world: none, with the reason, when the <c>render</c> module is off (batch mode, styles off)
    /// or no key has a material at all (fail safe: ghosts are not drawn, the rest runs).
    /// </summary>
    public static Result<Renderer, Reason> Open(LatticeWorld world, RenderPorts ports, LabelPorts? labels = null) =>
        Off(world).TryGet(out var why) || world.StyleModule is not { } styles
            ? Result<Renderer, Reason>.Error(why)
            : Over(styles.Plan, ports.Facts is null ? ports with { Facts = new WorldMarkFacts(world.Catalogue, world.Board, world.World, world.Events) } : ports,
                world.Scheduler, styles.Watch, labels);

    /// <summary>Why drawing does not open for a world (the <c>render</c> module is off, or styles are missing); none when it may.</summary>
    public static Option<Reason> Off(LatticeWorld world)
    {
        foreach (var module in world.Modules)
            if (module.Name == LatticeModules.Render && module.State is ModuleState.Off off)
                return Option.Some(off.Why);
        return world.StyleModule is null
            ? Option.Some(Reason.Of(ReasonCodes.ModuleMissing, RenderCodes.Rule, ValueMap.Of("module", "render").With("needs", "styles")))
            : Option<Reason>.None;
    }

    /// <summary>
    /// Drawing over a draw plan, its batch rebuilds a Lattice job on <paramref name="scheduler"/>'s drawing budget and
    /// its claims' matches watched through <paramref name="watch"/>, its labels drawn through <paramref name="labels"/> when given;
    /// refused when no key has a material at all.
    /// </summary>
    internal static Result<Renderer, Reason> Over(IDrawPlan plan, RenderPorts ports, FrameScheduler scheduler, Func<StyleMatch, IMatchWatch> watch, LabelPorts? labels = null)
    {
        var materials = new MaterialTable(RenderOrder.Default);
        materials.Load(ports.Materials);
        if (!materials.Any)
            return Result<Renderer, Reason>.Error(Reason.Of(ReasonCodes.ModuleMissing, RenderCodes.Rule, ValueMap.Of("module", "render").With("missing", "materials")));
        return Result<Renderer, Reason>.Ok(new Renderer(plan, ports, materials, scheduler, watch, labels));
    }

    /// <summary>Once a frame, after every late update and before any camera renders.</summary>
    public void BeforeCameras() => _clip.Apply(_plan.Clip);

    /// <summary>Registers a renderer claim (<c>IGhostDrawing.Claim</c>).</summary>
    public Result<IDisposable, Reason> Claim(RendererClaimDescriptor claim) => _claims.Add(claim);

    /// <summary>
    /// Gives a piece better parts than Game reads from its prefab (<c>IGhostDrawing.Register(PieceLook)</c>): its batches
    /// are drawn from them from the next sort on. Refused (<c>already-registered</c>) when the piece already has a look.
    /// </summary>
    public Result<IDisposable, Reason> Look(PrefabKey piece, IReadOnlyList<RenderPart> parts) => _looks.Register(piece, parts);

    /// <summary>
    /// Lattice's submission for this frame, <paramref name="now"/> in unscaled seconds, then the claims: their loop is the
    /// last statement, so a claim's exception leaves nothing of Lattice's frame undone.
    /// </summary>
    public void Frame(float now)
    {
        if (!_ports.Camera.Ready)
            return;
        _submission.Submit(now);
        Labels?.Frame();
        _claims.Draw();
    }

    /// <summary>Ends the rebuilds, releases every instance buffer and destroys every material Render made.</summary>
    public void End()
    {
        _rebuilds.End();
        _claims.End();
        _looks.End();
        Labels?.End();
        _state.End();
        Materials.End();
        (_ports.Facts as IDisposable)?.Dispose();
    }
}
