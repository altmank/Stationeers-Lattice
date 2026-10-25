using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Consequences;
using static Lattice.Tests.Consequences.Models;

namespace Lattice.Tests.Consequences;

/// <summary>Builders for forecast tests.</summary>
internal static class Edits
{
    public static ForecastNode P(long piece) => ForecastNode.Piece(Id(piece));

    public static ForecastNode W(long network) => ForecastNode.Whole(Net(network));

    public static ForecastPort Port(long device, int index, bool power, long? before, ForecastNode? after) => new(
        Id(device),
        index,
        power,
        before is { } network ? Option.Some(Net(network)) : Option<NetworkRef>.None,
        after is { } node ? Option.Some(node) : Option<ForecastNode>.None);

    public static EditAllowance Bridging(long[] networks, long[] devices, bool split = false)
    {
        var nets = new List<NetworkRef>();
        foreach (var network in networks)
            nets.Add(Net(network));
        var ids = new List<ReferenceId>();
        foreach (var device in devices)
            ids.Add(Id(device));
        return new EditAllowance(nets, ids, split);
    }

    public static bool Has(IEnumerable<Reason> reasons, ReasonCode code, string? fact = null, string? value = null)
    {
        foreach (var reason in reasons)
            if (reason.Code == code && (fact is null || (reason.Facts.TryGetValue(fact, out var found) && (value is null || found == value))))
                return true;
        return false;
    }
}

/// <summary>The networks an edit leaves.</summary>
public sealed class NetworkForecastTests
{
    private const long NetworkA = 1000;
    private const long NetworkB = 2000;
    private const long Apc = 500;

    [Fact]
    public void A_run_joining_one_network_merges_nothing()
    {
        var edit = new NetworkEdit();
        edit.AddNetwork(Net(NetworkA));
        edit.AddNewPiece(Id(-1));
        edit.AddNewPiece(Id(-2));
        edit.Link(Edits.P(-1), Edits.W(NetworkA));
        edit.Link(Edits.P(-1), Edits.P(-2));

        var forecast = NetworkForecaster.Of(edit, out _);

        Assert.Empty(forecast.Merges);
        Assert.Empty(forecast.Splits);
        var network = Assert.Single(forecast.Networks);
        Assert.Equal([Net(NetworkA)], network.NetworksBefore);
        Assert.Equal(2, network.NewPieces.Count);
        Assert.Empty(EditGuards.Check(forecast, EditAllowance.Nothing));
    }

    [Fact]
    public void A_run_between_two_networks_is_a_bridge_unless_both_are_named()
    {
        var edit = new NetworkEdit();
        edit.AddNetwork(Net(NetworkA));
        edit.AddNetwork(Net(NetworkB));
        edit.AddNewPiece(Id(-1));
        edit.Link(Edits.W(NetworkA), Edits.P(-1));
        edit.Link(Edits.P(-1), Edits.W(NetworkB));

        var forecast = NetworkForecaster.Of(edit, out _);

        Assert.Equal([Net(NetworkA), Net(NetworkB)], Assert.Single(forecast.Merges).NetworksBefore);
        Assert.True(Edits.Has(EditGuards.Check(forecast, EditAllowance.Nothing), ReasonCodes.WouldBridge, "networks", "1000, 2000"));
        Assert.True(Edits.Has(EditGuards.Check(forecast, Edits.Bridging([NetworkA], [])), ReasonCodes.WouldBridge));
        Assert.Empty(EditGuards.Check(forecast, Edits.Bridging([NetworkA, NetworkB], [])));
    }

    [Fact]
    public void An_apcs_input_and_output_on_one_network_is_a_device_bridge()
    {
        var edit = new NetworkEdit();
        edit.AddNetwork(Net(NetworkA));
        edit.AddNewPiece(Id(-1));
        edit.Link(Edits.W(NetworkA), Edits.P(-1));
        edit.Ports.Add(Edits.Port(Apc, 0, true, NetworkA, Edits.W(NetworkA)));
        edit.Ports.Add(Edits.Port(Apc, 1, true, null, Edits.P(-1)));

        var forecast = NetworkForecaster.Of(edit, out _);

        Assert.Empty(forecast.Merges);
        Assert.Equal(Id(Apc), Assert.Single(forecast.Bridges).Device);
        Assert.True(Edits.Has(EditGuards.Check(forecast, EditAllowance.Nothing), ReasonCodes.WouldBridge, "device", "500"));
        Assert.Empty(EditGuards.Check(forecast, Edits.Bridging([], [Apc])));
    }

    [Fact]
    public void A_data_port_joining_the_power_network_is_no_bridge()
    {
        var edit = new NetworkEdit();
        edit.AddNetwork(Net(NetworkA));
        edit.AddNewPiece(Id(-1));
        edit.Link(Edits.W(NetworkA), Edits.P(-1));
        edit.Ports.Add(Edits.Port(Apc, 0, true, NetworkA, Edits.W(NetworkA)));
        edit.Ports.Add(Edits.Port(Apc, 2, false, null, Edits.P(-1)));

        Assert.Empty(NetworkForecaster.Of(edit, out _).Bridges);
    }

    [Fact]
    public void Ports_already_on_one_network_stay_unflagged()
    {
        var edit = new NetworkEdit();
        edit.AddNetwork(Net(NetworkA));
        edit.AddNewPiece(Id(-1));
        edit.Link(Edits.W(NetworkA), Edits.P(-1));
        edit.Ports.Add(Edits.Port(Apc, 0, true, NetworkA, Edits.W(NetworkA)));
        edit.Ports.Add(Edits.Port(Apc, 1, true, NetworkA, Edits.W(NetworkA)));

        Assert.Empty(NetworkForecaster.Of(edit, out _).Bridges);
    }

    private static NetworkEdit Line(bool removeMiddle)
    {
        var edit = new NetworkEdit();
        for (long piece = 1; piece <= 5; piece++)
        {
            if (removeMiddle && piece == 3)
            {
                edit.Remove(Id(3), Net(NetworkA));
                continue;
            }

            edit.AddPiece(Id(piece), Net(NetworkA));
        }

        for (long piece = 1; piece < 5; piece++)
            edit.Link(Edits.P(piece), Edits.P(piece + 1));
        edit.Ports.Add(Edits.Port(700, 0, true, NetworkA, Edits.P(1)));
        edit.Ports.Add(Edits.Port(701, 0, true, NetworkA, Edits.P(5)));
        return edit;
    }

    [Fact]
    public void Removing_the_middle_of_a_line_splits_it_unless_allowed()
    {
        var forecast = NetworkForecaster.Of(Line(true), out var componentOf);

        var split = Assert.Single(forecast.Splits);
        Assert.Equal(Net(NetworkA), split.Network);
        Assert.Equal(2, split.Parts.Count);
        Assert.NotEqual(componentOf[Edits.P(1)], componentOf[Edits.P(5)]);
        Assert.True(Edits.Has(EditGuards.Check(forecast, EditAllowance.Nothing), ReasonCodes.WouldSplit, "into", "2"));
        Assert.Empty(EditGuards.Check(forecast, Edits.Bridging([], [], split: true)));
    }

    [Fact]
    public void A_reroute_that_reconnects_both_sides_is_no_split()
    {
        var edit = Line(true);
        edit.AddNewPiece(Id(-1));
        edit.AddNewPiece(Id(-2));
        edit.Link(Edits.P(2), Edits.P(-1));
        edit.Link(Edits.P(-1), Edits.P(-2));
        edit.Link(Edits.P(-2), Edits.P(4));

        var forecast = NetworkForecaster.Of(edit, out _);

        Assert.Empty(forecast.Splits);
        Assert.Empty(forecast.Cut);
        Assert.Empty(EditGuards.Check(forecast, EditAllowance.Nothing));
    }

    [Fact]
    public void Removing_the_last_piece_at_a_port_cuts_it()
    {
        var edit = new NetworkEdit();
        edit.Remove(Id(1), Net(NetworkA));
        edit.AddPiece(Id(2), Net(NetworkA));
        edit.Ports.Add(Edits.Port(700, 0, true, NetworkA, null));

        var forecast = NetworkForecaster.Of(edit, out _);

        Assert.Single(forecast.Cut);
        Assert.True(Edits.Has(EditGuards.Check(forecast, EditAllowance.Nothing), ReasonCodes.WouldSplit, "device", "700"));
    }

    [Fact]
    public void Removing_every_piece_leaves_the_network_gone()
    {
        var edit = new NetworkEdit();
        edit.Remove(Id(1), Net(NetworkA));
        edit.Remove(Id(2), Net(NetworkA));

        var forecast = NetworkForecaster.Of(edit, out _);

        Assert.Equal([Net(NetworkA)], forecast.Gone);
        Assert.Empty(forecast.Splits);
    }

    [Fact]
    public void A_single_carrying_a_removed_long_on_keeps_its_network_carried_not_gone()
    {
        var edit = new NetworkEdit();
        edit.Remove(Id(1), Net(NetworkA));
        edit.AddSuccessor(Id(-1), Net(NetworkA));

        var forecast = NetworkForecaster.Of(edit, out _);

        Assert.Empty(forecast.Gone);
        Assert.Equal([Net(NetworkA)], forecast.Carried);
        Assert.Equal([Net(NetworkA)], Assert.Single(forecast.Networks).NetworksBefore);
    }
}

/// <summary>Which devices a split cuts off from its root.</summary>
public sealed class SplitAnalysisTests
{
    [Fact]
    public void Cut_ports_on_two_networks_give_one_entry_each()
    {
        var edit = new NetworkEdit();
        edit.Remove(Id(1), Net(1219));
        edit.Remove(Id(2), Net(1221));
        edit.AddPiece(Id(3), Net(1221));
        edit.Ports.Add(Edits.Port(1123, 2, true, 1219, null));
        edit.Ports.Add(Edits.Port(1139, 0, true, 1221, null));
        edit.Ports.Add(Edits.Port(1500, 0, true, 1221, Edits.P(3)));
        var forecast = NetworkForecaster.Of(edit, out _);
        var roots = NetworkRootSet.None;
        roots.AddFeed(Id(1123), Net(1219));

        var details = SplitAnalysis.Of(forecast, roots);

        Assert.Equal(2, details.Count);
        Assert.Equal(Net(1219), details[0].Network);
        Assert.Equal([Id(1123)], details[0].Roots);
        Assert.Equal(Net(1221), details[1].Network);
        Assert.Empty(details[1].Roots);
        Assert.True(details[1].CutOff.IsNone);
        Assert.False(Edits.Has(EditGuards.Check(forecast, EditAllowance.Nothing, roots), ReasonCodes.WouldSplit, "cut-off"));
    }

    [Fact]
    public void Cutting_the_roots_own_port_cuts_off_every_device_left_on_the_network()
    {
        const long supply = 1155;
        const long apc = 461;
        const long console = 464;
        var edit = new NetworkEdit();
        edit.Remove(Id(1), Net(supply));
        edit.AddPiece(Id(2), Net(supply));
        edit.AddPiece(Id(3), Net(supply));
        edit.Link(Edits.P(2), Edits.P(3));
        edit.Ports.Add(Edits.Port(apc, 1, true, supply, null));
        edit.Ports.Add(Edits.Port(console, 0, true, supply, Edits.P(3)));
        var forecast = NetworkForecaster.Of(edit, out _);
        var roots = NetworkRootSet.None;
        roots.AddFeed(Id(apc), Net(supply));

        Assert.Empty(forecast.Splits);
        var detail = Assert.Single(SplitAnalysis.Of(forecast, roots));
        Assert.Equal(Net(supply), detail.Network);
        Assert.Equal([Id(apc)], detail.Roots);
        Assert.True(detail.CutOff.TryGet(out var cutOff));
        Assert.Equal([Id(console)], cutOff);
        var reason = Assert.Single(EditGuards.Check(forecast, EditAllowance.Nothing, roots));
        Assert.Equal("464", reason.Facts["cut-off"]);
    }

    [Fact]
    public void The_would_split_reason_names_the_cut_off_devices()
    {
        const long network = 1000;
        const long apc = 500;
        var edit = new NetworkEdit();
        for (long piece = 1; piece <= 5; piece++)
        {
            if (piece == 2)
            {
                edit.Remove(Id(piece), Net(network));
                continue;
            }

            edit.AddPiece(Id(piece), Net(network));
        }

        for (long piece = 1; piece < 5; piece++)
            edit.Link(Edits.P(piece), Edits.P(piece + 1));
        edit.Ports.Add(Edits.Port(apc, 1, true, network, Edits.P(1)));
        edit.Ports.Add(Edits.Port(600, 0, true, network, Edits.P(3)));
        edit.Ports.Add(Edits.Port(601, 0, true, network, Edits.P(4)));
        edit.Ports.Add(Edits.Port(602, 0, true, network, Edits.P(5)));

        var reasons = EditGuards.Check(NetworkForecaster.Of(edit, out _), EditAllowance.Nothing, NetworkRootSet.Named(Id(apc)));

        var reason = Assert.Single(reasons, r => r.Code == ReasonCodes.WouldSplit);
        Assert.Equal("600, 601, 602", reason.Facts["cut-off"]);
        Assert.Contains("(root)", reason.Facts["parts"]);
    }
}

/// <summary>The overload guard's numbers.</summary>
public sealed class PowerAfterTests
{
    private const double NormalCable = 5000.0;
    private const double StationBattery = 3600000.0;
    private const long Piece = 100;

    private static readonly NetworkTypes Data = new(4);
    private static readonly NetworkTypes Power = new(2);
    private static readonly NetworkTypes PowerAndData = new(6);

    private static readonly Dictionary<ReferenceId, double> Normal = new() { [Id(Piece)] = NormalCable };
    private static readonly Dictionary<ReferenceId, double> NormalNew = new() { [Id(-1)] = NormalCable };
    private static readonly Dictionary<NetworkRef, NetworkPower> NoNetworks = [];

    private static NetworkPower Power2(double potential, double required, double? cable) =>
        new(potential, required, cable is { } c ? Option.Some(c) : Option<double>.None, Option<double>.None);

    private static ForecastNetwork Merged(params long[] networks)
    {
        var after = new ForecastNetwork(0);
        foreach (var network in networks)
            after.NetworksBefore.Add(Net(network));
        after.NewPieces.Add(Id(-1));
        return after;
    }

    private static ForecastNetwork NewOnly(long piece, params ForecastPort[] ports)
    {
        var after = new ForecastNetwork(0);
        after.NewPieces.Add(Id(piece));
        after.Ports.AddRange(ports);
        return after;
    }

    private static Func<ForecastPort, Option<PortPower>> Each(PortPower power) => _ => Option.Some(power);

    private static Func<ForecastPort, Option<PortPower>> From(Dictionary<ForecastPort, PortPower> table) =>
        port => table.TryGetValue(port, out var found) ? Option.Some(found) : Option<PortPower>.None;

    /// <summary>A device port with its game facts, its load worked out by <see cref="PortSides.PowerOf"/>.</summary>
    private sealed record GamePort(ForecastPort Port, NetworkTypes Types, bool InputOutput, bool Output, bool Input, double PotentialW, double RequiredW)
    {
        public Option<PortPower> Load() =>
            PortSides.PowerOf(Types, InputOutput, Output, Input).Map(side => new PortPower(PotentialW, RequiredW, side));
    }

    private static Func<ForecastPort, Option<PortPower>> Loads(params GamePort[] ports)
    {
        var byPort = new Dictionary<ForecastPort, GamePort>();
        foreach (var port in ports)
            byPort[port.Port] = port;
        return port => byPort.TryGetValue(port, out var found) ? found.Load() : Option<PortPower>.None;
    }

    private static ForecastNetwork Network(long[] before, params GamePort[] ports)
    {
        var network = Merged(before);
        network.NewPieces.Clear();
        network.NewPieces.Add(Id(Piece));
        foreach (var port in ports)
            network.Ports.Add(port.Port);
        return network;
    }

    [Fact]
    public void Joining_a_generator_to_a_hungry_network_over_normal_cable_overloads()
    {
        var before = new Dictionary<NetworkRef, NetworkPower> { [Net(1)] = Power2(20000.0, 0.0, 100000.0), [Net(2)] = Power2(0.0, 8000.0, 100000.0) };

        var heavy = PowerAfter.Of(Merged(1, 2), before, new Dictionary<ReferenceId, double> { [Id(-1)] = 100000.0 });
        var normal = PowerAfter.Of(Merged(1, 2), before, NormalNew);

        Assert.Equal(8000.0, heavy.FlowW);
        Assert.False(heavy.Overloads);
        Assert.Equal(Option.Some(5000.0), normal.LowestCableW);
        Assert.True(normal.Overloads);
    }

    [Fact]
    public void A_network_without_supply_never_overloads() =>
        Assert.False(PowerAfter.Of(Merged(1), new Dictionary<NetworkRef, NetworkPower> { [Net(1)] = Power2(0.0, 50000.0, 5000.0) }, new Dictionary<ReferenceId, double>()).Overloads);

    [Fact]
    public void A_run_of_new_pieces_between_a_full_and_an_empty_battery_overloads_from_their_own_state()
    {
        var output = Edits.Port(10, 1, true, null, Edits.P(-1));
        var input = Edits.Port(11, 0, true, null, Edits.P(-1));
        var own = new Dictionary<ForecastPort, PortPower> { [output] = new(3600000.0, 0.0, PowerSide.Output), [input] = new(0.0, 3600000.0, PowerSide.Input) };

        var power = PowerAfter.Of(NewOnly(-1, output, input), NoNetworks, NormalNew, [], From(own));

        Assert.Equal(3600000.0, power.FlowW);
        Assert.True(power.Overloads);
    }

    [Fact]
    public void A_reroute_replacing_every_piece_keeps_the_old_networks_load_once()
    {
        var before = new Dictionary<NetworkRef, NetworkPower> { [Net(7)] = Power2(6000.0, 2830000.0, null) };
        var source = Edits.Port(20, 2, true, 7, Edits.P(-1));
        var sink = Edits.Port(21, 0, true, 7, Edits.P(-1));

        var power = PowerAfter.Of(NewOnly(-1, source, sink), before, NormalNew, [Net(7)], Each(new PortPower(1e9, 1e9, PowerSide.Device)));

        Assert.Equal(6000.0, power.PotentialW);
        Assert.Equal(2830000.0, power.RequiredW);
        Assert.True(power.Overloads);
    }

    [Fact]
    public void A_port_on_a_network_the_result_holds_is_not_counted_twice()
    {
        var after = Merged(1);
        after.Ports.Add(Edits.Port(30, 0, true, 1, Edits.W(1)));

        var power = PowerAfter.Of(after, new Dictionary<NetworkRef, NetworkPower> { [Net(1)] = Power2(4000.0, 4000.0, 5000.0) }, NormalNew, [], Each(new PortPower(1e9, 1e9, PowerSide.Device)));

        Assert.Equal(4000.0, power.FlowW);
        Assert.False(power.Overloads);
    }

    [Fact]
    public void A_battery_data_port_on_its_own_output_network_brings_no_load()
    {
        var dataPort = new GamePort(Edits.Port(399, 0, false, null, Edits.P(Piece)), Data, true, false, false, 0.0, StationBattery - 3558150.0);
        var outputPort = new GamePort(Edits.Port(399, 2, true, 423, Edits.W(423)), Power, true, true, false, 3558150.0, 0.0);
        var before = new Dictionary<NetworkRef, NetworkPower> { [Net(423)] = Power2(3558100.0, 15.0, NormalCable) };

        var power = PowerAfter.Of(Network([423], dataPort, outputPort), before, Normal, [], Loads(dataPort, outputPort));

        Assert.False(power.Overloads);
        Assert.Equal(15.0, power.FlowW);
    }

    [Fact]
    public void Pad_wiring_with_a_battery_data_port_warns_nothing_when_on()
    {
        var dataPort = new GamePort(Edits.Port(326, 0, false, null, Edits.P(Piece)), Data, true, false, false, 0.0, StationBattery);
        var padPower = new GamePort(Edits.Port(325, 1, true, null, Edits.P(Piece)), Power, false, false, false, 0.0, 30.0);

        var whenOn = PowerAfter.Of(Network([], dataPort, padPower), NoNetworks, Normal, [], static _ => Option<PortPower>.None, Loads(dataPort, padPower));

        Assert.Equal(30.0, whenOn.RequiredW);
        Assert.False(whenOn.Overloads);
        Assert.True(PowerAfter.WhenOnWarning(whenOn, [Id(325)]).IsNone);
    }

    [Fact]
    public void An_apc_data_port_beside_its_input_adds_nothing()
    {
        var dataPort = new GamePort(Edits.Port(50, 0, false, null, Edits.P(Piece)), Data, true, false, false, 0.0, 900.0);
        var input = new GamePort(Edits.Port(50, 1, true, null, Edits.P(Piece)), Power, true, false, true, 0.0, 900.0);
        var supply = new GamePort(Edits.Port(51, 0, true, null, Edits.P(Piece)), Power, false, false, false, 20000.0, 0.0);

        var power = PowerAfter.Of(Network([], dataPort, input, supply), NoNetworks, Normal, [], Loads(dataPort, input, supply));

        Assert.Equal(900.0, power.RequiredW);
        Assert.False(power.Overloads);
    }

    [Fact]
    public void A_device_with_two_ports_on_one_network_counts_once()
    {
        var first = new GamePort(Edits.Port(60, 0, true, null, Edits.P(Piece)), PowerAndData, false, false, false, 0.0, 3000.0);
        var second = new GamePort(Edits.Port(60, 1, true, null, Edits.P(Piece)), PowerAndData, false, false, false, 0.0, 3000.0);
        var supply = new GamePort(Edits.Port(61, 0, true, null, Edits.P(Piece)), Power, false, false, false, 20000.0, 0.0);
        var after = Network([], first, second, supply);
        var loads = Loads(first, second, supply);

        var power = PowerAfter.Of(after, NoNetworks, Normal, [], loads);
        var whenOn = PowerAfter.Of(after, NoNetworks, Normal, [], static _ => Option<PortPower>.None, loads);

        Assert.Equal(3000.0, power.RequiredW);
        Assert.False(power.Overloads);
        Assert.Equal(3000.0, whenOn.RequiredW);
    }

    [Fact]
    public void A_second_port_of_a_device_the_pooled_network_holds_brings_nothing()
    {
        var held = new GamePort(Edits.Port(70, 0, true, 7, Edits.W(7)), PowerAndData, false, false, false, 0.0, 4500.0);
        var joining = new GamePort(Edits.Port(70, 1, true, null, Edits.P(Piece)), PowerAndData, false, false, false, 0.0, 4500.0);
        var before = new Dictionary<NetworkRef, NetworkPower> { [Net(7)] = Power2(20000.0, 4500.0, NormalCable) };

        var power = PowerAfter.Of(Network([7], held, joining), before, Normal, [], Loads(held, joining));

        Assert.Equal(4500.0, power.RequiredW);
        Assert.False(power.Overloads);
    }

    [Fact]
    public void A_batterys_input_and_output_on_one_network_both_count()
    {
        var output = new GamePort(Edits.Port(80, 2, true, null, Edits.P(Piece)), Power, true, true, false, 1000.0, 0.0);
        var input = new GamePort(Edits.Port(80, 1, true, null, Edits.P(Piece)), Power, true, false, true, 0.0, 2000.0);

        var power = PowerAfter.Of(Network([], output, input), NoNetworks, Normal, [], Loads(output, input));

        Assert.Equal(1000.0, power.PotentialW);
        Assert.Equal(2000.0, power.RequiredW);
    }

    [Fact]
    public void No_when_on_warning_without_an_off_device()
    {
        var overloads = new PowerAfter(3589195.0, 10830.0, Option.Some(NormalCable), Option<double>.None);

        Assert.True(PowerAfter.WhenOnWarning(overloads, []).IsNone);
        Assert.True(PowerAfter.WhenOnWarning(overloads, [Id(12), Id(5)]).TryGet(out var named));
        Assert.Equal(ReasonCodes.WouldOverloadWhenOn, named.Code);
        Assert.Equal("5, 12", named.Facts["off-devices"]);
        Assert.Equal("5", named.Facts["device"]);
    }

    [Fact]
    public void Two_full_batteries_joined_while_off_overload_only_once_switched_on()
    {
        var output = Edits.Port(10, 1, true, null, Edits.P(-1));
        var input = Edits.Port(11, 0, true, null, Edits.P(-1));
        var switchedOn = new Dictionary<ForecastPort, PortPower> { [output] = new(3593900.0, 0.0, PowerSide.Output), [input] = new(0.0, 6100.0, PowerSide.Input) };
        var off = Each(new PortPower(0.0, 0.0, PowerSide.Device));

        var now = PowerAfter.Of(NewOnly(-1, output, input), NoNetworks, NormalNew, [], off);
        var whenOn = PowerAfter.Of(NewOnly(-1, output, input), NoNetworks, NormalNew, [], off, From(switchedOn));

        Assert.False(now.Overloads);
        Assert.Equal(6100.0, whenOn.FlowW);
        Assert.True(whenOn.Overloads);
    }

    [Fact]
    public void An_off_device_on_a_pooled_network_is_counted_on_top_of_its_network()
    {
        var after = Merged(7);
        after.Ports.Add(Edits.Port(20, 0, true, 7, Edits.W(7)));
        var before = new Dictionary<NetworkRef, NetworkPower> { [Net(7)] = Power2(20000.0, 4000.0, 5000.0) };

        var whenOn = PowerAfter.Of(after, before, NormalNew, null, null, port => port.Device == Id(20) ? Option.Some(new PortPower(0.0, 2000.0, PowerSide.Device)) : Option<PortPower>.None);

        Assert.Equal(6000.0, whenOn.RequiredW);
        Assert.True(whenOn.Overloads);
    }

    [Fact]
    public void A_battery_input_ceiling_is_bounded_by_the_supply()
    {
        var network = NewOnly(Piece, Edits.Port(2, 0, true, null, null));

        var whenOn = PowerAfter.Of(network, NoNetworks, Normal, [], Each(new PortPower(1200.0, 0.0, PowerSide.Device)), Each(new PortPower(0.0, StationBattery, PowerSide.Device)));

        Assert.False(whenOn.Overloads);
        Assert.Equal(1200.0, whenOn.FlowW);
    }

    [Fact]
    public void Joining_an_apc_input_warns_when_on_for_the_device_behind_it()
    {
        var network = NewOnly(Piece, Edits.Port(469, 0, true, null, null));

        var whenOn = PowerAfter.Of(network, NoNetworks, Normal, [], Each(new PortPower(20000.0, 10.0, PowerSide.Input)), Each(new PortPower(0.0, StationBattery, PowerSide.Input)));
        var warning = PowerAfter.WhenOnWarning(whenOn, [Id(471)]);

        Assert.True(whenOn.Overloads);
        Assert.True(warning.TryGet(out var reason));
        Assert.Equal("471", reason.Facts["off-devices"]);
    }
}

/// <summary>Port sides and the contents allowance.</summary>
public sealed class PortSideTests
{
    private static readonly Reason Split = ConsequenceCodes.Of(ReasonCodes.ContentsWouldMove);
    private static readonly Reason Emptied = Reason.Of(ReasonCodes.TargetGone, RuleId.Contract);

    [Fact]
    public void A_data_only_port_moves_no_power() =>
        Assert.True(PortSides.PowerOf(new NetworkTypes(4), false, false, false).IsNone);

    [Fact]
    public void An_input_output_device_moves_power_only_through_its_sides()
    {
        Assert.Equal(Option.Some(PowerSide.Device), PortSides.PowerOf(new NetworkTypes(6), false, false, false));
        Assert.Equal(Option.Some(PowerSide.Output), PortSides.PowerOf(new NetworkTypes(2), true, true, true));
        Assert.Equal(Option.Some(PowerSide.Input), PortSides.PowerOf(new NetworkTypes(2), true, false, true));
        Assert.True(PortSides.PowerOf(new NetworkTypes(2), true, false, false).IsNone);
    }

    [Fact]
    public void Shared_components_decide_the_side_before_the_role()
    {
        var output = new ConnectionRole(3);

        Assert.False(PortSides.IsOutput(Option.Some(false), Option.Some(true), output));
        Assert.True(PortSides.IsOutput(Option<bool>.None, Option.Some(true), new ConnectionRole(1)));
        Assert.True(PortSides.IsOutput(Option<bool>.None, Option<bool>.None, output));
        Assert.True(PortSides.IsInput(Option<bool>.None, Option<bool>.None, new ConnectionRole(2)));
    }

    [Fact]
    public void Allowing_contents_makes_a_split_of_a_holding_network_a_warning() =>
        Assert.True(ContentsGuard.Warns(Split, new EditAllowance([], [], Split: false, Contents: true)));

    [Fact]
    public void Without_it_the_split_is_reported() => Assert.False(ContentsGuard.Warns(Split, EditAllowance.Nothing));

    [Fact]
    public void Emptying_a_network_stays_reported() =>
        Assert.False(ContentsGuard.Warns(Emptied, new EditAllowance([], [], false, Contents: true)));
}
