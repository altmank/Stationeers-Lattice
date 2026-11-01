using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Consequences;
using Lattice.Core.Ports;

namespace Lattice.Core.Support;

/// <summary>
/// Which pipe networks the ghosts of a build order join, as they are placed: a ghost's pipe joins a network when one
/// of its ends links with a pipe of it (<see cref="Connectivity.Links"/> on catalogue ends); a ghost whose pipe links
/// two or more networks joins them. Built pipes count by the world's network; pipes of ghosts already placed form
/// networks of their own, merged as they link. Not thread-safe: one walk owns it.
/// <para>
/// A ghost held back to a later stage is asked again every stage, so what it reaches is kept per ghost: its model, the
/// cells its ends reach into, and the pipes it links there. Those are read again only when a stage puts or takes
/// something in one of those cells (<see cref="Changed"/>); merges since are followed through the union-find. Pipes
/// placed in the stage so far are found by cell. A ghost's question costs its ends, not the stage's size.
/// </para>
/// <para>One instance serves walk after walk: <see cref="Reset"/> starts the next.</para>
/// </summary>
internal sealed class PipeNetworks(ICatalogue catalogue, WorldModels models)
{
    private readonly record struct Node(int Kind, long Id);

    private const int NetworkNode = 1;
    private const int StructureNode = 2;
    private const int GhostNode = 3;

    private readonly Dictionary<Node, Node> _parent = [];
    private readonly Dictionary<Cell, List<(PieceModel Model, Node Node)>> _stage = [];
    private readonly Dictionary<GhostId, Reach> _reach = [];
    private readonly Dictionary<Cell, int> _changedIn = [];
    private readonly List<Node> _roots = new(4);
    private readonly Stack<List<(PieceModel Model, Node Node)>> _spare = new();

    // Pipe models by structure, kept from one walk to the next while the structure stands the same: those read in the
    // last walk or this one.
    private Dictionary<ReferenceId, Modelled> _models = [];
    private Dictionary<ReferenceId, Modelled> _modelsNow = [];
    private readonly Reach _noPipe = new(null, []);
    private int _number;

    /// <summary>Forgets every network and read for another walk, keeping its room.</summary>
    public void Reset()
    {
        _parent.Clear();
        foreach (var here in _stage.Values)
        {
            here.Clear();
            _spare.Push(here);
        }

        _stage.Clear();
        _reach.Clear();
        _changedIn.Clear();
        _roots.Clear();
        _number = 0;
        (_models, _modelsNow) = (_modelsNow, _models);
        _modelsNow.Clear();
    }

    // The structure's model, the one made before while its piece, cell and turn are the same.
    private Modelled ModelOf(ReferenceId id, PrefabKey piece, Cell at, Turn turn, PieceFacts facts, StructureInfo? info)
    {
        if (!(_modelsNow.TryGetValue(id, out var held) || _models.TryGetValue(id, out held)) || held.Piece != piece || held.At != at || held.Turn != turn)
        {
            var model = models.Piece(info ?? SupportWorld.AssumedInfo(new GhostId(-id.Value), new Placement(piece, at, turn)), facts);
            var into = new List<Cell>(model.Ends.Count);
            foreach (var end in model.Ends)
                if (!into.Contains(end.Into))
                    into.Add(end.Into);
            held = new Modelled(piece, at, turn, model, into);
        }

        _modelsNow[id] = held;
        return held;
    }

    /// <summary>A new stage: pipes placed in it so far are linked through <see cref="Accept"/>, not the world.</summary>
    public void StartStage(int number)
    {
        _number = number;
        foreach (var here in _stage.Values)
            here.Clear();
    }

    /// <summary>The stage puts or takes something in the cell; ghosts reaching it read the world there again.</summary>
    public void Changed(Cell cell, int stage) => _changedIn[cell] = stage;

    /// <summary>Whether placing the ghost's pipe would join two or more pipe networks.</summary>
    public bool Joins(SupportWorld placed, GhostId ghost, Placement put, PieceFacts piece) =>
        Roots(placed, ghost, put, piece).Model is not null && _roots.Count >= 2;

    /// <summary>The ghost's pipe is placed in this stage: it and every network it links become one.</summary>
    public void Accept(SupportWorld placed, GhostId ghost, Placement put, PieceFacts piece)
    {
        var reach = Roots(placed, ghost, put, piece);
        if (reach.Model is not { } model)
            return;
        var self = new Node(GhostNode, ghost.Value);
        foreach (var root in _roots)
            _parent[root] = Find(self);
        foreach (var cell in model.Cells)
        {
            if (!_stage.TryGetValue(cell, out var here))
                _stage.Add(cell, here = _spare.Count > 0 ? _spare.Pop() : new List<(PieceModel, Node)>(1));
            here.Add((model, self));
        }
    }

    // The distinct roots the ghost's pipe links, into _roots.
    private Reach Roots(SupportWorld placed, GhostId ghost, Placement put, PieceFacts piece)
    {
        _roots.Clear();
        var reach = ReachOf(placed, ghost, put, piece);
        if (reach.Model is not { } model)
            return reach;
        foreach (var node in reach.World)
            AddOnce(_roots, Find(node));
        foreach (var cell in reach.Into)
            if (_stage.TryGetValue(cell, out var here))
                foreach (var (other, node) in here)
                    if (Connectivity.Links(model, other) || Connectivity.Links(other, model))
                        AddOnce(_roots, Find(node));
        return reach;
    }

    // A piece with no pipe end reaches nothing, read or kept: most ghosts of a large plan are not pipes.
    private Reach ReachOf(SupportWorld placed, GhostId ghost, Placement put, PieceFacts piece)
    {
        if (!HasPipeEnd(piece))
            return _noPipe;
        if (_reach.TryGetValue(ghost, out var known) && Holds(known))
            return known;
        var reach = known ?? New(ghost, put, piece);
        reach.ReadAt = _number;
        reach.World.Clear();
        _reach[ghost] = reach;
        if (reach.Model is not { } model)
            return reach;
        var self = SupportWorld.IdOf(ghost);
        foreach (var cell in reach.Into)
            foreach (var info in placed.At(cell))
            {
                if (info.Ref.Id == self || !catalogue.Piece(info.Ref.Piece).TryGet(out var facts) || !HasPipeEnd(facts))
                    continue;
                var other = ModelOf(info.Ref.Id, info.Ref.Piece, info.Ref.At, info.Turn, facts, info).Model;
                if (Connectivity.Links(model, other) || Connectivity.Links(other, model))
                    AddOnce(reach.World, NodeOf(placed, info));
            }

        return reach;
    }

    // Read in this stage, or in an earlier one with nothing put or taken in its end cells since.
    private bool Holds(Reach reach)
    {
        if (reach.ReadAt == _number || reach.Model is null)
            return true;
        foreach (var cell in reach.Into)
            if (_changedIn.TryGetValue(cell, out var stage) && stage >= reach.ReadAt)
                return false;
        return true;
    }

    private Reach New(GhostId ghost, Placement put, PieceFacts piece)
    {
        var modelled = ModelOf(SupportWorld.IdOf(ghost), put.Piece, put.At, put.Turn, piece, null);
        return new Reach(modelled.Model, modelled.Into);
    }

    private Node NodeOf(SupportWorld placed, StructureInfo info)
    {
        if (placed.AssumedFrom(info.Ref).TryGet(out var ghost))
            return new Node(GhostNode, ghost.Value);
        return placed.NetworkOf(info.Ref.Id).TryGet(out var network)
            ? new Node(NetworkNode, network.Value)
            : new Node(StructureNode, info.Ref.Id.Value);
    }

    private Node Find(Node node)
    {
        var root = node;
        while (_parent.TryGetValue(root, out var up) && up != root)
            root = up;
        while (_parent.TryGetValue(node, out var up) && up != root)
        {
            _parent[node] = root;
            node = up;
        }

        return root;
    }

    private static bool HasPipeEnd(PieceFacts piece)
    {
        foreach (var end in piece.Ends)
            foreach (var kind in end.Kinds)
                if (kind is NetworkType.GasPipe or NetworkType.LiquidPipe)
                    return true;
        return false;
    }

    private static void AddOnce(List<Node> nodes, Node node)
    {
        if (!nodes.Contains(node))
            nodes.Add(node);
    }

    /// <summary>A structure's pipe model as it stood (piece, cell, turn), and the distinct cells its ends reach into.</summary>
    private readonly record struct Modelled(PrefabKey Piece, Cell At, Turn Turn, PieceModel Model, List<Cell> Into);

    /// <summary>A ghost's pipe as the walk last read it; no model for a piece with no pipe end.</summary>
    private sealed class Reach(PieceModel? model, List<Cell> into)
    {
        public PieceModel? Model => model;

        /// <summary>The cells its ends reach into.</summary>
        public List<Cell> Into => into;

        /// <summary>The pipes it links there, built or placed in earlier stages, as nodes.</summary>
        public List<Node> World { get; } = new(2);

        /// <summary>The stage it was read in.</summary>
        public int ReadAt { get; set; } = -1;
    }
}
