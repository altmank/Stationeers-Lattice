using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Matching;
using Lattice.Core.Ports;

namespace Lattice.Core.Transforms;

/// <summary>Why one ghost or template draft cannot be transformed. Values are reported, never compiled into consumers.</summary>
internal enum TransformProblem
{
    /// <summary>A remove, swap or finish ghost: it is about a structure that stands where it is.</summary>
    TargetsAStructure = 1,

    /// <summary>The catalogue does not hold its piece.</summary>
    UnknownPiece = 2,

    /// <summary>Its piece has no mirror image among its turns (a machine with an uneven port layout).</summary>
    NoMirrorImage = 3,

    /// <summary>It would land off the cell centres of its piece's grid, or, for a piece the game places on a face, off the spots its own spots are taken to.</summary>
    OffGrid = 4,

    /// <summary>A transformation or intent this version does not know.</summary>
    UnknownKind = 5,

    /// <summary>It would land on a cell centre where the game does not snap its piece (<see cref="PieceFacts.Snap"/>).</summary>
    OffSnap = 6,

    /// <summary>
    /// It would land in a turn the game's builder cannot give its piece (<see cref="PieceFacts.AllowedTurns"/>), and no
    /// allowed turn gives it the same spots and ends.
    /// </summary>
    TurnNotAllowed = 7,
}

/// <summary>The rule id and codes the transforms and templates modules give their reasons under.</summary>
internal static class TransformCodes
{
    /// <summary>Who gives the transforms module's reasons.</summary>
    public static RuleId Transforms => RuleId.Known("lattice.transforms");

    /// <summary>
    /// The refusal for every ghost (or template draft, by index) a problem stops, ids ascending and comma separated in
    /// fact <c>ghosts</c> (<c>drafts</c>). Unknown pieces are <see cref="ReasonCodes.UnknownPiece"/>; unknown kinds
    /// <see cref="ReasonCodes.UnknownKind"/>; a piece with no mirror image <see cref="ReasonCodes.NoMirrorImage"/> and a
    /// piece off its snap <see cref="ReasonCodes.OffSnap"/> and one in a turn its builder cannot give
    /// <see cref="ReasonCodes.TurnNotAllowed"/>, each with fact <c>pieces</c> (prefab hashes); the rest
    /// <see cref="ReasonCodes.InvalidValue"/> with fact <c>problem</c> (<c>targets-a-structure</c>, <c>off-grid</c>).
    /// </summary>
    public static Reason Refusal(TransformProblem problem, string listName, List<long> which, List<PrefabKey> pieces)
    {
        which.Sort();
        var facts = ValueMap.Of(listName, Joined(which));
        return problem switch
        {
            TransformProblem.UnknownPiece => Reason.Of(ReasonCodes.UnknownPiece, Transforms, facts),
            TransformProblem.UnknownKind => Reason.Of(ReasonCodes.UnknownKind, Transforms, facts),
            TransformProblem.NoMirrorImage => Reason.Of(ReasonCodes.NoMirrorImage, Transforms, facts.With("pieces", Hashes(pieces))),
            TransformProblem.OffSnap => Reason.Of(ReasonCodes.OffSnap, Transforms, facts.With("pieces", Hashes(pieces))),
            TransformProblem.TurnNotAllowed => Reason.Of(ReasonCodes.TurnNotAllowed, Transforms, facts.With("pieces", Hashes(pieces))),
            TransformProblem.TargetsAStructure => Reason.Of(ReasonCodes.InvalidValue, Transforms, facts.With("problem", "targets-a-structure")),
            TransformProblem.OffGrid => Reason.Of(ReasonCodes.InvalidValue, Transforms, facts.With("problem", "off-grid")),
            _ => Reason.Of(ReasonCodes.UnknownKind, Transforms, facts.With("problem", problem.ToString())),
        };
    }

    private static string Joined(List<long> values)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < values.Count; i++)
            text.Append(i == 0 ? "" : ",").Append(values[i]);
        return text.ToString();
    }

    private static string Hashes(List<PrefabKey> pieces)
    {
        var seen = new List<long>(pieces.Count);
        foreach (var piece in pieces)
            if (!seen.Contains(piece.Hash))
                seen.Add(piece.Hash);
        return Joined(seen);
    }
}

/// <summary>
/// Moves, turns and mirrors single placements, and places template placements, from catalogue facts alone. Every
/// result lands on a cell centre of its piece's grid or is refused <see cref="TransformProblem.OffGrid"/>: large cells
/// centre on odd metres (keys 10 modulo 20), small cells on every half metre (keys multiples of 5), as
/// <see cref="CubeGrid"/> snaps; a centre where the game does not snap the piece (a rocket engine off a frame centre)
/// is refused <see cref="TransformProblem.OffSnap"/>. A result in a turn the game's builder cannot give its piece takes
/// the allowed turn that gives it the same spots and ends (<see cref="TurnFreePieces.AllowedTwin"/>), or is refused
/// <see cref="TransformProblem.TurnNotAllowed"/>. A piece the game places on a face stands on a face point, not a centre: its result
/// lands when it takes exactly the spots its original's spots are taken to (<see cref="Shapes"/>, <see cref="SpotMaps"/>),
/// all on large cells, so a wall keeps its face and its side. Constant cost per placement once a piece's mirror turn is
/// known. Main thread.
/// </summary>
internal sealed class Placements(ICatalogue catalogue, IGrid grid, IMirrorPartners partners)
{
    /// <summary>Template placements are about cell (0, 0, 0), a large cell centre; their shapes are worked out about this centre, where the grid has one.</summary>
    private static readonly Cell TemplateCentre = new(10, 10, 10);

    private readonly Shapes _shapes = new(catalogue, grid);
    private readonly MirrorImages _mirrors = new(catalogue, grid, partners);
    private TurnFreePieces? _twins;

    /// <summary>
    /// The placement after the transformation. A move adds the offset; a turn turns the cell about the pivot and
    /// the placement's turn after its own; a mirror reflects the cell across the plane and lays the piece, or its
    /// registered mirror partner, on its mirror image (<see cref="MirrorImages"/>), so the image's cells and ends are
    /// the original's reflected.
    /// </summary>
    public Result<Placement, TransformProblem> Apply(Transformation how, Placement placement)
    {
        if (!catalogue.Piece(placement.Piece).TryGet(out var piece))
            return Result<Placement, TransformProblem>.Error(TransformProblem.UnknownPiece);
        return Allowed(how switch
        {
            Transformation.Move move => Landed(piece, placement, placement with { At = placement.At.Plus(move.By) }, new Moving(move.By)),
            Transformation.Rotate rotate => Landed(
                piece, placement, Turned(placement, rotate.Pivot, rotate.Turn), new Turning(grid, rotate.Pivot, rotate.Pivot, rotate.Turn)),
            Transformation.Mirror mirror => Mirrored(piece, placement, mirror.Plane),
            _ => Result<Placement, TransformProblem>.Error(TransformProblem.UnknownKind),
        });
    }

    /// <summary>A template's placement, in local coordinates about cell (0, 0, 0), placed with its origin on <paramref name="origin"/> and turned by <paramref name="turn"/>.</summary>
    public Result<Placement, TransformProblem> Place(Placement local, Cell origin, Turn turn)
    {
        if (!catalogue.Piece(local.Piece).TryGet(out var piece))
            return Result<Placement, TransformProblem>.Error(TransformProblem.UnknownPiece);
        var offset = new CellOffset(local.At.X, local.At.Y, local.At.Z);
        var placed = new Placement(local.Piece, origin.Plus(grid.Apply(turn, offset)), grid.Compose(local.Turn, turn));
        return Allowed(Landed(piece, local with { At = TemplateCentre.Plus(offset) }, placed, new Turning(grid, TemplateCentre, origin, turn)));
    }

    /// <summary>Whether a cell is a cell centre of the grid.</summary>
    public static bool IsCentre(Cell cell, GridSize size)
    {
        var (step, centre) = size == GridSize.Large ? (20, 10) : (5, 0);
        return Mod(cell.X, step) == centre && Mod(cell.Y, step) == centre && Mod(cell.Z, step) == centre;
    }

    private Placement Turned(Placement placement, Cell pivot, Turn turn)
    {
        var offset = grid.Apply(turn, new CellOffset(placement.At.X - pivot.X, placement.At.Y - pivot.Y, placement.At.Z - pivot.Z));
        return new Placement(placement.Piece, pivot.Plus(offset), grid.Compose(placement.Turn, turn));
    }

    private Result<Placement, TransformProblem> Mirrored(PieceFacts piece, Placement placement, MirrorPlane plane)
    {
        var at = grid.Reflect(placement.At, plane);
        var seen = _mirrors.Seen(placement.Turn, plane.Normal);
        if (!piece.Face.IsSome)
            return _mirrors.ImageOf(piece, plane.Normal).TryGet(out var image)
                ? OnGrid(new Placement(image.Piece, at, grid.Compose(image.Turn, seen)), piece.Grid)
                : Result<Placement, TransformProblem>.Error(TransformProblem.NoMirrorImage);

        if (!Target(placement, new Reflecting(grid, plane)).TryGet(out var target))
            return OffGrid();
        return _mirrors.FaceImageOf(piece, placement.Turn, at, plane.Normal, target).TryGet(out var faceImage)
            ? Ok(new Placement(faceImage.Piece, at, grid.Compose(faceImage.Turn, seen)))
            : Result<Placement, TransformProblem>.Error(TransformProblem.NoMirrorImage);
    }

    /// <summary>The result, if it stands where the transformation takes the original: on a cell centre, or for a face piece on the spots its own are taken to.</summary>
    private Result<Placement, TransformProblem> Landed<TMap>(PieceFacts piece, Placement original, Placement result, TMap map) where TMap : ISpotMap
    {
        if (!piece.Face.IsSome)
            return OnGrid(result, piece.Grid);
        return Target(original, map).TryGet(out var target) && _shapes.Of(result).TryGet(out var shape) && SpotMaps.Same(shape.Spots, target)
            ? Ok(result)
            : OffGrid();
    }

    private Option<SpotKey[]> Target<TMap>(Placement original, TMap map) where TMap : ISpotMap =>
        _shapes.Of(original).TryGet(out var shape) ? SpotMaps.Target(shape.Spots, map) : Option<SpotKey[]>.None;

    private Result<Placement, TransformProblem> OnGrid(Placement placement, GridSize size)
    {
        if (!IsCentre(placement.At, size))
            return OffGrid();
        return catalogue.Piece(placement.Piece).TryGet(out var piece) && !piece.Snap.Holds(placement.At)
            ? Result<Placement, TransformProblem>.Error(TransformProblem.OffSnap)
            : Ok(placement);
    }

    /// <summary>A landed result in a turn its builder can give, its allowed twin's turn, or refused <see cref="TransformProblem.TurnNotAllowed"/>.</summary>
    private Result<Placement, TransformProblem> Allowed(Result<Placement, TransformProblem> landed)
    {
        if (!landed.TryGet(out var placement) || !catalogue.Piece(placement.Piece).TryGet(out var piece) || piece.AllowedTurns.Contains(placement.Turn))
            return landed;
        _twins ??= new TurnFreePieces(catalogue, grid, _shapes);
        return _twins.AllowedTwin(piece, placement.Turn).TryGet(out var twin)
            ? Ok(placement with { Turn = twin })
            : Result<Placement, TransformProblem>.Error(TransformProblem.TurnNotAllowed);
    }

    private static Result<Placement, TransformProblem> OffGrid() => Result<Placement, TransformProblem>.Error(TransformProblem.OffGrid);

    private static Result<Placement, TransformProblem> Ok(Placement placement) => Result<Placement, TransformProblem>.Ok(placement);

    private static int Mod(int value, int step) => ((value % step) + step) % step;
}
