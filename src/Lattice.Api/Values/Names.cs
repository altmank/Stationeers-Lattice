namespace Lattice.Api;

// Machine names: lowercase words (see Token).

/// <summary>
/// A mod's id, such as <c>planner</c>; the owner prefix of every code a mod mints. Lattice's own is its About.xml ModID,
/// <c>net.xceled.stationeers.lattice</c> (its makers and actor); other mods pick another. <c>default</c> is no id; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.
/// </summary>
public readonly record struct ModId
{
    private readonly string? _value;

    private ModId(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps an id: lowercase words joined by '-', '.' or '_'.</summary>
    public static Result<ModId, Reason> Create(string value) =>
        Token.Check(value, nameof(ModId)).Map(static v => new ModId(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>An agent's id, such as <c>foreman</c>. <c>default</c> is no id; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct AgentId
{
    private readonly string? _value;

    private AgentId(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps an id: lowercase words joined by '-', '.' or '_'.</summary>
    public static Result<AgentId, Reason> Create(string value) =>
        Token.Check(value, nameof(AgentId)).Map(static v => new AgentId(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A Lattice module's name, such as <c>run-planner</c>. <c>default</c> is no id; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct ModuleName
{
    private readonly string? _value;

    private ModuleName(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps an id: lowercase words joined by '-', '.' or '_'.</summary>
    public static Result<ModuleName, Reason> Create(string value) =>
        Token.Check(value, nameof(ModuleName)).Map(static v => new ModuleName(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A gas, such as <c>oxygen</c>. <c>default</c> is no id; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct GasKind
{
    private readonly string? _value;

    private GasKind(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps an id: lowercase words joined by '-', '.' or '_'.</summary>
    public static Result<GasKind, Reason> Create(string value) =>
        Token.Check(value, nameof(GasKind)).Map(static v => new GasKind(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>What made a ghost or plan (a tool, an agent job), written <c>owner/name</c> such as <c>planner/run-tool</c>; told when its ghosts are blocked. <c>default</c> is no id; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct Maker
{
    private readonly string? _value;

    private Maker(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own id, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<Maker, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(Maker)).Map(static v => new Maker(v));

    /// <summary>Reads an id as written (<c>owner/name</c>, or bare for Lattice's own).</summary>
    internal static Result<Maker, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(Maker)).Map(static v => new Maker(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>The key attached data lives under, written <c>owner/name</c> such as <c>builder/preferred-bay</c>. <c>default</c> is no id; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct AttachmentKey
{
    private readonly string? _value;

    private AttachmentKey(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own id, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<AttachmentKey, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(AttachmentKey)).Map(static v => new AttachmentKey(v));

    /// <summary>Reads an id as written (<c>owner/name</c>, or bare for Lattice's own).</summary>
    internal static Result<AttachmentKey, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(AttachmentKey)).Map(static v => new AttachmentKey(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>An executor's id, written <c>owner/name</c> such as <c>builder/drone-3</c>. <c>default</c> is no id; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct ExecutorId
{
    private readonly string? _value;

    private ExecutorId(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own id, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<ExecutorId, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(ExecutorId)).Map(static v => new ExecutorId(v));

    /// <summary>Reads an id as written (<c>owner/name</c>, or bare for Lattice's own).</summary>
    internal static Result<ExecutorId, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(ExecutorId)).Map(static v => new ExecutorId(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

// Names people read and type (see Text).

/// <summary>A plan's name, such as "North store". <c>default</c> is no name; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct PlanName
{
    private readonly string? _value;

    private PlanName(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps a name: 1 to 256 characters, not blank, no control characters.</summary>
    public static Result<PlanName, Reason> Create(string value) =>
        Text.Check(value, nameof(PlanName)).Map(static v => new PlanName(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A label a player puts on a ghost. <c>default</c> is no name; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct Label
{
    private readonly string? _value;

    private Label(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps a name: 1 to 256 characters, not blank, no control characters.</summary>
    public static Result<Label, Reason> Create(string value) =>
        Text.Check(value, nameof(Label)).Map(static v => new Label(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A group of ghosts by name, such as "Hydro power". <c>default</c> is no name; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct GroupName
{
    private readonly string? _value;

    private GroupName(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps a name: 1 to 256 characters, not blank, no control characters.</summary>
    public static Result<GroupName, Reason> Create(string value) =>
        Text.Check(value, nameof(GroupName)).Map(static v => new GroupName(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A team of actors that a <see cref="Visibility.Team"/> names; the access policy decides who is in it. <c>default</c> is no name; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct TeamName
{
    private readonly string? _value;

    private TeamName(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps a name: 1 to 256 characters, not blank, no control characters.</summary>
    public static Result<TeamName, Reason> Create(string value) =>
        Text.Check(value, nameof(TeamName)).Map(static v => new TeamName(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>The name the game shows on a structure (what a labeller writes). <c>default</c> is no name; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct ThingName
{
    private readonly string? _value;

    private ThingName(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps a name: 1 to 256 characters, not blank, no control characters.</summary>
    public static Result<ThingName, Reason> Create(string value) =>
        Text.Check(value, nameof(ThingName)).Map(static v => new ThingName(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A template's name. <c>default</c> is no name; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct TemplateName
{
    private readonly string? _value;

    private TemplateName(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps a name: 1 to 256 characters, not blank, no control characters.</summary>
    public static Result<TemplateName, Reason> Create(string value) =>
        Text.Check(value, nameof(TemplateName)).Map(static v => new TemplateName(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A name shown for a player or an executor. <c>default</c> is no name; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct DisplayName
{
    private readonly string? _value;

    private DisplayName(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps a name: 1 to 256 characters, not blank, no control characters.</summary>
    public static Result<DisplayName, Reason> Create(string value) =>
        Text.Check(value, nameof(DisplayName)).Map(static v => new DisplayName(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A game save as the inspector names it. <c>default</c> is no name; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct SaveName
{
    private readonly string? _value;

    private SaveName(string value) => _value = value;

    /// <summary>The text as written.</summary>
    public string Value => _value ?? "";

    /// <summary>Checks and wraps a name: 1 to 256 characters, not blank, no control characters.</summary>
    public static Result<SaveName, Reason> Create(string value) =>
        Text.Check(value, nameof(SaveName)).Map(static v => new SaveName(v));

    /// <inheritdoc />
    public override string ToString() => Value;
}
