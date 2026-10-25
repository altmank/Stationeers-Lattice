using System;
using TMPro;
using UnityEngine;

namespace Lattice.Api;

/// <summary>
/// Lattice's ghost drawing, for what needs Unity types: named materials, text styles, icons, decoration meshes, better
/// piece meshes, renderer claims, draw order and the camera. Present only while the <c>render</c> module runs (never in
/// batch mode). Main thread. Registrations end with the world. Added in contract 0.2.
/// Lattice implements this; consumers call it. A minor may add members, so consumer fakes of it are not covered by
/// compatibility.
/// </summary>
public interface IGhostDrawing
{
    /// <summary>
    /// A named material (see <see cref="MaterialKeys"/>), for a consumer's own overlays to match the ghosts: the override
    /// on top when one is installed. Refused (<c>not-found</c>) for a key Lattice has no material for. Constant cost.
    /// </summary>
    Result<Material, Reason> Material(MaterialKey key);

    /// <summary>
    /// Draws a key's things with another material: overrides stack per key, the last one draws, disposing reveals the
    /// one under it. The material must meet the material contract (<see cref="MaterialKeys"/>). Refused
    /// (<c>invalid-value</c>) when <c>material.enableInstancing</c> is off: an argument check at the call, never a
    /// per-frame guard.
    /// </summary>
    Result<IDisposable, Reason> Override(MaterialKey key, Material material);

    /// <summary>Registers a text style labels can name. Refused (<c>invalid-value</c>) for a size of zero or less, (<c>already-registered</c>) for a key already registered.</summary>
    Result<IDisposable, Reason> Register(TextStyleKey key, TextStyle style);

    /// <summary>Registers an image icon styles can name. Refused (<c>already-registered</c>) for a key already registered.</summary>
    Result<IDisposable, Reason> Register(IconKey key, Texture2D icon);

    /// <summary>Registers a decoration mesh, drawn with its material, that <see cref="Style.Mark"/> can name. Refused (<c>already-registered</c>) for a key already registered.</summary>
    Result<IDisposable, Reason> Register(DecorationKey key, Mesh mesh, Material material);

    /// <summary>
    /// Gives a piece a better ghost mesh than Lattice reads from its prefab (a modded piece that draws through
    /// something Lattice cannot read). Refused (<c>already-registered</c>) when the piece already has one.
    /// </summary>
    Result<IDisposable, Reason> Register(PieceLook look);

    /// <summary>
    /// Draws matched things yourself: Lattice leaves their claimed parts out of its own drawing and calls the claim once
    /// a frame, after its own submission, with the claimed things in view. Several claims on one thing: the highest
    /// order, then the later registration, wins. A callback's exception leaves the callback: Lattice's drawing is already
    /// submitted, later claims are skipped that frame, the claim stays registered. Refused (<c>already-registered</c>)
    /// for an id already registered.
    /// </summary>
    Result<IDisposable, Reason> Claim(RendererClaimDescriptor claim);

    /// <summary>The render queue values Lattice draws at, for a consumer's own drawing to sit between them. Fixed for the world.</summary>
    DrawOrder Order { get; }

    /// <summary>The camera Lattice draws for; none while the game has no main camera.</summary>
    Option<Camera> Camera { get; }
}

/// <summary>
/// The way into Lattice's drawing. <see cref="Current"/> is set when the <c>render</c> module starts, before
/// <see cref="LatticeSystem.Started"/>, and cleared at world end before <see cref="IGhostEvents.BoardReset"/>; it stays
/// none on a seat without drawing. Main thread.
/// </summary>
public static class LatticeDrawing
{
    private static IGhostDrawing? _current;

    /// <summary>The running world's drawing, or none (outside a world, or the <c>render</c> module is off).</summary>
    public static Option<IGhostDrawing> Current => _current is null ? Option<IGhostDrawing>.None : Option<IGhostDrawing>.Some(_current);

    /// <summary>
    /// Raised on the main thread when drawing starts for a world, after <see cref="Current"/> is set. Check
    /// <see cref="Current"/> first, then subscribe. Subscribers are called in subscription order; a handler's failure is
    /// its own mod's to handle.
    /// </summary>
    public static event Action<IGhostDrawing>? Started;

    internal static void Publish(IGhostDrawing drawing)
    {
        _current = drawing ?? throw new ArgumentNullException(nameof(drawing));
        Started?.Invoke(drawing);
    }

    internal static void Clear() => _current = null;
}

/// <summary>
/// The render queues Lattice draws at, lowest first. A consumer's faded built pieces go at <see cref="FadedWorld"/>
/// and write no depth, so a ghost behind a faded wall draws over it and see-through keeps meaning "through solid walls".
/// </summary>
/// <param name="FadedWorld">A consumer's faded built pieces.</param>
/// <param name="Ghosts">Ghosts and drafts.</param>
/// <param name="Overlays">Built-structure overlays.</param>
/// <param name="SeeThrough">The see-through pass.</param>
/// <param name="Labels">Labels and glyph icons.</param>
public sealed record DrawOrder(int FadedWorld, int Ghosts, int Overlays, int SeeThrough, int Labels);

/// <summary>A text style for labels: a TextMeshPro font asset, size, colour and how reasons are worded.</summary>
/// <param name="Font">The font asset.</param>
/// <param name="Size">The size in points, more than zero.</param>
/// <param name="Colour">The text colour.</param>
public sealed record TextStyle(TMP_FontAsset Font, float Size, Rgba Colour)
{
    /// <summary>Words for reason codes (<see cref="LabelText.FindingReason"/>); a code with no entry is shown as written.</summary>
    public ValueMap<ReasonCode, Label> Wording { get; init; } = ValueMap<ReasonCode, Label>.Empty;
}

/// <summary>A better ghost mesh for a piece.</summary>
/// <param name="Piece">The piece.</param>
/// <param name="Meshes">Its meshes, each placed in the piece's own space.</param>
public sealed record PieceLook(PrefabKey Piece, ValueList<PieceMesh> Meshes);

/// <summary>One mesh of a piece look.</summary>
/// <param name="Mesh">The mesh.</param>
/// <param name="Local">Where it sits in the piece's own space.</param>
public readonly record struct PieceMesh(Mesh Mesh, Matrix4x4 Local);

/// <summary>Which parts of a claimed thing the claimant draws. Values are fixed for the life of the contract.</summary>
public enum ClaimParts
{
    /// <summary>The mesh; Lattice still draws edges, glows, labels, icons and decorations.</summary>
    Mesh = 1,

    /// <summary>Everything; Lattice draws nothing of it.</summary>
    Everything = 2,
}

/// <summary>A renderer claim: things a match holds for, drawn by the claimant.</summary>
/// <param name="Id">The claim's id.</param>
/// <param name="Match">What it claims.</param>
/// <param name="Parts">Which parts the claimant draws.</param>
/// <param name="Draw">Called once a frame on the main thread, after Lattice's own submission, with the claimed things in view.</param>
public sealed record RendererClaimDescriptor(ClaimId Id, StyleMatch Match, ClaimParts Parts, Action<IClaimFrame> Draw)
{
    /// <summary>Its precedence over other claims on the same thing: higher wins (default 0).</summary>
    public int Order { get; init; }
}

/// <summary>
/// The claimed things in view this frame: a read-only view, valid only during the claim's callback. Allocates nothing.
/// Lattice implements this; consumers call it. A minor may add members, so consumer fakes of it are not covered by
/// compatibility.
/// </summary>
public interface IClaimFrame
{
    /// <summary>How many claimed things are in view.</summary>
    int Count { get; }

    /// <summary>One of them, 0 to <see cref="Count"/> - 1.</summary>
    ClaimedThing this[int index] { get; }
}

/// <summary>A claimed thing in view.</summary>
/// <param name="Thing">What it is.</param>
/// <param name="Part">Which part.</param>
/// <param name="Piece">Its piece.</param>
/// <param name="Matrix">Where it is drawn, in world space.</param>
/// <param name="Style">Its resolved look.</param>
public readonly record struct ClaimedThing(DrawnThing Thing, PartKind Part, PrefabKey Piece, Matrix4x4 Matrix, Style Style);
