namespace Lattice.Api;

/// <summary>A piece (a structure prefab), such as StructureCableStraightH10, by the game's prefab hash. <c>default</c> is no piece.</summary>
public readonly record struct PrefabKey(int Hash);

/// <summary>A kit (the item a piece is placed from), such as ItemCableCoilHeavy, by the game's prefab hash. <c>default</c> is no kit.</summary>
public readonly record struct KitKey(int Hash);

/// <summary>Any item (tool, material, kit), by the game's prefab hash. <c>default</c> is no item.</summary>
public readonly record struct ItemKey(int Hash);

/// <summary>Which of a kit's pieces is placed: the option the game cycles through. <c>default</c> is the first.</summary>
public readonly record struct KitOption
{
    private KitOption(int index) => Index = index;

    /// <summary>The option's index, from 0.</summary>
    public int Index { get; }

    /// <summary>Refuses a negative index.</summary>
    public static Result<KitOption, Reason> Create(int index) =>
        index >= 0
            ? Result<KitOption, Reason>.Ok(new KitOption(index))
            : Result<KitOption, Reason>.Error(Reason.InvalidValue(nameof(KitOption), index.ToString(), "not-negative"));
}

/// <summary>A build step of a structure. <c>default</c> is step 0: the kit just placed.</summary>
public readonly record struct StepIndex
{
    private StepIndex(int value) => Value = value;

    /// <summary>The step, from 0.</summary>
    public int Value { get; }

    /// <summary>Refuses a negative step.</summary>
    public static Result<StepIndex, Reason> Create(int value) =>
        value >= 0
            ? Result<StepIndex, Reason>.Ok(new StepIndex(value))
            : Result<StepIndex, Reason>.Error(Reason.InvalidValue(nameof(StepIndex), value.ToString(), "not-negative"));
}

/// <summary>A number of an item, at least one. <c>default</c> is no item and no count; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct ItemQuantity
{
    private ItemQuantity(ItemKey item, int count)
    {
        Item = item;
        Count = count;
    }

    /// <summary>The item.</summary>
    public ItemKey Item { get; }

    /// <summary>How many, at least one.</summary>
    public int Count { get; }

    /// <summary>Refuses a count below one.</summary>
    public static Result<ItemQuantity, Reason> Create(ItemKey item, int count) =>
        count >= 1
            ? Result<ItemQuantity, Reason>.Ok(new ItemQuantity(item, count))
            : Result<ItemQuantity, Reason>.Error(Reason.InvalidValue(nameof(ItemQuantity), count.ToString(), "at-least-one"));
}

/// <summary>A change in an item's count: positive needed, negative given back.</summary>
public readonly record struct ItemDelta(ItemKey Item, int Change);

/// <summary>A paint colour by the game's colour index. <c>default</c> is index 0.</summary>
public readonly record struct PaintColour
{
    private PaintColour(int index) => Index = index;

    /// <summary>The game's colour index.</summary>
    public int Index { get; }

    /// <summary>Refuses a negative index; the catalogue checks the index exists.</summary>
    public static Result<PaintColour, Reason> Create(int index) =>
        index >= 0
            ? Result<PaintColour, Reason>.Ok(new PaintColour(index))
            : Result<PaintColour, Reason>.Error(Reason.InvalidValue(nameof(PaintColour), index.ToString(), "not-negative"));
}

/// <summary>The one value a labeller sets on a device. Finite. <c>default</c> is 0.</summary>
public readonly record struct Setting
{
    private Setting(double value) => Value = value;

    /// <summary>The value.</summary>
    public double Value { get; }

    /// <summary>Refuses NaN and infinities.</summary>
    public static Result<Setting, Reason> Create(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value)
            ? Result<Setting, Reason>.Ok(new Setting(value))
            : Result<Setting, Reason>.Error(Reason.InvalidValue(nameof(Setting), value.ToString(), "finite"));
}

/// <summary>The colour a player sorts a ghost by (&quot;power is yellow&quot;): ghost data every viewer sees alike; styles read it (<see cref="Paint.GhostTint"/>).</summary>
public readonly record struct Tint(byte R, byte G, byte B, byte A);

/// <summary>
/// Opaque data a mod attaches to a ghost or plan: saved and synced, never read by the core. <c>default</c> is empty text.
/// </summary>
public readonly record struct Attachment
{
    private readonly string? _value;

    private Attachment(string value) => _value = value;

    /// <summary>The mod's own text, in any format it likes.</summary>
    public string Value => _value ?? "";

    /// <summary>Refuses null; size limits are the host's (client command checks).</summary>
    public static Result<Attachment, Reason> Create(string value) =>
        value is null
            ? Result<Attachment, Reason>.Error(Reason.InvalidValue(nameof(Attachment), "", "not-null"))
            : Result<Attachment, Reason>.Ok(new Attachment(value));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A point in game time, in seconds since the world started.</summary>
public readonly record struct GameTime(double Seconds);
