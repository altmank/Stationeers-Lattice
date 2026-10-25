using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Consequences;
using static Lattice.Tests.Consequences.Models;

namespace Lattice.Tests.Consequences;

/// <summary>
/// The gas check: audits and refills.
/// The mechanism it guards against: the game's merge queues the old network's gas for the survivor and applies it only
/// at the next tick, so two merges in one held tick copy a survivor that has not received the first merge's gas yet.
/// </summary>
public sealed class GasAuditTests
{
    private const int Types = 3;
    private const int Methane = 0;
    private const int Ozone = 1;
    private const int NitrousOxide = 2;
    private const double Rating = 60795.0;

    private static readonly GasTolerance Tolerance = GasTolerance.Default;

    private static GasContents Mix(params (int Type, double Mol, double Joules)[] parts)
    {
        var moles = new double[Types];
        var energies = new double[Types];
        foreach (var (type, mol, joules) in parts)
        {
            moles[type] += mol;
            energies[type] += joules;
        }

        return new GasContents(moles, energies);
    }

    private static GasContents Empty => GasContents.Empty(Types);

    private static GasContents One(double mol, double joulesPerMol) => new([mol], [mol * joulesPerMol]);

    private static ReferenceId[] Pipes(params long[] ids)
    {
        var pipes = new ReferenceId[ids.Length];
        for (var index = 0; index < ids.Length; index++)
            pipes[index] = Id(ids[index]);
        return pipes;
    }

    private static NetworkGas Reading(long id, GasContents gas, double volumeL, long[] members, params long[] devices) =>
        new(Net(id), gas, volumeL, Pipes(members), Pipes(devices), [], Option<double>.None);

    private static NetworkGas Ghost(long id, GasContents gas, params long[] devices) =>
        new(Net(id), gas, 0.0, [], Pipes(devices), [], Option<double>.None);

    private static NetworkGas Rated(long id, double mol, double volumeL, params long[] members) =>
        new(Net(id), One(mol, 6000.0), volumeL, Pipes(members), [], [], Option.Some(Rating));

    private static double IdealKpa(NetworkGas network, GasContents contents) => contents.TotalMol * 8.314462618 * 293.0 / network.VolumeL;

    private static List<long> Refs(List<NetworkGas> networks) => networks.ConvertAll(static n => n.Ref.Value);

    [Fact]
    public void A_merge_that_keeps_every_mole_is_ok()
    {
        List<NetworkGas> before = [Reading(10, Mix((Methane, 100, 2_000_000)), 20, [1, 2]), Reading(20, Mix((Methane, 50, 1_000_000)), 10, [3])];
        List<NetworkGas> after = [Reading(10, Mix((Methane, 150, 3_000_000)), 40, [1, 2, 3, 4])];

        var audit = GasAudit.Of(before, after, Tolerance);

        Assert.True(audit.Ok);
        var family = Assert.Single(audit.Families);
        Assert.Equal([10L, 20L], Refs(family.Before));
        Assert.Equal([10L], Refs(family.After));
        Assert.Equal(0.0, family.MissingMol, 9);
        Assert.Empty(GasRefills.For(audit, Tolerance));
    }

    [Fact]
    public void Untouched_networks_are_left_out()
    {
        var bystander = Reading(99, Mix((Ozone, 7, 10_000)), 5, [90, 91]);

        var audit = GasAudit.Of([bystander, Reading(10, Empty, 5, [1])], [bystander, Reading(10, Empty, 10, [1, 2])], Tolerance);

        Assert.False(Assert.Single(audit.Families).Contains(Net(99)));
        Assert.True(audit.Ok);
    }

    [Fact]
    public void A_split_dividing_the_contents_is_ok()
    {
        var audit = GasAudit.Of(
            [Reading(10, Mix((Methane, 90, 900)), 35, [1, 2, 3])],
            [Reading(11, Mix((Methane, 30, 300)), 10, [1]), Reading(12, Mix((Methane, 60, 600)), 20, [3])],
            Tolerance);

        Assert.True(audit.Ok);
        Assert.Equal([11L, 12L], Refs(Assert.Single(audit.Families).After));
    }

    [Fact]
    public void The_double_merge_loss_is_found_and_located_and_its_refill_restores_the_family()
    {
        var fuel = Mix((Methane, 11526, 250_000_000));
        List<NetworkGas> before = [Reading(10, fuel, 100, [1, 2]), Reading(20, Mix((Methane, 40, 900_000)), 50, [5])];
        List<NetworkGas> after = [Reading(20, Mix((Methane, 40, 900_000)), 200, [1, 2, 5, 100, 101]), Ghost(30, fuel, 777)];

        var audit = GasAudit.Of(before, after, Tolerance);

        Assert.False(audit.Ok);
        var family = Assert.Single(audit.Families);
        Assert.False(family.Conserved);
        Assert.Equal(11526.0, family.MissingMol, 6);
        var ghost = Assert.Single(audit.Ghosts);
        Assert.Equal(Net(30), ghost.Network.Ref);
        Assert.True(ghost.Family.IsNone);

        var refill = Assert.Single(GasRefills.For(audit, Tolerance));
        Assert.Equal(Net(20), refill.Into);
        Assert.Equal(11526.0, refill.Gas.MolesOf(Methane), 6);
        Assert.Equal(250_000_000.0, refill.Gas.EnergyOf(Methane), 3);

        List<NetworkGas> repaired = [Reading(20, Mix((Methane, 40, 900_000)).Plus(refill.Gas), 200, [1, 2, 5, 100, 101])];
        Assert.True(GasAudit.Of(before, repaired, Tolerance).Ok);
    }

    [Fact]
    public void The_in_line_tank_ghost_is_reported_with_the_devices_still_on_it()
    {
        var oxidiser = Mix((Ozone, 1000, 6_000_000), (NitrousOxide, 433, 3_000_000));
        List<NetworkGas> before = [Reading(124742, oxidiser, 60, [124640, 124683], 501, 502)];
        List<NetworkGas> after = [Reading(171240, Empty, 90, [124640, 200001, 200002], 501, 502, 503), Ghost(171238, oxidiser, 501, 502, 503)];

        var audit = GasAudit.Of(before, after, Tolerance);

        Assert.False(audit.Ok);
        Assert.Equal(Pipes(501, 502, 503), Assert.Single(audit.Ghosts).Network.Devices);
        var refill = Assert.Single(GasRefills.For(audit, Tolerance));
        Assert.Equal(Net(171240), refill.Into);
        Assert.Equal(1000.0, refill.Gas.MolesOf(Ozone), 9);
        Assert.Equal(433.0, refill.Gas.MolesOf(NitrousOxide), 9);
    }

    [Fact]
    public void A_ghost_that_held_the_familys_pipes_knows_its_family()
    {
        var audit = GasAudit.Of(
            [Reading(10, Mix((Methane, 5, 50)), 10, [1]), Reading(20, Empty, 10, [2])],
            [Ghost(10, Mix((Methane, 5, 50))), Reading(20, Empty, 30, [1, 2, 3])],
            Tolerance);

        Assert.True(Assert.Single(audit.Ghosts).Family.TryGet(out var family));
        Assert.Equal([20L], Refs(family.After));
    }

    [Fact]
    public void Removing_a_networks_last_pipe_deletes_its_contents_and_is_not_a_loss()
    {
        var audit = GasAudit.Of([Reading(10, Mix((Methane, 3, 30)), 10, [1])], [], Tolerance);

        Assert.True(audit.Ok);
        Assert.True(Assert.Single(audit.Families).Emptied);
        Assert.Empty(GasRefills.For(audit, Tolerance));
    }

    [Fact]
    public void A_ghost_that_was_already_there_is_reported_but_is_not_the_changes()
    {
        var old = Ghost(55, Mix((Ozone, 12, 100)), 7);

        var audit = GasAudit.Of([old, Reading(10, Empty, 5, [1])], [old, Reading(10, Empty, 10, [1, 2])], Tolerance);

        Assert.True(audit.Ok);
        Assert.Empty(audit.Ghosts);
        Assert.Equal(Net(55), Assert.Single(audit.OldGhosts).Ref);
    }

    [Fact]
    public void Gas_that_appears_fails_the_check_and_is_not_refilled()
    {
        var audit = GasAudit.Of([Reading(10, Mix((Methane, 10, 100)), 5, [1])], [Reading(10, Mix((Methane, 20, 200)), 10, [1, 2])], Tolerance);

        Assert.False(audit.Ok);
        Assert.Equal(-10.0, audit.Families[0].MissingMol, 9);
        Assert.Empty(GasRefills.For(audit, Tolerance));
    }

    [Fact]
    public void A_refill_is_shared_by_volume_like_the_games_split()
    {
        var refills = GasRefills.For(GasAudit.Of([Reading(10, Mix((Methane, 90, 900)), 30, [1, 2, 3])], [Reading(11, Empty, 10, [1]), Reading(12, Empty, 20, [3])], Tolerance), Tolerance);

        Assert.Equal(2, refills.Count);
        Assert.Equal(30.0, refills[0].Gas.TotalMol, 9);
        Assert.Equal(60.0, refills[1].Gas.TotalMol, 9);
    }

    [Fact]
    public void Rounding_in_the_games_arithmetic_is_not_a_loss() =>
        Assert.True(GasAudit.Of(
            [Reading(10, Mix((Methane, 11526.123456, 250_000_000)), 10, [1])],
            [Reading(10, Mix((Methane, 11526.123456 * (1 + 1e-9), 250_000_000 + 0.5)), 20, [1, 2])],
            Tolerance).Ok);

    [Fact]
    public void Two_merges_in_one_tick_lose_the_first_networks_gas()
    {
        var game = new MergeModel();
        game.Network(10, 100, 1, 2);
        game.Network(20, 40, 5);
        game.Network(30, 0, 100);
        var before = game.Read();

        game.Merge(survivor: 30, old: 10);
        game.Merge(survivor: 20, old: 30);
        game.ApplyQueued();

        var audit = GasAudit.Of(before, game.Read(), Tolerance);
        Assert.False(audit.Ok);
        Assert.Equal(100.0, audit.Families[0].MissingMol, 9);
        Assert.Equal(Net(30), Assert.Single(audit.Ghosts).Network.Ref);
    }

    [Fact]
    public void Applying_the_queued_gas_after_each_merge_keeps_it()
    {
        var game = new MergeModel();
        game.Network(10, 100, 1, 2);
        game.Network(20, 40, 5);
        game.Network(30, 0, 100);
        var before = game.Read();

        game.Merge(survivor: 30, old: 10);
        game.ApplyQueued();
        game.Merge(survivor: 20, old: 30);
        game.ApplyQueued();

        var audit = GasAudit.Of(before, game.Read(), Tolerance);
        Assert.True(audit.Ok);
        Assert.Empty(audit.Ghosts);
        Assert.Equal(140.0, Assert.Single(audit.Families).GasAfter.TotalMol, 9);
    }

    [Theory]
    [InlineData(605.0)]
    [InlineData(300.0)]
    [InlineData(0.0)]
    public void Anything_from_none_to_all_of_the_planned_release_gone_is_ok(double left)
    {
        var audit = GasAudit.Of(BurstBefore(), BurstAfter(left), Tolerance, [PlannedGasLoss.Burst(Net(2679), 605.0, 605.0)]);
        var plan = GasRefills.Plan(audit, Tolerance, IdealKpa);

        Assert.True(audit.Ok);
        var family = Assert.Single(audit.Families);
        Assert.True(family.Conserved);
        Assert.Equal(605.0, family.PlannedRelease.TotalMol, 6);
        Assert.Equal(0.0, family.PlannedLoss.TotalMol, 6);
        Assert.Empty(plan.Refills);
        Assert.Empty(plan.Withheld);
    }

    [Fact]
    public void Gas_that_appears_is_still_a_failure_with_a_planned_release() =>
        Assert.False(GasAudit.Of(BurstBefore(), BurstAfter(700.0), Tolerance, [PlannedGasLoss.Burst(Net(2679), 605.0, 605.0)]).Ok);

    [Fact]
    public void A_loss_beyond_the_planned_release_is_refilled_only_beyond_it()
    {
        var audit = GasAudit.Of(BurstBefore(), BurstAfter(200.0), Tolerance, [PlannedGasLoss.Burst(Net(2679), 302.5, 605.0)]);
        var plan = GasRefills.Plan(audit, Tolerance, static (_, _) => 0.0);

        Assert.False(audit.Ok);
        Assert.Equal(102.5, Assert.Single(plan.Refills).Gas.TotalMol, 6);
    }

    [Fact]
    public void Without_a_planned_burst_the_check_is_unchanged()
    {
        var audit = GasAudit.Of(BurstBefore(), BurstAfter(300.0), Tolerance);

        Assert.False(audit.Ok);
        Assert.Equal(0.0, Assert.Single(audit.Families).PlannedRelease.TotalMol);
        Assert.False(new PlannedGasLoss(Net(1), 0.5).AtMost);
        Assert.True(PlannedGasLoss.Burst(Net(1), 1.0, 2.0).AtMost);
        Assert.Equal(0.5, PlannedGasLoss.Burst(Net(1), 1.0, 2.0).Share, 9);
    }

    [Fact]
    public void A_handed_over_gain_is_expected_by_the_gas_check()
    {
        var before = new GasContents([10.0, 0.0], [1000.0, 0.0]);
        var gain = new GasContents([2.0, 1.0], [200.0, 50.0]);
        var then = new NetworkGas(Net(7), before, 100.0, Pipes(70), [], [], Option.Some(Rating));
        var now = then with { Gas = before.Plus(gain) };

        Assert.False(GasAudit.Of([then], [now], Tolerance).Ok);
        Assert.True(GasAudit.Of([then], [now], Tolerance, null, [new PlannedGasGain(Net(7), gain)]).Ok);
    }

    [Fact]
    public void A_failed_check_names_its_family_and_missing_moles()
    {
        var audit = GasAudit.Of(
            [Reading(10, One(100, 1), 20, [1, 2]), Reading(99, One(7, 1), 5, [90])],
            [Reading(10, One(60, 1), 30, [1, 2, 3]), Reading(99, One(7, 1), 5, [90])],
            Tolerance);

        Assert.False(audit.Ok);
        var family = Assert.Single(audit.Families);
        Assert.Equal([10L], Refs(family.Before));
        Assert.Equal(40.0, family.MissingMol, 9);
    }

    [Fact]
    public void An_orphan_network_holding_a_copy_of_the_gas_fails_the_check()
    {
        var orphan = Reading(917, One(230, 6000), 50, [11, 12, 13, 14, 15]);

        var audit = GasAudit.Of([Reading(917, One(230, 6000), 320, [1, 2, 3, 4])], [Reading(6755, One(230, 6000), 270, [2, 3, 4]), orphan], Tolerance);

        Assert.False(audit.Ok);
        Assert.Equal(-230.0, Assert.Single(audit.Families).MissingMol, 6);
    }

    [Fact]
    public void An_orphan_that_was_there_before_is_not_the_changes()
    {
        List<NetworkGas> readings = [Reading(10, One(5, 6000), 10, [1]), Reading(917, One(230, 6000), 50, [11, 12])];

        var audit = GasAudit.Of(readings, readings, Tolerance);

        Assert.True(audit.Ok);
        Assert.Empty(audit.Families);
    }

    [Fact]
    public void A_long_swapped_for_singles_and_merged_into_a_new_network_keeps_its_family()
    {
        var audit = GasAudit.Of(LoneLong(), [Merged(30)], Tolerance);

        Assert.True(audit.Ok);
        var family = Assert.Single(audit.Families);
        Assert.Equal([1193L], Refs(family.Before));
        Assert.Equal([1223L], Refs(family.After));
        Assert.False(family.Emptied);
        Assert.True(family.Conserved);
    }

    [Fact]
    public void Gas_that_vanishes_in_that_merge_is_caught_and_put_back()
    {
        var audit = GasAudit.Of(LoneLong(), [Merged(0)], Tolerance);

        Assert.False(audit.Ok);
        var family = Assert.Single(audit.Families);
        Assert.False(family.Emptied);
        Assert.Equal(30.0, family.MissingMol, 6);
        var refill = Assert.Single(GasRefills.For(audit, Tolerance));
        Assert.Equal(Net(1223), refill.Into);
        Assert.Equal(30.0, refill.Gas.TotalMol, 6);
    }

    [Fact]
    public void Gas_that_vanishes_when_the_merge_goes_the_other_way_is_caught_too()
    {
        var after = Long(1193, 0, [1217, 1220, 1221, 1222, 1225, 1226], [AtZ(0), AtZ(1), AtZ(2), AtZ(-1), AtZ(-2), AtZ(-3)]);

        var audit = GasAudit.Of(LoneLong(), [after], Tolerance);

        Assert.False(audit.Ok);
        Assert.Equal(30.0, Assert.Single(audit.Families).MissingMol, 6);
    }

    [Fact]
    public void A_removal_with_nothing_in_its_cells_is_still_emptied()
    {
        var audit = GasAudit.Of(LoneLong(), [Long(2000, 0, [1999], [AtZ(10)])], Tolerance);

        Assert.True(audit.Ok);
        Assert.Equal(2, audit.Families.Count);
        Assert.True(audit.Families[0].Emptied);
        Assert.Equal([1193L], Refs(audit.Families[0].Before));
        Assert.True(audit.Families[1].Conserved);
    }

    [Fact]
    public void Held_air_compares_within_the_gas_audits_tolerance()
    {
        Assert.True(Tolerance.SameMol(99.3170922629665, 99.3170907811472));
        Assert.False(Tolerance.SameMol(99.3, 98.8));
        Assert.True(Tolerance.SameEnergy(1_000_000.0, 1_000_000.5));
    }

    [Fact]
    public void The_planned_deletion_is_expected_and_not_put_back()
    {
        var audit = GasAudit.Of(SplitBefore(), SplitAfter(62.7275), Tolerance, [PlannedGasLoss.Of(Net(639), 564.545, 690.0)]);
        var plan = GasRefills.Plan(audit, Tolerance, IdealKpa);

        Assert.True(audit.Ok);
        var family = Assert.Single(audit.Families);
        Assert.True(family.Conserved);
        Assert.Equal(564.545, family.PlannedLoss.TotalMol, 6);
        Assert.Equal(0.0, family.MissingMol, 6);
        Assert.Empty(plan.Refills);
        Assert.Empty(plan.Withheld);
    }

    [Fact]
    public void A_true_loss_beyond_the_plan_is_still_put_back()
    {
        var audit = GasAudit.Of(SplitBefore(), SplitAfter(57.7275), Tolerance, [PlannedGasLoss.Of(Net(639), 564.545, 690.0)]);
        var plan = GasRefills.Plan(audit, Tolerance, IdealKpa);

        Assert.False(audit.Ok);
        Assert.Equal(10.0, Assert.Single(audit.Families).MissingMol, 6);
        Assert.Equal(2, plan.Refills.Count);
        Assert.All(plan.Refills, refill => Assert.Equal(5.0, refill.Gas.TotalMol, 6));
        Assert.Empty(plan.Withheld);
    }

    [Fact]
    public void A_refill_never_takes_a_network_over_its_weakest_pipe()
    {
        var audit = GasAudit.Of(SplitBefore(), SplitAfter(62.7275), Tolerance);
        var plan = GasRefills.Plan(audit, Tolerance, IdealKpa);

        Assert.False(audit.Ok);
        Assert.Empty(plan.Refills);
        var held = Assert.Single(plan.Withheld);
        Assert.Equal(564.545, held.Lacking.TotalMol, 6);
        Assert.Equal(Rating, held.RatingKpa);
        Assert.True(held.PressureAfterKpa > Rating);
    }

    [Fact]
    public void A_refill_within_the_rating_is_made()
    {
        var audit = GasAudit.Of([Rated(700, 50.0, 30.0, 701, 702, 703)], [Rated(710, 10.0, 10.0, 701), Rated(711, 10.0, 10.0, 703)], Tolerance);
        var plan = GasRefills.Plan(audit, Tolerance, IdealKpa);

        Assert.Empty(plan.Withheld);
        Assert.Equal(2, plan.Refills.Count);
        Assert.All(plan.Refills, refill => Assert.Equal(15.0, refill.Gas.TotalMol, 6));
    }

    [Fact]
    public void An_unrated_network_is_refilled_as_before()
    {
        var audit = GasAudit.Of([Reading(639, One(690.0, 6000.0), 140.0, [641, 646])], [Reading(931, One(10.0, 6000.0), 10.0, [641, 646])], Tolerance);

        Assert.Equal(680.0, Assert.Single(GasRefills.For(audit, Tolerance)).Gas.TotalMol, 6);
    }

    private static List<NetworkGas> BurstBefore() => [Rated(2679, 605.0, 270.0, 2677, 2681, 2678)];

    private static List<NetworkGas> BurstAfter(double mol) => [Rated(2679, mol, 20.0, 2677, 2678)];

    private static List<NetworkGas> SplitBefore() => [Rated(639, 690.0, 140.0, 641, 644, 638, 645, 646)];

    private static List<NetworkGas> SplitAfter(double eachMol) => [Rated(931, eachMol, 10.0, 641), Rated(933, eachMol, 10.0, 646)];

    private static Cell AtZ(int z) => At(0, 0, z * 5);

    private static NetworkGas Long(long id, double mol, long[] members, Cell[] cells) =>
        new(Net(id), One(mol, 5800), members.Length * 10.0, Pipes(members), [], cells, Option<double>.None);

    private static List<NetworkGas> LoneLong() => [Long(1193, 30, [1192], [AtZ(0), AtZ(1), AtZ(2)])];

    private static NetworkGas Merged(double mol) =>
        Long(1223, mol, [1217, 1220, 1221, 1222, 1225, 1226], [AtZ(0), AtZ(1), AtZ(2), AtZ(-1), AtZ(-2), AtZ(-3)]);

    /// <summary>
    /// <c>AtmosphericsNetwork.Merge</c> and the tick's event queue, reduced to moles: the merge copies the old network's
    /// gas now and queues it for the survivor (marking the survivor's atmosphere as awaiting); the old network's members
    /// move at once, and it is dropped unless its own atmosphere awaits an event (<c>ReferencableNetwork.RefreshNetwork</c>).
    /// </summary>
    private sealed class MergeModel
    {
        private readonly Dictionary<long, double> _gas = [];
        private readonly Dictionary<long, List<long>> _members = [];
        private readonly HashSet<long> _awaiting = [];
        private readonly List<long> _listed = [];
        private readonly Queue<(long Into, double Mol)> _queue = new();

        public void Network(long id, double mol, params long[] members)
        {
            _gas[id] = mol;
            _members[id] = [.. members];
            _listed.Add(id);
        }

        public void Merge(long survivor, long old)
        {
            _queue.Enqueue((survivor, _gas[old]));
            _awaiting.Add(survivor);
            _members[survivor].AddRange(_members[old]);
            _members[old].Clear();
            if (!_awaiting.Contains(old))
                _listed.Remove(old);
        }

        public void ApplyQueued()
        {
            while (_queue.Count > 0)
            {
                var (into, mol) = _queue.Dequeue();
                _gas[into] += mol;
                _awaiting.Remove(into);
            }
        }

        public List<NetworkGas> Read()
        {
            var read = new List<NetworkGas>();
            foreach (var id in _listed)
                read.Add(Reading(id, Mix((Methane, _gas[id], _gas[id] * 100)), _members[id].Count, _members[id].ToArray()));
            return read;
        }
    }
}
