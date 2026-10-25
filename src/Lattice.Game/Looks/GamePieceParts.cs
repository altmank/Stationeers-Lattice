using System.Collections.Generic;
using Lattice.Api;
using Lattice.Render.Gpu;
using Lattice.Render.Unity;
using RenderShows = Lattice.Render.Gpu.PartShows;

namespace Lattice.Game.Looks;

/// <summary>
/// Render's piece parts over <see cref="PieceMeshes"/>: whatever prefab the game holds, modded pieces included, each
/// piece read once and kept for the world. Main thread.
/// </summary>
internal sealed class GamePieceParts(PieceMeshes meshes) : IPieceParts
{
    private readonly Dictionary<PrefabKey, IReadOnlyList<RenderPart>?> _parts = new(256);

    public Option<IReadOnlyList<RenderPart>> Of(PrefabKey piece)
    {
        if (!_parts.TryGetValue(piece, out var parts))
        {
            parts = meshes.Of(piece).TryGet(out var drawing) ? Convert(drawing.Parts) : null;
            _parts.Add(piece, parts);
        }

        return parts is null ? Option<IReadOnlyList<RenderPart>>.None : Option.Some(parts);
    }

    private static IReadOnlyList<RenderPart> Convert(IReadOnlyList<PiecePart> parts)
    {
        var converted = new RenderPart[parts.Count];
        for (var i = 0; i < converted.Length; i++)
        {
            var part = parts[i];
            converted[i] = new RenderPart(new UnityMesh(part.Mesh), part.Local, part.Shows switch
            {
                PartShows.FromStep from => new RenderShows.FromStep(from.Step),
                PartShows.AtStep at => new RenderShows.AtStep(at.Step),
                _ => RenderShows.Every,
            });
        }

        return converted;
    }
}
