namespace Lattice.Api;

/// <summary>
/// What a ghost asks for. A known set per version; a later minor may add kinds, each with an <see cref="IntentKind"/>.
/// A consumer skips and reports a kind it does not know, and asks <see cref="IGhostSystem.Has"/> before using a newer one.
/// </summary>
public abstract record Intent
{
    private Intent() { }

    /// <summary>Which kind this is.</summary>
    public abstract IntentKind Kind { get; }

    /// <summary>Done when that piece stands there in that turn, fully built, with the finish if one is given.</summary>
    /// <param name="Target">What to build where.</param>
    /// <param name="Finish">How it should look once built, if anything is asked.</param>
    public sealed record Build(Placement Target, Option<Finish> Finish) : Intent
    {
        /// <inheritdoc />
        public override IntentKind Kind => IntentKind.Build;
    }

    /// <summary>Done when the structure is gone.</summary>
    /// <param name="Target">The structure to remove.</param>
    public sealed record Remove(StructureRef Target) : Intent
    {
        /// <inheritdoc />
        public override IntentKind Kind => IntentKind.Remove;
    }

    /// <summary>Done when the old structure is gone and the new piece stands, fully built, with the finish if one is given.</summary>
    /// <param name="Old">The structure to replace.</param>
    /// <param name="New">What stands in its place.</param>
    /// <param name="Finish">How the new piece should look, if anything is asked.</param>
    public sealed record Swap(StructureRef Old, Placement New, Option<Finish> Finish) : Intent
    {
        /// <inheritdoc />
        public override IntentKind Kind => IntentKind.Swap;
    }

    /// <summary>Done when the structure has the finish.</summary>
    /// <param name="Target">The structure.</param>
    /// <param name="Finish">Its colour, name or setting.</param>
    public sealed record FinishOnly(StructureRef Target, Finish Finish) : Intent
    {
        /// <inheritdoc />
        public override IntentKind Kind => IntentKind.FinishOnly;
    }
}

/// <summary>
/// The intent kinds. Only Lattice adds kinds; values are fixed for the life of the contract and compiled into
/// consumers. A value a consumer does not know is a kind from a newer minor: skip and report it.
/// </summary>
public enum IntentKind
{
    /// <summary><see cref="Intent.Build"/>.</summary>
    Build = 1,

    /// <summary><see cref="Intent.Remove"/>.</summary>
    Remove = 2,

    /// <summary><see cref="Intent.Swap"/>.</summary>
    Swap = 3,

    /// <summary><see cref="Intent.FinishOnly"/>.</summary>
    FinishOnly = 4,
}

/// <summary>How a structure should look once built: colour, name, setting. At least one is given.</summary>
public sealed record Finish
{
    private Finish(Option<PaintColour> colour, Option<ThingName> name, Option<Setting> setting)
    {
        Colour = colour;
        Name = name;
        Setting = setting;
    }

    /// <summary>The paint colour, if asked.</summary>
    public Option<PaintColour> Colour { get; }

    /// <summary>The structure's name, if asked.</summary>
    public Option<ThingName> Name { get; }

    /// <summary>The labeller setting, if asked.</summary>
    public Option<Setting> Setting { get; }

    /// <summary>Refuses a finish that asks for nothing.</summary>
    public static Result<Finish, Reason> Create(Option<PaintColour> colour, Option<ThingName> name, Option<Setting> setting) =>
        colour.IsSome || name.IsSome || setting.IsSome
            ? Result<Finish, Reason>.Ok(new Finish(colour, name, setting))
            : Result<Finish, Reason>.Error(Reason.InvalidValue(nameof(Finish), "", "at-least-one"));
}
