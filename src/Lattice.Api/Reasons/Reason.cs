namespace Lattice.Api;

/// <summary>
/// Why something was refused, will fail, waits or warns: a code agents act on and the UI words, the facts behind it,
/// the game's own message when the game has one, and the rule that produced it.
/// </summary>
public sealed record Reason(
    ReasonCode Code,
    ValueMap<string, string> Facts,
    Option<string> GameMessage,
    RuleId From)
{
    /// <summary>A reason with no facts and no game message.</summary>
    public static Reason Of(ReasonCode code, RuleId from) =>
        new(code, ValueMap<string, string>.Empty, Option<string>.None, from);

    /// <summary>A reason with facts and no game message.</summary>
    public static Reason Of(ReasonCode code, RuleId from, ValueMap<string, string> facts) =>
        new(code, facts, Option<string>.None, from);

    /// <summary>
    /// A builder lacks items for a ghost: <see cref="ReasonCodes.NeedsItems"/> with facts <c>item</c> (the item's prefab
    /// hash) and <c>count</c> (how many more it needs). Added in contract 0.19.
    /// </summary>
    public static Reason NeedsItems(RuleId from, ItemQuantity missing) =>
        Of(ReasonCodes.NeedsItems, from, ValueMap<string, string>.Empty
            .With("item", Number(missing.Item.Hash))
            .With("count", Number(missing.Count)));

    /// <summary>
    /// A builder is too far from a ghost to build it: <see cref="ReasonCodes.OutOfReach"/> with facts <c>metres</c> (how
    /// far it is) and <c>reach</c> (how far it reaches), in metres. Added in contract 0.19.
    /// </summary>
    public static Reason OutOfReach(RuleId from, Metres distance, Metres reach) =>
        Of(ReasonCodes.OutOfReach, from, ValueMap<string, string>.Empty
            .With("metres", Number(distance.Value))
            .With("reach", Number(reach.Value)));

    /// <summary>A builder has no power: <see cref="ReasonCodes.NoPower"/> with no facts. Added in contract 0.19.</summary>
    public static Reason NoPower(RuleId from) => Of(ReasonCodes.NoPower, from);

    /// <summary>
    /// The device a builder works from has no power: <see cref="ReasonCodes.NoPower"/> with fact <c>device</c> (its
    /// reference id). Added in contract 0.19.
    /// </summary>
    public static Reason NoPower(RuleId from, ReferenceId device) =>
        Of(ReasonCodes.NoPower, from, ValueMap<string, string>.Empty.With("device", Number(device.Value)));

    /// <summary>
    /// Something a builder cannot name stands in a cell it must build or pass: <see cref="ReasonCodes.Blocked"/> with
    /// fact <c>cell</c> ("x,y,z"). Added in contract 0.19.
    /// </summary>
    public static Reason Blocked(RuleId from, Cell cell) =>
        Of(ReasonCodes.Blocked, from, ValueMap<string, string>.Empty.With("cell", CellText(cell)));

    /// <summary>
    /// A ghost stands in the way: <see cref="ReasonCodes.Blocked"/> with facts <c>cell</c>, <c>by</c> (<c>ghost</c>)
    /// and <c>ghost</c> (its id). Added in contract 0.19.
    /// </summary>
    public static Reason Blocked(RuleId from, Cell cell, GhostId by) =>
        Of(ReasonCodes.Blocked, from, BlockedFacts(cell, "ghost").With("ghost", Number(by.Value)));

    /// <summary>
    /// A built structure stands in the way: <see cref="ReasonCodes.Blocked"/> with facts <c>cell</c>, <c>by</c>
    /// (<c>structure</c>) and <c>structure</c> (its reference id). Added in contract 0.19.
    /// </summary>
    public static Reason Blocked(RuleId from, Cell cell, ReferenceId by) =>
        Of(ReasonCodes.Blocked, from, BlockedFacts(cell, "structure").With("structure", Number(by.Value)));

    /// <summary>
    /// A player stands in the way: <see cref="ReasonCodes.Blocked"/> with facts <c>cell</c> and <c>by</c>
    /// (<c>player</c>), and no id. Added in contract 0.19.
    /// </summary>
    public static Reason BlockedByPlayer(RuleId from, Cell cell) => Of(ReasonCodes.Blocked, from, BlockedFacts(cell, "player"));

    private static ValueMap<string, string> BlockedFacts(Cell cell, string by) =>
        ValueMap<string, string>.Empty.With("cell", CellText(cell)).With("by", by);

    private static string CellText(Cell cell) => Number(cell.X) + "," + Number(cell.Y) + "," + Number(cell.Z);

    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Number(float value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    internal static Reason InvalidValue(string type, string value, string rule) =>
        new(ReasonCodes.InvalidValue,
            ValueMap<string, string>.Empty.With("type", type).With("value", value).With("rule", rule),
            Option<string>.None,
            RuleId.Contract);

    /// <summary>
    /// One line for a log: <c>&lt;code&gt; from &lt;rule&gt;: k1=v1 k2=v2 (game: "&lt;message&gt;")</c>. Facts are
    /// sorted by key and escaped as the written forms escape them (space, '%' and control characters as <c>%XX</c>);
    /// the part after the colon is left out when there are no facts, the game part when there is no game message.
    /// Linear in the facts. Added in contract 0.17.
    /// </summary>
    public override string ToString()
    {
        var text = new System.Text.StringBuilder(32 + 24 * Facts.Count);
        text.Append(Code.Value).Append(" from ").Append(From.Value);
        if (Facts.Count > 0)
        {
            var keys = new string[Facts.Count];
            var at = 0;
            foreach (var fact in Facts)
                keys[at++] = fact.Key;
            System.Array.Sort(keys, System.StringComparer.Ordinal);
            text.Append(':');
            foreach (var key in keys)
                text.Append(' ').Append(TextEscape.Escape(key, key: true)).Append('=').Append(TextEscape.Escape(Facts[key], key: false));
        }

        if (GameMessage.TryGet(out var message))
            text.Append(" (game: \"").Append(message).Append("\")");
        return text.ToString();
    }
}

/// <summary>
/// A reason's machine-readable code. Lattice's own are bare (<c>needs-support</c>, see <see cref="ReasonCodes"/>);
/// a mod's are prefixed with its id (<c>mymod/window-clear</c>). <c>default</c> is no code.
/// </summary>
public readonly record struct ReasonCode
{
    private readonly string? _value;

    private ReasonCode(string value) => _value = value;

    /// <summary>The code as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own code, written <c>owner/code</c>; refused unless <paramref name="code"/> is lowercase words.</summary>
    public static Result<ReasonCode, Reason> Create(ModId owner, string code) =>
        Token.Qualify(owner, code, nameof(ReasonCode)).Map(static v => new ReasonCode(v));

    /// <summary>Reads a code as written (bare or <c>owner/code</c>).</summary>
    internal static Result<ReasonCode, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(ReasonCode)).Map(static v => new ReasonCode(v));

    internal static ReasonCode Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Names the rule, analyzer or core check that produced a reason. Lattice's own are bare (<c>lattice.board</c>);
/// a mod's are prefixed with its id (<c>mymod/window-clear</c>). <c>default</c> is no rule.
/// </summary>
public readonly record struct RuleId
{
    private readonly string? _value;

    private RuleId(string value) => _value = value;

    /// <summary>The id as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>The value-object checks of this contract.</summary>
    public static readonly RuleId Contract = new("lattice.contract");

    /// <summary>A mod's own rule id, written <c>owner/name</c>.</summary>
    public static Result<RuleId, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(RuleId)).Map(static v => new RuleId(v));

    /// <summary>Reads an id as written (bare or <c>owner/name</c>).</summary>
    internal static Result<RuleId, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(RuleId)).Map(static v => new RuleId(v));

    internal static RuleId Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>The reason codes Lattice itself produces. Mods add their own with <see cref="ReasonCode.Create"/>.</summary>
public static class ReasonCodes
{
    /// <summary>A value object refused its input.</summary>
    public static readonly ReasonCode InvalidValue = ReasonCode.Known("invalid-value");

    /// <summary>A command needs a module that is not installed or is switched off.</summary>
    public static readonly ReasonCode ModuleMissing = ReasonCode.Known("module-missing");

    /// <summary>The access policy refused the actor.</summary>
    public static readonly ReasonCode NotAllowed = ReasonCode.Known("not-allowed");

    /// <summary>Another actor changed the ghost or plan since the revision the command expected.</summary>
    public static readonly ReasonCode ChangedSince = ReasonCode.Known("changed-since");

    /// <summary>No ghost, plan, change set or structure with that id.</summary>
    public static readonly ReasonCode NotFound = ReasonCode.Known("not-found");

    /// <summary>A command carries a <c>default</c> id or value.</summary>
    public static readonly ReasonCode DefaultValue = ReasonCode.Known("default-value");

    /// <summary>The piece is not in the catalogue.</summary>
    public static readonly ReasonCode UnknownPiece = ReasonCode.Known("unknown-piece");

    /// <summary>A kind (intent, command, selector, step, ...) this side does not know.</summary>
    public static readonly ReasonCode UnknownKind = ReasonCode.Known("unknown-kind");

    /// <summary>A command or plan is past a size limit.</summary>
    public static readonly ReasonCode TooLarge = ReasonCode.Known("too-large");

    /// <summary>A value is outside the range the host accepts.</summary>
    public static readonly ReasonCode OutOfRange = ReasonCode.Known("out-of-range");

    /// <summary>A client's command places or names more ghosts than the host accepts in one command.</summary>
    public static readonly ReasonCode TooManyGhosts = ReasonCode.Known("too-many-ghosts");

    /// <summary>A client's command nests selectors deeper than the host accepts.</summary>
    public static readonly ReasonCode SelectorTooDeep = ReasonCode.Known("selector-too-deep");

    /// <summary>A client's command nests batches deeper than the host accepts.</summary>
    public static readonly ReasonCode BatchTooDeep = ReasonCode.Known("batch-too-deep");

    /// <summary>A client's command carries text (attached data) longer than the host accepts for its key.</summary>
    public static readonly ReasonCode TextTooLong = ReasonCode.Known("text-too-long");

    /// <summary>The piece has nothing to stand on or hang from.</summary>
    public static readonly ReasonCode NeedsSupport = ReasonCode.Known("needs-support");

    /// <summary>Two pieces whose kinds never share a small cell (cable with chute or device, for example).</summary>
    public static readonly ReasonCode CannotShare = ReasonCode.Known("cannot-share");

    /// <summary>An end of one piece is within 0.1 m of an end of another in the same small cells (cable and pipe running side by side).</summary>
    public static readonly ReasonCode EndsCollide = ReasonCode.Known("ends-collide");

    /// <summary>The face's pressure difference is above the piece's rating.</summary>
    public static readonly ReasonCode FaceOverRating = ReasonCode.Known("face-over-rating");

    /// <summary>The game's placement check refused; its message is in <see cref="Reason.GameMessage"/>.</summary>
    public static readonly ReasonCode GameRefused = ReasonCode.Known("game-refused");

    /// <summary>The structure a ghost points at is gone.</summary>
    public static readonly ReasonCode TargetGone = ReasonCode.Known("target-gone");

    /// <summary>Something other than the ghost's piece stands in its spot.</summary>
    public static readonly ReasonCode Different = ReasonCode.Known("different");

    /// <summary>Another executor holds the claim.</summary>
    public static readonly ReasonCode Claimed = ReasonCode.Known("claimed");

    /// <summary>The claim's lease ran out.</summary>
    public static readonly ReasonCode LeaseExpired = ReasonCode.Known("lease-expired");

    /// <summary>Only the host may do this.</summary>
    public static readonly ReasonCode HostOnly = ReasonCode.Known("host-only");

    /// <summary>
    /// A client's command whose outcome the host no longer has, when the client asked for the board again after it
    /// dropped a change set: the command may or may not have applied; the board after the join is the host's. Fact
    /// <c>command</c>. Added in contract 0.17.
    /// </summary>
    public static readonly ReasonCode OutcomeLost = ReasonCode.Known("outcome-lost");

    /// <summary>Something is already registered under that id.</summary>
    public static readonly ReasonCode AlreadyRegistered = ReasonCode.Known("already-registered");

    /// <summary>A rule from another mod threw and is switched off for the rest of the world.</summary>
    public static readonly ReasonCode RuleSwitchedOff = ReasonCode.Known("rule-switched-off");

    /// <summary>A rule had not answered when its budget ended; validation completed without it.</summary>
    public static readonly ReasonCode RuleTimedOut = ReasonCode.Known("rule-timed-out");

    /// <summary>Ghosts wait for each other round a loop, so none can be built first; fact <c>ghosts</c>: the ids, ascending, comma separated.</summary>
    public static readonly ReasonCode Cycle = ReasonCode.Known("cycle");

    /// <summary>A loop stays: every way round it runs through a junction, a device link or a piece that may not go.</summary>
    public static readonly ReasonCode NoRemovableChain = ReasonCode.Known("no-removable-chain");

    /// <summary>Networks, or two sides of one device, would be joined.</summary>
    public static readonly ReasonCode WouldBridge = ReasonCode.Known("would-bridge");

    /// <summary>A network would fall apart, or a device port lose its network.</summary>
    public static readonly ReasonCode WouldSplit = ReasonCode.Known("would-split");

    /// <summary>A split network holds contents the game would divide among the parts.</summary>
    public static readonly ReasonCode ContentsWouldMove = ReasonCode.Known("contents-would-move");

    /// <summary>Safe as the devices stand; switching on the ones now off would burn a cable.</summary>
    public static readonly ReasonCode WouldOverloadWhenOn = ReasonCode.Known("would-overload-when-on");

    /// <summary>No room has the recorded room's id any more.</summary>
    public static readonly ReasonCode RoomGone = ReasonCode.Known("room-gone");

    /// <summary>A room lost or gained cells it was not expected to.</summary>
    public static readonly ReasonCode RoomCellsChanged = ReasonCode.Known("room-cells-changed");

    /// <summary>A room's air moved outside the tolerance.</summary>
    public static readonly ReasonCode RoomAirChanged = ReasonCode.Known("room-air-changed");

    /// <summary>
    /// A kit has no piece for the ways a cell needs; facts <c>kit</c>, <c>cell</c> ("x,y,z") and <c>shape</c> (directions as the
    /// catalogue writes them, for example <c>x+,y+,z+</c>, or <c>straight</c> when the kit has no straight piece).
    /// </summary>
    public static readonly ReasonCode NoPiece = ReasonCode.Known("no-piece");

    /// <summary>
    /// No route exists inside the search box under the request's toggles; facts <c>from</c>, <c>to</c>, <c>nearest</c>
    /// (the reached cell closest to the far end, "x,y,z"), <c>cells-left</c>, <c>expanded</c>, <c>bound</c>, <c>margin</c>;
    /// <c>branch</c> for a branch, and <c>leg</c> for a leg of a route with <see cref="RouteRequest.Via"/> cells, whose
    /// <c>from</c>, <c>to</c>, <c>nearest</c> and <c>cells-left</c> are that leg's. Fact <c>stopped</c> says why the route
    /// went no further from <c>nearest</c>: for each neighbour it never reached and could not enter,
    /// <c>&lt;direction&gt;:&lt;cause&gt;</c> (directions as the catalogue writes them, <c>x+</c>), comma-separated, in
    /// direction order; empty when there is none. Causes: <c>collides</c> (what stands there), <c>off-frame</c>,
    /// <c>opening</c> (a door or window keep-out), <c>port</c> (a free device port), <c>join</c> (another network),
    /// <c>crosses</c> (an earlier leg, or an end or via cell not this leg's), <c>along-line</c> (an existing line, with
    /// <see cref="RouteToggles.KeepOffLines"/>; from contract 0.14), <c>outside-box</c> (past the search box); a later
    /// version may add causes. Moving an end or relaxing a toggle may help; a bigger bound does not.
    /// </summary>
    public static readonly ReasonCode NoRoute = ReasonCode.Known("no-route");

    /// <summary>
    /// A route search reached its bound before it found a route or ran out of cells; the same facts as
    /// <see cref="NoRoute"/>. A bigger <see cref="RouteRequest.SearchBound"/> may find one.
    /// </summary>
    public static readonly ReasonCode SearchLimit = ReasonCode.Known("search-limit");

    /// <summary>
    /// A run's kit shares no network kind with the port it was asked to join (a pipe kit to a power port, a gas pipe to
    /// a liquid port); facts <c>ghost</c> or <c>structure</c>, <c>port</c> and <c>kit</c>. Added in contract 0.8.
    /// </summary>
    public static readonly ReasonCode PortMismatch = ReasonCode.Known("port-mismatch");

    /// <summary>
    /// A drawn run's cell breaks a rule the request asked for (<see cref="RunRequest.Rules"/>); facts <c>cell</c>
    /// ("x,y,z") and <c>rule</c>: <c>opening</c>, <c>port</c>, <c>join</c> or <c>along-line</c>, the words a route's
    /// <c>stopped</c> uses. A later version may add rules. Added in contract 0.14.
    /// </summary>
    public static readonly ReasonCode RuleBroken = ReasonCode.Known("rule-broken");

    /// <summary>
    /// A piece placed where the game does not snap it (<see cref="PieceEntry.Snap"/>), so the game will not build it
    /// there: a rocket engine or umbilical off a frame centre on x or z. Facts <c>piece</c> (prefab hash), <c>cell</c>
    /// ("x,y,z") and <c>axis</c> (<c>x</c>, <c>y</c> or <c>z</c>, the first one off). A ghost there fails with it; a run,
    /// route or transform that would put a piece there is refused with it. Added in contract 0.18.
    /// </summary>
    public static readonly ReasonCode OffSnap = ReasonCode.Known("off-snap");

    /// <summary>
    /// The catalogue names no tool for a step; facts <c>piece</c> (prefab hash), <c>way</c> (<c>build</c>,
    /// <c>take-apart</c> or <c>broken</c>) and, for build and take-apart, <c>step</c> (the build state index).
    /// </summary>
    public static readonly ReasonCode NoTool = ReasonCode.Known("no-tool");

    /// <summary>
    /// A builder lacks items to build a ghost; facts <c>item</c> (the item's prefab hash, <see cref="ItemKey"/>) and
    /// <c>count</c> (how many more). <see cref="Reason.NeedsItems"/> makes it. Added in contract 0.19.
    /// </summary>
    public static readonly ReasonCode NeedsItems = ReasonCode.Known("needs-items");

    /// <summary>
    /// A builder is too far from a ghost to build it; facts <c>metres</c> (how far) and <c>reach</c> (how far it
    /// reaches). <see cref="Reason.OutOfReach"/> makes it. Added in contract 0.19.
    /// </summary>
    public static readonly ReasonCode OutOfReach = ReasonCode.Known("out-of-reach");

    /// <summary>
    /// A builder, or the device it works from, has no power; no facts required, and fact <c>device</c> (reference id)
    /// when a device is named. <see cref="Reason.NoPower(RuleId)"/> makes it. Added in contract 0.19.
    /// </summary>
    public static readonly ReasonCode NoPower = ReasonCode.Known("no-power");

    /// <summary>
    /// Something stands in a cell a builder must build or pass; fact <c>cell</c> ("x,y,z") and, when the builder knows
    /// what, <c>by</c>: its kind, <c>ghost</c>, <c>structure</c> or <c>player</c>, with the id in the fact named for the
    /// kind (<c>ghost</c>: the ghost's id; <c>structure</c>: its reference id; none for a player).
    /// <see cref="Reason.Blocked(RuleId, Cell)"/> makes it. Not <see cref="Different"/>, which is the world's verdict on
    /// a ghost's own spot. Added in contract 0.19.
    /// </summary>
    public static readonly ReasonCode Blocked = ReasonCode.Known("blocked");

    /// <summary>
    /// A mirror picked a ghost whose piece has no mirror image among its turns (a machine with an uneven port
    /// layout); facts <c>ghosts</c> (ids ascending, comma separated; <c>drafts</c> for a template's drafts by index)
    /// and <c>pieces</c> (prefab hashes). The refused ghosts are also in <see cref="Refusal.Ghosts"/>; a transform that
    /// meets other problems too answers one refusal for each.
    /// </summary>
    public static readonly ReasonCode NoMirrorImage = ReasonCode.Known("no-mirror-image");

    /// <summary>Build safety warning, never a refusal: a swap takes out a wall, window or door that holds air in a room.</summary>
    public static readonly ReasonCode SwapOnAirRoom = ReasonCode.Known("swap-on-air-room");

    /// <summary>Build safety warning, never a refusal: removals cut the power to a device that keeps air, heating or life support going.</summary>
    public static readonly ReasonCode CutsPower = ReasonCode.Known("cuts-power");

    /// <summary>Build safety warning, never a refusal: a planned vent lets a pipe network's gas into a room.</summary>
    public static readonly ReasonCode GasIndoors = ReasonCode.Known("gas-indoors");

    /// <summary>Build safety warning, never a refusal: oxidiser would reach the outside.</summary>
    public static readonly ReasonCode OxidiserOutside = ReasonCode.Known("oxidiser-outside");

    /// <summary>Build safety warning, never a refusal: a path opens to hot gas.</summary>
    public static readonly ReasonCode HotGas = ReasonCode.Known("hot-gas");

    /// <summary>Build safety warning, never a refusal: a planned piece stands in front of a device's controls.</summary>
    public static readonly ReasonCode ControlsUnreachable = ReasonCode.Known("controls-unreachable");

    /// <summary>Build safety warning, never a refusal: a cable, pipe or chute runs through the space in front of a door.</summary>
    public static readonly ReasonCode RunThroughDoor = ReasonCode.Known("run-through-door");

    /// <summary>Build safety warning, never a refusal: a planned piece stands on or in front of a window.</summary>
    public static readonly ReasonCode OnWindow = ReasonCode.Known("on-window");

    /// <summary>Build safety warning, never a refusal: painting in a room adds pollutant to its air.</summary>
    public static readonly ReasonCode PaintIndoors = ReasonCode.Known("paint-indoors");

    /// <summary>Build safety warning, never a refusal: terrain fills a cell a planned piece takes; Lattice warns and never digs.</summary>
    public static readonly ReasonCode TerrainInTheWay = ReasonCode.Known("terrain-in-the-way");
}
