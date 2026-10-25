using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Consequences;

/// <summary>
/// A pipe network's contents, gas by gas: the moles and energy of each single gas, in one fixed order the caller
/// keeps (the game's gas types). Immutable; sums and differences make new contents. Unlike the contract's
/// <see cref="GasMix"/> it keeps energy per gas, which conservation needs.
/// </summary>
internal sealed class GasContents
{
    private readonly double[] _moles;
    private readonly double[] _energies;

    /// <summary>Contents from moles and energies per gas; both arrays are kept, so the caller hands them over.</summary>
    public GasContents(double[] moles, double[] energies)
    {
        if (moles.Length != energies.Length)
            throw new ArgumentException("Contents need one energy per gas.", nameof(energies));
        _moles = moles;
        _energies = energies;
        for (var index = 0; index < moles.Length; index++)
        {
            TotalMol += moles[index];
            TotalEnergyJ += energies[index];
        }
    }

    /// <summary>How many gases it lists.</summary>
    public int Types => _moles.Length;

    /// <summary>Every mole.</summary>
    public double TotalMol { get; }

    /// <summary>Every joule.</summary>
    public double TotalEnergyJ { get; }

    /// <summary>No gas of any of the types.</summary>
    public static GasContents Empty(int types) => new(new double[types], new double[types]);

    /// <summary>The moles of one gas.</summary>
    public double MolesOf(int index) => _moles[index];

    /// <summary>The energy of one gas.</summary>
    public double EnergyOf(int index) => _energies[index];

    /// <summary>Both added, gas by gas.</summary>
    public GasContents Plus(GasContents other) => Combine(other, 1.0);

    /// <summary>The other taken away, gas by gas.</summary>
    public GasContents Minus(GasContents other) => Combine(other, -1.0);

    /// <summary>Every amount times the factor (a share of the whole).</summary>
    public GasContents Scaled(double factor)
    {
        var moles = new double[Types];
        var energies = new double[Types];
        for (var index = 0; index < Types; index++)
        {
            moles[index] = _moles[index] * factor;
            energies[index] = _energies[index] * factor;
        }

        return new GasContents(moles, energies);
    }

    /// <summary>What this has that <paramref name="after"/> lacks, gas by gas: the positive part of each difference.</summary>
    public GasContents Lacking(GasContents after)
    {
        var moles = new double[Types];
        var energies = new double[Types];
        for (var index = 0; index < Types; index++)
        {
            moles[index] = Math.Max(0.0, _moles[index] - after._moles[index]);
            energies[index] = Math.Max(0.0, _energies[index] - after._energies[index]);
        }

        return new GasContents(moles, energies);
    }

    private GasContents Combine(GasContents other, double sign)
    {
        if (other.Types != Types)
            throw new ArgumentException("Both must list the same gases.", nameof(other));
        var moles = new double[Types];
        var energies = new double[Types];
        for (var index = 0; index < Types; index++)
        {
            moles[index] = _moles[index] + sign * other._moles[index];
            energies[index] = _energies[index] + sign * other._energies[index];
        }

        return new GasContents(moles, energies);
    }
}

/// <summary>
/// How far two totals may differ and still be the same contents: an absolute floor (moles, joules) plus a share of the
/// larger total, for the rounding of the game's double arithmetic.
/// </summary>
internal readonly record struct GasTolerance(double AbsoluteMol, double AbsoluteJ, double Relative)
{
    /// <summary>0.01 mol, 10 J and a 1e-5 share.</summary>
    public static GasTolerance Default => new(0.01, 10.0, 1e-5);

    /// <summary>Both totals within the tolerance.</summary>
    public bool Same(GasContents a, GasContents b) =>
        Close(a.TotalMol, b.TotalMol, AbsoluteMol) && Close(a.TotalEnergyJ, b.TotalEnergyJ, AbsoluteJ);

    /// <summary>Two amounts of gas, in moles, within the tolerance.</summary>
    public bool SameMol(double a, double b) => Close(a, b, AbsoluteMol);

    /// <summary>Two energies, in joules, within the tolerance.</summary>
    public bool SameEnergy(double a, double b) => Close(a, b, AbsoluteJ);

    /// <summary>Contents indistinguishable from none.</summary>
    public bool Negligible(GasContents contents) =>
        Close(contents.TotalMol, 0.0, AbsoluteMol) && Close(contents.TotalEnergyJ, 0.0, AbsoluteJ);

    /// <summary>Contents whose totals lie between two others (moles and energy), within the tolerance at either end.</summary>
    public bool Between(GasContents low, GasContents high, GasContents value) =>
        AtLeast(value.TotalMol, low.TotalMol, AbsoluteMol) && AtLeast(high.TotalMol, value.TotalMol, AbsoluteMol) &&
        AtLeast(value.TotalEnergyJ, low.TotalEnergyJ, AbsoluteJ) && AtLeast(high.TotalEnergyJ, value.TotalEnergyJ, AbsoluteJ);

    private bool AtLeast(double a, double b, double absolute) => a >= b || Close(a, b, absolute);

    private bool Close(double a, double b, double absolute) =>
        Math.Abs(a - b) <= absolute + Relative * Math.Max(Math.Abs(a), Math.Abs(b));
}

/// <summary>
/// One pipe network as read at one moment: its ref, contents, volume, member pipes, devices registered on it, the
/// small-grid cells its members fill, and the rating of its weakest pipe. A network with no members is still listed by
/// the game while its atmosphere awaits an event (<c>ReferencableNetwork.RefreshNetwork</c> skips deregistering it):
/// that is a ghost.
/// </summary>
/// <param name="Ref">The network.</param>
/// <param name="Gas">Its contents.</param>
/// <param name="VolumeL">Its volume in litres.</param>
/// <param name="Members">Its member pipes.</param>
/// <param name="Devices">The devices registered on it.</param>
/// <param name="Cells">The cells its member pipes fill, as registered on the small grid.</param>
/// <param name="RatingKpa">The lowest <c>MaxPressure</c> of its member pipes; none when none is rated or it was not read.</param>
internal sealed record NetworkGas(
    NetworkRef Ref,
    GasContents Gas,
    double VolumeL,
    IReadOnlyList<ReferenceId> Members,
    IReadOnlyList<ReferenceId> Devices,
    IReadOnlyList<Cell> Cells,
    Option<double> RatingKpa)
{
    /// <summary>It has member pipes: not a ghost.</summary>
    public bool Live => Members.Count > 0;
}

/// <summary>
/// Gas deleted from one pipe network on purpose, as forecast and allowed (a part of a split network that loses its
/// last member while it still holds its share): a share of what the network held before. The gas check expects it
/// gone and does not put it back.
/// </summary>
internal sealed record PlannedGasLoss
{
    /// <summary>A loss of a share (clamped to 0 to 1) of the network's contents.</summary>
    /// <param name="network">The network.</param>
    /// <param name="share">The share deleted.</param>
    /// <param name="atMost">A release only set up (a burst on a later tick): anything from none of the share to all of it may be gone.</param>
    public PlannedGasLoss(NetworkRef network, double share, bool atMost = false)
    {
        Network = network;
        Share = Math.Max(0.0, Math.Min(1.0, share));
        AtMost = atMost;
    }

    /// <summary>The network.</summary>
    public NetworkRef Network { get; }

    /// <summary>The part of the network's contents deleted, 0 to 1.</summary>
    public double Share { get; }

    /// <summary>
    /// A release only set up (a network left over its weakest pipe bursts on a later atmospheric tick and leaks there):
    /// at the check anything from none of the share to all of it may be gone. False for a deletion, all of which must
    /// be gone.
    /// </summary>
    public bool AtMost { get; }

    /// <summary>A forecast's moles as a share of the moles it was made from.</summary>
    public static PlannedGasLoss Of(NetworkRef network, double lostMol, double molesBefore) =>
        new(network, ShareOf(lostMol, molesBefore));

    /// <summary>A burst's release (<see cref="AtMost"/>): the moles a network left holds, as a share of the network's before.</summary>
    public static PlannedGasLoss Burst(NetworkRef network, double releasedMol, double molesBefore) =>
        new(network, ShareOf(releasedMol, molesBefore), true);

    private static double ShareOf(double moles, double molesBefore) => molesBefore > 0.0 ? moles / molesBefore : 0.0;
}

/// <summary>Gas put into a network on purpose (a removed device's contents handed to one of its pipe networks).</summary>
internal sealed record PlannedGasGain(NetworkRef Network, GasContents Gas);

/// <summary>
/// Networks a change touched that belong together: every network before and after that shares a pipe or a pipe's
/// cell, directly or through another. Its contents before (the networks live then), less what is deleted on purpose
/// (<see cref="PlannedLoss"/>), must equal its contents after (the networks live now), unless every one of its pipes
/// was removed and nothing stands in their cells (<see cref="Emptied"/>: the game's removal of a network's last pipe
/// deletes its contents).
/// </summary>
internal sealed class GasFamily
{
    /// <summary>A family from its readings and what was planned for it.</summary>
    public GasFamily(
        List<NetworkGas> before,
        List<NetworkGas> after,
        GasContents gasBefore,
        GasContents gasAfter,
        GasContents plannedLoss,
        GasTolerance tolerance,
        GasContents plannedRelease)
    {
        Before = before;
        After = after;
        GasBefore = gasBefore;
        GasAfter = gasAfter;
        PlannedLoss = plannedLoss;
        PlannedRelease = plannedRelease;
        Expected = gasBefore.Minus(plannedLoss);
        Floor = Expected.Minus(PlannedRelease);
        Conserved = tolerance.Same(Expected, gasAfter) || tolerance.Between(Floor, Expected, gasAfter);
    }

    /// <summary>What a planned burst may let out; at the check anything from none of it to all of it may be gone.</summary>
    public GasContents PlannedRelease { get; }

    /// <summary>The least it may hold after: <see cref="Expected"/> less the planned release.</summary>
    public GasContents Floor { get; }

    /// <summary>Its networks before, live then.</summary>
    public List<NetworkGas> Before { get; }

    /// <summary>Its networks after, live now.</summary>
    public List<NetworkGas> After { get; }

    /// <summary>Contents before.</summary>
    public GasContents GasBefore { get; }

    /// <summary>Contents after.</summary>
    public GasContents GasAfter { get; }

    /// <summary>What is deleted on purpose (less what is handed in on purpose).</summary>
    public GasContents PlannedLoss { get; }

    /// <summary>What it should hold after: contents before less the planned loss.</summary>
    public GasContents Expected { get; }

    /// <summary>Every network of it is gone.</summary>
    public bool Emptied => After.Count == 0;

    /// <summary>It holds what it was expected to hold, within the tolerance; with a planned release, anything from <see cref="Floor"/> to <see cref="Expected"/>.</summary>
    public bool Conserved { get; }

    /// <summary>Expected moles less moles after: positive when gas went missing beyond the plan, negative when gas appeared.</summary>
    public double MissingMol => Expected.TotalMol - GasAfter.TotalMol;

    /// <summary>Expected energy less energy after.</summary>
    public double MissingEnergyJ => Expected.TotalEnergyJ - GasAfter.TotalEnergyJ;

    /// <summary>Whether the network is among its networks before or after.</summary>
    public bool Contains(NetworkRef network) => IndexIn(Before, network) >= 0 || IndexIn(After, network) >= 0;

    private static int IndexIn(List<NetworkGas> networks, NetworkRef network) => networks.FindIndex(n => n.Ref == network);
}

/// <summary>A ghost network and the family whose pipes it once held, when it held any before.</summary>
internal sealed record GasNetworkGhost(NetworkGas Network, Option<GasFamily> Family);

/// <summary>What a change did to the pipe networks' contents, from a reading before it and one after.</summary>
internal sealed class GasAudit
{
    private GasAudit(List<GasFamily> families, List<GasNetworkGhost> ghosts, List<NetworkGas> oldGhosts, bool ok)
    {
        Families = families;
        Ghosts = ghosts;
        OldGhosts = oldGhosts;
        Ok = ok;
    }

    /// <summary>Every family of networks the change touched, in order of their lowest network.</summary>
    public List<GasFamily> Families { get; }

    /// <summary>
    /// Networks with no member left that hold gas and were not ghosts with that gas before, each with the family whose
    /// pipes it held before (none for a network made and lost).
    /// </summary>
    public List<GasNetworkGhost> Ghosts { get; }

    /// <summary>Ghosts that were already there, holding the same gas, before: reported, not the change's.</summary>
    public List<NetworkGas> OldGhosts { get; }

    /// <summary>Every family kept its contents (or was emptied by removal) and no ghost holds gas.</summary>
    public bool Ok { get; }

    /// <summary>
    /// Compares the two readings. A network is changed when it is gone, new, holds other contents or other pipes;
    /// unchanged networks are left out. Changed networks are joined into families by what physically stands: a pipe in
    /// network A before and network B after puts A and B in one family, and so does a cell a pipe of A filled before
    /// and a pipe of B fills after (a piece replaced in place, such as a long straight swapped for its singles, is a
    /// new pipe with a new id). Network ids are never followed: the game's merge keeps whichever network the joining
    /// piece met first (<c>StructureNetwork.Merge</c>), so the one that carries the contents on may be new. A planned
    /// loss takes its share of a network's contents before off what its family should hold. Linear in the readings.
    /// </summary>
    public static GasAudit Of(
        IReadOnlyList<NetworkGas> before,
        IReadOnlyList<NetworkGas> after,
        GasTolerance tolerance,
        IReadOnlyList<PlannedGasLoss>? planned = null,
        IReadOnlyList<PlannedGasGain>? gains = null)
    {
        var liveBefore = LiveById(before);
        var liveAfter = LiveById(after);
        var pipeBefore = Owners(liveBefore, static network => network.Members);
        var pipeAfter = Owners(liveAfter, static network => network.Members);
        var cellBefore = Owners(liveBefore, static network => network.Cells);
        var cellAfter = Owners(liveAfter, static network => network.Cells);

        var families = new FamilyBuilder();
        foreach (var network in liveBefore.Values)
            if (Changed(network, liveAfter, pipeAfter, tolerance))
            {
                families.Join(network.Ref, network.Members, pipeAfter);
                families.Join(network.Ref, network.Cells, cellAfter);
            }

        foreach (var network in liveAfter.Values)
            if (!liveBefore.ContainsKey(network.Ref))
            {
                families.Join(network.Ref, network.Members, pipeBefore);
                families.Join(network.Ref, network.Cells, cellBefore);
            }

        var grouped = families.Group(liveBefore, liveAfter, tolerance, TypesOf(before, after), SharesOf(planned, false), SharesOf(planned, true), GainsOf(gains));
        var ghosts = new List<NetworkGas>();
        var oldGhosts = new List<NetworkGas>();
        var beforeById = AllById(before);
        foreach (var network in after)
        {
            if (network.Live || tolerance.Negligible(network.Gas))
                continue;
            var wasGhost = beforeById.TryGetValue(network.Ref, out var old) && !old.Live && tolerance.Same(old.Gas, network.Gas);
            (wasGhost ? oldGhosts : ghosts).Add(network);
        }

        ghosts.Sort(static (a, b) => a.Ref.Value.CompareTo(b.Ref.Value));
        oldGhosts.Sort(static (a, b) => a.Ref.Value.CompareTo(b.Ref.Value));
        var located = new List<GasNetworkGhost>(ghosts.Count);
        foreach (var ghost in ghosts)
        {
            var family = grouped.Find(f => f.Contains(ghost.Ref));
            located.Add(new GasNetworkGhost(ghost, family is null ? Option<GasFamily>.None : Option.Some(family)));
        }

        var ok = ghosts.Count == 0 && grouped.TrueForAll(static family => family.Emptied || family.Conserved);
        return new GasAudit(grouped, located, oldGhosts, ok);
    }

    // Gone, now a ghost, other contents, or other pipes (a pipe gone, added, or in another network now).
    private static bool Changed(NetworkGas before, Dictionary<NetworkRef, NetworkGas> liveAfter, Dictionary<ReferenceId, NetworkRef> pipeAfter, GasTolerance tolerance)
    {
        if (!liveAfter.TryGetValue(before.Ref, out var now) || !tolerance.Same(before.Gas, now.Gas) || now.Members.Count != before.Members.Count)
            return true;
        foreach (var pipe in before.Members)
            if (!pipeAfter.TryGetValue(pipe, out var owner) || owner != before.Ref)
                return true;
        return false;
    }

    private static Dictionary<NetworkRef, NetworkGas> LiveById(IReadOnlyList<NetworkGas> networks)
    {
        var byId = new Dictionary<NetworkRef, NetworkGas>(networks.Count);
        foreach (var network in networks)
            if (network.Live)
                byId[network.Ref] = network;
        return byId;
    }

    private static Dictionary<NetworkRef, NetworkGas> AllById(IReadOnlyList<NetworkGas> networks)
    {
        var byId = new Dictionary<NetworkRef, NetworkGas>(networks.Count);
        foreach (var network in networks)
            byId[network.Ref] = network;
        return byId;
    }

    // Which network holds each pipe (or cell).
    private static Dictionary<TKey, NetworkRef> Owners<TKey>(Dictionary<NetworkRef, NetworkGas> networks, Func<NetworkGas, IReadOnlyList<TKey>> keysOf)
        where TKey : notnull
    {
        var owners = new Dictionary<TKey, NetworkRef>();
        foreach (var network in networks.Values)
            foreach (var key in keysOf(network))
                owners[key] = network.Ref;
        return owners;
    }

    // The planned share of each network, deletions (atMost false) or burst releases (atMost true), several losses of
    // one network added (at most all of it).
    private static Dictionary<NetworkRef, double> SharesOf(IReadOnlyList<PlannedGasLoss>? planned, bool atMost)
    {
        var shares = new Dictionary<NetworkRef, double>();
        if (planned is null)
            return shares;
        foreach (var loss in planned)
        {
            if (loss.AtMost != atMost)
                continue;
            shares[loss.Network] = shares.TryGetValue(loss.Network, out var share) ? Math.Min(1.0, share + loss.Share) : loss.Share;
        }

        return shares;
    }

    // Each network's planned gains, added together.
    private static Dictionary<NetworkRef, GasContents> GainsOf(IReadOnlyList<PlannedGasGain>? gains)
    {
        var byNetwork = new Dictionary<NetworkRef, GasContents>();
        if (gains is null)
            return byNetwork;
        foreach (var gain in gains)
            byNetwork[gain.Network] = byNetwork.TryGetValue(gain.Network, out var sum) ? sum.Plus(gain.Gas) : gain.Gas;
        return byNetwork;
    }

    private static int TypesOf(IReadOnlyList<NetworkGas> before, IReadOnlyList<NetworkGas> after) =>
        before.Count > 0 ? before[0].Gas.Types : after.Count > 0 ? after[0].Gas.Types : 0;

    /// <summary>Union-find over networks, keyed by the lowest network of each group.</summary>
    private sealed class FamilyBuilder
    {
        private readonly Dictionary<NetworkRef, NetworkRef> _parent = [];

        public void Join<TKey>(NetworkRef network, IReadOnlyList<TKey> keys, Dictionary<TKey, NetworkRef> otherSide)
            where TKey : notnull
        {
            Root(network);
            foreach (var key in keys)
                if (otherSide.TryGetValue(key, out var other))
                    Union(network, other);
        }

        public List<GasFamily> Group(
            Dictionary<NetworkRef, NetworkGas> liveBefore,
            Dictionary<NetworkRef, NetworkGas> liveAfter,
            GasTolerance tolerance,
            int types,
            Dictionary<NetworkRef, double> plannedShares,
            Dictionary<NetworkRef, double> releaseShares,
            Dictionary<NetworkRef, GasContents> gains)
        {
            var members = new Dictionary<NetworkRef, List<NetworkRef>>();
            var ids = new List<NetworkRef>(_parent.Keys);
            ids.SortIds();
            foreach (var id in ids)
            {
                var root = Root(id);
                if (!members.TryGetValue(root, out var list))
                    members[root] = list = [];
                list.Add(id);
            }

            var families = new List<GasFamily>(members.Count);
            foreach (var group in members.Values)
                families.Add(FamilyOf(group, liveBefore, liveAfter, tolerance, types, plannedShares, releaseShares, gains));
            families.Sort(static (a, b) => Lowest(a).CompareTo(Lowest(b)));
            return families;
        }

        private static GasFamily FamilyOf(
            List<NetworkRef> group,
            Dictionary<NetworkRef, NetworkGas> liveBefore,
            Dictionary<NetworkRef, NetworkGas> liveAfter,
            GasTolerance tolerance,
            int types,
            Dictionary<NetworkRef, double> plannedShares,
            Dictionary<NetworkRef, double> releaseShares,
            Dictionary<NetworkRef, GasContents> gains)
        {
            var before = new List<NetworkGas>();
            var after = new List<NetworkGas>();
            var gasBefore = GasContents.Empty(types);
            var gasAfter = GasContents.Empty(types);
            var plannedLoss = GasContents.Empty(types);
            var plannedRelease = GasContents.Empty(types);
            foreach (var id in group)
            {
                if (liveBefore.TryGetValue(id, out var then))
                {
                    before.Add(then);
                    gasBefore = gasBefore.Plus(then.Gas);

                    // The game's split divides a network's whole mixture by volume: a share of every gas goes.
                    if (plannedShares.TryGetValue(id, out var share))
                        plannedLoss = plannedLoss.Plus(then.Gas.Scaled(share));
                    if (releaseShares.TryGetValue(id, out var released))
                        plannedRelease = plannedRelease.Plus(then.Gas.Scaled(released));

                    // Gas handed in on purpose: what the family should hold grows by it.
                    if (gains.TryGetValue(id, out var gained))
                        plannedLoss = plannedLoss.Minus(gained);
                }

                if (liveAfter.TryGetValue(id, out var now))
                {
                    after.Add(now);
                    gasAfter = gasAfter.Plus(now.Gas);
                }
            }

            return new GasFamily(before, after, gasBefore, gasAfter, plannedLoss, tolerance, plannedRelease);
        }

        private static long Lowest(GasFamily family)
        {
            var lowest = long.MaxValue;
            foreach (var network in family.Before)
                lowest = Math.Min(lowest, network.Ref.Value);
            foreach (var network in family.After)
                lowest = Math.Min(lowest, network.Ref.Value);
            return lowest;
        }

        private NetworkRef Root(NetworkRef id)
        {
            if (!_parent.TryGetValue(id, out var parent))
            {
                _parent[id] = id;
                return id;
            }

            if (parent == id)
                return id;
            var root = Root(parent);
            _parent[id] = root;
            return root;
        }

        private void Union(NetworkRef a, NetworkRef b)
        {
            var rootA = Root(a);
            var rootB = Root(b);
            if (rootA == rootB)
                return;
            if (rootA.Value < rootB.Value)
                _parent[rootB] = rootA;
            else
                _parent[rootA] = rootB;
        }
    }
}

/// <summary>Gas to put into one network to make its family whole again.</summary>
internal sealed record GasRefill(NetworkRef Into, GasContents Gas);

/// <summary>
/// A family's refill held back: putting what it lacks back would take one of its networks over the rating of its
/// weakest pipe, which would burst. The family stays short instead.
/// </summary>
/// <param name="Family">The family.</param>
/// <param name="Lacking">What it lacks, not put back.</param>
/// <param name="Network">The network the refill would have taken furthest over its rating.</param>
/// <param name="PressureAfterKpa">That network's pressure after the refill.</param>
/// <param name="RatingKpa">That network's weakest pipe's rating.</param>
internal sealed record GasRefillWithheld(GasFamily Family, GasContents Lacking, NetworkRef Network, double PressureAfterKpa, double RatingKpa);

/// <summary>The refills to make, and the families whose refill is held back.</summary>
internal sealed record GasRefillPlan(IReadOnlyList<GasRefill> Refills, IReadOnlyList<GasRefillWithheld> Withheld);

/// <summary>
/// How a family that lost gas is made whole: what it lacks, gas by gas (its floor minus contents after, each gas's
/// positive part; a planned loss is not lacking, nor is a planned burst's release), goes into the family's live
/// networks by volume, as the game's own split divides a network's contents (<c>NetworkAtmosphereEvent.Apply</c>). A
/// family that gained gas, or has no live network, gets nothing. A refill never takes a network over the rating of its
/// weakest pipe: when any of the family's networks would end above it, the family gets nothing and stays short, since a
/// burst pipe vents everything.
/// </summary>
internal static class GasRefills
{
    /// <summary>
    /// The refills, each network's pressure after its share judged by <paramref name="pressureAfterKpa"/> (the network
    /// with the given contents in its own volume). Networks without a rating are not judged.
    /// </summary>
    public static GasRefillPlan Plan(GasAudit audit, GasTolerance tolerance, Func<NetworkGas, GasContents, double> pressureAfterKpa)
    {
        var refills = new List<GasRefill>();
        var withheld = new List<GasRefillWithheld>();
        foreach (var family in audit.Families)
        {
            if (family.Conserved || family.Emptied)
                continue;

            // Below the floor only: a planned burst's release is expected gone, not put back.
            var lacking = family.Floor.Lacking(family.GasAfter);
            if (tolerance.Negligible(lacking))
                continue;
            var own = Spread(family, lacking);
            var over = Worst(family, lacking, own, pressureAfterKpa);
            if (over.TryGet(out var held))
                withheld.Add(held);
            else
                refills.AddRange(own);
        }

        return new GasRefillPlan(refills, withheld);
    }

    /// <summary>The refills of families whose networks carry no rating (every refill goes).</summary>
    public static IReadOnlyList<GasRefill> For(GasAudit audit, GasTolerance tolerance) =>
        Plan(audit, tolerance, static (_, _) => 0.0).Refills;

    private static List<GasRefill> Spread(GasFamily family, GasContents lacking)
    {
        var volumes = new double[family.After.Count];
        for (var index = 0; index < volumes.Length; index++)
            volumes[index] = family.After[index].VolumeL;
        var shares = GasShares.ByVolume(volumes);
        var refills = new List<GasRefill>(shares.Length);
        for (var index = 0; index < shares.Length; index++)
            refills.Add(new GasRefill(family.After[index].Ref, lacking.Scaled(shares[index])));
        return refills;
    }

    // The network the refill takes furthest over its rating; none when every one stays at or under it.
    private static Option<GasRefillWithheld> Worst(GasFamily family, GasContents lacking, List<GasRefill> refills, Func<NetworkGas, GasContents, double> pressureAfterKpa)
    {
        var worst = Option<GasRefillWithheld>.None;
        var worstRatio = 1.0;
        for (var index = 0; index < refills.Count; index++)
        {
            var network = family.After[index];
            if (!network.RatingKpa.TryGet(out var rating) || rating <= 0.0)
                continue;
            var after = pressureAfterKpa(network, network.Gas.Plus(refills[index].Gas));
            var ratio = after / rating;
            if (ratio > worstRatio)
            {
                worst = Option.Some(new GasRefillWithheld(family, lacking, network.Ref, after, rating));
                worstRatio = ratio;
            }
        }

        return worst;
    }
}
