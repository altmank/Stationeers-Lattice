using System;
using Lattice.Api;
using TMPro;
using UnityEngine;

namespace Lattice.Consumer.Drawing;

/// <summary>
/// What a renderer mod does with Lattice's drawing (claims, drawing registrations and fades): wait for the
/// drawing, match its materials and order, register its own fonts, icons, meshes and claims, and draw its faded world.
/// </summary>
internal sealed class DrawingFlows : IDisposable
{
    private static readonly ModId Planner = Must(ModId.Create("planner"));

    private readonly Material _faded;
    private readonly TMP_FontAsset _font;
    private readonly Texture2D _icon;
    private readonly Mesh _arrow;
    private IDisposable[] _registrations = Array.Empty<IDisposable>();

    public DrawingFlows(Material faded, TMP_FontAsset font, Texture2D icon, Mesh arrow)
    {
        _faded = faded;
        _font = font;
        _icon = icon;
        _arrow = arrow;
    }

    public int FadedQueue { get; private set; }

    public void Start()
    {
        if (LatticeDrawing.Current.TryGet(out var drawing))
            Use(drawing);
        else
            LatticeDrawing.Started += Use;
    }

    private void Use(IGhostDrawing drawing)
    {
        LatticeDrawing.Started -= Use;
        var order = drawing.Order;
        FadedQueue = order.FadedWorld;
        if (order.FadedWorld >= order.Ghosts || order.Overlays >= order.SeeThrough || order.SeeThrough >= order.Labels)
            throw new InvalidOperationException("draw order out of sequence");

        var lattice = Must(drawing.Material(MaterialKeys.Faded));
        _faded.CopyPropertiesFromMaterial(lattice);
        if (drawing.Camera.TryGet(out var camera))
            _faded.SetVector("_PlannerEye", camera.transform.position);

        var text = new TextStyle(_font, 3.5f, new Rgba(240, 240, 240, 255))
        {
            Wording = ValueMap.Of(ReasonCodes.NeedsSupport, Must(Label.Create("Needs a frame under it"))),
        };
        var pipe = new PieceLook(new PrefabKey(1234), ValueList.Of(new PieceMesh(_arrow, Matrix4x4.identity)));
        var claim = new RendererClaimDescriptor(Must(ClaimId.Create(Planner, "x-ray")), new StyleMatch.Family(Must(PieceFamily.Create(Planner, "pipes"))),
            ClaimParts.Mesh, Draw)
        {
            Order = 10,
        };

        _registrations = new[]
        {
            Must(drawing.Override(MaterialKeys.Ghost, _faded)),
            Must(drawing.Register(Must(TextStyleKey.Create(Planner, "labels")), text)),
            Must(drawing.Register(Must(IconKey.Create(Planner, "wrench")), _icon)),
            Must(drawing.Register(Must(DecorationKey.Create(Planner, "arrow")), _arrow, _faded)),
            Must(drawing.Register(pipe)),
            Must(drawing.Claim(claim)),
        };
    }

    private void Draw(IClaimFrame frame)
    {
        for (var i = 0; i < frame.Count; i++)
        {
            var thing = frame[i];
            if (thing.Part == PartKind.Placement && thing.Style.FillOpacity.TryGet(out var opacity))
            {
                _faded.SetFloat("_Opacity", opacity.Value);
                Graphics.DrawMesh(_arrow, thing.Matrix, _faded, 0);
            }
        }
    }

    public void Dispose()
    {
        LatticeDrawing.Started -= Use;
        foreach (var registration in _registrations)
            registration.Dispose();
        _registrations = Array.Empty<IDisposable>();
    }

    private static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static value => value, static why => throw new InvalidOperationException(why.Code.Value));
}
