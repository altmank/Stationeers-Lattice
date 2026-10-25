using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>
/// A piece's ports placed at a cell and turn (<see cref="IPieceCatalogue.PortsAt"/>): each kept end of its catalogue
/// facts, turned as <see cref="Shapes"/> turns it for runs and routes, with the small cell a joining piece takes one small
/// cell out. What does not turn (index, kinds, name, role) is made once per piece. Main thread.
/// </summary>
internal sealed class PiecePorts(ICatalogue catalogue, Shapes shapes)
{
    private readonly Dictionary<PrefabKey, PortFacts[]> _facts = [];

    /// <summary>The ports of a catalogue's pieces on the game's grid.</summary>
    public static PiecePorts Over(ICatalogue catalogue) => new(catalogue, new Shapes(catalogue, new CubeGrid()));

    /// <summary>
    /// The ports in the piece's <c>OpenEnds</c> order; the catalogue's refusal for a piece it does not hold. Linear in
    /// the piece's ports; allocates the list and its ports.
    /// </summary>
    public Result<ValueList<Port>, Reason> At(Placement placement)
    {
        var found = catalogue.Piece(placement.Piece);
        if (found.TryGetError(out var why) || !found.TryGet(out var piece))
            return Result<ValueList<Port>, Reason>.Error(why);
        var facts = FactsOf(piece);
        if (facts.Length == 0)
            return Result<ValueList<Port>, Reason>.Ok(ValueList<Port>.Empty);
        var ports = new Port[facts.Length];
        for (var i = 0; i < facts.Length; i++)
        {
            var (from, toward) = shapes.Leaving(placement, piece.Ends[i]);
            var joins = shapes.Grid.Neighbour(from, toward, GridSize.Small);
            var fact = facts[i];
            ports[i] = new Port(fact.Index, fact.Kinds, fact.GameName, fact.Role, from, toward.ToFace(), joins);
        }

        return Result<ValueList<Port>, Reason>.Ok(ValueList<Port>.Own(ports));
    }

    private PortFacts[] FactsOf(PieceFacts piece)
    {
        if (_facts.TryGetValue(piece.Key, out var known))
            return known;
        var facts = new PortFacts[piece.Ends.Count];
        for (var i = 0; i < facts.Length; i++)
        {
            var end = piece.Ends[i];
            facts[i] = new PortFacts(
                PortIndex.Known(end.GameIndex.Reduce(i)),
                PortNames.KindsOf(end.Kinds),
                end.GameName.Reduce(() => PortNames.GameName(end.Kinds)),
                end.Role.Map(static role => (PortRole)(int)role));
        }

        _facts[piece.Key] = facts;
        return facts;
    }

    private sealed record PortFacts(PortIndex Index, ValueSet<PortKind> Kinds, string GameName, Option<PortRole> Role);
}

/// <summary>The contract's words for an end's network kinds, and the game's name for them.</summary>
internal static class PortNames
{
    /// <summary>How many members the game's <c>NetworkType</c> names past <c>None</c> and <c>All</c>.</summary>
    private const int GameMemberCount = 10;

    /// <summary>The game's <c>NetworkType</c> members, highest first, as its flags <c>ToString</c> takes them (Assets.Scripts.Objects/NetworkType.cs).</summary>
    private static (int Bits, string Name) GameMember(int highestFirst) => highestFirst switch
    {
        0 => (0x100, "RoboticArmRail"),
        1 => (0x80, "LaunchPad"),
        2 => (0x40, "LandingPad"),
        3 => (0x20, "PipeLiquid"),
        4 => (0x10, "Elevator"),
        5 => (0x8, "Chute"),
        6 => (0x6, "PowerAndData"),
        7 => (0x4, "Data"),
        8 => (0x2, "Power"),
        _ => (0x1, "Pipe"),
    };

    /// <summary>The kinds as the contract names them; a modded kind by its bits.</summary>
    public static ValueSet<PortKind> KindsOf(ValueSet<NetworkType> kinds)
    {
        var words = new List<PortKind>(kinds.Count);
        foreach (var kind in kinds)
            words.Add(KindOf(kind));
        return ValueSet.From(words);
    }

    public static PortKind KindOf(NetworkType kind) => kind switch
    {
        NetworkType.Power => PortKinds.Power,
        NetworkType.Data => PortKinds.Data,
        NetworkType.GasPipe => PortKinds.GasPipe,
        NetworkType.LiquidPipe => PortKinds.LiquidPipe,
        NetworkType.Chute => PortKinds.Chute,
        NetworkType.Elevator => PortKinds.Elevator,
        NetworkType.LandingPad => PortKinds.LandingPad,
        NetworkType.LaunchPad => PortKinds.LaunchPad,
        NetworkType.Rail => PortKinds.Rail,
        NetworkType.Unknown unknown => PortKind.OfBits(unknown.Bits),
        _ => PortKind.OfBits(0),
    };

    /// <summary>
    /// The name the game's flags enum gives the kinds' bits: its members joined by <c>", "</c> lowest first, taking
    /// the highest that fit first (so power and data read <c>PowerAndData</c>), or the number when bits are left over.
    /// </summary>
    public static string GameName(ValueSet<NetworkType> kinds)
    {
        var bits = Bits(kinds);
        var left = bits;
        var names = new List<string>(2);
        for (var i = 0; i < GameMemberCount; i++)
        {
            var (memberBits, name) = GameMember(i);
            if ((left & memberBits) == memberBits)
            {
                names.Add(name);
                left &= ~memberBits;
            }
        }

        if (left != 0 || names.Count == 0)
            return bits.ToString(CultureInfo.InvariantCulture);
        names.Reverse();
        return string.Join(", ", names);
    }

    private static int Bits(ValueSet<NetworkType> kinds)
    {
        var bits = 0;
        foreach (var kind in kinds)
            bits |= kind switch
            {
                NetworkType.GasPipe => 0x1,
                NetworkType.Power => 0x2,
                NetworkType.Data => 0x4,
                NetworkType.Chute => 0x8,
                NetworkType.Elevator => 0x10,
                NetworkType.LiquidPipe => 0x20,
                NetworkType.LandingPad => 0x40,
                NetworkType.LaunchPad => 0x80,
                NetworkType.Rail => 0x100,
                NetworkType.Unknown unknown => unknown.Bits,
                _ => 0,
            };
        return bits;
    }
}
