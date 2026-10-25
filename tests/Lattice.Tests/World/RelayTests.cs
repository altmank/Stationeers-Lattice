using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.World;
using Lattice.TestKit.Allocations;
using Lattice.Tests.Contracts;

namespace Lattice.Tests.World;

/// <summary>The relay passes the world changes suite: each Cause reports one change and flushes.</summary>
public sealed class RelaySuite : WorldChangesContract
{
    private readonly WorldChangeRelay _relay = new();
    private long _next = 1;

    internal override IWorldChanges Subject => _relay;

    internal override void Cause(string eventName)
    {
        var frame = Relays.Frame(_next++);
        switch (eventName)
        {
            case "Registered":
                _relay.Report(new WorldChange.Registered(Relays.Info(frame)));
                break;
            case "StateChanged":
                _relay.Report(new WorldChange.StateSet(frame, new BuildState.Built()));
                break;
            case "Removed":
                _relay.Report(new WorldChange.Deregistered(frame));
                break;
            case "Painted":
                _relay.Report(new WorldChange.Painted(frame, Relays.Colour(3)));
                break;
            case "Renamed":
                _relay.Report(new WorldChange.Renamed(frame, Relays.Name("Hall")));
                break;
            case "SettingChanged":
                _relay.Report(new WorldChange.SettingSet(frame, Relays.Setting(2.5)));
                break;
            case "Moved":
                _relay.Report(new WorldChange.Detached(frame));
                _relay.Report(new WorldChange.Attached(frame.Id, new Cell(10, 20, 30), Turn.None));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null);
        }

        _relay.Flush();
    }

    internal override void EndWorld() => _relay.End();
}

public sealed class RelayTests
{
    private readonly WorldChangeRelay _relay = new();
    private readonly List<string> _heard = new();
    private readonly List<StructureInfo> _registered = new();

    public RelayTests()
    {
        _relay.Registered += info =>
        {
            _heard.Add($"registered {info.Ref.Id.Value}");
            _registered.Add(info);
        };
        _relay.StateChanged += (s, state) => _heard.Add($"state {s.Id.Value} {Relays.Text(state)}");
        _relay.Removed += s => _heard.Add($"removed {s.Id.Value}");
        _relay.Painted += (s, c) => _heard.Add($"painted {s.Id.Value} {c.Index}");
        _relay.Renamed += (s, n) => _heard.Add($"renamed {s.Id.Value} {n.Value}");
        _relay.SettingChanged += (s, v) => _heard.Add($"setting {s.Id.Value} {v.Value}");
        _relay.Moved += moves =>
        {
            foreach (var move in moves)
                _heard.Add($"moved {move.From.Id.Value} to {move.To.X},{move.To.Y},{move.To.Z}");
        };
    }

    [Fact]
    public void Nothing_is_raised_before_the_flush()
    {
        _relay.Report(new WorldChange.Registered(Relays.Info(Relays.Frame(1))));
        Assert.Empty(_heard);
        Assert.Equal(1, _relay.Waiting);
    }

    [Fact]
    public void A_hand_removal_through_index_below_zero_gives_only_removed()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.StateSet(frame, new BuildState.Broken()));
        _relay.Report(new WorldChange.Deregistered(frame));
        _relay.Flush();
        Assert.Equal(new[] { "removed 1" }, _heard);
    }

    [Fact]
    public void A_structure_broken_while_it_stands_gives_state_changed_broken()
    {
        _relay.Report(new WorldChange.StateSet(Relays.Frame(1), new BuildState.Broken()));
        _relay.Flush();
        Assert.Equal(new[] { "state 1 broken" }, _heard);
    }

    [Fact]
    public void A_broken_state_of_a_structure_the_game_is_destroying_is_dropped()
    {
        var relay = new WorldChangeRelay(_ => false);
        var heard = 0;
        relay.StateChanged += (_, _) => heard++;
        relay.Report(new WorldChange.StateSet(Relays.Frame(1), new BuildState.Broken()));
        relay.Flush();
        Assert.Equal(0, heard);
    }

    [Fact]
    public void Other_states_do_not_ask_whether_the_structure_stands()
    {
        var relay = new WorldChangeRelay(_ => false);
        var heard = 0;
        relay.StateChanged += (_, _) => heard++;
        relay.Report(new WorldChange.StateSet(Relays.Frame(1), new BuildState.Built()));
        relay.Flush();
        Assert.Equal(1, heard);
    }

    [Fact]
    public void Several_state_changes_in_a_frame_give_the_last_state_once()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.StateSet(frame, Relays.Building(0)));
        _relay.Report(new WorldChange.StateSet(frame, Relays.Building(1)));
        _relay.Report(new WorldChange.StateSet(frame, new BuildState.Built()));
        _relay.Flush();
        Assert.Equal(new[] { "state 1 built" }, _heard);
    }

    [Fact]
    public void Several_setting_changes_in_a_frame_give_the_last_value_once()
    {
        var memory = Relays.Frame(1);
        _relay.Report(new WorldChange.SettingSet(memory, Relays.Setting(1)));
        _relay.Report(new WorldChange.SettingSet(memory, Relays.Setting(2)));
        _relay.Report(new WorldChange.SettingSet(memory, Relays.Setting(3)));
        _relay.Flush();
        Assert.Equal(new[] { "setting 1 3" }, _heard);
    }

    [Fact]
    public void State_changes_in_different_frames_are_each_raised()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.StateSet(frame, Relays.Building(1)));
        _relay.Flush();
        _relay.Report(new WorldChange.StateSet(frame, new BuildState.Built()));
        _relay.Flush();
        Assert.Equal(new[] { "state 1 building 1", "state 1 built" }, _heard);
    }

    [Fact]
    public void Paint_and_name_of_a_structure_removed_in_the_frame_are_dropped()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.Painted(frame, Relays.Colour(2)));
        _relay.Report(new WorldChange.Renamed(frame, Relays.Name("Old")));
        _relay.Report(new WorldChange.SettingSet(frame, Relays.Setting(1)));
        _relay.Report(new WorldChange.Deregistered(frame));
        _relay.Flush();
        Assert.Equal(new[] { "removed 1" }, _heard);
    }

    [Fact]
    public void Changes_are_raised_in_the_order_they_happened()
    {
        _relay.Report(new WorldChange.Registered(Relays.Info(Relays.Frame(1))));
        _relay.Report(new WorldChange.Painted(Relays.Frame(2), Relays.Colour(4)));
        _relay.Report(new WorldChange.Deregistered(Relays.Frame(3)));
        _relay.Flush();
        Assert.Equal(new[] { "registered 1", "painted 2 4", "removed 3" }, _heard);
    }

    [Fact]
    public void A_part_detached_in_one_frame_and_attached_in_a_later_one_gives_one_move()
    {
        var part = Relays.Frame(1);
        _relay.Report(new WorldChange.Detached(part));
        _relay.Flush();
        Assert.Empty(_heard);
        _relay.Report(new WorldChange.Attached(part.Id, new Cell(5, 15, 25), Turn.None));
        _relay.Flush();
        Assert.Equal(new[] { "moved 1 to 5,15,25" }, _heard);
    }

    [Fact]
    public void The_frames_moves_are_raised_together()
    {
        var batches = 0;
        _relay.Moved += _ => batches++;
        for (var id = 1; id <= 3; id++)
            _relay.Report(new WorldChange.Detached(Relays.Frame(id)));
        for (var id = 1; id <= 3; id++)
            _relay.Report(new WorldChange.Attached(new ReferenceId(id), new Cell(id, 0, 0), Turn.None));
        _relay.Flush();
        Assert.Equal(1, batches);
        Assert.Equal(3, _heard.Count);
    }

    [Fact]
    public void A_detached_part_that_is_removed_gives_removed_and_no_move()
    {
        var part = Relays.Frame(1);
        _relay.Report(new WorldChange.Detached(part));
        _relay.Flush();
        _relay.Report(new WorldChange.Deregistered(part));
        _relay.Report(new WorldChange.Attached(part.Id, new Cell(1, 1, 1), Turn.None));
        _relay.Flush();
        Assert.Equal(new[] { "removed 1" }, _heard);
    }

    [Fact]
    public void An_attach_without_a_detach_is_not_a_move()
    {
        _relay.Report(new WorldChange.Attached(new ReferenceId(1), new Cell(1, 1, 1), Turn.None));
        _relay.Flush();
        Assert.Empty(_heard);
    }

    [Fact]
    public void A_handler_that_throws_leaves_the_rest_for_the_next_flush()
    {
        var relay = new WorldChangeRelay();
        var heard = new List<long>();
        var thrown = false;
        relay.Removed += s =>
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("a consumer's bug");
            }

            heard.Add(s.Id.Value);
        };
        relay.Report(new WorldChange.Deregistered(Relays.Frame(1)));
        relay.Report(new WorldChange.Deregistered(Relays.Frame(2)));

        Assert.Throws<InvalidOperationException>(relay.Flush);
        relay.Flush();

        Assert.Equal(new long[] { 2 }, heard);
    }

    [Fact]
    public void State_changes_of_a_structure_registered_in_the_frame_give_one_registered_with_the_last_state()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.Registered(Relays.Info(frame) with { State = Relays.Building(0) }));
        _relay.Report(new WorldChange.StateSet(frame, Relays.Building(1)));
        _relay.Report(new WorldChange.StateSet(frame, new BuildState.Built()));
        _relay.Flush();
        Assert.Equal(new[] { "registered 1" }, _heard);
        Assert.Equal(new BuildState.Built(), Assert.Single(_registered).State);
    }

    [Fact]
    public void Paint_name_and_setting_of_a_structure_registered_in_the_frame_are_in_its_registered_info()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.Registered(Relays.Info(frame)));
        _relay.Report(new WorldChange.Painted(frame, Relays.Colour(2)));
        _relay.Report(new WorldChange.Renamed(frame, Relays.Name("Old")));
        _relay.Report(new WorldChange.SettingSet(frame, Relays.Setting(1)));
        _relay.Report(new WorldChange.Painted(frame, Relays.Colour(5)));
        _relay.Report(new WorldChange.Renamed(frame, Relays.Name("Hall")));
        _relay.Report(new WorldChange.SettingSet(frame, Relays.Setting(4)));
        _relay.Flush();
        Assert.Equal(new[] { "registered 1" }, _heard);
        var info = Assert.Single(_registered);
        Assert.Equal(Option.Some(Relays.Colour(5)), info.Colour);
        Assert.Equal(Option.Some(Relays.Name("Hall")), info.Name);
        Assert.Equal(Option.Some(Relays.Setting(4)), info.Setting);
    }

    [Fact]
    public void A_structure_registered_and_broken_in_the_frame_while_it_stands_is_registered_broken()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.Registered(Relays.Info(frame)));
        _relay.Report(new WorldChange.StateSet(frame, new BuildState.Broken()));
        _relay.Flush();
        Assert.Equal(new[] { "registered 1" }, _heard);
        Assert.Equal(new BuildState.Broken(), Assert.Single(_registered).State);
    }

    [Fact]
    public void A_broken_state_of_a_structure_registered_in_the_frame_the_game_is_destroying_leaves_its_registered_state()
    {
        var relay = new WorldChangeRelay(_ => false);
        var registered = new List<StructureInfo>();
        var states = 0;
        relay.Registered += registered.Add;
        relay.StateChanged += (_, _) => states++;
        var frame = Relays.Frame(1);
        relay.Report(new WorldChange.Registered(Relays.Info(frame) with { State = Relays.Building(2) }));
        relay.Report(new WorldChange.StateSet(frame, new BuildState.Built()));
        relay.Report(new WorldChange.StateSet(frame, new BuildState.Broken()));
        relay.Flush();
        Assert.Equal(Relays.Building(2), Assert.Single(registered).State);
        Assert.Equal(0, states);
    }

    [Fact]
    public void A_structure_registered_and_removed_in_the_frame_gives_registered_as_reported_then_removed()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.Registered(Relays.Info(frame)));
        _relay.Report(new WorldChange.StateSet(frame, new BuildState.Broken()));
        _relay.Report(new WorldChange.Painted(frame, Relays.Colour(2)));
        _relay.Report(new WorldChange.Deregistered(frame));
        _relay.Flush();
        Assert.Equal(new[] { "registered 1", "removed 1" }, _heard);
        Assert.Equal(Relays.Info(frame), Assert.Single(_registered));
    }

    [Fact]
    public void A_state_change_before_the_registration_in_the_frame_is_raised_before_it()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.StateSet(frame, Relays.Building(0)));
        _relay.Report(new WorldChange.Registered(Relays.Info(frame)));
        _relay.Flush();
        Assert.Equal(new[] { "state 1 building 0", "registered 1" }, _heard);
        Assert.Equal(Relays.Info(frame), Assert.Single(_registered));
    }

    [Fact]
    public void Changes_of_a_structure_registered_in_an_earlier_frame_are_raised_as_changes()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.Registered(Relays.Info(frame) with { State = Relays.Building(0) }));
        _relay.Flush();
        _relay.Report(new WorldChange.StateSet(frame, new BuildState.Built()));
        _relay.Report(new WorldChange.Painted(frame, Relays.Colour(3)));
        _relay.Flush();
        Assert.Equal(new[] { "registered 1", "state 1 built", "painted 1 3" }, _heard);
    }

    [Fact]
    public void A_folded_registration_keeps_its_place_among_the_frames_other_changes()
    {
        var frame = Relays.Frame(1);
        _relay.Report(new WorldChange.Registered(Relays.Info(frame) with { State = Relays.Building(0) }));
        _relay.Report(new WorldChange.Painted(Relays.Frame(2), Relays.Colour(4)));
        _relay.Report(new WorldChange.StateSet(frame, new BuildState.Built()));
        _relay.Report(new WorldChange.Deregistered(Relays.Frame(3)));
        _relay.Flush();
        Assert.Equal(new[] { "registered 1", "painted 2 4", "removed 3" }, _heard);
        Assert.Equal(new BuildState.Built(), Assert.Single(_registered).State);
    }

    [Fact]
    public void A_flush_with_nothing_waiting_allocates_nothing()
    {
        Allocations.AssertNone(_relay.Flush, "Flush");
    }

    [Fact]
    public void Reports_after_the_world_ended_are_ignored()
    {
        _relay.End();
        _relay.Report(new WorldChange.Deregistered(Relays.Frame(1)));
        Assert.Equal(0, _relay.Waiting);
    }
}

internal static class Relays
{
    public static StructureRef Frame(long id) => new(new ReferenceId(id), new PrefabKey(1234), new Cell(10, 10, 10));

    public static StructureInfo Info(StructureRef structure) =>
        new(structure, Turn.None, new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);

    public static PaintColour Colour(int index) => PaintColour.Create(index).TryGet(out var colour) ? colour : throw new ArgumentException(null, nameof(index));

    public static ThingName Name(string name) => ThingName.Create(name).TryGet(out var value) ? value : throw new ArgumentException(null, nameof(name));

    public static BuildState Building(int step) =>
        new BuildState.Building(StepIndex.Create(step).TryGet(out var at) ? at : throw new ArgumentException(null, nameof(step)));

    public static Setting Setting(double value) => Lattice.Api.Setting.Create(value).TryGet(out var setting) ? setting : throw new ArgumentException(null, nameof(value));

    public static string Text(BuildState state) => state switch
    {
        BuildState.Building building => $"building {building.At.Value}",
        BuildState.Built => "built",
        BuildState.Broken => "broken",
        _ => "unknown",
    };
}
