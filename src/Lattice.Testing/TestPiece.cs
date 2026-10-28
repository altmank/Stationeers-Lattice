using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lattice.Api;
using Lattice.TestKit.Catalogue;

namespace Lattice.Testing;

/// <summary>
/// What a test piece takes in a cell: on the large grid the centre or one face, on the small grid one slot per
/// kind, as the game places them.
/// </summary>
public enum TestSlot
{
    /// <summary>A large cell's centre (a frame).</summary>
    Centre = 1,

    /// <summary>A large cell's +X face.</summary>
    FaceXPlus = 2,

    /// <summary>A large cell's -X face.</summary>
    FaceXMinus = 3,

    /// <summary>A large cell's +Y face.</summary>
    FaceYPlus = 4,

    /// <summary>A large cell's -Y face.</summary>
    FaceYMinus = 5,

    /// <summary>A large cell's +Z face.</summary>
    FaceZPlus = 6,

    /// <summary>A large cell's -Z face.</summary>
    FaceZMinus = 7,

    /// <summary>A small cell's cable slot.</summary>
    Cable = 8,

    /// <summary>A small cell's pipe slot.</summary>
    Pipe = 9,

    /// <summary>A small cell's chute slot.</summary>
    Chute = 10,

    /// <summary>A small cell's device slot.</summary>
    Device = 11,

    /// <summary>A small cell's rail slot.</summary>
    Rail = 12,

    /// <summary>A small cell's other slot.</summary>
    Other = 13,
}

/// <summary>
/// A network an end of a test piece joins: the game's kinds, or a mod's own kind by its game type name and bits.
/// <c>default</c> is no network; a piece with such an end is refused at <see cref="TestWorld.Start"/>.
/// </summary>
public readonly record struct TestNetwork
{
    private TestNetwork(string word) => Word = word;

    /// <summary>The catalogue file's word for it (<c>power</c>, <c>mod:&lt;type&gt;:&lt;bits&gt;</c>).</summary>
    public string Word { get; }

    /// <summary>Power cables.</summary>
    public static TestNetwork Power => new("power");

    /// <summary>Data cables.</summary>
    public static TestNetwork Data => new("data");

    /// <summary>Gas pipes.</summary>
    public static TestNetwork Gas => new("gas");

    /// <summary>Liquid pipes.</summary>
    public static TestNetwork Liquid => new("liquid");

    /// <summary>Chutes.</summary>
    public static TestNetwork Chute => new("chute");

    /// <summary>Elevator shafts.</summary>
    public static TestNetwork Elevator => new("elevator");

    /// <summary>Landing pads.</summary>
    public static TestNetwork LandingPad => new("landing-pad");

    /// <summary>Launch pads.</summary>
    public static TestNetwork LaunchPad => new("launch-pad");

    /// <summary>Robotic arm rails.</summary>
    public static TestNetwork Rail => new("rail");

    /// <summary>
    /// A mod's own network kind, as the game's <c>NetworkType</c> holds it: its type name and bits. Refused with
    /// <see cref="ReasonCodes.InvalidValue"/> for a blank name, a name with a space or colon, or bits below 1.
    /// </summary>
    public static Result<TestNetwork, Reason> Modded(string gameTypeName, int bits)
    {
        if (!Words.Valid(gameTypeName) || gameTypeName.IndexOf(':') >= 0 || bits < 1)
            return Result<TestNetwork, Reason>.Error(TestCodes.Refuse(ReasonCodes.InvalidValue, "network"));
        return Result<TestNetwork, Reason>.Ok(new TestNetwork("mod:" + gameTypeName + ":" + bits.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>
/// A modded piece a scenario adds to its catalogue, by its facts: name, display name, kit and option, other kits,
/// grid, family, build steps, ends, footprint, small-grid collision and support group. Lattice treats it as it treats
/// any piece the game holds, modded ones included. The <c>With...</c> methods return new values; what does not
/// fit together (an end with no network, a name the catalogue holds already, an option another piece takes, a kit whose
/// options leave a gap) is refused at <see cref="TestWorld.Start"/> with a reason naming it.
/// </summary>
public sealed record TestPiece
{
    private TestPiece(string name, string kit, PieceGrid grid)
    {
        Name = name;
        Kit = kit;
        Grid = grid;
    }

    /// <summary>The prefab name; its key is the game's hash of it.</summary>
    public string Name { get; }

    /// <summary>The item name of the kit that places it.</summary>
    public string Kit { get; }

    /// <summary>The grid it sits on.</summary>
    public PieceGrid Grid { get; }

    /// <summary>Its display name (<see cref="PieceEntry.Name"/>); <see cref="Name"/> unless given.</summary>
    public string DisplayName
    {
        get => _displayName ?? Name;
        private init => _displayName = value;
    }

    private readonly string? _displayName;

    /// <summary>Its option in <see cref="Kit"/>; none for the kit's next free option.</summary>
    public Option<int> ChosenOption { get; private init; }

    /// <summary>The other kits that place it, each with the piece's option there, in the order given.</summary>
    public ValueList<TestKitPlace> OtherKits { get; private init; } = ValueList<TestKitPlace>.Empty;

    /// <summary>The piece's key: the game's prefab hash of <see cref="Name"/>.</summary>
    public PrefabKey Key => PrefabHash.Piece(Name);

    /// <summary>The kit's key: the game's prefab hash of <see cref="Kit"/>.</summary>
    public KitKey KitKey => PrefabHash.Kit(Kit);

    /// <summary>The family it is drawn and filtered by; <see cref="PieceFamilies.Other"/> unless given.</summary>
    public PieceFamily Family { get; private init; } = PieceFamilies.Other;

    /// <summary>How many build steps it has; each takes one kit and the wrench takes it back. 1 unless given.</summary>
    public int Steps { get; private init; } = 1;

    /// <summary>The support rule group that holds it up (none: it needs no support).</summary>
    public Option<RuleId> SupportGroup { get; private init; }

    /// <summary>The small-grid collision bits (<c>SmallCollisionType</c>); 0 unless given.</summary>
    public int Collision { get; private init; }

    internal ValueList<string> EndLines { get; private init; } = ValueList<string>.Empty;

    internal ValueList<string> CellLines { get; private init; } = ValueList<string>.Empty;

    /// <summary>
    /// A piece of this name, placed by this kit, on this grid. Refused with <see cref="ReasonCodes.InvalidValue"/> for a
    /// blank name or kit, or one holding a space or <c>=</c>.
    /// </summary>
    public static Result<TestPiece, Reason> Create(string name, string kit, PieceGrid grid)
    {
        if (!Words.Valid(name) || name.IndexOf('=') >= 0)
            return Result<TestPiece, Reason>.Error(TestCodes.Refuse(ReasonCodes.InvalidValue, "piece-name"));
        if (!Words.Valid(kit) || kit.IndexOf('=') >= 0)
            return Result<TestPiece, Reason>.Error(TestCodes.Refuse(ReasonCodes.InvalidValue, "kit-name"));
        if (grid is not (PieceGrid.Small or PieceGrid.Large))
            return Result<TestPiece, Reason>.Error(TestCodes.Refuse(ReasonCodes.InvalidValue, "grid"));
        return Result<TestPiece, Reason>.Ok(new TestPiece(name, kit, grid));
    }

    /// <summary>This piece at an option of its kit (0 or more) instead of the kit's next free one.</summary>
    public TestPiece AtOption(int option) => this with { ChosenOption = Option.Some(option) };

    /// <summary>This piece also placed by another kit (an item name, no space, <c>=</c>, <c>:</c> or <c>,</c>) at an option there.</summary>
    public TestPiece AlsoIn(string kit, int option) => this with { OtherKits = Words.Plus(OtherKits, new TestKitPlace(kit, option)) };

    /// <summary>This piece with a display name (any text).</summary>
    public TestPiece Named(string displayName) => this with { DisplayName = displayName ?? "" };

    /// <summary>The first problem with its own words, as a reason; none when it can be written.</summary>
    internal Option<Reason> Problem()
    {
        if (ChosenOption.TryGet(out var own) && own < 0)
            return Option.Some(TestCodes.Refuse(ReasonCodes.InvalidValue, ValueMap.Of("piece", Name).With("problem", "option")));
        foreach (var place in OtherKits)
            if (!Words.Valid(place.Kit) || place.Kit.IndexOfAny(new[] { '=', ':', ',' }) >= 0 || place.Option < 0 || place.Kit == Kit)
                return Option.Some(TestCodes.Refuse(ReasonCodes.InvalidValue, ValueMap.Of("piece", Name).With("problem", "also-in").With("kit", place.Kit ?? "")));
        return Option<Reason>.None;
    }

    /// <summary>This piece in a family (a mod's own, or one of <see cref="PieceFamilies"/>).</summary>
    public TestPiece InFamily(PieceFamily family) => this with { Family = family };

    /// <summary>This piece with <paramref name="count"/> build steps (1 or more).</summary>
    public TestPiece WithSteps(int count) => this with { Steps = count };

    /// <summary>This piece held up by a support rule group (a <see cref="SupportRuleDescriptor"/>'s group).</summary>
    public TestPiece SupportedBy(RuleId group) => this with { SupportGroup = Option.Some(group) };

    /// <summary>
    /// This piece with small-grid collision bits (0 to 31): two pieces sharing a small cell clash when their bits meet.
    /// The game's bits: pipes 1, cables 2, devices 4, covers 8, rails 16; its small devices are 23, so no cable or pipe
    /// shares their cells.
    /// </summary>
    public TestPiece WithCollision(int bits) => this with { Collision = bits };

    /// <summary>
    /// This piece with one more connection end: at an offset in decimetres from its own cell, unturned, facing a grid
    /// direction, joining one network.
    /// </summary>
    public TestPiece WithEnd(CellOffset at, SignedAxis facing, TestNetwork network) => WithEnd(at, facing, ValueSet.Of(network));

    /// <summary>This piece with one more connection end joining several networks (a cable end joins power and data).</summary>
    public TestPiece WithEnd(CellOffset at, SignedAxis facing, ValueSet<TestNetwork> networks) => this with { EndLines = Words.Plus(EndLines, EndLine(at, facing, networks)) };

    /// <summary>
    /// This piece with one more connection end that does a job for the device (<see cref="Port.Role"/>), joining
    /// several networks. Its <see cref="Port.Index"/> is its place among the piece's ends.
    /// </summary>
    public TestPiece WithPort(CellOffset at, SignedAxis facing, ValueSet<TestNetwork> networks, PortRole role) =>
        this with { EndLines = Words.Plus(EndLines, EndLine(at, facing, networks) + " role=" + RoleWord(role)) };

    private static string EndLine(CellOffset at, SignedAxis facing, ValueSet<TestNetwork> networks)
    {
        var words = new List<string>(networks.Count);
        foreach (var network in networks)
            words.Add(network.Word ?? "");
        words.Sort(System.StringComparer.Ordinal);
        return $"end {Offset(at)} {Direction(facing)} {string.Join(",", words)}";
    }

    private static string RoleWord(PortRole role) => role switch
    {
        PortRole.Input => "input",
        PortRole.Input2 => "input2",
        PortRole.Output => "output",
        PortRole.Output2 => "output2",
        PortRole.Waste => "waste",
        _ => "none",
    };

    /// <summary>This piece taking one more cell: an offset in decimetres from its own cell, unturned, and the slot there.</summary>
    public TestPiece WithCell(CellOffset at, TestSlot slot) =>
        this with { CellLines = Words.Plus(CellLines, $"cell {Offset(at)} {SlotWord(slot)}") };

    /// <summary>The piece in the catalogue file format, its kit option given.</summary>
    internal string Text(int option)
    {
        var text = new StringBuilder(256);
        text.Append("piece ").Append(Name)
            .Append(" kit=").Append(Kit)
            .Append(" option=").Append(option.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < OtherKits.Count; i++)
            text.Append(i == 0 ? " also=" : ",").Append(OtherKits[i].Kit).Append(':').Append(OtherKits[i].Option.ToString(CultureInfo.InvariantCulture));
        text.Append(" grid=").Append(Grid == PieceGrid.Small ? "small" : "large");
        if (_displayName is not null)
            text.Append(" name=").Append(Lattice.Core.Catalogue.CatalogueText.Name(DisplayName));
        if (Family != PieceFamilies.Other)
            text.Append(" family=").Append(Family.Value);
        if (SupportGroup.TryGet(out var group))
            text.Append(" support=").Append(group.Value);
        text.Append('\n');
        for (var i = 0; i < Steps; i++)
            text.Append("  step material=").Append(Kit).Append("*1 exit=ItemWrench air=no\n");
        foreach (var end in EndLines)
            text.Append("  ").Append(end).Append('\n');
        foreach (var cell in CellLines)
            text.Append("  ").Append(cell).Append('\n');
        if (Collision != 0)
            text.Append("  collision ").Append(Collision.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return text.ToString();
    }

    private static string Offset(CellOffset at) =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}", at.X, at.Y, at.Z);

    private static string Direction(SignedAxis facing) => (facing.Axis, facing.Sign) switch
    {
        (Axis.X, AxisSign.Plus) => "x+",
        (Axis.X, AxisSign.Minus) => "x-",
        (Axis.Y, AxisSign.Plus) => "y+",
        (Axis.Y, AxisSign.Minus) => "y-",
        (Axis.Z, AxisSign.Plus) => "z+",
        (Axis.Z, AxisSign.Minus) => "z-",
        _ => "none",
    };

    private static string SlotWord(TestSlot slot) => slot switch
    {
        TestSlot.Centre => "centre",
        TestSlot.FaceXPlus => "face-x+",
        TestSlot.FaceXMinus => "face-x-",
        TestSlot.FaceYPlus => "face-y+",
        TestSlot.FaceYMinus => "face-y-",
        TestSlot.FaceZPlus => "face-z+",
        TestSlot.FaceZMinus => "face-z-",
        TestSlot.Cable => "cable",
        TestSlot.Pipe => "pipe",
        TestSlot.Chute => "chute",
        TestSlot.Device => "device",
        TestSlot.Rail => "rail",
        TestSlot.Other => "other",
        _ => "none",
    };
}

/// <summary>Another kit that places a test piece, by its item name, and the piece's option there.</summary>
/// <param name="Kit">The kit's item name.</param>
/// <param name="Option">The piece's option in it, from 0.</param>
public sealed record TestKitPlace(string Kit, int Option);

/// <summary>Names the host writes into a catalogue file line: not blank, no white space.</summary>
internal static class Words
{
    /// <summary>A new list: <paramref name="list"/> then <paramref name="item"/>.</summary>
    public static ValueList<T> Plus<T>(ValueList<T> list, T item)
    {
        var items = new List<T>(list.Count + 1);
        foreach (var held in list)
            items.Add(held);
        items.Add(item);
        return ValueList.From(items);
    }

    public static bool Valid(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var c in text!)
            if (char.IsWhiteSpace(c))
                return false;
        return true;
    }
}
