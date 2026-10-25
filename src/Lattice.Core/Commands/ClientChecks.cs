using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Commands;

/// <summary>
/// The host's size limits on a client's command: ghosts placed or named per command, selector nesting, batch nesting,
/// and the cells it may name (a box from <see cref="Min"/> to <see cref="Max"/>). <see cref="Default"/>'s range is
/// Lattice's own bound against runaway coordinates, not a game fact; the game bridge may set a tighter one.
/// </summary>
internal sealed record CommandLimits
{
    private CommandLimits(int maxGhosts, int maxSelectorDepth, int maxBatchDepth, Cell min, Cell max)
    {
        MaxGhosts = maxGhosts;
        MaxSelectorDepth = maxSelectorDepth;
        MaxBatchDepth = maxBatchDepth;
        Min = min;
        Max = max;
    }

    /// <summary>10,000 ghosts, selectors 8 deep, batches 4 deep, cells within ±1,000,000 dm on each axis.</summary>
    public static CommandLimits Default { get; } =
        new(10_000, 8, 4, new Cell(-1_000_000, -1_000_000, -1_000_000), new Cell(1_000_000, 1_000_000, 1_000_000));

    public int MaxGhosts { get; }

    public int MaxSelectorDepth { get; }

    public int MaxBatchDepth { get; }

    public Cell Min { get; }

    public Cell Max { get; }

    /// <summary>Limits; refused <see cref="ReasonCodes.OutOfRange"/> unless each count is positive and min is below max on every axis.</summary>
    public static Result<CommandLimits, Reason> Create(int maxGhosts, int maxSelectorDepth, int maxBatchDepth, Cell min, Cell max)
    {
        if (maxGhosts < 1 || maxSelectorDepth < 1 || maxBatchDepth < 1)
            return Result<CommandLimits, Reason>.Error(Reason.Of(ReasonCodes.OutOfRange, LatticeIdentity.Board, ValueMap.Of("limit", "count")));
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            return Result<CommandLimits, Reason>.Error(Reason.Of(ReasonCodes.OutOfRange, LatticeIdentity.Board, ValueMap.Of("limit", "range")));
        return Result<CommandLimits, Reason>.Ok(new CommandLimits(maxGhosts, maxSelectorDepth, maxBatchDepth, min, max));
    }
}

/// <summary>
/// The longest attached text the host accepts from a client per attachment key: the declared
/// <see cref="AttachmentDescriptor.MaxLength"/>, or <see cref="DefaultMaxLength"/> for a key nobody declared.
/// </summary>
internal sealed class AttachmentLimits
{
    /// <summary>The descriptor's default length, for keys with no declaration.</summary>
    public const int DefaultMaxLength = 1024;

    private readonly Dictionary<AttachmentKey, Declared> _declared = new();

    /// <summary>Declares a key; refused <see cref="ReasonCodes.AlreadyRegistered"/> while declared, <see cref="ReasonCodes.OutOfRange"/> for a negative length.</summary>
    public Result<IDisposable, Reason> Register(AttachmentDescriptor descriptor)
    {
        if (descriptor.MaxLength < 0)
            return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.OutOfRange, LatticeIdentity.Board, ValueMap.Of("limit", descriptor.MaxLength.ToString())));
        if (_declared.ContainsKey(descriptor.Key))
            return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.AlreadyRegistered, LatticeIdentity.Board, ValueMap.Of("key", descriptor.Key.Value)));
        var declared = new Declared(this, descriptor);
        _declared.Add(descriptor.Key, declared);
        return Result<IDisposable, Reason>.Ok(declared);
    }

    public int MaxLength(AttachmentKey key) => _declared.TryGetValue(key, out var declared) ? declared.Descriptor.MaxLength : DefaultMaxLength;

    private void Remove(Declared declared)
    {
        if (_declared.TryGetValue(declared.Descriptor.Key, out var current) && ReferenceEquals(current, declared))
            _declared.Remove(declared.Descriptor.Key);
    }

    private sealed class Declared(AttachmentLimits owner, AttachmentDescriptor descriptor) : IDisposable
    {
        public AttachmentDescriptor Descriptor { get; } = descriptor;

        public void Dispose() => owner.Remove(this);
    }
}

/// <summary>
/// The host's size and sanity checks on a command a client sent, each refusing with its own code: too many ghosts
/// placed or named (<see cref="ReasonCodes.TooManyGhosts"/>), selectors nested too deep
/// (<see cref="ReasonCodes.SelectorTooDeep"/>), batches nested too deep (<see cref="ReasonCodes.BatchTooDeep"/>),
/// attached text too long (<see cref="ReasonCodes.TextTooLong"/>), a cell out of range
/// (<see cref="ReasonCodes.OutOfRange"/>), a piece or kit the catalogue does not know
/// (<see cref="ReasonCodes.UnknownPiece"/>; a modded piece is known like any other). Names are bounded by their own
/// types. A kind this version does not know passes; the board refuses it. Linear in the command, stopping at the first
/// problem.
/// </summary>
internal sealed class ClientChecks(CommandLimits limits, ICatalogue catalogue, AttachmentLimits attachments)
{
    public Option<Refusal> Check(Command command)
    {
        var walk = new Walk(limits, catalogue, attachments);
        return walk.Command(command, 0) is { } why
            ? Option.Some(new Refusal(why, ValueList<GhostId>.Empty))
            : Option<Refusal>.None;
    }

    private sealed class Walk(CommandLimits limits, ICatalogue catalogue, AttachmentLimits attachments)
    {
        private int _ghosts;

        public Reason? Command(Command command, int batches) => command switch
        {
            Api.Command.Place place => Drafts(place.Ghosts, cells: true),
            Api.Command.Remove remove => Selector(remove.Which, 1),
            Api.Command.Change change => Selector(change.Which, 1) ?? Edit(change.Edit),
            Api.Command.Move move => Moves(move.To),
            Api.Command.CreatePlan create => Data(create.Draft.HigherLayerData) ?? Drafts(create.Ghosts, cells: true),
            Api.Command.EditPlan edit => edit.Edit is PlanEdit.SetData set ? Text(set.Key, set.Value) : null,
            Api.Command.MoveToPlan move => Selector(move.Which, 1),
            Api.Command.Batch batch => Steps(batch, batches + 1),
            Api.Command.MarkRemoval mark => Structures(mark.Which, 1),
            Api.Command.PlanSwap swap => Structures(swap.Which, 1) ?? SwapTo(swap.To),
            Api.Command.PlanFinish finish => Structures(finish.Which, 1),
            Api.Command.PlanRun run => Cells(run.Request.From, run.Request.To, run.Request.Via) ?? Kit(run.Request.Kit),
            Api.Command.PlanRoute route => Cells(route.Request.From, route.Request.To, ValueList<Cell>.Empty) ?? Kit(route.Request.Kit),
            Api.Command.Transform transform => Selector(transform.Which, 1) ?? (transform.How is Transformation.Rotate rotate ? Cell(rotate.Pivot) : null),
            Api.Command.PlaceTemplate template => Cell(template.Origin) ?? Data(template.As.HigherLayerData) ?? Drafts(template.Template.Ghosts, cells: false),
            _ => null,
        };

        private Reason? Steps(Command.Batch batch, int depth)
        {
            if (depth > limits.MaxBatchDepth)
                return Too(ReasonCodes.BatchTooDeep, depth, limits.MaxBatchDepth);
            foreach (var step in batch.Steps)
                if (Command(step, depth) is { } why)
                    return why;
            return null;
        }

        private Reason? Drafts(ValueList<GhostDraft> drafts, bool cells)
        {
            if (Count(drafts.Count) is { } tooMany)
                return tooMany;
            foreach (var draft in drafts)
                if ((Intent(draft.Intent, cells) ?? Data(draft.Attached)) is { } why)
                    return why;
            return null;
        }

        private Reason? Intent(Intent intent, bool cells) => intent switch
        {
            Api.Intent.Build build => Placement(build.Target, cells),
            Api.Intent.Swap swap => (cells ? Cell(swap.Old.At) : null) ?? Placement(swap.New, cells),
            Api.Intent.Remove remove => cells ? Cell(remove.Target.At) : null,
            Api.Intent.FinishOnly finish => cells ? Cell(finish.Target.At) : null,
            _ => null,
        };

        private Reason? Placement(Placement placement, bool cells) => (cells ? Cell(placement.At) : null) ?? Piece(placement.Piece);

        private Reason? Edit(GhostEdit edit) => edit switch
        {
            GhostEdit.Attach attach => Text(attach.Key, attach.Value),
            _ => null,
        };

        private Reason? Moves(ValueMap<GhostId, Placement> to)
        {
            if (Count(to.Count) is { } tooMany)
                return tooMany;
            foreach (var pair in to)
                if (Placement(pair.Value, cells: true) is { } why)
                    return why;
            return null;
        }

        private Reason? Selector(Selector selector, int depth)
        {
            if (depth > limits.MaxSelectorDepth)
                return Too(ReasonCodes.SelectorTooDeep, depth, limits.MaxSelectorDepth);
            return selector switch
            {
                Api.Selector.Ids ids => Count(ids.Values.Count),
                Api.Selector.Box box => Cell(box.Corner) ?? Cell(box.Opposite),
                Api.Selector.RunThrough run => Cell(run.Cell),
                Api.Selector.Tagged tagged => tagged.Value.TryGet(out var value) ? Text(tagged.Key, value) : null,
                Api.Selector.All all => Selectors(all.Of, depth + 1),
                Api.Selector.Any any => Selectors(any.Of, depth + 1),
                _ => null,
            };
        }

        private Reason? Selectors(ValueList<Selector> selectors, int depth)
        {
            foreach (var selector in selectors)
                if (Selector(selector, depth) is { } why)
                    return why;
            return null;
        }

        private Reason? Structures(StructureSelector selector, int depth)
        {
            if (depth > limits.MaxSelectorDepth)
                return Too(ReasonCodes.SelectorTooDeep, depth, limits.MaxSelectorDepth);
            switch (selector)
            {
                case StructureSelector.Refs refs:
                    foreach (var structure in refs.Values)
                        if (Cell(structure.At) is { } why)
                            return why;
                    return null;
                case StructureSelector.Box box:
                    return Cell(box.Corner) ?? Cell(box.Opposite);
                case StructureSelector.RunThrough run:
                    return Cell(run.Cell);
                case StructureSelector.All all:
                    return StructureList(all.Of, depth + 1);
                case StructureSelector.Any any:
                    return StructureList(any.Of, depth + 1);
                default:
                    return null;
            }
        }

        private Reason? StructureList(ValueList<StructureSelector> selectors, int depth)
        {
            foreach (var selector in selectors)
                if (Structures(selector, depth) is { } why)
                    return why;
            return null;
        }

        private Reason? SwapTo(SwapTo to) => to switch
        {
            Api.SwapTo.Exactly exactly => Piece(exactly.Piece),
            Api.SwapTo.SameShapeFrom shape => Kit(shape.Kit),
            _ => null,
        };

        private Reason? Count(int more)
        {
            _ghosts += more;
            return _ghosts > limits.MaxGhosts ? Too(ReasonCodes.TooManyGhosts, _ghosts, limits.MaxGhosts) : null;
        }

        private Reason? Data(ValueMap<AttachmentKey, Attachment> data)
        {
            foreach (var pair in data)
                if (Text(pair.Key, pair.Value) is { } why)
                    return why;
            return null;
        }

        private Reason? Text(AttachmentKey key, Attachment value)
        {
            var limit = attachments.MaxLength(key);
            return value.Value.Length > limit
                ? Reason.Of(ReasonCodes.TextTooLong, LatticeIdentity.Board, ValueMap.Of("key", key.Value).With("length", value.Value.Length.ToString()).With("limit", limit.ToString()))
                : null;
        }

        private Reason? Cells(Cell from, Cell to, ValueList<Cell> via)
        {
            if ((Cell(from) ?? Cell(to)) is { } why)
                return why;
            foreach (var cell in via)
                if (Cell(cell) is { } outside)
                    return outside;
            return null;
        }

        private Reason? Cell(Cell cell) =>
            cell.X < limits.Min.X || cell.Y < limits.Min.Y || cell.Z < limits.Min.Z ||
            cell.X > limits.Max.X || cell.Y > limits.Max.Y || cell.Z > limits.Max.Z
                ? Reason.Of(ReasonCodes.OutOfRange, LatticeIdentity.Board, ValueMap.Of("cell", cell.X + "," + cell.Y + "," + cell.Z))
                : null;

        private Reason? Piece(PrefabKey piece) => catalogue.Piece(piece).IsOk
            ? null
            : Reason.Of(ReasonCodes.UnknownPiece, LatticeIdentity.Board, ValueMap.Of("piece", piece.Hash.ToString()));

        private Reason? Kit(KitKey kit) => catalogue.Kit(kit).IsOk
            ? null
            : Reason.Of(ReasonCodes.UnknownPiece, LatticeIdentity.Board, ValueMap.Of("kit", kit.Hash.ToString()));

        private static Reason Too(ReasonCode code, int size, int limit) =>
            Reason.Of(code, LatticeIdentity.Board, ValueMap.Of("size", size.ToString()).With("limit", limit.ToString()));
    }
}
