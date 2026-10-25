using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;

namespace Lattice.Core.Consequences;

/// <summary>A network a split leaves: its forecast index, its device ports and whether a root is on it.</summary>
internal sealed record SplitPart(int Index, IReadOnlyList<ForecastPort> Ports, bool HoldsRoot);

/// <summary>
/// A network an edit splits, part by part, with the devices cut off from its root: the devices of every part that
/// holds no root, and every port of the network left joined to nothing.
/// </summary>
/// <param name="Network">The network before the edit (for cut ports, the network every port of the entry was on).</param>
/// <param name="Parts">The parts, for a split; empty for cut ports.</param>
/// <param name="Roots">The root devices found on the network, sorted.</param>
/// <param name="CutOff">Devices cut off from every root, sorted; none when the network has no root (nothing to be cut off from).</param>
internal sealed record SplitDetail(NetworkRef Network, IReadOnlyList<SplitPart> Parts, IReadOnlyList<ReferenceId> Roots, Option<IReadOnlyList<ReferenceId>> CutOff);

/// <summary>
/// What a split is measured against: devices named as the root (every port of theirs counts), and supplier ports, a
/// device with the network it feeds (an APC's or battery's output network, a generator's). A battery's input port is
/// not a root of the network that charges it, though the battery feeds another network. A builder: fill, then read.
/// </summary>
internal sealed class NetworkRootSet
{
    private readonly HashSet<ReferenceId> _named = [];
    private readonly HashSet<(ReferenceId Device, NetworkRef Network)> _feeds = [];

    /// <summary>No root named and no feed known yet.</summary>
    public static NetworkRootSet None => new();

    /// <summary>Whether the caller named the root (a network with none on it is then not the named root's).</summary>
    public bool IsNamed => _named.Count > 0;

    /// <summary>The devices named as the root.</summary>
    public static NetworkRootSet Named(params ReferenceId[] devices)
    {
        var roots = new NetworkRootSet();
        roots._named.UnionWith(devices);
        return roots;
    }

    /// <summary>The device feeds the network.</summary>
    public void AddFeed(ReferenceId device, NetworkRef network) => _feeds.Add((device, network));

    /// <summary>Whether the port is a root's: its device was named, or it is on a network its device feeds.</summary>
    public bool Holds(ForecastPort port) => Holds(port.Device, port.NetworkBefore);

    /// <summary>Whether the device is a root of the network.</summary>
    public bool Holds(ReferenceId device, Option<NetworkRef> network) =>
        _named.Contains(device) || (network.TryGet(out var on) && _feeds.Contains((device, on)));
}

/// <summary>
/// Which devices a split cuts off from the network's root. A root is a device that feeds the network: the caller's
/// root, or every supplier the game side finds (an APC's, transformer's or battery's output, a generator, a solar
/// panel), each only on the network it feeds. A device is cut off when none of its ports on the network is on a part
/// that holds a root after the edit; when a root's own port is cut and the network stays one piece, every device left on
/// it is cut off.
/// </summary>
internal static class SplitAnalysis
{
    /// <summary>One detail per split, then one per network that has cut ports (in network order).</summary>
    public static List<SplitDetail> Of(Forecast forecast, NetworkRootSet roots)
    {
        var details = new List<SplitDetail>(forecast.Splits.Count + 1);
        foreach (var split in forecast.Splits)
            details.Add(OfSplit(forecast, split, roots));
        details.AddRange(OfCutPorts(forecast, roots));
        return details;
    }

    /// <summary>The cut-port entry for the port's network (<see cref="Of"/> lists the splits first, then one per network).</summary>
    public static Option<SplitDetail> CutEntryOf(List<SplitDetail> details, int splits, ForecastPort port)
    {
        var network = port.NetworkBefore.Reduce(default(NetworkRef));
        for (var index = splits; index < details.Count; index++)
            if (details[index].Network == network)
                return Option.Some(details[index]);
        return Option<SplitDetail>.None;
    }

    /// <summary>
    /// The detail as reason facts: <c>parts</c> (each part's devices, the root's marked), <c>cut-off</c> (devices cut
    /// off from the root), or <c>root</c> (<c>none-on-it</c>, or <c>named-not-on-it</c> when a root was named).
    /// </summary>
    public static ValueMap<string, string> Facts(SplitDetail detail, bool rootNamed = false)
    {
        var facts = ValueMap<string, string>.Empty;
        if (detail.Parts.Count > 0)
        {
            var parts = new List<string>(detail.Parts.Count);
            foreach (var part in detail.Parts)
            {
                var devices = new List<ReferenceId>();
                foreach (var port in part.Ports)
                    if (!devices.Contains(port.Device))
                        devices.Add(port.Device);
                devices.SortIds();
                parts.Add($"[{IdOrder.Text(devices)}]{(part.HoldsRoot ? " (root)" : string.Empty)}");
            }

            facts = facts.With("parts", string.Join("; ", parts));
        }

        return detail.CutOff.TryGet(out var cutOff)
            ? facts.With("cut-off", IdOrder.Text(cutOff))
            : facts.With("root", rootNamed ? "named-not-on-it" : "none-on-it");
    }

    private static SplitDetail OfSplit(Forecast forecast, ForecastSplit split, NetworkRootSet roots)
    {
        var network = Option.Some(split.Network);
        var parts = new List<SplitPart>(split.Parts.Count);
        var present = new HashSet<ReferenceId>();
        var rooted = new HashSet<ReferenceId>();
        foreach (var index in split.Parts)
        {
            var after = forecast.Networks[index];
            var holdsRoot = false;
            foreach (var port in after.Ports)
                if (roots.Holds(port.Device, network))
                {
                    holdsRoot = true;
                    present.Add(port.Device);
                }

            if (holdsRoot)
                foreach (var port in after.Ports)
                    rooted.Add(port.Device);
            parts.Add(new SplitPart(index, new List<ForecastPort>(after.Ports), holdsRoot));
        }

        foreach (var port in forecast.Cut)
            if (port.NetworkBefore == network && roots.Holds(port))
                present.Add(port.Device);

        var found = IdOrder.Sorted(present);
        if (found.Count == 0)
            return new SplitDetail(split.Network, parts, found, Option<IReadOnlyList<ReferenceId>>.None);

        var cut = new HashSet<ReferenceId>();
        foreach (var part in parts)
        {
            if (part.HoldsRoot)
                continue;
            foreach (var port in part.Ports)
                if (port.NetworkBefore == network && !rooted.Contains(port.Device) && !roots.Holds(port.Device, network))
                    cut.Add(port.Device);
        }

        foreach (var port in forecast.Cut)
            if (port.NetworkBefore == network && !rooted.Contains(port.Device) && !roots.Holds(port.Device, network))
                cut.Add(port.Device);

        return new SplitDetail(split.Network, parts, found, Option.Some<IReadOnlyList<ReferenceId>>(IdOrder.Sorted(cut)));
    }

    // Ports joined to nothing after the edit, one entry per network they were on (in id order): every device among
    // them is cut off, unless it is a root itself or keeps another port on a network. A root port cut from a network
    // that stays whole cuts off every device left on it that no other root feeds. Roots are the network's own: those
    // among its cut ports and those its ports keep after the edit; with none, nothing is called cut off.
    private static List<SplitDetail> OfCutPorts(Forecast forecast, NetworkRootSet roots)
    {
        var stillJoined = new HashSet<ReferenceId>();
        foreach (var network in forecast.Networks)
            foreach (var port in network.Ports)
                stillJoined.Add(port.Device);

        var byNetwork = new Dictionary<NetworkRef, List<ForecastPort>>();
        foreach (var port in forecast.Cut)
        {
            var network = port.NetworkBefore.Reduce(default(NetworkRef));
            if (!byNetwork.TryGetValue(network, out var ports))
                byNetwork[network] = ports = [];
            ports.Add(port);
        }

        var order = new List<NetworkRef>(byNetwork.Keys);
        order.SortIds();
        var details = new List<SplitDetail>(order.Count);
        foreach (var network in order)
            details.Add(OfCutNetwork(forecast, network, byNetwork[network], stillJoined, roots));
        return details;
    }

    private static SplitDetail OfCutNetwork(Forecast forecast, NetworkRef network, List<ForecastPort> ports, HashSet<ReferenceId> stillJoined, NetworkRootSet roots)
    {
        var on = Option.Some(network);
        var cut = new HashSet<ReferenceId>();
        var present = new HashSet<ReferenceId>();
        var rootCut = false;
        foreach (var port in ports)
        {
            if (roots.Holds(port))
            {
                present.Add(port.Device);
                rootCut = true;
            }
            else if (!stillJoined.Contains(port.Device))
            {
                cut.Add(port.Device);
            }
        }

        foreach (var after in forecast.Networks)
            foreach (var port in after.Ports)
                if (port.NetworkBefore == on && roots.Holds(port.Device, on))
                    present.Add(port.Device);

        if (rootCut && !forecast.Splits.Exists(split => split.Network == network))
            cut.UnionWith(LeftWithoutRoot(forecast, network, roots));
        cut.ExceptWith(present);
        return new SplitDetail(
            network,
            [],
            IdOrder.Sorted(present),
            present.Count > 0 ? Option.Some<IReadOnlyList<ReferenceId>>(IdOrder.Sorted(cut)) : Option<IReadOnlyList<ReferenceId>>.None);
    }

    // The devices whose ports stay on what is left of the network when no port left on it is a root's.
    private static List<ReferenceId> LeftWithoutRoot(Forecast forecast, NetworkRef network, NetworkRootSet roots)
    {
        var on = Option.Some(network);
        var devices = new List<ReferenceId>();
        foreach (var after in forecast.Networks)
        {
            if (!after.NetworksBefore.Contains(network) || after.Ports.Exists(port => roots.Holds(port.Device, on)))
                continue;
            foreach (var port in after.Ports)
                if (port.NetworkBefore == on)
                    devices.Add(port.Device);
        }

        return devices;
    }

    /// <summary>A port index as fact text.</summary>
    internal static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
