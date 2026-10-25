using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Consequences;

/// <summary>
/// A loop of a network: a group of pieces joined in more than one way (a 2-edge-connected part of the piece graph),
/// the pieces cut from it, and why it stays whole where it does.
/// </summary>
/// <param name="Index">Its place among the loops found.</param>
/// <param name="Pieces">Every piece on the loop, sorted.</param>
/// <param name="Spared">A keep id is on it: nothing is cut.</param>
/// <param name="Cut">The pieces removed to break it, sorted.</param>
/// <param name="Unbroken">Why a cycle of it stays after the cuts (every way round it runs through a piece that may not go).</param>
internal sealed record NetworkLoop(int Index, IReadOnlyList<ReferenceId> Pieces, bool Spared, IReadOnlyList<ReferenceId> Cut, Option<Reason> Unbroken);

/// <summary>The loops found.</summary>
internal sealed record LoopResult(IReadOnlyList<NetworkLoop> Loops)
{
    /// <summary>Every piece to cut, sorted. Linear in the cuts.</summary>
    public List<ReferenceId> Cut
    {
        get
        {
            var cut = new List<ReferenceId>();
            foreach (var loop in Loops)
                cut.AddRange(loop.Cut);
            cut.SortIds();
            return cut;
        }
    }
}

/// <summary>
/// Loop removal on plain values. Pieces link by the game's rule (<see cref="Connectivity.Links"/> either way); devices
/// take part only as what a piece must stay linked to, never as a way through (a device's ports are separate
/// networks). A chain is a run of pieces with exactly two links each, none linked to a device, between two anchors
/// (any other piece); it may go only when every piece in it is a candidate and not blocked. Chains are the unit of
/// cutting: removing a whole chain leaves no stub, and its two anchors lose one end each. A loop is a group of chains
/// joined in more than one way (bridges of the chain graph removed); each is broken by removing, one at a time, the
/// chain with the fewest pieces whose anchors stay joined without it, until no cycle is left or none of its chains may
/// go. A chain of no pieces (two junctions joined directly) is never cut: that would change pieces that stay, not
/// remove any. A loop holding a keep id is spared whole.
/// </summary>
internal static class LoopCutting
{
    /// <summary>The loops among the candidates and the pieces around them, and the pieces to cut from each.</summary>
    public static LoopResult Find(
        IReadOnlyList<PieceModel> candidates,
        IReadOnlyList<PieceModel> others,
        HashSet<ReferenceId> devices,
        IReadOnlyDictionary<ReferenceId, Reason> blocked,
        HashSet<ReferenceId> keep)
    {
        var graph = Graph.Of(candidates, others, devices);
        var candidateIds = new HashSet<ReferenceId>();
        foreach (var candidate in candidates)
            candidateIds.Add(candidate.Id);

        var chains = Chains(graph, candidateIds, blocked);
        var loops = new List<NetworkLoop>();
        foreach (var group in Groups(chains))
        {
            var pieces = PiecesOf(group);
            var spared = pieces.Exists(keep.Contains);
            loops.Add(spared
                ? new NetworkLoop(loops.Count, pieces, true, [], Option<Reason>.None)
                : Break(loops.Count, pieces, group, chains));
        }

        return new LoopResult(loops);
    }

    // Removes the cheapest chain that leaves its anchors joined, again and again, until the group has no cycle.
    private static NetworkLoop Break(int index, List<ReferenceId> pieces, List<Chain> group, List<Chain> all)
    {
        var present = new HashSet<Chain>(all);
        var cut = new List<ReferenceId>();
        while (true)
        {
            Chain? best = null;
            foreach (var chain in group)
            {
                if (!present.Contains(chain) || !chain.Removable || chain.Interior.Count == 0 || !StillJoined(chain, present))
                    continue;
                if (best is null || chain.Interior.Count < best.Interior.Count ||
                    (chain.Interior.Count == best.Interior.Count && chain.MinimumId < best.MinimumId))
                    best = chain;
            }

            if (best is null)
                break;
            present.Remove(best);
            cut.AddRange(best.Interior);
        }

        cut.SortIds();
        var unbroken = CycleRank(group, present) > 0
            ? Option.Some(ConsequenceCodes.Of(ReasonCodes.NoRemovableChain))
            : Option<Reason>.None;
        return new NetworkLoop(index, pieces, false, cut, unbroken);
    }

    // Whether the chain's anchors stay joined through the other chains present.
    private static bool StillJoined(Chain removed, HashSet<Chain> present)
    {
        if (removed.From == removed.To)
            return true;
        var next = new Dictionary<ReferenceId, List<ReferenceId>>();
        foreach (var chain in present)
        {
            if (chain == removed)
                continue;
            Add(next, chain.From, chain.To);
            Add(next, chain.To, chain.From);
        }

        var seen = new HashSet<ReferenceId> { removed.From };
        var queue = new Queue<ReferenceId>();
        queue.Enqueue(removed.From);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (at == removed.To)
                return true;
            if (!next.TryGetValue(at, out var around))
                continue;
            foreach (var neighbour in around)
                if (seen.Add(neighbour))
                    queue.Enqueue(neighbour);
        }

        return false;
    }

    // Independent cycles left among the group's chains still present: edges - nodes + parts.
    private static int CycleRank(List<Chain> group, HashSet<Chain> present)
    {
        var parent = new Dictionary<ReferenceId, ReferenceId>();
        var edges = 0;
        foreach (var chain in group)
        {
            if (!present.Contains(chain))
                continue;
            edges++;
            parent.TryAdd(chain.From, chain.From);
            parent.TryAdd(chain.To, chain.To);
            parent[Root(parent, chain.From)] = Root(parent, chain.To);
        }

        // Root compresses paths as it goes, so the keys are copied first: Mono's Dictionary (the game's runtime) counts
        // setting an existing key as a change and throws on the enumeration's next step (.NET does not, so a test
        // passes either way).
        var parts = 0;
        foreach (var node in new List<ReferenceId>(parent.Keys))
            parts += Root(parent, node) == node ? 1 : 0;
        return edges - parent.Count + parts;
    }

    private static ReferenceId Root(Dictionary<ReferenceId, ReferenceId> parent, ReferenceId node)
    {
        while (parent[node] != node)
        {
            parent[node] = parent[parent[node]];
            node = parent[node];
        }

        return node;
    }

    // The chains between anchors. A piece is inside a chain when it has exactly two links and none to a device; every
    // other piece is an anchor. A chain may go only when every piece inside it is a candidate and not blocked, so a
    // cut never leaves a stub behind a piece that stays.
    private static List<Chain> Chains(Graph graph, HashSet<ReferenceId> candidates, IReadOnlyDictionary<ReferenceId, Reason> blocked)
    {
        var inner = new HashSet<ReferenceId>();
        foreach (var node in graph.Links)
            if (!graph.DeviceLinked.Contains(node.Key) && node.Value.Count == 2 && node.Value[0] != node.Value[1])
                inner.Add(node.Key);

        var chains = new List<Chain>();
        var walked = new HashSet<(ReferenceId, ReferenceId)>();
        var anchors = new List<ReferenceId>();
        foreach (var node in graph.Links.Keys)
            if (!inner.Contains(node))
                anchors.Add(node);
        anchors.SortIds();

        foreach (var anchor in anchors)
        {
            foreach (var first in graph.Links[anchor])
            {
                if (walked.Contains((anchor, first)))
                    continue;
                var interior = new List<ReferenceId>();
                var previous = anchor;
                var at = first;
                while (inner.Contains(at))
                {
                    interior.Add(at);
                    var around = graph.Links[at];
                    var next = around[0] == previous ? around[1] : around[0];
                    previous = at;
                    at = next;
                }

                walked.Add((anchor, first));
                walked.Add((at, previous));
                var removable = interior.TrueForAll(id => candidates.Contains(id) && !blocked.ContainsKey(id));
                chains.Add(new Chain(anchor, at, interior, removable));
            }
        }

        // A ring of two-link pieces has no anchor: any of its pieces anchors it (a chain from it back to itself).
        var walkedInner = new HashSet<ReferenceId>();
        foreach (var chain in chains)
            walkedInner.UnionWith(chain.Interior);

        var rest = new List<ReferenceId>(inner);
        rest.SortIds();
        foreach (var start in rest)
        {
            if (walkedInner.Contains(start))
                continue;
            var interior = new List<ReferenceId>();
            var previous = start;
            var at = graph.Links[start][0];
            while (at != start)
            {
                interior.Add(at);
                var around = graph.Links[at];
                var next = around[0] == previous ? around[1] : around[0];
                previous = at;
                at = next;
            }

            walkedInner.Add(start);
            walkedInner.UnionWith(interior);

            // One piece breaks a ring: its first removable piece stands for the whole ring as one chain, which still
            // lists every piece of the ring for the report.
            var first = interior.FindIndex(id => candidates.Contains(id) && !blocked.ContainsKey(id));
            chains.Add(first >= 0
                ? new Chain(start, start, [interior[first]], true, interior)
                : new Chain(start, start, interior, false));
        }

        return chains;
    }

    // Groups of chains joined in more than one way: the chain graph without its bridges, parts with a cycle.
    private static List<List<Chain>> Groups(List<Chain> chains)
    {
        var all = new HashSet<Chain>(chains);
        var onCycles = chains.FindAll(chain => StillJoined(chain, all));
        var parent = new Dictionary<ReferenceId, ReferenceId>();
        foreach (var chain in onCycles)
        {
            parent.TryAdd(chain.From, chain.From);
            parent.TryAdd(chain.To, chain.To);
            parent[Root(parent, chain.From)] = Root(parent, chain.To);
        }

        var byRoot = new Dictionary<ReferenceId, List<Chain>>();
        foreach (var chain in onCycles)
        {
            var root = Root(parent, chain.From);
            if (!byRoot.TryGetValue(root, out var group))
                byRoot[root] = group = [];
            group.Add(chain);
        }

        var groups = new List<List<Chain>>(byRoot.Values);
        groups.Sort(static (a, b) => PiecesOf(a)[0].Value.CompareTo(PiecesOf(b)[0].Value));
        return groups;
    }

    private static List<ReferenceId> PiecesOf(List<Chain> group)
    {
        var pieces = new HashSet<ReferenceId>();
        foreach (var chain in group)
        {
            pieces.Add(chain.From);
            pieces.Add(chain.To);
            pieces.UnionWith(chain.Interior);
            pieces.UnionWith(chain.Ring);
        }

        return IdOrder.Sorted(pieces);
    }

    private static void Add(Dictionary<ReferenceId, List<ReferenceId>> next, ReferenceId a, ReferenceId b)
    {
        if (!next.TryGetValue(a, out var list))
            next[a] = list = [];
        list.Add(b);
    }

    /// <summary>A run of two-link pieces between two anchors; compared by reference.</summary>
    private sealed class Chain
    {
        public Chain(ReferenceId from, ReferenceId to, List<ReferenceId> interior, bool removable, List<ReferenceId>? ring = null)
        {
            From = from;
            To = to;
            Interior = interior;
            Removable = removable;
            Ring = ring ?? [];
            MinimumId = long.MaxValue;
            foreach (var id in interior)
                MinimumId = System.Math.Min(MinimumId, id.Value);
        }

        public ReferenceId From { get; }

        public ReferenceId To { get; }

        public List<ReferenceId> Interior { get; }

        /// <summary>For a ring standing as one chain: every piece of the ring (Interior holds the one to cut).</summary>
        public List<ReferenceId> Ring { get; }

        /// <summary>Every piece inside is a candidate and not blocked.</summary>
        public bool Removable { get; }

        public long MinimumId { get; }
    }

    /// <summary>The pieces' links to each other (devices left out) and which pieces link to a device.</summary>
    private sealed class Graph
    {
        private Graph(Dictionary<ReferenceId, List<ReferenceId>> links, HashSet<ReferenceId> deviceLinked)
        {
            Links = links;
            DeviceLinked = deviceLinked;
        }

        public Dictionary<ReferenceId, List<ReferenceId>> Links { get; }

        public HashSet<ReferenceId> DeviceLinked { get; }

        public static Graph Of(IReadOnlyList<PieceModel> candidates, IReadOnlyList<PieceModel> others, HashSet<ReferenceId> devices)
        {
            var models = new List<PieceModel>(candidates);
            models.AddRange(others);
            var byCell = new Dictionary<Cell, List<PieceModel>>();
            var links = new Dictionary<ReferenceId, List<ReferenceId>>();
            foreach (var model in models)
            {
                if (!devices.Contains(model.Id))
                    links[model.Id] = [];
                foreach (var cell in model.Cells)
                {
                    if (!byCell.TryGetValue(cell, out var list))
                        byCell[cell] = list = new List<PieceModel>(2);
                    list.Add(model);
                }
            }

            var deviceLinked = new HashSet<ReferenceId>();
            var seen = new HashSet<(ReferenceId, ReferenceId)>();
            foreach (var model in models)
                foreach (var end in model.Ends)
                {
                    if (!byCell.TryGetValue(end.Into, out var there))
                        continue;
                    foreach (var other in there)
                        if (other.Id != model.Id && Connectivity.Links(model, other))
                            Join(model.Id, other.Id, devices, links, deviceLinked, seen);
                }

            return new Graph(links, deviceLinked);
        }

        private static void Join(
            ReferenceId a,
            ReferenceId b,
            HashSet<ReferenceId> devices,
            Dictionary<ReferenceId, List<ReferenceId>> links,
            HashSet<ReferenceId> deviceLinked,
            HashSet<(ReferenceId, ReferenceId)> seen)
        {
            if (devices.Contains(a) || devices.Contains(b))
            {
                if (!devices.Contains(a))
                    deviceLinked.Add(a);
                if (!devices.Contains(b))
                    deviceLinked.Add(b);
                return;
            }

            var key = a.Value < b.Value ? (a, b) : (b, a);
            if (seen.Add(key))
            {
                links[a].Add(b);
                links[b].Add(a);
            }
        }
    }
}
