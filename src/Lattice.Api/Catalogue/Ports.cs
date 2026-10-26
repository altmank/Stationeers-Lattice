using System.Globalization;

namespace Lattice.Api;

/// <summary>
/// One connection port of a piece placed at a cell and turn (<see cref="IPieceCatalogue.PortsAt"/>).
/// Added in contract 0.8.
/// </summary>
/// <param name="Index">Its index in the piece's game <c>OpenEnds</c> list: the index the game and a built structure's port use.</param>
/// <param name="Kinds">The networks it joins; a cable end joins power and data. Never empty.</param>
/// <param name="GameName">
/// The game's own name for its kinds (its <c>ConnectionType</c>, such as <c>PowerAndData</c> or <c>Pipe</c>); the
/// same on every seat.
/// </param>
/// <param name="Role">What it does for its device; none for a plain run end.</param>
/// <param name="From">The small cell it leaves from, inside the piece (the game's <c>GetFacingGrid</c>).</param>
/// <param name="Toward">The way it points out of the piece.</param>
/// <param name="Joins">
/// The small cell a joining piece takes: <paramref name="From"/> one small cell <paramref name="Toward"/> (the game's
/// <c>GetLocalGrid</c>). A run that starts or ends here, leaving back toward the port, joins it.
/// </param>
public sealed record Port(
    PortIndex Index,
    ValueSet<PortKind> Kinds,
    string GameName,
    Option<PortRole> Role,
    Cell From,
    Face Toward,
    Cell Joins);

/// <summary>
/// A run or route end named by what it joins rather than by a cell (<see cref="RunRequest.FromPort"/>,
/// <see cref="RouteRequest.FromPort"/>). Union: a later minor may add cases; a host that does not know a case refuses
/// it <see cref="ReasonCodes.UnknownKind"/>. Added in contract 0.8.
/// </summary>
public abstract record PortEnd
{
    private PortEnd() { }

    /// <summary>
    /// A port of a build or swap ghost's new piece, numbered as <see cref="IPieceCatalogue.PortsAt"/> numbers it. Refused
    /// <see cref="ReasonCodes.NotFound"/> for a ghost the sender may not see or a port its piece lacks, and
    /// <see cref="ReasonCodes.InvalidValue"/> for a ghost with no new piece (a removal or finish). Added in contract 0.8.
    /// </summary>
    /// <param name="Ghost">The ghost.</param>
    /// <param name="Port">The port of its new piece.</param>
    public sealed record OnGhost(GhostId Ghost, PortIndex Port) : PortEnd;

    /// <summary>
    /// A port of a built structure, numbered as <see cref="IPieceCatalogue.PortsAt"/> numbers it for the structure's
    /// piece, cell and turn. The structure is found by its reference id, else by its piece and cell (as
    /// <see cref="IGhostBoard.On"/> finds it). Refused <see cref="ReasonCodes.NotFound"/> for a structure that no
    /// longer stands or a port its piece lacks. Any build state joins. A planned removal or swap of the structure is not
    /// followed: to join what replaces it, name the swap ghost's port (<see cref="OnGhost"/>). Added in contract 0.12.
    /// </summary>
    /// <param name="Structure">The structure.</param>
    /// <param name="Port">The port of its piece.</param>
    public sealed record OnStructure(StructureRef Structure, PortIndex Port) : PortEnd;
}

/// <summary>A port's index in its piece's game <c>OpenEnds</c> list. <c>default</c> is index 0. Added in contract 0.8.</summary>
public readonly record struct PortIndex
{
    private PortIndex(int value) => Value = value;

    /// <summary>The index, from 0.</summary>
    public int Value { get; }

    /// <summary>Refuses a negative index (<see cref="ReasonCodes.InvalidValue"/>).</summary>
    public static Result<PortIndex, Reason> Create(int value) =>
        value >= 0
            ? Result<PortIndex, Reason>.Ok(new PortIndex(value))
            : Result<PortIndex, Reason>.Error(Reason.InvalidValue(nameof(PortIndex), value.ToString(CultureInfo.InvariantCulture), "not-negative"));

    internal static PortIndex Known(int value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A network a port joins. Lattice's own are bare (see <see cref="PortKinds"/>). A kind a mod adds to the game is
/// kept, never dropped, under a key made from its bits in the game's <c>NetworkType</c> (<c>bits-512</c>), equal on
/// every seat. <c>default</c> is no kind. Added in contract 0.8.
/// </summary>
public readonly record struct PortKind
{
    private readonly string? _value;

    private PortKind(string value) => _value = value;

    /// <summary>The key as written.</summary>
    public string Value => _value ?? "";

    internal static PortKind Known(string value) => new(value);

    /// <summary>The kind a mod's network bits make, the same key for the same bits on every seat.</summary>
    internal static PortKind OfBits(int bits) => new("bits-" + bits.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>The network kinds of the base game. Added in contract 0.8.</summary>
public static class PortKinds
{
    /// <summary>Power cables.</summary>
    public static readonly PortKind Power = PortKind.Known("power");

    /// <summary>Data cables.</summary>
    public static readonly PortKind Data = PortKind.Known("data");

    /// <summary>Gas pipes.</summary>
    public static readonly PortKind GasPipe = PortKind.Known("gas-pipe");

    /// <summary>Liquid pipes.</summary>
    public static readonly PortKind LiquidPipe = PortKind.Known("liquid-pipe");

    /// <summary>Chutes.</summary>
    public static readonly PortKind Chute = PortKind.Known("chute");

    /// <summary>Elevator shafts.</summary>
    public static readonly PortKind Elevator = PortKind.Known("elevator");

    /// <summary>Landing pads.</summary>
    public static readonly PortKind LandingPad = PortKind.Known("landing-pad");

    /// <summary>Launch pads.</summary>
    public static readonly PortKind LaunchPad = PortKind.Known("launch-pad");

    /// <summary>Robotic arm rails.</summary>
    public static readonly PortKind Rail = PortKind.Known("rail");
}

/// <summary>
/// What a port does for its device (the game's <c>ConnectionRole</c>). Values are fixed. A value a consumer does not
/// know comes from a newer minor: show it as unknown. Added in contract 0.8.
/// </summary>
public enum PortRole
{
    /// <summary>The device's input.</summary>
    Input = 1,

    /// <summary>The device's second input.</summary>
    Input2 = 2,

    /// <summary>The device's output.</summary>
    Output = 3,

    /// <summary>The device's second output.</summary>
    Output2 = 4,

    /// <summary>The device's waste output.</summary>
    Waste = 5,
}
