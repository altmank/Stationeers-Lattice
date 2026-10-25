using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Consequences;
using Lattice.Core.Ports;
using Lattice.Core.Support;

namespace Lattice.Core.Safety;

/// <summary>
/// Gas let out of pipes. Taking a pipe apart does not release its gas: the game divides the network's gas over the
/// networks left (<c>Pipe.OnDestroy</c>, <c>NetworkAtmosphereEvent.DivideNetworkAtmosphere</c>, decompile
/// Assets.Scripts.Objects.Pipes/Pipe.cs, Assets.Scripts/NetworkAtmosphereEvent.cs). Gas reaches the air through a
/// vent, a pipe piece that mixes its network with the air of its cell (<c>PassiveVent : Pipe</c>,
/// Assets.Scripts.Objects.Pipes/PassiveVent.cs). So each planned vent is followed through the planned world (its own
/// ends, then planned pipes, up to <see cref="MostFollowed"/> pieces) to the built networks it joins; for each one
/// holding gas: gas-indoors when the vent stands in a room, oxidiser-outside when it stands outside and the gas holds
/// oxidiser, hot-gas when the gas is hot. A planned piece is found in the cell it stands in (its own cell).
/// </summary>
internal static class Vents
{
    /// <summary>How many planned pieces are followed behind one vent.</summary>
    public const int MostFollowed = 4096;

    /// <summary>The whole visit of one ghost.</summary>
    public static void Visit(SafetyScan scan, Ghost ghost)
    {
        var reads = int.MaxValue;
        Follow? follow = null;
        Visit(scan, ghost, ref follow, ref reads);
    }

    /// <summary>
    /// Visits one ghost, following a vent behind it a bounded number of cell reads at a time: one read off
    /// <paramref name="reads"/> per cell an end reaches into. <paramref name="follow"/> holds the walk between calls (none
    /// to start a ghost). True once the ghost is done; its warnings come out as a whole visit gives them.
    /// </summary>
    public static bool Visit(SafetyScan scan, Ghost ghost, ref Follow? follow, ref int reads)
    {
        if (follow is null)
        {
            reads--;
            if (!SafetyScan.Put(ghost).TryGet(out var placement) || !scan.Facts.Vents(placement.Piece)
                || !scan.Piece(placement.Piece).TryGet(out var piece))
                return true;
            var vent = scan.Models.Piece(SupportWorld.AssumedInfo(ghost.Id, placement), piece);
            follow = new Follow(vent, scan.RoomAt(scan.LargeOf(placement.At, piece.Grid)));
        }

        if (!follow.Walk(scan, ref reads))
            return false;
        foreach (var network in follow.Networks)
            Report(scan, ghost, network, follow.Room, scan.Oxidisers);
        follow = null;
        return true;
    }

    private static void Report(SafetyScan scan, Ghost ghost, NetworkRef network, Option<RoomInfo> room, HashSet<GasKind> oxidisers)
    {
        if (!scan.World.Network(network).TryGet(out var info) || !info.Contents.TryGet(out var contents))
            return;
        var moles = Oxidisers.Total(contents);
        if (moles <= 0.0)
            return;
        var about = "network " + SafetyScan.Text(network.Value);
        if (room.TryGet(out var inside))
            scan.Warnings.Add(SafetyCodes.GasIndoors, about, new Dictionary<string, string>
            {
                ["network"] = SafetyScan.Text(network.Value),
                ["room"] = SafetyScan.Text(inside.Ref.Value),
                ["mol"] = SafetyScan.Text(moles),
            }, ghost.Id);
        var oxidiser = Oxidisers.In(contents, oxidisers);
        if (room.IsNone && oxidiser > 0.0)
            scan.Warnings.Add(SafetyCodes.OxidiserOutside, about, new Dictionary<string, string>
            {
                ["network"] = SafetyScan.Text(network.Value),
                ["oxidiser-mol"] = SafetyScan.Text(oxidiser),
            }, ghost.Id);
        if (scan.Facts.Temperature(network).TryGet(out var kelvin) && kelvin.Value >= scan.Limits.HotGas.Value)
            scan.Warnings.Add(SafetyCodes.HotGas, about, new Dictionary<string, string>
            {
                ["network"] = SafetyScan.Text(network.Value),
                ["kelvin"] = SafetyScan.Text(kelvin.Value),
            }, ghost.Id);
    }

    /// <summary>The built networks the vent joins in the planned world, in the order found.</summary>
    public static List<NetworkRef> Joined(SafetyScan scan, PieceModel vent)
    {
        var follow = new Follow(vent, Option<RoomInfo>.None);
        var reads = int.MaxValue;
        follow.Walk(scan, ref reads);
        return follow.Networks;
    }

    /// <summary>
    /// A walk from a vent through planned pieces whose ends link (up to <see cref="MostFollowed"/> pieces), collecting
    /// the built networks it reaches in the order found; it can stop between pieces and go on later.
    /// </summary>
    public sealed class Follow
    {
        private readonly HashSet<ReferenceId> _seen;
        private readonly Queue<PieceModel> _next = new();
        private int _followed;

        public Follow(PieceModel vent, Option<RoomInfo> room)
        {
            Room = room;
            _seen = new HashSet<ReferenceId> { vent.Id };
            _next.Enqueue(vent);
        }

        /// <summary>The room the vent stands in; none outside.</summary>
        public Option<RoomInfo> Room { get; }

        /// <summary>The built networks reached so far.</summary>
        public List<NetworkRef> Networks { get; } = new(1);

        /// <summary>Follows pieces until done (true) or until no reads are left between pieces (false).</summary>
        public bool Walk(SafetyScan scan, ref int reads)
        {
            while (_next.Count > 0 && _followed < MostFollowed)
            {
                if (reads <= 0)
                    return false;
                _followed++;
                var here = _next.Dequeue();
                foreach (var end in here.Ends)
                {
                    reads--;
                    foreach (var info in scan.Planned.At(end.Into))
                    {
                        if (_seen.Contains(info.Ref.Id) || !scan.Piece(info.Ref.Piece).TryGet(out var facts))
                            continue;
                        var there = scan.Models.Piece(info, facts);
                        if (!Connectivity.Links(here, there) && !Connectivity.Links(there, here))
                            continue;
                        _seen.Add(info.Ref.Id);
                        if (scan.Planned.AssumedFrom(info.Ref).IsSome)
                            _next.Enqueue(there);
                        else if (scan.World.NetworkOf(info.Ref.Id).TryGet(out var network) && !Networks.Contains(network))
                            Networks.Add(network);
                    }
                }
            }

            return true;
        }
    }
}
