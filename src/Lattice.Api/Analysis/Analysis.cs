namespace Lattice.Api;

/// <summary>A gas and how much of it.</summary>
/// <param name="Gas">The gas.</param>
/// <param name="Amount">How much.</param>
public sealed record GasAmount(GasKind Gas, Moles Amount);

/// <summary>A mix of gases at a temperature.</summary>
/// <param name="Gases">The gases.</param>
/// <param name="Temperature">The temperature.</param>
public sealed record GasMix(ValueList<GasAmount> Gases, Kelvin Temperature);

/// <summary>One effect on networks. A later minor may add cases; a consumer skips one it does not know.</summary>
public abstract record NetworkEffect
{
    private NetworkEffect() { }

    /// <summary>Two networks become one.</summary>
    /// <param name="A">One network.</param>
    /// <param name="B">The other.</param>
    public sealed record Joins(NetworkRef A, NetworkRef B) : NetworkEffect;

    /// <summary>A network falls apart into more than one.</summary>
    /// <param name="Network">The network.</param>
    public sealed record Splits(NetworkRef Network) : NetworkEffect;

    /// <summary>A network's power draw changes.</summary>
    /// <param name="Network">The network.</param>
    /// <param name="Before">The draw before.</param>
    /// <param name="After">The draw after.</param>
    /// <param name="Rating">The weakest cable's rating, if it is a cable network.</param>
    public sealed record Load(NetworkRef Network, Watts Before, Watts After, Option<Watts> Rating) : NetworkEffect;
}

/// <summary>One effect on rooms. A later minor may add cases; a consumer skips one it does not know.</summary>
public abstract record RoomEffect
{
    private RoomEffect() { }

    /// <summary>A room is opened to the outside or another room.</summary>
    /// <param name="Room">The room.</param>
    public sealed record Opens(RoomRef Room) : RoomEffect;

    /// <summary>A new room is sealed.</summary>
    /// <param name="Cells">Its size in large cells.</param>
    public sealed record Seals(int Cells) : RoomEffect;

    /// <summary>A room changes size.</summary>
    /// <param name="Room">The room.</param>
    /// <param name="Before">Large cells before.</param>
    /// <param name="After">Large cells after.</param>
    public sealed record Resizes(RoomRef Room, int Before, int After) : RoomEffect;
}

/// <summary>One effect on gas. A later minor may add cases; a consumer skips one it does not know.</summary>
public abstract record GasEffect
{
    private GasEffect() { }

    /// <summary>Gas is released from a pipe or device into a room or outside.</summary>
    /// <param name="At">Where.</param>
    /// <param name="Into">The room, or none for outside.</param>
    /// <param name="Gas">What is released.</param>
    public sealed record Releases(Cell At, Option<RoomRef> Into, GasMix Gas) : GasEffect;

    /// <summary>Painting adds pollutant to a room.</summary>
    /// <param name="Room">The room.</param>
    /// <param name="Amount">How much pollutant.</param>
    public sealed record Pollutes(RoomRef Room, Moles Amount) : GasEffect;
}

/// <summary>A fact a registered analyzer adds about a set of ghosts, shown in previews.</summary>
/// <param name="From">The analyzer.</param>
/// <param name="Code">What the fact is.</param>
/// <param name="Values">Its values, by name.</param>
public sealed record AnalyzerFact(RuleId From, ReasonCode Code, ValueMap<string, string> Values);

/// <summary>What a set of ghosts would do to networks, rooms and gas, plus registered analyzers' facts.</summary>
/// <param name="Networks">Network effects.</param>
/// <param name="Rooms">Room effects.</param>
/// <param name="Gas">Gas effects.</param>
/// <param name="Facts">Analyzers' facts.</param>
public sealed record AnalysisReport(
    ValueList<NetworkEffect> Networks,
    ValueList<RoomEffect> Rooms,
    ValueList<GasEffect> Gas,
    ValueList<AnalyzerFact> Facts);

/// <summary>
/// What ghosts would do to the world if built. Main thread; cached per revision. Lattice implements this; consumers
/// call it. A minor may add members: to fake it in a test, inherit <c>Lattice.Testing.Stubs.StubAnalysis</c>, which
/// gains every added member in the same release.
/// </summary>
public interface IAnalysis
{
    /// <summary>
    /// The effects of the ghosts a selector picks, pending until the worker analyzers have answered on their own
    /// thread (<see cref="IGhostEvents.AnswersReady"/> is raised then); refused for a selector the board refuses or
    /// an unknown selector kind. The refusal is outside the answer, as in <see cref="IValidation.Check"/>, because the
    /// selection is known at once; <see cref="IPreview.Of"/> nests the other way because a command's refusals are known
    /// only after it is expanded. A main-thread analyzer is called inside this call: its exception leaves it. Cost:
    /// constant when kept for the revision; otherwise linear in the ghosts and the networks and rooms they touch.
    /// </summary>
    Result<Answer<AnalysisReport>, Reason> Of(Selector which);
}
