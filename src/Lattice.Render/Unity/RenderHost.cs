using System;
using Lattice.Api;
using Lattice.Core.EntryPoint;
using Lattice.Core.Slices;
using Lattice.Core.Styles;
using Lattice.Render.Drawing;
using Lattice.Render.Gpu;
using Lattice.Render.Labels;
using TMPro;
using UnityEngine;

namespace Lattice.Render.Unity;

/// <summary>
/// Lattice's drawing in the game for one world: the Unity side of <see cref="Renderer"/>. It sets the clip globals once
/// a frame from <c>Application.onBeforeRender</c> (after every late update, before any camera), and in
/// its late update hands the styles the camera for the label choice and submits the frame. Ended with the world:
/// every buffer, material copy, text object and the bundle go with it. Main thread.
/// </summary>
internal sealed class RenderHost : MonoBehaviour
{
    private Renderer _renderer = null!;
    private GhostStyles _styles = null!;
    private MainCamera _camera = null!;
    private ShaderBundle _bundle = null!;
    private TmpLabelSurface _labels = null!;
    private bool _ended;

    /// <summary>What consumers reach through <c>LatticeDrawing.Current</c>.</summary>
    public GhostDrawing Drawing { get; private set; } = null!;

    /// <summary>The world's renderer: its counts and claims.</summary>
    internal Renderer Renderer => _renderer;

    /// <summary>Whether the materials are Lattice's own shaders, not the stand-in, and how many keys have one.</summary>
    internal (bool Own, int Materials) Shaders => (_bundle.Own, _bundle.Count);

    /// <summary>
    /// Opens drawing for a world: refused, with nothing loaded, when the <c>render</c> module is off; refused when no
    /// key has a material (the bundle and the stand-in both missing). <paramref name="pieces"/> reads the prefabs' meshes;
    /// <paramref name="font"/> is the game's text font, else TextMeshPro's default.
    /// </summary>
    public static Result<RenderHost, Reason> Open(LatticeWorld world, IPieceParts pieces, TMP_FontAsset? font, Action<string> log)
    {
        if (Renderer.Off(world).TryGet(out var off) || world.StyleModule is not { } styles)
            return Result<RenderHost, Reason>.Error(off);

        var camera = new MainCamera();
        var bundle = ShaderBundle.Load(ShaderBundle.DefaultPath, log);
        var holder = new GameObject("Lattice.Render");
        DontDestroyOnLoad(holder);
        var order = RenderOrder.Default;
        var labels = new TmpLabelSurface(holder.transform, camera, order.Labels);
        var text = new TextStyle(font != null ? font : TMP_Settings.defaultFontAsset, 24f, new Rgba(255, 255, 255, 255));
        var ports = new RenderPorts(new UnityGpu(camera), camera, pieces, bundle.Material, log);
        var labelPorts = new LabelPorts(labels, new WorldLabelFacts(world.Plans, world.Validation, world.Board), text, new StopwatchClock());
        var opened = Renderer.Open(world, ports, labelPorts);
        if (opened.TryGetError(out var refused) || !opened.TryGet(out var renderer))
        {
            labels.End();
            bundle.Unload();
            Destroy(holder);
            return Result<RenderHost, Reason>.Error(refused);
        }

        var host = holder.AddComponent<RenderHost>();
        host._renderer = renderer;
        host._styles = styles;
        host._camera = camera;
        host._bundle = bundle;
        host._labels = labels;
        host.Drawing = new GhostDrawing(renderer, new UnityObjects(), () => camera.Current is { } c ? Option.Some(c) : Option<Camera>.None);
        styles.Projector = renderer.Labels;
        Application.onBeforeRender += host.BeforeCameras;
        log($"Lattice render: drawing with {(bundle.Own ? "Lattice's shaders" : "the stand-in")} ({bundle.Count} materials).");
        return Result<RenderHost, Reason>.Ok(host);
    }

    private void BeforeCameras() => _renderer.BeforeCameras();

    private void LateUpdate()
    {
        if (_ended || _camera.Current is not { } camera)
            return;
        var at = camera.transform.position;
        _styles.View(new WorldPoint(at.x, at.y, at.z));
        _renderer.Frame(Time.unscaledTime);
    }

    /// <summary>Ends drawing for the world: unsubscribes, releases and destroys everything it made, unloads the bundle.</summary>
    public void End()
    {
        if (_ended)
            return;
        _ended = true;
        Application.onBeforeRender -= BeforeCameras;
        if (ReferenceEquals(_styles.Projector, _renderer.Labels))
            _styles.Projector = null;
        Drawing.End();
        _renderer.End();
        _labels.End();
        _bundle.Unload();
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (!_ended && _renderer is not null)
            End();
    }
}
