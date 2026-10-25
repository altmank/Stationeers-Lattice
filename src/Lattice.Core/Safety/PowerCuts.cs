using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Consequences;
using Lattice.Core.Ports;

namespace Lattice.Core.Safety;

/// <summary>
/// Cutting power to air, heating or life support. The plan's removals and swaps are grouped by the power network of
/// what they take out (a swap takes its piece out before the new one goes in, so it cuts while it is done). On each
/// such network the pieces and devices left are linked again by the game's connection rule (<see cref="Connectivity"/>
/// on the catalogue's ends); a device that keeps air, heating or life support going warns when the part it is left on
/// holds no supplier, while the network held one before. A network with no supplier known is left alone: there is no
/// power to cut. One warning per network, naming every ghost that takes from it and the devices cut off.
/// </summary>
internal static class PowerCuts
{
    /// <summary>Notes what the ghost takes out under the network it is on (<see cref="SafetyScan.PowerOrder"/>).</summary>
    public static void Gather(SafetyScan scan, Ghost ghost)
    {
        if (!SafetyScan.Taken(ghost).TryGet(out var target) || !scan.World.NetworkOf(target.Id).TryGet(out var network))
            return;
        if (!scan.TakenOn.TryGetValue(network, out var taken))
        {
            scan.TakenOn[network] = taken = new List<(ReferenceId, GhostId)>(1);
            scan.PowerOrder.Add(network);
        }

        taken.Add((target.Id, ghost.Id));
    }

    /// <summary>Checks one network the plan takes from, once every ghost is gathered.</summary>
    public static void Check(SafetyScan scan, NetworkRef network)
    {
        var taken = scan.TakenOn[network];
        if (!scan.World.Network(network).TryGet(out var info) || !info.Kinds.Contains(new NetworkType.Power()))
            return;
        var gone = new HashSet<ReferenceId>();
        foreach (var (id, _) in taken)
            gone.Add(id);

        var kept = new List<(ReferenceId Id, Keeps Keeps)>();
        var suppliers = new List<ReferenceId>();
        var suppliedBefore = false;
        foreach (var device in info.Devices)
        {
            if (!scan.World.Structure(device).TryGet(out var structure))
                continue;
            if (scan.Facts.Supplies(structure.Ref.Piece))
            {
                suppliedBefore = true;
                if (!gone.Contains(device))
                    suppliers.Add(device);
            }

            var keeps = scan.Facts.Keeps(structure.Ref.Piece);
            if (keeps != Keeps.None && !gone.Contains(device))
                kept.Add((device, keeps));
        }

        if (kept.Count == 0 || !suppliedBefore)
            return;

        var part = PartsWithout(scan, info, gone);
        var cut = new List<ReferenceId>();
        var what = Keeps.None;
        foreach (var (device, keeps) in kept)
        {
            var mine = part.TryGetValue(device, out var p) ? p : -1;
            var fed = false;
            foreach (var supplier in suppliers)
                if (mine >= 0 && part.TryGetValue(supplier, out var theirs) && theirs == mine)
                    fed = true;
            if (fed)
                continue;
            cut.Add(device);
            what |= keeps;
        }

        if (cut.Count == 0)
            return;
        cut.SortIds();
        var ghosts = new List<GhostId>(taken.Count);
        foreach (var (_, ghost) in taken)
            ghosts.Add(ghost);
        scan.Warnings.Add(SafetyCodes.CutsPower, "network " + SafetyScan.Text(network.Value), new Dictionary<string, string>
        {
            ["network"] = SafetyScan.Text(network.Value),
            ["devices"] = IdOrder.Text(cut),
            ["keeps"] = KeepsText(what),
        }, ghosts);
    }

    /// <summary>The part of the network each piece and device left falls in, by the game's connection rule.</summary>
    private static Dictionary<ReferenceId, int> PartsWithout(SafetyScan scan, NetworkInfo info, HashSet<ReferenceId> gone)
    {
        var models = new List<PieceModel>(info.Pieces.Count + info.Devices.Count);
        var ids = new HashSet<ReferenceId>();
        Add(scan, info.Pieces, gone, models, ids);
        Add(scan, info.Devices, gone, models, ids);

        var parent = new Dictionary<ReferenceId, ReferenceId>(models.Count);
        foreach (var model in models)
            parent[model.Id] = model.Id;
        foreach (var link in Connectivity.LinksTouching(models, ids))
            Union(parent, link.From, link.To);

        var part = new Dictionary<ReferenceId, int>(models.Count);
        var numbers = new Dictionary<ReferenceId, int>();
        foreach (var model in models)
        {
            var root = Find(parent, model.Id);
            if (!numbers.TryGetValue(root, out var number))
                numbers[root] = number = numbers.Count;
            part[model.Id] = number;
        }

        return part;
    }

    private static void Add(SafetyScan scan, ValueList<ReferenceId> members, HashSet<ReferenceId> gone, List<PieceModel> models, HashSet<ReferenceId> ids)
    {
        foreach (var id in members)
            if (!gone.Contains(id) && ids.Add(id) && scan.Models.Piece(id).TryGet(out var model))
                models.Add(model);
    }

    private static ReferenceId Find(Dictionary<ReferenceId, ReferenceId> parent, ReferenceId id)
    {
        var root = id;
        while (parent[root] != root)
            root = parent[root];
        while (parent[id] != root)
        {
            var next = parent[id];
            parent[id] = root;
            id = next;
        }

        return root;
    }

    private static void Union(Dictionary<ReferenceId, ReferenceId> parent, ReferenceId a, ReferenceId b)
    {
        if (!parent.ContainsKey(a) || !parent.ContainsKey(b))
            return;
        var ra = Find(parent, a);
        var rb = Find(parent, b);
        if (ra != rb)
            parent[ra.Value < rb.Value ? rb : ra] = ra.Value < rb.Value ? ra : rb;
    }

    private static string KeepsText(Keeps keeps)
    {
        var words = new List<string>(3);
        if ((keeps & Keeps.Air) != 0)
            words.Add("air");
        if ((keeps & Keeps.Heat) != 0)
            words.Add("heat");
        if ((keeps & Keeps.Life) != 0)
            words.Add("life");
        return string.Join(",", words);
    }
}
