using System;
using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;

namespace Lattice.Core.Consequences;

/// <summary>What the caller accepts an edit doing to networks: merges of named networks, bridges through named devices, splits, moving contents.</summary>
/// <param name="BridgeNetworks">Networks the caller names as meant to be joined.</param>
/// <param name="BridgeDevices">Devices the caller names as meant to have their sides joined.</param>
/// <param name="Split">Networks may fall apart and ports lose their connection.</param>
/// <param name="Contents">A network holding gas may split, the game dividing it among the parts.</param>
internal sealed record EditAllowance(IReadOnlyCollection<NetworkRef> BridgeNetworks, IReadOnlyCollection<ReferenceId> BridgeDevices, bool Split, bool Contents = false)
{
    /// <summary>Nothing accepted.</summary>
    public static EditAllowance Nothing { get; } = new(Array.Empty<NetworkRef>(), Array.Empty<ReferenceId>(), false);
}

/// <summary>
/// A removal that splits a network holding contents (<see cref="ReasonCodes.ContentsWouldMove"/>) is a warning,
/// not a problem, when the caller accepts contents moving: the game divides the contents among the parts by volume.
/// Emptying a network is never covered: the game deletes the contents with the last pipe.
/// </summary>
internal static class ContentsGuard
{
    /// <summary>Whether the reason is only a warning under the allowance.</summary>
    public static bool Warns(Reason removal, EditAllowance allow) => allow.Contents && removal.Code == ReasonCodes.ContentsWouldMove;
}

/// <summary>A network's power numbers before an edit (<c>CableNetwork.PotentialLoad</c>, <c>RequiredLoad</c>, weakest ratings).</summary>
internal sealed record NetworkPower(double PotentialW, double RequiredW, Option<double> LowestCableW, Option<double> LowestFuseW);

/// <summary>
/// What one device port supplies to and draws from its network (the power tick's per-device numbers), and the side of
/// the device it moves them on: the power tick asks each device once per network, so two ports of one side on one
/// network count once.
/// </summary>
internal sealed record PortPower(double PotentialW, double RequiredW, PowerSide Side);

/// <summary>A network after an edit as power sees it: the supply and demand it pools and its weakest cable.</summary>
internal sealed record PowerAfter(double PotentialW, double RequiredW, Option<double> LowestCableW, Option<double> LowestFuseW)
{
    /// <summary>
    /// What would flow: min(potential, required), the power the devices draw. A split network's numbers are counted
    /// whole in each part, so this is an upper bound there.
    /// </summary>
    public double FlowW => Math.Min(PotentialW, RequiredW);

    /// <summary>The flow is above the weakest cable: the power tick breaks a fuse or burns a cable each tick.</summary>
    public bool Overloads => LowestCableW.TryGet(out var cable) && FlowW > cable;

    /// <summary>
    /// The pooled numbers of the networks before it holds, with the weakest of their cables and of the new pieces, plus
    /// what its ports bring from outside those networks: a port whose network the edit removes whole brings that
    /// network's numbers (a reroute replacing every piece keeps its devices' load), and a port on no network now, or on
    /// one that stays apart, brings its device's own estimate (<paramref name="joining"/>). Without them a network of new
    /// pieces only would forecast 0 W however much its devices move. <paramref name="dormant"/>: what each port adds on
    /// top once its device, off now, is switched on, counted for every port of the network after, those of the
    /// networks pooled too; none for the network as it is. <paramref name="split"/>: networks before that fall into
    /// several parts; each part counts only its own ports (joining, as a port from outside would) instead of the whole
    /// network's numbers, keeping that network's weakest cable; none counts every part whole (an upper bound). A port
    /// with no power role brings nothing; a device side is counted once however many of its ports are on the network,
    /// and not at all when the pooled numbers already hold it. Linear in the network's ports and pieces.
    /// </summary>
    public static PowerAfter Of(
        ForecastNetwork network,
        IReadOnlyDictionary<NetworkRef, NetworkPower> before,
        IReadOnlyDictionary<ReferenceId, double> newRatings,
        ICollection<NetworkRef>? gone = null,
        Func<ForecastPort, Option<PortPower>>? joining = null,
        Func<ForecastPort, Option<PortPower>>? dormant = null,
        ICollection<NetworkRef>? split = null)
    {
        var potential = 0.0;
        var required = 0.0;
        var cable = Option<double>.None;
        var fuse = Option<double>.None;
        foreach (var id in network.NetworksBefore)
        {
            if (!before.TryGetValue(id, out var power))
                continue;
            var parted = split is not null && split.Contains(id);
            potential += parted ? 0.0 : power.PotentialW;
            required += parted ? 0.0 : power.RequiredW;
            cable = Lowest(cable, power.LowestCableW);
            fuse = Lowest(fuse, power.LowestFuseW);
        }

        var pooledGone = new HashSet<NetworkRef>();
        var counted = new HashSet<(ReferenceId, PowerSide)>();
        var woken = new HashSet<(ReferenceId, PowerSide)>();
        var outside = new List<ForecastPort>();
        foreach (var port in network.Ports)
        {
            if (dormant is not null && dormant(port).TryGet(out var extra) && woken.Add((port.Device, extra.Side)))
            {
                potential += extra.PotentialW;
                required += extra.RequiredW;
            }

            var hasWas = port.NetworkBefore.TryGet(out var was);
            var ownPart = hasWas && split is not null && split.Contains(was);
            if (hasWas && network.NetworksBefore.Contains(was) && !ownPart)
            {
                Held(port, joining, counted);
                continue;
            }

            if (hasWas && gone is not null && gone.Contains(was))
            {
                if (pooledGone.Add(was) && before.TryGetValue(was, out var old))
                {
                    potential += old.PotentialW;
                    required += old.RequiredW;
                }

                Held(port, joining, counted);
                continue;
            }

            outside.Add(port);
        }

        foreach (var port in outside)
            if (joining is not null && joining(port).TryGet(out var own) && counted.Add((port.Device, own.Side)))
            {
                potential += own.PotentialW;
                required += own.RequiredW;
            }

        foreach (var piece in network.NewPieces)
            if (newRatings.TryGetValue(piece, out var rating))
                cable = Lowest(cable, Option.Some(rating));

        return new PowerAfter(potential, required, cable, fuse);
    }

    /// <summary>
    /// The would-overload-when-on warning: the edit is safe as the devices stand, but switching on the ones now off
    /// would burn a cable. <paramref name="offDevices"/>: the devices off now with a power role on the network after.
    /// None when nothing overloads or no device is off: with every device on there is nothing to switch on.
    /// </summary>
    public static Option<Reason> WhenOnWarning(PowerAfter whenOn, IReadOnlyCollection<ReferenceId> offDevices)
    {
        if (!whenOn.Overloads || offDevices.Count == 0)
            return Option<Reason>.None;
        var off = IdOrder.Sorted(offDevices);
        var facts = ValueMap<string, string>.Of("off-devices", IdOrder.Text(off))
            .With("device", Text(off[0].Value))
            .With("flow-w", Text(whenOn.FlowW))
            .With("potential-w", Text(whenOn.PotentialW))
            .With("required-w", Text(whenOn.RequiredW))
            .With("rating-w", Text(whenOn.LowestCableW.Reduce(0.0)));
        return Option.Some(ConsequenceCodes.Of(ReasonCodes.WouldOverloadWhenOn, facts));
    }

    // A port the pooled numbers already hold: its device side is in them, so no other port brings it again.
    private static void Held(ForecastPort port, Func<ForecastPort, Option<PortPower>>? joining, HashSet<(ReferenceId, PowerSide)> counted)
    {
        if (joining is not null && joining(port).TryGet(out var held))
            counted.Add((port.Device, held.Side));
    }

    private static Option<double> Lowest(Option<double> a, Option<double> b) =>
        !a.TryGet(out var left) ? b : !b.TryGet(out var right) ? a : Option.Some(Math.Min(left, right));

    private static string Text(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The guards on a forecast: a merge of networks or a device bridge the caller did not name is would-bridge; a split,
/// a cut port or a removed whole network with devices, without <see cref="EditAllowance.Split"/>, is would-split.
/// Lattice reports these; whether one refuses an edit is the caller's choice.
/// </summary>
internal static class EditGuards
{
    /// <summary>
    /// The guards' reasons. <paramref name="roots"/>: the devices that feed the networks; each would-split names every
    /// part's devices and those cut off from a root. Linear in the forecast.
    /// </summary>
    public static List<Reason> Check(Forecast forecast, EditAllowance allow, NetworkRootSet? roots = null)
    {
        var problems = new List<Reason>();
        foreach (var merge in forecast.Merges)
            if (!AllAllowed(merge.NetworksBefore, allow.BridgeNetworks))
                problems.Add(ConsequenceCodes.Of(ReasonCodes.WouldBridge, ValueMap<string, string>.Of("networks", IdOrder.Text(merge.NetworksBefore))));

        foreach (var bridge in forecast.Bridges)
        {
            if (Contains(allow.BridgeDevices, bridge.Device))
                continue;
            var ports = new List<string>(bridge.Ports.Count);
            foreach (var port in bridge.Ports)
                ports.Add(SplitAnalysis.Text(port.Index));
            problems.Add(ConsequenceCodes.Of(ReasonCodes.WouldBridge, ValueMap<string, string>
                .Of("device", IdOrder.Text([bridge.Device]))
                .With("ports", string.Join(", ", ports))));
        }

        if (allow.Split)
            return problems;

        var rootSet = roots ?? NetworkRootSet.None;
        var details = SplitAnalysis.Of(forecast, rootSet);
        for (var index = 0; index < forecast.Splits.Count; index++)
        {
            var split = forecast.Splits[index];
            var facts = SplitAnalysis.Facts(details[index], rootSet.IsNamed)
                .With("network", IdOrder.Text([split.Network]))
                .With("into", SplitAnalysis.Text(split.Parts.Count));
            problems.Add(ConsequenceCodes.Of(ReasonCodes.WouldSplit, facts));
        }

        foreach (var port in forecast.Cut)
        {
            var facts = ValueMap<string, string>
                .Of("device", IdOrder.Text([port.Device]))
                .With("port", SplitAnalysis.Text(port.Index))
                .With("network", port.NetworkBefore.TryGet(out var network) ? IdOrder.Text([network]) : string.Empty);
            if (SplitAnalysis.CutEntryOf(details, forecast.Splits.Count, port).TryGet(out var entry)
                && entry.CutOff.TryGet(out var cutOff) && cutOff.Count > 0)
                facts = facts.With("cut-off", IdOrder.Text(cutOff));
            problems.Add(ConsequenceCodes.Of(ReasonCodes.WouldSplit, facts));
        }

        return problems;
    }

    private static bool AllAllowed(List<NetworkRef> ids, IReadOnlyCollection<NetworkRef> allowed)
    {
        foreach (var id in ids)
            if (!Contains(allowed, id))
                return false;
        return true;
    }

    private static bool Contains<T>(IReadOnlyCollection<T> items, T item)
        where T : IEquatable<T>
    {
        foreach (var candidate in items)
            if (candidate.Equals(item))
                return true;
        return false;
    }
}
