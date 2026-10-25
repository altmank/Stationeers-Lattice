using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Render.Gpu;

namespace Lattice.Render.Drawing;

/// <summary>
/// The parts a piece is drawn with: a registered look (<c>IGhostDrawing.Register(PieceLook)</c>) wins over what Game
/// reads from the prefab, for its piece only. A registration or its disposal tells drawing, which re-reads that piece.
/// </summary>
internal sealed class PieceLooks : IPieceParts
{
    private readonly IPieceParts _game;
    private readonly Registry<PrefabKey, IReadOnlyList<RenderPart>> _looks;

    public PieceLooks(IPieceParts game, Action<PrefabKey> changed)
    {
        _game = game;
        _looks = new Registry<PrefabKey, IReadOnlyList<RenderPart>>("piece", static key => key.Hash.ToString(System.Globalization.CultureInfo.InvariantCulture), changed);
    }

    public Option<IReadOnlyList<RenderPart>> Of(PrefabKey piece) =>
        _looks.TryGet(piece, out var parts) ? Option.Some(parts) : _game.Of(piece);

    /// <summary>Refused (<c>already-registered</c>) when the piece already has a registered look.</summary>
    public Result<IDisposable, Reason> Register(PrefabKey piece, IReadOnlyList<RenderPart> parts) => _looks.Register(piece, parts);

    public void End() => _looks.End();
}
