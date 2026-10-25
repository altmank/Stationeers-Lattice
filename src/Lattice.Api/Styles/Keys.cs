namespace Lattice.Api;

// Open code sets of the style contract: string keys, Lattice's bare, a mod's owner/name.

/// <summary>
/// A colour token in a palette, such as <c>fail</c>; a style's paint names it and the palette in force turns it into a colour. Lattice's own are bare (see <see cref="LibraryPalette"/>); a mod's are <c>owner/name</c>. <c>default</c> is no key; Lattice refuses it where a key is needed.
/// </summary>
public readonly record struct PaletteKey
{
    private readonly string? _value;

    private PaletteKey(string value) => _value = value;

    /// <summary>The key as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own key, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<PaletteKey, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(PaletteKey)).Map(static v => new PaletteKey(v));

    /// <summary>Reads a key as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<PaletteKey, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(PaletteKey)).Map(static v => new PaletteKey(v));

    internal static PaletteKey Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A palette's id, such as <c>colour-safe</c>: Lattice's own are bare (see <see cref="LibraryPalettes"/>); a mod's are <c>owner/name</c>. Not a colour token (<see cref="PaletteKey"/>). <c>default</c> is no id; Lattice refuses it where an id is needed.
/// </summary>
public readonly record struct PaletteId
{
    private readonly string? _value;

    private PaletteId(string value) => _value = value;

    /// <summary>The id as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own id, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<PaletteId, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(PaletteId)).Map(static v => new PaletteId(v));

    /// <summary>Reads an id as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<PaletteId, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(PaletteId)).Map(static v => new PaletteId(v));

    internal static PaletteId Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A registered text style (font, size, colour, reason wording) a label is drawn in, such as <c>default</c> (see <see cref="LibraryText"/>); a mod's are <c>owner/name</c>. <c>default</c> is no key; Lattice refuses it where a key is needed.
/// </summary>
public readonly record struct TextStyleKey
{
    private readonly string? _value;

    private TextStyleKey(string value) => _value = value;

    /// <summary>The key as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own key, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<TextStyleKey, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(TextStyleKey)).Map(static v => new TextStyleKey(v));

    /// <summary>Reads a key as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<TextStyleKey, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(TextStyleKey)).Map(static v => new TextStyleKey(v));

    internal static TextStyleKey Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A registered icon a style shows on a drawn thing. Lattice's own are bare (see <see cref="LibraryIcons"/>); a mod's are <c>owner/name</c>. <c>default</c> is no key; Lattice refuses it where a key is needed.
/// </summary>
public readonly record struct IconKey
{
    private readonly string? _value;

    private IconKey(string value) => _value = value;

    /// <summary>The key as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own key, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<IconKey, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(IconKey)).Map(static v => new IconKey(v));

    /// <summary>Reads a key as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<IconKey, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(IconKey)).Map(static v => new IconKey(v));

    internal static IconKey Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A registered decoration mesh a style's <see cref="Style.Mark"/> puts on a drawn thing; a mod's are <c>owner/name</c>. <c>default</c> is no key; Lattice refuses it where a key is needed.
/// </summary>
public readonly record struct DecorationKey
{
    private readonly string? _value;

    private DecorationKey(string value) => _value = value;

    /// <summary>The key as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own key, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<DecorationKey, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(DecorationKey)).Map(static v => new DecorationKey(v));

    /// <summary>Reads a key as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<DecorationKey, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(DecorationKey)).Map(static v => new DecorationKey(v));

    internal static DecorationKey Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A named material Lattice draws with (see <see cref="MaterialKeys"/> for Lattice's own and the material contract every material drawn by Lattice meets); a mod's are <c>owner/name</c>. <c>default</c> is no key; Lattice refuses it where a key is needed.
/// </summary>
public readonly record struct MaterialKey
{
    private readonly string? _value;

    private MaterialKey(string value) => _value = value;

    /// <summary>The key as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own key, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<MaterialKey, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(MaterialKey)).Map(static v => new MaterialKey(v));

    /// <summary>Reads a key as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<MaterialKey, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(MaterialKey)).Map(static v => new MaterialKey(v));

    internal static MaterialKey Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A style layer's id; a mod's are <c>owner/name</c>, such as <c>planner/planning</c>. <c>default</c> is no key; Lattice refuses it where a key is needed.
/// </summary>
public readonly record struct StyleLayerId
{
    private readonly string? _value;

    private StyleLayerId(string value) => _value = value;

    /// <summary>The key as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own key, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<StyleLayerId, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(StyleLayerId)).Map(static v => new StyleLayerId(v));

    /// <summary>Reads a key as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<StyleLayerId, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(StyleLayerId)).Map(static v => new StyleLayerId(v));

    internal static StyleLayerId Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A set of ghosts or structures a style rule can match (<see cref="StyleMatch.InSet"/>). Lattice's offered sets are bare (see <see cref="LibrarySets"/>); a mod's are <c>owner/name</c>. <c>default</c> is no key; Lattice refuses it where a key is needed.
/// </summary>
public readonly record struct StyleSetId
{
    private readonly string? _value;

    private StyleSetId(string value) => _value = value;

    /// <summary>The key as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own key, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<StyleSetId, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(StyleSetId)).Map(static v => new StyleSetId(v));

    /// <summary>Reads a key as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<StyleSetId, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(StyleSetId)).Map(static v => new StyleSetId(v));

    internal static StyleSetId Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A drafts handle's id: one stream of uncommitted ghosts a consumer shows, such as <c>planner/cursor</c>. <c>default</c> is no key; Lattice refuses it where a key is needed.
/// </summary>
public readonly record struct DraftsId
{
    private readonly string? _value;

    private DraftsId(string value) => _value = value;

    /// <summary>The key as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own key, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<DraftsId, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(DraftsId)).Map(static v => new DraftsId(v));

    /// <summary>Reads a key as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<DraftsId, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(DraftsId)).Map(static v => new DraftsId(v));

    internal static DraftsId Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A renderer claim's id, such as <c>mymod/rocket-parts</c>. <c>default</c> is no key; Lattice refuses it where a key is needed.
/// </summary>
public readonly record struct ClaimId
{
    private readonly string? _value;

    private ClaimId(string value) => _value = value;

    /// <summary>The key as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own key, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<ClaimId, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(ClaimId)).Map(static v => new ClaimId(v));

    /// <summary>Reads a key as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<ClaimId, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(ClaimId)).Map(static v => new ClaimId(v));

    internal static ClaimId Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A family of pieces (cable, pipe, frame, ...), found by type in the catalogue so a modded subclass inherits its base's family; pieces Lattice cannot classify are <see cref="PieceFamilies.Other"/>. Lattice's own are bare (see <see cref="PieceFamilies"/>). <c>default</c> is no key; Lattice refuses it where a key is needed.
/// </summary>
public readonly record struct PieceFamily
{
    private readonly string? _value;

    private PieceFamily(string value) => _value = value;

    /// <summary>The key as written, with its owner prefix when it has one.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own key, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<PieceFamily, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(PieceFamily)).Map(static v => new PieceFamily(v));

    /// <summary>Reads a key as written (bare for Lattice's own, or <c>owner/name</c>).</summary>
    internal static Result<PieceFamily, Reason> Parse(string value) =>
        Token.CheckQualified(value, nameof(PieceFamily)).Map(static v => new PieceFamily(v));

    internal static PieceFamily Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
