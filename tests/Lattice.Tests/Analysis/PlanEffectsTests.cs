using System.Linq;
using Lattice.Api;
using Lattice.Core.Analysis;
using Lattice.Core.Ports;
using static Lattice.Tests.Analysis.EffectsBench;

namespace Lattice.Tests.Analysis;

public sealed class NetworkEffectTests
{
    [Fact]
    public void A_wire_between_two_networks_joins_them_and_their_loads_add_up()
    {
        var bench = new EffectsBench();
        var left = bench.Real("ModWire", Small(0));
        var right = bench.Real("ModWire", Small(2));
        var (a, b) = (bench.NetworkOf(left), bench.NetworkOf(right));
        bench.World.Loads[a] = (new Watts(100), new Watts(500));
        bench.World.Loads[b] = (new Watts(50), new Watts(300));
        bench.Build("ModWire", Small(1));

        var effects = bench.Effects().Networks;

        var first = a.Value < b.Value ? a : b;
        var second = first == a ? b : a;
        Assert.Contains(new NetworkEffect.Joins(first, second), effects);
        var load = Assert.Single(effects.OfType<NetworkEffect.Load>());
        Assert.Equal(first, load.Network);
        Assert.Equal(150, load.After.Value);
        Assert.Equal(Option.Some(new Watts(300)), load.Rating);
    }

    [Fact]
    public void Taking_out_the_middle_wire_splits_the_network()
    {
        var bench = new EffectsBench();
        var wires = new[] { bench.Real("ModWire", Small(0)), bench.Real("ModWire", Small(1)), bench.Real("ModWire", Small(2)) };
        bench.Remove(wires[1]);

        var effects = bench.Effects().Networks;

        Assert.Equal(ValueList.Of<NetworkEffect>(new NetworkEffect.Splits(bench.NetworkOf(wires[0]))), effects);
    }

    [Fact]
    public void Taking_out_an_end_wire_or_adding_to_one_network_changes_no_network()
    {
        var bench = new EffectsBench();
        var wires = new[] { bench.Real("ModWire", Small(0)), bench.Real("ModWire", Small(1)) };
        bench.Remove(wires[1]);
        bench.Build("ModWire", Small(-1));

        Assert.Empty(bench.Effects().Networks);
    }

    [Fact]
    public void A_modded_conduit_of_a_kind_the_game_lacks_joins_its_networks_and_never_a_wire()
    {
        var bench = new EffectsBench();
        var left = bench.Real("ModConduit", Small(0));
        var right = bench.Real("ModConduit", Small(2));
        bench.Real("ModWire", Small(3));
        bench.Build("ModConduit", Small(1));

        var joins = bench.Effects().Networks.OfType<NetworkEffect.Joins>().ToList();

        var join = Assert.Single(joins);
        Assert.Equal(
            new[] { bench.NetworkOf(left), bench.NetworkOf(right) }.OrderBy(n => n.Value),
            new[] { join.A, join.B });
    }
}

public sealed class RoomEffectTests
{
    [Fact]
    public void Taking_out_a_rooms_wall_opens_it_to_the_outside()
    {
        var bench = new EffectsBench();
        var room = bench.Room(Large(0, 0, 0));
        bench.WallIn(Large(0, 0, 0), Direction.XPlus);
        var panel = bench.Real("ModPanel", Large(0, 0, 0));
        bench.Remove(panel);

        Assert.Equal(ValueList.Of<RoomEffect>(new RoomEffect.Opens(room)), bench.Effects().Rooms);
    }

    [Fact]
    public void Closing_the_last_face_of_a_space_with_no_room_seals_a_room()
    {
        var bench = new EffectsBench();
        bench.WallIn(Large(0, 0, 0), Direction.XPlus);
        bench.Build("ModPanel", Large(0, 0, 0));

        Assert.Equal(ValueList.Of<RoomEffect>(new RoomEffect.Seals(1)), bench.Effects().Rooms);
    }

    [Fact]
    public void A_closed_space_below_the_games_fill_limit_seals_a_room()
    {
        var bench = TwoCellSpace();

        Assert.Equal(ValueList.Of<RoomEffect>(new RoomEffect.Seals(2)), bench.Effects(new GameRooms(3)).Rooms);
    }

    [Fact]
    public void A_closed_space_as_big_as_the_games_fill_limit_makes_no_room()
    {
        var bench = TwoCellSpace();

        Assert.Empty(bench.Effects(new GameRooms(2)).Rooms);
    }

    [Fact]
    public void A_room_that_would_reach_the_games_fill_limit_opens()
    {
        var bench = new EffectsBench();
        var room = bench.Room(Large(0, 0, 0));
        foreach (var (i, j, k) in new[] { (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1), (1, 1, 0), (1, -1, 0), (1, 0, 1), (1, 0, -1) })
            bench.Real("ModBlock", Large(i, j, k));
        bench.WallIn(Large(2, 0, 0), Direction.XMinus);
        var between = bench.Real("ModBlock", Large(1, 0, 0));
        bench.Remove(between);

        Assert.Equal(ValueList.Of<RoomEffect>(new RoomEffect.Resizes(room, 1, 3)), bench.Effects(new GameRooms(4)).Rooms);
        Assert.Equal(ValueList.Of<RoomEffect>(new RoomEffect.Opens(room)), bench.Effects(new GameRooms(3)).Rooms);
    }

    [Fact]
    public void The_games_fill_limit_is_the_decompiled_constant() => Assert.Equal(1200, GameRooms.Default.FillLimit);

    // Large cells (0,0,0) and (1,0,0), closed in on every side once a block goes in at (2,0,0).
    private static EffectsBench TwoCellSpace()
    {
        var bench = new EffectsBench();
        bench.WallIn(Large(0, 0, 0), Direction.XPlus);
        bench.WallIn(Large(1, 0, 0), Direction.XMinus, Direction.XPlus);
        bench.Build("ModBlock", Large(2, 0, 0));
        return bench;
    }

    [Fact]
    public void A_wall_across_a_room_resizes_it()
    {
        var bench = new EffectsBench();
        var room = bench.Room(Large(0, 0, 0), Large(1, 0, 0));
        bench.WallIn(Large(0, 0, 0), Direction.XPlus);
        bench.WallIn(Large(1, 0, 0), Direction.XMinus);
        bench.Build("ModPanel", Large(0, 0, 0));

        Assert.Equal(ValueList.Of<RoomEffect>(new RoomEffect.Resizes(room, 2, 1)), bench.Effects().Rooms);
    }

    [Fact]
    public void Swapping_nothing_that_blocks_air_changes_no_room()
    {
        var bench = new EffectsBench();
        bench.Room(Large(0, 0, 0));
        bench.WallIn(Large(0, 0, 0), Direction.XPlus);
        bench.Real("ModPanel", Large(0, 0, 0));
        bench.Build("ModWire", Small(1));

        Assert.Empty(bench.Effects().Rooms);
    }
}

public sealed class GasEffectTests
{
    [Fact]
    public void A_vent_on_a_network_holding_gas_releases_it_into_the_room()
    {
        var bench = new EffectsBench();
        var tube = bench.Real("ModTube", Small(0));
        var network = bench.NetworkOf(tube);
        bench.Fake.SetNetworkGas(network, new Litres(20), Holding("oxygen", 50));
        bench.World.Heat[network] = new Kelvin(300);
        var room = bench.Room(Large(0, 0, 0));
        bench.Build("ModVent", Small(1));

        var release = Assert.IsType<GasEffect.Releases>(Assert.Single(bench.Effects().Gas));

        Assert.Equal(Small(1), release.At);
        Assert.Equal(Option.Some(room), release.Into);
        Assert.Equal(new GasMix(ValueList.Of(new GasAmount(Gas("oxygen"), new Moles(50))), new Kelvin(300)), release.Gas);
    }

    [Fact]
    public void A_vent_on_a_network_whose_temperature_is_not_read_releases_nothing_Lattice_can_state()
    {
        var bench = new EffectsBench();
        var tube = bench.Real("ModTube", Small(0));
        bench.Fake.SetNetworkGas(bench.NetworkOf(tube), new Litres(20), Holding("oxygen", 50));
        bench.Build("ModVent", Small(1));

        Assert.Empty(bench.Effects().Gas);
    }

    [Fact]
    public void Taking_out_a_pipe_releases_nothing()
    {
        var bench = new EffectsBench();
        var tubes = new[] { bench.Real("ModTube", Small(0)), bench.Real("ModTube", Small(1)) };
        bench.Fake.SetNetworkGas(bench.NetworkOf(tubes[0]), new Litres(20), Holding("oxygen", 50));
        bench.Remove(tubes[1]);

        Assert.Empty(bench.Effects().Gas);
    }

    [Fact]
    public void Painting_in_a_room_adds_the_spray_cans_pollutant_per_painted_piece()
    {
        var bench = new EffectsBench();
        var room = bench.Room(Large(0, 0, 0));
        bench.Paint(bench.Real("ModWire", Small(0)));
        bench.Paint(bench.Real("ModWire", Small(1)));

        var pollutes = Assert.IsType<GasEffect.Pollutes>(Assert.Single(bench.Effects().Gas));

        Assert.Equal(room, pollutes.Room);
        Assert.Equal(2 * GasEffects.PerSpray.Value, pollutes.Amount.Value, 9);
    }
}
