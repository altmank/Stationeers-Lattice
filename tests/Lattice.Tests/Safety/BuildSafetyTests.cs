using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Safety;
using static Lattice.Tests.Safety.SafetyBench;

namespace Lattice.Tests.Safety;

/// <summary>
/// Build safety: one scenario per warning raises it, the same plan without the cause does
/// not. Every piece is modded (<see cref="SafetyBench"/>).
/// </summary>
public sealed class FaceWarningTests
{
    [Fact]
    public void A_wall_swap_on_a_room_with_air_warns_and_names_the_room()
    {
        var bench = new SafetyBench();
        var panel = bench.Real("ModPanel", Large(0, 0, 0));
        var room = bench.Room(Holding("nitrogen", 100), Large(0, 0, 0));
        var swap = bench.Swap(panel, "ModPane");

        var warning = bench.Single(SafetyCodes.SwapOnAirRoom);

        Assert.Equal(ValueList.Of(swap), warning.Ghosts);
        Assert.Equal(room.Value.ToString(), warning.Why.Facts["room"]);
        Assert.Equal(panel.Id.Value.ToString(), warning.Why.Facts["structure"]);
        Assert.Equal(SafetyCodes.Rule, warning.Why.From);
    }

    [Fact]
    public void A_wall_swap_on_a_room_without_air_does_not_warn()
    {
        var bench = new SafetyBench();
        var panel = bench.Real("ModPanel", Large(0, 0, 0));
        bench.Room(ValueList<GasHeld>.Empty, Large(0, 0, 0));
        bench.Swap(panel, "ModPane");

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void Removing_a_wall_is_not_a_swap()
    {
        var bench = new SafetyBench();
        var panel = bench.Real("ModPanel", Large(0, 0, 0));
        bench.Room(Holding("nitrogen", 100), Large(0, 0, 0));
        bench.Remove(panel);

        Assert.Empty(bench.Warnings(SafetyCodes.SwapOnAirRoom));
    }

    [Fact]
    public void Taking_out_a_wall_between_a_room_with_oxygen_and_the_outside_warns()
    {
        var bench = new SafetyBench();
        var panel = bench.Real("ModPanel", Large(0, 0, 0));
        bench.Room(Holding("oxygen", 80), Large(0, 0, 0));
        var remove = bench.Remove(panel);

        var warning = bench.Single(SafetyCodes.OxidiserOutside);

        Assert.Equal(ValueList.Of(remove), warning.Ghosts);
        Assert.Equal("80", warning.Why.Facts["oxidiser-mol"]);
    }

    [Fact]
    public void Taking_out_a_wall_between_two_rooms_with_oxygen_does_not_warn_of_oxidiser_outside()
    {
        var bench = new SafetyBench();
        var panel = bench.Real("ModPanel", Large(0, 0, 0));
        bench.Room(Holding("oxygen", 80), Large(0, 0, 0));
        bench.Room(Holding("oxygen", 80), Large(1, 0, 0));
        bench.Remove(panel);

        Assert.Empty(bench.Warnings(SafetyCodes.OxidiserOutside));
    }

    [Fact]
    public void Taking_out_a_wall_without_oxidiser_behind_it_does_not_warn_of_oxidiser_outside()
    {
        var bench = new SafetyBench();
        var panel = bench.Real("ModPanel", Large(0, 0, 0));
        bench.Room(Holding("nitrogen", 80), Large(0, 0, 0));
        bench.Remove(panel);

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void Taking_out_a_wall_of_a_hot_room_opens_a_path_to_hot_gas()
    {
        var bench = new SafetyBench();
        var panel = bench.Real("ModPanel", Large(0, 0, 0));
        var room = bench.Room(Holding("nitrogen", 80), Large(0, 0, 0));
        bench.Facts.RoomHeat[room] = new Kelvin(400);
        var remove = bench.Remove(panel);

        var warning = bench.Single(SafetyCodes.HotGas);

        Assert.Equal(ValueList.Of(remove), warning.Ghosts);
        Assert.Equal("400", warning.Why.Facts["kelvin"]);
    }

    [Fact]
    public void Taking_out_a_wall_of_a_cool_room_does_not_warn_of_hot_gas()
    {
        var bench = new SafetyBench();
        var panel = bench.Real("ModPanel", Large(0, 0, 0));
        var room = bench.Room(Holding("nitrogen", 80), Large(0, 0, 0));
        bench.Facts.RoomHeat[room] = new Kelvin(293);
        bench.Remove(panel);

        Assert.Empty(bench.Check());
    }
}

public sealed class PowerWarningTests
{
    // A power cell, three wires and a scrubber along x: cell -> wire -> wire -> wire -> scrubber.
    private static (SafetyBench Bench, StructureRef Cell, StructureRef[] Wires, StructureRef Scrubber) Line()
    {
        var bench = new SafetyBench();
        var cell = bench.Real("ModCell", Small(0));
        var wires = new[] { bench.Real("ModWire", Small(1)), bench.Real("ModWire", Small(2)), bench.Real("ModWire", Small(3)) };
        var scrubber = bench.Real("ModScrubber", Small(4));
        bench.Facts.Suppliers.Add(bench.Key("ModCell"));
        return (bench, cell, wires, scrubber);
    }

    [Fact]
    public void Removing_a_wire_between_the_supply_and_an_air_device_cuts_its_power()
    {
        var (bench, _, wires, scrubber) = Line();
        bench.Facts.Keeping[bench.Key("ModScrubber")] = Keeps.Air;
        var remove = bench.Remove(wires[1]);

        var warning = bench.Single(SafetyCodes.CutsPower);

        Assert.Equal(ValueList.Of(remove), warning.Ghosts);
        Assert.Equal(scrubber.Id.Value.ToString(), warning.Why.Facts["devices"]);
        Assert.Equal("air", warning.Why.Facts["keeps"]);
        Assert.Equal(bench.NetworkOf(wires[0]).Value.ToString(), warning.Why.Facts["network"]);
    }

    [Fact]
    public void The_check_worked_a_unit_a_step_gives_the_warnings_of_the_whole_check()
    {
        var (bench, _, wires, _) = Line();
        bench.Facts.Keeping[bench.Key("ModScrubber")] = Keeps.Air;
        bench.Remove(wires[1]);
        bench.Facts.Controls[bench.Key("ModConsole")] = Direction.XPlus;
        bench.Real("ModConsole", Large(4, 0, 0));
        bench.Build("ModTruss", Large(5, 0, 0));
        bench.Real("ModPane", Large(8, 0, 0));
        bench.Build("ModTube", new Cell(Large(8, 0, 0).X + 5, Large(8, 0, 0).Y, Large(8, 0, 0).Z));
        var panel = bench.Real("ModPanel", Large(12, 0, 0));
        bench.Room(Holding("nitrogen", 100), Large(12, 0, 0));
        bench.Swap(panel, "ModPane");

        var whole = bench.Check();

        Assert.Equal(4, whole.Select(w => w.Why.Code).Distinct().Count());
        Assert.Equal(whole, bench.CheckInSteps(1));
        Assert.Equal(whole, bench.CheckInSteps(3));
        Assert.Equal(whole, bench.CheckInSteps(4, 1, out var oneRead));
        Assert.Equal(whole, bench.CheckInSteps(4, 7, out _));
        bench.CheckInSteps(4, int.MaxValue, out var unbounded);
        Assert.True(oneRead > unbounded, $"{oneRead} steps at one read, {unbounded} unbounded");
    }

    [Fact]
    public void A_vent_followed_one_cell_read_a_step_reaches_the_network_as_the_whole_check()
    {
        var bench = new SafetyBench();
        var tube = bench.Real("ModTube", Small(-1));
        bench.Facts.Venting.Add(bench.Key("ModVent"));
        bench.World.SetNetworkGas(bench.NetworkOf(tube), new Litres(10), Holding("nitrogen", 5));
        bench.Room(Holding("nitrogen", 100), Large(0, 0, 0));
        bench.Build("ModTube", Small(0));
        bench.Build("ModVent", Small(1));

        var whole = bench.Check();

        Assert.Single(whole, w => w.Why.Code == SafetyCodes.GasIndoors);
        Assert.Equal(whole, bench.CheckInSteps(4, 1, out _));
    }

    [Fact]
    public void Removing_the_supply_cuts_the_power()
    {
        var (bench, cell, _, _) = Line();
        bench.Facts.Keeping[bench.Key("ModScrubber")] = Keeps.Heat;
        bench.Remove(cell);

        Assert.Equal("heat", bench.Single(SafetyCodes.CutsPower).Why.Facts["keeps"]);
    }

    [Fact]
    public void Removing_a_wire_before_a_device_that_keeps_nothing_does_not_warn()
    {
        var (bench, _, wires, _) = Line();
        bench.Remove(wires[1]);

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void Removing_the_air_device_itself_does_not_warn()
    {
        var (bench, _, _, scrubber) = Line();
        bench.Facts.Keeping[bench.Key("ModScrubber")] = Keeps.Air;
        bench.Remove(scrubber);

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void A_network_with_no_known_supply_has_no_power_to_cut()
    {
        var (bench, _, wires, _) = Line();
        bench.Facts.Suppliers.Clear();
        bench.Facts.Keeping[bench.Key("ModScrubber")] = Keeps.Air;
        bench.Remove(wires[1]);

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void Facts_Lattice_does_not_hold_leave_their_warnings_silent()
    {
        var (bench, _, wires, _) = Line();
        bench.Facts.Keeping[bench.Key("ModScrubber")] = Keeps.Air;
        bench.Remove(wires[1]);

        Assert.Empty(bench.Check(new NoSafetyFacts()));
    }
}

public sealed class VentWarningTests
{
    // A real tube network of two pieces left of the vent's cell; the vent's x- end points at it.
    private static (SafetyBench Bench, NetworkRef Network) Tubes(ValueList<GasHeld> contents)
    {
        var bench = new SafetyBench();
        var tube = bench.Real("ModTube", Small(0));
        bench.Real("ModTube", Small(-1));
        bench.Facts.Venting.Add(bench.Key("ModVent"));
        var network = bench.NetworkOf(tube);
        bench.World.SetNetworkGas(network, new Litres(20), contents);
        return (bench, network);
    }

    [Fact]
    public void A_vent_in_a_room_on_a_network_holding_gas_lets_gas_indoors()
    {
        var (bench, network) = Tubes(Holding("nitrogen", 50));
        var room = bench.Room(Holding("nitrogen", 100), Large(0, 0, 0));
        var vent = bench.Build("ModVent", Small(1));

        var warning = bench.Single(SafetyCodes.GasIndoors);

        Assert.Equal(ValueList.Of(vent), warning.Ghosts);
        Assert.Equal(network.Value.ToString(), warning.Why.Facts["network"]);
        Assert.Equal(room.Value.ToString(), warning.Why.Facts["room"]);
        Assert.Equal("50", warning.Why.Facts["mol"]);
    }

    [Fact]
    public void A_vent_on_an_empty_network_lets_nothing_out()
    {
        var (bench, _) = Tubes(ValueList<GasHeld>.Empty);
        bench.Room(Holding("nitrogen", 100), Large(0, 0, 0));
        bench.Build("ModVent", Small(1));

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void A_vent_reaches_the_network_through_planned_tubes()
    {
        var bench = new SafetyBench();
        var tube = bench.Real("ModTube", Small(-1));
        bench.Facts.Venting.Add(bench.Key("ModVent"));
        bench.World.SetNetworkGas(bench.NetworkOf(tube), new Litres(10), Holding("nitrogen", 5));
        bench.Room(Holding("nitrogen", 100), Large(0, 0, 0));
        bench.Build("ModTube", Small(0));
        var vent = bench.Build("ModVent", Small(1));

        Assert.Equal(ValueList.Of(vent), bench.Single(SafetyCodes.GasIndoors).Ghosts);
    }

    [Fact]
    public void A_vent_outside_on_a_network_holding_oxygen_lets_oxidiser_outside()
    {
        var (bench, _) = Tubes(Holding("oxygen", 12));
        var vent = bench.Build("ModVent", Small(1));

        var warning = bench.Single(SafetyCodes.OxidiserOutside);

        Assert.Equal(ValueList.Of(vent), warning.Ghosts);
        Assert.Equal("12", warning.Why.Facts["oxidiser-mol"]);
        Assert.Empty(bench.Warnings(SafetyCodes.GasIndoors));
    }

    [Fact]
    public void A_vent_outside_on_a_network_without_oxidiser_does_not_warn()
    {
        var (bench, _) = Tubes(Holding("nitrogen", 12));
        bench.Build("ModVent", Small(1));

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void A_vent_on_a_hot_network_opens_a_path_to_hot_gas()
    {
        var (bench, network) = Tubes(Holding("nitrogen", 12));
        bench.Facts.NetworkHeat[network] = new Kelvin(500);
        var vent = bench.Build("ModVent", Small(1));

        var warning = bench.Single(SafetyCodes.HotGas);

        Assert.Equal(ValueList.Of(vent), warning.Ghosts);
        Assert.Equal("500", warning.Why.Facts["kelvin"]);
    }

    [Fact]
    public void A_vent_on_a_cool_network_does_not_warn_of_hot_gas()
    {
        var (bench, network) = Tubes(Holding("nitrogen", 12));
        bench.Facts.NetworkHeat[network] = new Kelvin(300);
        bench.Build("ModVent", Small(1));

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void A_tube_that_is_no_vent_lets_nothing_out()
    {
        var (bench, _) = Tubes(Holding("oxygen", 12));
        bench.Build("ModTube", Small(1));

        Assert.Empty(bench.Check());
    }
}

public sealed class ControlWarningTests
{
    private static (SafetyBench Bench, StructureRef Console) Console()
    {
        var bench = new SafetyBench();
        var console = bench.Real("ModConsole", Large(0, 0, 0));
        bench.Facts.Controls[bench.Key("ModConsole")] = Direction.XPlus;
        return (bench, console);
    }

    [Fact]
    public void A_frame_in_front_of_a_console_makes_its_controls_unreachable()
    {
        var (bench, console) = Console();
        var truss = bench.Build("ModTruss", Large(1, 0, 0));

        var warning = bench.Single(SafetyCodes.ControlsUnreachable);

        Assert.Equal(ValueList.Of(truss), warning.Ghosts);
        Assert.Equal(console.Id.Value.ToString(), warning.Why.Facts["device"]);
    }

    [Fact]
    public void A_frame_beside_a_console_leaves_its_controls_free()
    {
        var (bench, _) = Console();
        bench.Build("ModTruss", Large(0, 0, 1));
        bench.Build("ModTruss", Large(-1, 0, 0));

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void A_wall_on_the_control_side_makes_the_controls_unreachable()
    {
        var (bench, _) = Console();
        var panel = bench.Build("ModPanel", Large(0, 0, 0));

        Assert.Equal(ValueList.Of(panel), bench.Single(SafetyCodes.ControlsUnreachable).Ghosts);
    }

    [Fact]
    public void A_planned_console_facing_a_frame_warns_and_names_the_console()
    {
        var bench = new SafetyBench();
        bench.Facts.Controls[bench.Key("ModConsole")] = Direction.XPlus;
        bench.Real("ModTruss", Large(1, 0, 0));
        var console = bench.Build("ModConsole", Large(0, 0, 0));

        Assert.Equal(ValueList.Of(console), bench.Single(SafetyCodes.ControlsUnreachable).Ghosts);
    }

    [Fact]
    public void A_piece_with_no_controls_has_nothing_to_keep_clear()
    {
        var bench = new SafetyBench();
        bench.Real("ModConsole", Large(0, 0, 0));
        bench.Build("ModTruss", Large(1, 0, 0));

        Assert.Empty(bench.Check());
    }
}

public sealed class OpeningWarningTests
{
    [Fact]
    public void A_wire_run_through_the_space_in_front_of_a_door_warns_once_for_the_door()
    {
        var bench = new SafetyBench();
        var gate = bench.Real("ModGate", Large(0, 0, 0));
        var wires = new[]
        {
            bench.Build("ModWire", Small(1, 0, -1)),
            bench.Build("ModWire", Small(1, 0, 0)),
            bench.Build("ModWire", Small(1, 0, 1)),
        };

        var warning = bench.Single(SafetyCodes.RunThroughDoor);

        Assert.Equal(ValueList.From(wires), warning.Ghosts);
        Assert.Equal(gate.Id.Value.ToString(), warning.Why.Facts["door"]);
    }

    [Fact]
    public void A_wire_run_clear_of_the_door_does_not_warn()
    {
        var bench = new SafetyBench();
        bench.Real("ModGate", Large(0, 0, 0));
        bench.Build("ModWire", Small(-2, 0, 0));
        bench.Build("ModWire", Small(-2, 0, 1));

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void A_chute_junction_in_front_of_a_door_is_a_run_though_its_ends_have_flow_roles()
    {
        var bench = new SafetyBench("""
            lattice-catalogue 3
            piece ModChuteMerge kit=ItemModChute option=0 grid=small family=chute
              step material=ItemModChute*1 exit=ItemWrench air=no
              end 0 0 0 x+ chute role=input
              end 0 0 0 z+ chute role=input2
              end 0 0 0 x- chute role=output
              cell 0 0 0 chute
              collision 23
            """);
        bench.Real("ModGate", Large(0, 0, 0));
        var merge = bench.Build("ModChuteMerge", Small(1, 0, 0));

        Assert.Equal(ValueList.Of(merge), bench.Single(SafetyCodes.RunThroughDoor).Ghosts);
    }

    [Fact]
    public void A_device_in_front_of_a_door_is_no_run()
    {
        var bench = new SafetyBench();
        bench.Real("ModGate", Large(0, 0, 0));
        bench.Build("ModScrubber", Small(1, 0, 0));

        Assert.Empty(bench.Warnings(SafetyCodes.RunThroughDoor));
    }

    [Fact]
    public void A_tube_on_a_window_warns_for_the_window()
    {
        var bench = new SafetyBench();
        var pane = bench.Real("ModPane", Large(0, 0, 0));
        var tube = bench.Build("ModTube", Small(1, 0, 0));

        var warning = bench.Single(SafetyCodes.OnWindow);

        Assert.Equal(ValueList.Of(tube), warning.Ghosts);
        Assert.Equal(pane.Id.Value.ToString(), warning.Why.Facts["window"]);
    }

    [Fact]
    public void A_tube_clear_of_the_window_does_not_warn()
    {
        var bench = new SafetyBench();
        bench.Real("ModPane", Large(0, 0, 0));
        bench.Build("ModTube", Small(-1, 0, 0));

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void A_window_beside_a_window_is_not_on_it()
    {
        var bench = new SafetyBench();
        bench.Real("ModPane", Large(0, 0, 0));
        bench.Build("ModPane", Large(0, 0, 1));

        Assert.Empty(bench.Check());
    }
}

public sealed class PaintAndTerrainWarningTests
{
    [Fact]
    public void Painting_a_planned_piece_in_a_room_warns_for_the_room()
    {
        var bench = new SafetyBench();
        var room = bench.Room(Holding("nitrogen", 100), Large(0, 0, 0));
        var truss = bench.Build("ModTruss", Large(0, 0, 0), Option.Some(Colour));

        var warning = bench.Single(SafetyCodes.PaintIndoors);

        Assert.Equal(ValueList.Of(truss), warning.Ghosts);
        Assert.Equal(room.Value.ToString(), warning.Why.Facts["room"]);
    }

    [Fact]
    public void Painting_a_wall_of_a_room_warns_and_painting_outside_does_not()
    {
        var bench = new SafetyBench();
        bench.Room(Holding("nitrogen", 100), Large(1, 0, 0));
        var inside = bench.Paint(bench.Real("ModPanel", Large(0, 0, 0)));
        bench.Paint(bench.Real("ModPanel", Large(0, 3, 0)));

        Assert.Equal(ValueList.Of(inside), bench.Single(SafetyCodes.PaintIndoors).Ghosts);
    }

    [Fact]
    public void Building_in_a_room_without_paint_does_not_warn()
    {
        var bench = new SafetyBench();
        bench.Room(Holding("nitrogen", 100), Large(0, 0, 0));
        bench.Build("ModTruss", Large(0, 0, 0));

        Assert.Empty(bench.Check());
    }

    [Fact]
    public void Terrain_in_a_planned_piece_cell_warns_and_never_refuses()
    {
        var bench = new SafetyBench();
        bench.Facts.TerrainCells.Add(Large(0, 0, 0));
        var truss = bench.Build("ModTruss", Large(0, 0, 0));

        var warning = bench.Single(SafetyCodes.TerrainInTheWay);

        Assert.Equal(ValueList.Of(truss), warning.Ghosts);
        Assert.Equal($"{Large(0, 0, 0).X},{Large(0, 0, 0).Y},{Large(0, 0, 0).Z}", warning.Why.Facts["cell"]);
        Assert.True(bench.Boards.View.Find(truss).IsSome);
    }

    [Fact]
    public void A_piece_clear_of_terrain_does_not_warn()
    {
        var bench = new SafetyBench();
        bench.Facts.TerrainCells.Add(Large(0, 0, 0));
        bench.Build("ModTruss", Large(1, 0, 0));

        Assert.Empty(bench.Check());
    }
}
