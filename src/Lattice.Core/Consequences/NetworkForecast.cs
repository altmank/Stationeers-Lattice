using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Consequences;

/// <summary>
/// A node of an edit's graph: a piece, or a whole network the edit takes nothing from. Ordered by value, then kind.
/// </summary>
internal readonly record struct ForecastNode
{
    private ForecastNode(bool isNetwork, long value)
    {
        IsNetwork = isNetwork;
        Value = value;
    }

    /// <summary>The node stands for a whole network.</summary>
    public bool IsNetwork { get; }

    /// <summary>The piece's or network's id value.</summary>
    public long Value { get; }

    /// <summary>A piece.</summary>
    public static ForecastNode Piece(ReferenceId piece) => new(false, piece.Value);

    /// <summary>A whole network.</summary>
    public static ForecastNode Whole(NetworkRef network) => new(true, network.Value);

    /// <summary>The piece this node is; none for a whole network.</summary>
    public Option<ReferenceId> AsPiece => IsNetwork ? Option<ReferenceId>.None : Option.Some(new ReferenceId(Value));

    /// <summary>The order of nodes: by value, a piece before a network of the same value.</summary>
    public static int Order(ForecastNode a, ForecastNode b) =>
        a.Value != b.Value ? a.Value.CompareTo(b.Value) : a.IsNetwork.CompareTo(b.IsNetwork);

    /// <inheritdoc />
    public override string ToString() => IsNetwork ? $"network {Value}" : $"piece {Value}";
}

/// <summary>A device port as it is before an edit and what it is joined to after.</summary>
/// <param name="Device">The device.</param>
/// <param name="Index">The port's index on the device.</param>
/// <param name="Power">It carries power (bridges count only power ports).</param>
/// <param name="NetworkBefore">The network the port is on now; none when nothing is joined to it.</param>
/// <param name="AttachedAfter">The node joined to it after the edit; none for none.</param>
internal sealed record ForecastPort(ReferenceId Device, int Index, bool Power, Option<NetworkRef> NetworkBefore, Option<ForecastNode> AttachedAfter);

/// <summary>
/// An edit to one family's networks as a graph after the edit. A network the edit takes nothing from stays whole, so
/// it is one node; a network the edit takes pieces from is modelled piece by piece, each remaining piece a node under
/// that network, so a split shows. New pieces are nodes of no network; a new piece that takes over a removed piece's
/// cells (a split long straight's singles) is still new, but carries that piece's network on: the network is held
/// where its successors are, and is not gone while they stand. Links join nodes; device ports hang on the node they
/// are joined to after the edit, and a device never joins its own ports (an APC's input and output stay two networks
/// unless the cables join them). A builder: fill it, then forecast.
/// </summary>
internal sealed class NetworkEdit
{
    /// <summary>Every node, with the network it holds pieces of (none for a new piece).</summary>
    public Dictionary<ForecastNode, Option<NetworkRef>> Nodes { get; } = [];

    /// <summary>Networks the edit takes nothing from.</summary>
    public HashSet<NetworkRef> WholeNetworks { get; } = [];

    /// <summary>Links between nodes.</summary>
    public List<(ForecastNode A, ForecastNode B)> Links { get; } = [];

    /// <summary>Device ports and what they hang on.</summary>
    public List<ForecastPort> Ports { get; } = [];

    /// <summary>Removed pieces, with the network each was in.</summary>
    public Dictionary<ReferenceId, NetworkRef> Removed { get; } = [];

    /// <summary>New pieces that carry a removed piece's network on, with that network.</summary>
    public Dictionary<ReferenceId, NetworkRef> Successors { get; } = [];

    /// <summary>A network the edit takes nothing from.</summary>
    public void AddNetwork(NetworkRef network)
    {
        WholeNetworks.Add(network);
        Nodes[ForecastNode.Whole(network)] = Option.Some(network);
    }

    /// <summary>A remaining piece of a network.</summary>
    public void AddPiece(ReferenceId piece, NetworkRef network) => Nodes[ForecastNode.Piece(piece)] = Option.Some(network);

    /// <summary>A new piece of no network.</summary>
    public void AddNewPiece(ReferenceId piece) => Nodes[ForecastNode.Piece(piece)] = Option<NetworkRef>.None;

    /// <summary>A new piece standing where a removed piece of the network stood (a split long's single).</summary>
    public void AddSuccessor(ReferenceId piece, NetworkRef network)
    {
        Nodes[ForecastNode.Piece(piece)] = Option<NetworkRef>.None;
        Successors[piece] = network;
    }

    /// <summary>Joins two nodes.</summary>
    public void Link(ForecastNode a, ForecastNode b) => Links.Add((a, b));

    /// <summary>A piece the edit removes from its network.</summary>
    public void Remove(ReferenceId piece, NetworkRef network) => Removed[piece] = network;
}

/// <summary>One network after the edit: the networks it holds pieces of, its new pieces, its ports.</summary>
internal sealed class ForecastNetwork(int index)
{
    /// <summary>A number for this forecast only; the game gives merged and split networks their ids itself.</summary>
    public int Index { get; } = index;

    /// <summary>The networks before the edit it holds (all or part of), sorted.</summary>
    public List<NetworkRef> NetworksBefore { get; } = [];

    /// <summary>Its new pieces (nodes of no network).</summary>
    public List<ReferenceId> NewPieces { get; } = [];

    /// <summary>The device ports joined to it.</summary>
    public List<ForecastPort> Ports { get; } = [];
}

/// <summary>A network before the edit whose remaining pieces fall into more than one network after it.</summary>
/// <param name="Network">The network.</param>
/// <param name="Parts">The <see cref="ForecastNetwork.Index"/> of each part, sorted.</param>
internal sealed record ForecastSplit(NetworkRef Network, IReadOnlyList<int> Parts);

/// <summary>A device whose power ports end on one network after the edit though they were not before.</summary>
internal sealed record ForecastBridge(ReferenceId Device, IReadOnlyList<ForecastPort> Ports, int Network);

/// <summary>The networks an edit leaves, and every merge, split, device bridge and cut port it makes.</summary>
internal sealed class Forecast
{
    /// <summary>Every network after the edit, by index.</summary>
    public List<ForecastNetwork> Networks { get; } = [];

    /// <summary>Networks after the edit holding pieces of two or more networks before it.</summary>
    public List<ForecastNetwork> Merges { get; } = [];

    /// <summary>Networks before the edit falling into several.</summary>
    public List<ForecastSplit> Splits { get; } = [];

    /// <summary>Devices bridged through their own ports.</summary>
    public List<ForecastBridge> Bridges { get; } = [];

    /// <summary>Ports joined to a network before the edit and to nothing after it.</summary>
    public List<ForecastPort> Cut { get; } = [];

    /// <summary>Networks before the edit with every piece removed and no new piece carrying them on.</summary>
    public List<NetworkRef> Gone { get; } = [];

    /// <summary>
    /// Networks before the edit with every piece removed that new pieces carry on: the network lives on in them, so
    /// what it holds must be handed over to them.
    /// </summary>
    public List<NetworkRef> Carried { get; } = [];

    /// <summary>
    /// Whether the port is one of a device bridge's: the edit puts it on one network with another port of its device
    /// that it was not on one network with before. A port whose network the edit leaves as it was never is.
    /// </summary>
    public bool IsBridging(ForecastPort port) => Bridges.Exists(bridge => Contains(bridge.Ports, port));

    /// <summary>The network after the edit that holds the node.</summary>
    public Option<ForecastNetwork> NetworkOf(ForecastNode node, Dictionary<ForecastNode, int> componentOf) =>
        componentOf.TryGetValue(node, out var index) ? Option.Some(Networks[index]) : Option<ForecastNetwork>.None;

    private static bool Contains(IReadOnlyList<ForecastPort> ports, ForecastPort port)
    {
        for (var index = 0; index < ports.Count; index++)
            if (ReferenceEquals(ports[index], port))
                return true;
        return false;
    }
}

/// <summary>
/// The networks after an edit, on plain values: the connected parts of the edit's graph (union-find over its links),
/// then which parts hold pieces of several networks (a merge), which networks' remaining pieces fall into several
/// parts (a split), which devices get two power ports on one part (a bridge through the device: both sides of an APC
/// or transformer, a battery's input and output), and which ports lose everything they were joined to.
/// </summary>
internal static class NetworkForecaster
{
    /// <summary>The forecast, and the network index each node falls into. Near linear in nodes and links.</summary>
    public static Forecast Of(NetworkEdit edit, out Dictionary<ForecastNode, int> componentOf)
    {
        var parent = new Dictionary<ForecastNode, ForecastNode>();
        foreach (var node in edit.Nodes.Keys)
            parent[node] = node;
        foreach (var (a, b) in edit.Links)
            if (parent.ContainsKey(a) && parent.ContainsKey(b))
                Union(parent, a, b);

        var forecast = new Forecast();
        componentOf = [];
        var byRoot = new Dictionary<ForecastNode, int>();
        var ordered = new List<ForecastNode>(edit.Nodes.Keys);
        ordered.Sort(static (a, b) => ForecastNode.Order(a, b));
        foreach (var node in ordered)
        {
            var root = Find(parent, node);
            if (!byRoot.TryGetValue(root, out var index))
            {
                index = forecast.Networks.Count;
                byRoot[root] = index;
                forecast.Networks.Add(new ForecastNetwork(index));
            }

            componentOf[node] = index;
            var network = forecast.Networks[index];
            var before = edit.Nodes[node];
            if (before.IsNone && node.AsPiece.TryGet(out var piece))
            {
                network.NewPieces.Add(piece);
                before = edit.Successors.TryGetValue(piece, out var carried) ? Option.Some(carried) : Option<NetworkRef>.None;
            }

            if (before.TryGet(out var held) && !network.NetworksBefore.Contains(held))
                network.NetworksBefore.Add(held);
        }

        foreach (var network in forecast.Networks)
        {
            network.NetworksBefore.SortIds();
            if (network.NetworksBefore.Count > 1)
                forecast.Merges.Add(network);
        }

        Splits(edit, forecast, componentOf);
        Ports(edit, forecast, componentOf);
        return forecast;
    }

    // Each modelled network's remaining pieces and the new pieces carrying it on, by the parts they fall into: more
    // than one part is a split, none is gone, successors only is carried.
    private static void Splits(NetworkEdit edit, Forecast forecast, Dictionary<ForecastNode, int> componentOf)
    {
        var parts = new Dictionary<NetworkRef, List<int>>();
        var kept = new HashSet<NetworkRef>();
        foreach (var node in edit.Nodes)
        {
            if (node.Key.IsNetwork)
                continue;
            NetworkRef network;
            if (node.Value.TryGet(out var held))
            {
                network = held;
                kept.Add(network);
            }
            else if (!node.Key.AsPiece.TryGet(out var piece) || !edit.Successors.TryGetValue(piece, out network))
            {
                continue;
            }

            if (!parts.TryGetValue(network, out var list))
                parts[network] = list = [];
            var part = componentOf[node.Key];
            if (!list.Contains(part))
                list.Add(part);
        }

        var networks = new List<NetworkRef>(parts.Keys);
        networks.SortIds();
        foreach (var network in networks)
        {
            if (parts[network].Count <= 1)
                continue;
            parts[network].Sort();
            forecast.Splits.Add(new ForecastSplit(network, parts[network]));
        }

        var gone = new HashSet<NetworkRef>();
        var carriedOn = new HashSet<NetworkRef>();
        foreach (var removed in edit.Removed)
        {
            if (edit.WholeNetworks.Contains(removed.Value))
                continue;
            if (!parts.ContainsKey(removed.Value))
                gone.Add(removed.Value);
            else if (!kept.Contains(removed.Value))
                carriedOn.Add(removed.Value);
        }

        forecast.Gone.AddRange(gone);
        forecast.Gone.SortIds();
        forecast.Carried.AddRange(carriedOn);
        forecast.Carried.SortIds();
    }

    private static void Ports(NetworkEdit edit, Forecast forecast, Dictionary<ForecastNode, int> componentOf)
    {
        var byDevice = new Dictionary<ReferenceId, List<ForecastPort>>();
        foreach (var port in edit.Ports)
        {
            if (port.AttachedAfter.TryGet(out var node) && componentOf.TryGetValue(node, out var index))
                forecast.Networks[index].Ports.Add(port);
            else if (port.NetworkBefore.IsSome)
                forecast.Cut.Add(port);
            if (!byDevice.TryGetValue(port.Device, out var list))
                byDevice[port.Device] = list = [];
            list.Add(port);
        }

        var devices = new List<ReferenceId>(byDevice.Keys);
        devices.SortIds();
        foreach (var device in devices)
            Bridge(forecast, device, byDevice[device], componentOf);
    }

    // Two power ports on one network after the edit that were not on one network before.
    private static void Bridge(Forecast forecast, ReferenceId device, List<ForecastPort> ports, Dictionary<ForecastNode, int> componentOf)
    {
        var byNetwork = new Dictionary<int, List<ForecastPort>>();
        foreach (var port in ports)
        {
            if (!port.Power || !port.AttachedAfter.TryGet(out var node) || !componentOf.TryGetValue(node, out var index))
                continue;
            if (!byNetwork.TryGetValue(index, out var list))
                byNetwork[index] = list = [];
            list.Add(port);
        }

        foreach (var entry in byNetwork)
            if (entry.Value.Count > 1 && !AllOnOneNetworkBefore(entry.Value))
                forecast.Bridges.Add(new ForecastBridge(device, entry.Value, entry.Key));
    }

    private static bool AllOnOneNetworkBefore(List<ForecastPort> ports)
    {
        if (!ports[0].NetworkBefore.TryGet(out var first))
            return false;
        foreach (var port in ports)
            if (!port.NetworkBefore.TryGet(out var network) || network != first)
                return false;
        return true;
    }

    private static ForecastNode Find(Dictionary<ForecastNode, ForecastNode> parent, ForecastNode node)
    {
        var root = node;
        while (parent[root] != root)
            root = parent[root];
        while (parent[node] != root)
        {
            var next = parent[node];
            parent[node] = root;
            node = next;
        }

        return root;
    }

    private static void Union(Dictionary<ForecastNode, ForecastNode> parent, ForecastNode a, ForecastNode b)
    {
        var rootA = Find(parent, a);
        var rootB = Find(parent, b);
        if (rootA == rootB)
            return;
        var aFirst = ForecastNode.Order(rootA, rootB) < 0;
        parent[aFirst ? rootB : rootA] = aFirst ? rootA : rootB;
    }
}

/// <summary>What a device port is to the power tick: the whole device's load, or one side of an input/output device.</summary>
internal enum PowerSide
{
    /// <summary>A device with no input/output sides: <c>PowerTick</c> counts its load once on its power cable's network.</summary>
    Device = 1,

    /// <summary>An input/output device's <c>InputConnection</c>: <c>GetUsedPower</c> answers for its <c>InputNetwork</c> only.</summary>
    Input = 2,

    /// <summary>An input/output device's <c>OutputConnection</c>: <c>GetGeneratedPower</c> answers for its <c>OutputNetwork</c> only.</summary>
    Output = 3,
}

/// <summary>
/// Which side of an input/output device a port is. The game bridge compares the port's <c>Transform</c> (or, without
/// one, its <c>Collider</c>) with the device's <c>OutputConnection</c> and <c>InputConnection</c>: Unity deserializes
/// that field and the <c>OpenEnds</c> entry as separate copies, so reference equality between them is always false,
/// but they share their components (<c>ElectricalInputOutput.GetPassiveTooltip</c> tells ports apart by collider).
/// Without either, the port's connection role decides.
/// </summary>
internal static class PortSides
{
    /// <summary><c>NetworkType.Power</c>: set on Power and PowerAndData ports, clear on Data-only ones.</summary>
    public static NetworkTypes PowerFlag => NetworkTypes.Power;

    /// <param name="sameTransform">Both have a Transform: whether it is the same one; none when either lacks one.</param>
    /// <param name="sameCollider">Both have a Collider: whether it is the same one; none when either lacks one.</param>
    /// <param name="role">The port's connection role.</param>
    public static bool IsOutput(Option<bool> sameTransform, Option<bool> sameCollider, ConnectionRole role) =>
        sameTransform.TryGet(out var transform) ? transform : sameCollider.TryGet(out var collider) ? collider : role.LetsOut;

    /// <summary>The same test against the device's <c>InputConnection</c> (<c>Input</c> or <c>Input2</c> without shared components).</summary>
    public static bool IsInput(Option<bool> sameTransform, Option<bool> sameCollider, ConnectionRole role) =>
        sameTransform.TryGet(out var transform) ? transform : sameCollider.TryGet(out var collider) ? collider : role.TakesIn;

    /// <summary>
    /// The side a port moves power on, by the game's own connection roles; none for a port that moves none. A
    /// Data-only port carries no power (a device finds its power cable among Power-flagged ends only). An input/output
    /// device moves power only through its input and output connections, so any other port of it moves none; the
    /// output wins when a port is both.
    /// </summary>
    /// <param name="types">The port's network types.</param>
    /// <param name="inputOutput">The device is an input/output device.</param>
    /// <param name="output">The port is its output connection (<see cref="IsOutput"/>).</param>
    /// <param name="input">The port is its input connection (<see cref="IsInput"/>).</param>
    public static Option<PowerSide> PowerOf(NetworkTypes types, bool inputOutput, bool output, bool input) =>
        !types.Shares(PowerFlag) ? Option<PowerSide>.None
        : !inputOutput ? Option.Some(PowerSide.Device)
        : output ? Option.Some(PowerSide.Output)
        : input ? Option.Some(PowerSide.Input)
        : Option<PowerSide>.None;
}
