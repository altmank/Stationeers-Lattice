using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Consequences;

/// <summary>A connection end's network type flags, as the game's <c>NetworkType</c> bits.</summary>
internal readonly record struct NetworkTypes(int Mask)
{
    /// <summary>No type.</summary>
    public static NetworkTypes None => new(0);

    /// <summary><c>NetworkType.Pipe</c> (decompile <c>Assets.Scripts.Objects/NetworkType.cs</c>).</summary>
    public static NetworkTypes Pipe => new(1);

    /// <summary><c>NetworkType.Power</c>.</summary>
    public static NetworkTypes Power => new(2);

    /// <summary><c>NetworkType.Data</c>.</summary>
    public static NetworkTypes Data => new(4);

    /// <summary><c>NetworkType.Chute</c>.</summary>
    public static NetworkTypes Chute => new(8);

    /// <summary><c>NetworkType.PipeLiquid</c>.</summary>
    public static NetworkTypes PipeLiquid => new(0x20);

    /// <summary><c>NetworkType.PowerAndData</c>.</summary>
    public static NetworkTypes PowerAndData => new(6);

    /// <summary><c>NetworkType.All</c>.</summary>
    public static NetworkTypes All => new(int.MaxValue);

    /// <summary>Whether the two share any flag.</summary>
    public bool Shares(NetworkTypes other) => (Mask & other.Mask) != 0;
}

/// <summary>A connection end's role, as the game's <c>ConnectionRole</c> value (decompile <c>Assets.Scripts.Objects/ConnectionRole.cs</c>).</summary>
internal readonly record struct ConnectionRole(int Value)
{
    /// <summary>No role.</summary>
    public static ConnectionRole None => default;

    /// <summary><c>Input</c> or <c>Input2</c>: the end takes in.</summary>
    public bool TakesIn => Value is 1 or 2;

    /// <summary><c>Output</c> or <c>Output2</c>: the end lets out.</summary>
    public bool LetsOut => Value is 3 or 4;
}

/// <summary>
/// One connection end in world cells: <see cref="Into"/> is the cell its transform sits in, the cell of the piece it
/// joins (the game's <c>Connection.LocalGrid</c>); <see cref="From"/> is the cell one step back along its forward, the
/// owner's own cell (<c>FacingGrid</c>).
/// </summary>
internal readonly record struct PieceEnd(Cell Into, Cell From, NetworkTypes Types, ConnectionRole Role);

/// <summary>What a pipe carries (the game's <c>Pipe.ContentType</c>), and whether it takes any (<c>ContentType.All</c>).</summary>
internal sealed record PipeContent(int Kind, bool TakesAny)
{
    /// <summary><c>Pipe.IsContentMatch</c>: the same content, or this pipe takes any.</summary>
    public bool Accepts(PipeContent other) => other.Kind == Kind || TakesAny;

    /// <summary>Whether two pieces may join: either carries no content, or each accepts the other.</summary>
    public static bool Join(Option<PipeContent> a, Option<PipeContent> b) =>
        !a.TryGet(out var left) || !b.TryGet(out var right) || (left.Accepts(right) && right.Accepts(left));
}

/// <summary>A thing on the small grid as connectivity sees it: its id, the cells it occupies, its ends, its pipe content.</summary>
internal sealed record PieceModel(ReferenceId Id, IReadOnlyList<Cell> Cells, IReadOnlyList<PieceEnd> Ends, Option<PipeContent> Content)
{
    /// <summary>Whether the piece fills the cell. Linear in its cells.</summary>
    public bool Occupies(Cell cell)
    {
        for (var index = 0; index < Cells.Count; index++)
            if (Cells[index] == cell)
                return true;
        return false;
    }
}

/// <summary>A directed link: <see cref="To"/> is among the things <see cref="From"/>'s <c>SmallGrid.FillConnected</c> finds.</summary>
internal readonly record struct Link(ReferenceId From, ReferenceId To)
{
    /// <inheritdoc />
    public override string ToString() => $"{From.Value}->{To.Value}";
}

/// <summary>Links present after but not before (<see cref="Added"/>) and before but not after (<see cref="Lost"/>).</summary>
internal sealed record LinkDiff(IReadOnlyList<Link> Added, IReadOnlyList<Link> Lost)
{
    /// <summary>Nothing added and nothing lost.</summary>
    public bool Same => Added.Count == 0 && Lost.Count == 0;
}

/// <summary>
/// The game's connection rule on plain values, so a planned change can be checked before anything is built.
/// <c>SmallGrid.FillConnected</c> looks, for each end of a thing, in the cell the end sits in and keeps each occupant
/// whose <c>IsConnected</c> accepts the end: <c>SmallGrid.IsConnected</c> wants an end of its own that shares a
/// <c>NetworkType</c> flag and sits in the first end's facing cell; <c>Pipe.IsConnected</c> also wants the content to
/// match when both are pipes.
/// </summary>
internal static class Connectivity
{
    /// <summary>Models indexed by cell per step of <see cref="LinksTouchingSteps"/> (a long network's run holds thousands).</summary>
    private const int ModelsIndexedAStep = 128;

    /// <summary>Whether <paramref name="to"/> is among what <paramref name="from"/>'s <c>FillConnected</c> finds.</summary>
    public static bool Links(PieceModel from, PieceModel to)
    {
        if (from.Id == to.Id)
            return false;
        for (var index = 0; index < from.Ends.Count; index++)
        {
            var end = from.Ends[index];
            if (to.Occupies(end.Into) && Accepts(to, end, from.Content))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The piece's ends that take part in a link with any of the others, in the piece's own order: an end links out
    /// when another occupies its cell and accepts it (<see cref="Links"/>), and links in when another's end sits in one
    /// of the piece's cells and faces this end's cell with a shared network type (the other's <c>FillConnected</c>
    /// finding the piece through this end). Every other end is open: nothing is connected to it.
    /// </summary>
    public static List<PieceEnd> ConnectedEnds(PieceModel piece, IReadOnlyList<PieceModel> others)
    {
        var connected = new List<PieceEnd>(piece.Ends.Count);
        for (var index = 0; index < piece.Ends.Count; index++)
            if (LinksThrough(piece, piece.Ends[index], others))
                connected.Add(piece.Ends[index]);
        return connected;
    }

    /// <summary>Every link among the models that starts or ends at a focus id.</summary>
    public static HashSet<Link> LinksTouching(IReadOnlyList<PieceModel> models, HashSet<ReferenceId> focus)
    {
        var links = new HashSet<Link>();
        foreach (var _ in LinksTouchingSteps(models, focus, links))
        {
        }

        return links;
    }

    /// <summary>
    /// <see cref="LinksTouching"/> spread over steps for the frame slices (<see cref="Slices.ISlicedWork"/> through
    /// <see cref="StepsWork"/>): the models indexed by cell a batch per step, then one model's ends per step, into
    /// <paramref name="links"/>.
    /// </summary>
    public static IEnumerable<Unit> LinksTouchingSteps(IReadOnlyList<PieceModel> models, HashSet<ReferenceId> focus, HashSet<Link> links)
    {
        var byCell = new Dictionary<Cell, List<PieceModel>>();
        for (var from = 0; from < models.Count; from += ModelsIndexedAStep)
        {
            IndexByCell(models, from, System.Math.Min(from + ModelsIndexedAStep, models.Count), byCell);
            yield return Unit.Value;
        }

        for (var index = 0; index < models.Count; index++)
        {
            var from = models[index];
            var fromFocus = focus.Contains(from.Id);
            for (var endIndex = 0; endIndex < from.Ends.Count; endIndex++)
            {
                if (!byCell.TryGetValue(from.Ends[endIndex].Into, out var there))
                    continue;
                foreach (var to in there)
                    if ((fromFocus || focus.Contains(to.Id)) && Links(from, to))
                        links.Add(new Link(from.Id, to.Id));
            }

            yield return Unit.Value;
        }
    }

    /// <summary>
    /// The links with every id replaced by its group's id (a piece swapped together with others, and every piece
    /// replacing them, count as one), dropping links inside a group; an id in no group stands for itself.
    /// </summary>
    public static HashSet<Link> Grouped(IEnumerable<Link> links, IReadOnlyDictionary<ReferenceId, ReferenceId> groupOf)
    {
        var grouped = new HashSet<Link>();
        foreach (var link in links)
        {
            var from = groupOf.TryGetValue(link.From, out var a) ? a : link.From;
            var to = groupOf.TryGetValue(link.To, out var b) ? b : link.To;
            if (from != to)
                grouped.Add(new Link(from, to));
        }

        return grouped;
    }

    /// <summary>The links that touch none of the ids (those of removed pieces).</summary>
    public static HashSet<Link> Without(HashSet<Link> links, HashSet<ReferenceId> gone)
    {
        var kept = new HashSet<Link>();
        foreach (var link in links)
            if (!gone.Contains(link.From) && !gone.Contains(link.To))
                kept.Add(link);
        return kept;
    }

    /// <summary>The links only one side has, each list sorted by from then to.</summary>
    public static LinkDiff Compare(HashSet<Link> before, HashSet<Link> after)
    {
        var added = new List<Link>();
        foreach (var link in after)
            if (!before.Contains(link))
                added.Add(link);
        var lost = new List<Link>();
        foreach (var link in before)
            if (!after.Contains(link))
                lost.Add(link);
        added.Sort(static (a, b) => Order(a, b));
        lost.Sort(static (a, b) => Order(a, b));
        return new LinkDiff(added, lost);
    }

    /// <summary>
    /// The same cells and the same ends (as multisets) and the same content, so every <c>IsConnected</c> and
    /// <c>FillConnected</c> answer involving either is the same.
    /// </summary>
    public static bool SameShape(PieceModel a, PieceModel b) =>
        SameCells(a.Cells, b.Cells) && SameEnds(a.Ends, b.Ends) && a.Content == b.Content;

    private static bool Accepts(PieceModel owner, PieceEnd other, Option<PipeContent> otherContent)
    {
        if (owner.Content.TryGet(out var mine) && otherContent.TryGet(out var theirs) && !mine.Accepts(theirs))
            return false;
        for (var index = 0; index < owner.Ends.Count; index++)
        {
            var end = owner.Ends[index];
            if (end.Types.Shares(other.Types) && end.Into == other.From)
                return true;
        }

        return false;
    }

    private static bool LinksThrough(PieceModel piece, PieceEnd end, IReadOnlyList<PieceModel> others)
    {
        for (var index = 0; index < others.Count; index++)
        {
            var other = others[index];
            if (other.Id == piece.Id)
                continue;
            var outward = other.Occupies(end.Into) && Accepts(other, end, piece.Content);
            if (outward || ReachesInThrough(other, piece, end))
                return true;
        }

        return false;
    }

    // Links(other, piece) found through this end of the piece: the other's end sits in the piece's cells and the
    // piece's end is the one whose cell that end faces.
    private static bool ReachesInThrough(PieceModel other, PieceModel piece, PieceEnd end)
    {
        if (piece.Content.TryGet(out var mine) && other.Content.TryGet(out var theirs) && !mine.Accepts(theirs))
            return false;
        for (var index = 0; index < other.Ends.Count; index++)
        {
            var their = other.Ends[index];
            if (piece.Occupies(their.Into) && their.Types.Shares(end.Types) && end.Into == their.From)
                return true;
        }

        return false;
    }

    private static void IndexByCell(IReadOnlyList<PieceModel> models, int from, int until, Dictionary<Cell, List<PieceModel>> byCell)
    {
        for (var index = from; index < until; index++)
        {
            var model = models[index];
            for (var cellIndex = 0; cellIndex < model.Cells.Count; cellIndex++)
            {
                var cell = model.Cells[cellIndex];
                if (!byCell.TryGetValue(cell, out var list))
                    byCell[cell] = list = new List<PieceModel>(2);
                if (!list.Contains(model))
                    list.Add(model);
            }
        }
    }

    private static int Order(Link a, Link b) =>
        a.From != b.From ? a.From.Value.CompareTo(b.From.Value) : a.To.Value.CompareTo(b.To.Value);

    private static bool SameCells(IReadOnlyList<Cell> a, IReadOnlyList<Cell> b)
    {
        var left = new HashSet<Cell>();
        for (var index = 0; index < a.Count; index++)
            left.Add(a[index]);
        var right = new HashSet<Cell>();
        for (var index = 0; index < b.Count; index++)
            right.Add(b[index]);
        return left.SetEquals(right);
    }

    private static bool SameEnds(IReadOnlyList<PieceEnd> a, IReadOnlyList<PieceEnd> b)
    {
        if (a.Count != b.Count)
            return false;
        var used = new bool[b.Count];
        for (var index = 0; index < a.Count; index++)
            if (!TakeMatch(a[index], b, used))
                return false;
        return true;
    }

    private static bool TakeMatch(PieceEnd end, IReadOnlyList<PieceEnd> candidates, bool[] used)
    {
        for (var index = 0; index < candidates.Count; index++)
            if (!used[index] && candidates[index] == end)
                return used[index] = true;
        return false;
    }
}
