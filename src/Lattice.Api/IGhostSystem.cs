using System;

namespace Lattice.Api;

/// <summary>
/// Lattice for one running world: every service in one place. Published through <see cref="LatticeSystem"/> when the
/// world starts; stale once it ends. Main thread unless a member says otherwise. Lattice implements this; consumers
/// call it. A minor may add members: to fake it in a test, inherit <c>Lattice.Testing.Stubs.StubGhostSystem</c>, which
/// gains every added member in the same release.
/// </summary>
public interface IGhostSystem
{
    /// <summary>
    /// The ghosts, as the local viewer may see them: the local player on a listen host, in single player and on a
    /// client; Lattice's own actor on a dedicated server, which has no local player (it sees everything; an agent reads
    /// through <see cref="IGhostBoard.As"/> with its own actor).
    /// </summary>
    IGhostBoard Board { get; }

    /// <summary>The built world, for selecting structures.</summary>
    IWorldQuery World { get; }

    /// <summary>Plan-level answers.</summary>
    IPlans Plans { get; }

    /// <summary>Changing the board.</summary>
    ICommands Commands { get; }

    /// <summary>What a change would do.</summary>
    IPreview Preview { get; }

    /// <summary>Findings per ghost.</summary>
    IValidation Validation { get; }

    /// <summary>Steps per ghost.</summary>
    ISteps Steps { get; }

    /// <summary>Bills for sets of ghosts.</summary>
    IBills Bills { get; }

    /// <summary>What a built structure can become in place.</summary>
    ISwaps Swaps { get; }

    /// <summary>Effects on networks, rooms and gas.</summary>
    IAnalysis Analysis { get; }

    /// <summary>Claims and status.</summary>
    IExecutors Executors { get; }

    /// <summary>Rules, analyzers, attachments, catalogue additions, expanders, access policy.</summary>
    IRegistries Registries { get; }

    /// <summary>Stories and change-log export.</summary>
    IInspector Inspector { get; }

    /// <summary>What happened on the board.</summary>
    IGhostEvents Events { get; }

    /// <summary>Long drawing work in frame time slices.</summary>
    ISlices Slices { get; }

    /// <summary>How ghosts look to the local viewer: style layers, sets, drafts, the clip, picking. Added in contract 0.2.</summary>
    IGhostStyles Styles { get; }

    /// <summary>The pieces and kits the game holds, by kind. Added in contract 0.3.</summary>
    IPieceCatalogue Catalogue { get; }

    /// <summary>The running contract's version.</summary>
    ApiVersion Version { get; }

    /// <summary>Whether this Lattice knows an intent kind. Ask before using a kind newer than the one you built against.</summary>
    bool Has(IntentKind kind);

    /// <summary>Lattice's modules, each on or off with the reason (a module turns itself off when its game members are missing). Fixed for the world.</summary>
    ValueList<ModuleStatus> Modules { get; }
}

/// <summary>
/// The way into Lattice. <see cref="Current"/> is the one mutable static of the library: set when a world starts,
/// cleared when it ends. Ask it when you need it, not at load, so load order between mods does not matter.
/// Main thread.
/// </summary>
public static class LatticeSystem
{
    private static IGhostSystem? _current;

    /// <summary>The running world's Lattice, or none outside a world.</summary>
    public static Option<IGhostSystem> Current => _current is null ? Option<IGhostSystem>.None : Option<IGhostSystem>.Some(_current);

    /// <summary>
    /// Raised on the main thread when a world starts, with its Lattice, after <see cref="Current"/> is set: the place
    /// to register rules, analyzers, policies and executors for that world. Check <see cref="Current"/> first, then
    /// subscribe: a mod that subscribes after a world has started never hears that world's Started. Subscribers are
    /// called in subscription order; a handler's failure is its own mod's to handle.
    /// </summary>
    public static event Action<IGhostSystem>? Started;

    internal static void Publish(IGhostSystem system)
    {
        _current = system ?? throw new ArgumentNullException(nameof(system));
        Started?.Invoke(system);
    }

    internal static void Clear() => _current = null;
}

/// <summary>A contract version. Majors break, minors only add.</summary>
/// <param name="Major">The major version.</param>
/// <param name="Minor">The minor version.</param>
public readonly record struct ApiVersion(int Major, int Minor)
{
    /// <summary>The major of this contract. A constant, so a consumer's build keeps the value it compiled against.</summary>
    public const int ContractMajor = 0;

    /// <summary>The minor of this contract. A constant, so a consumer's build keeps the value it compiled against.</summary>
    public const int ContractMinor = 22;

    /// <summary>The running contract's version.</summary>
    public static ApiVersion Current => new(ContractMajor, ContractMinor);
}

/// <summary>Whether a Lattice module runs in this world.</summary>
public abstract record ModuleState
{
    private ModuleState() { }

    /// <summary>It runs.</summary>
    public sealed record On : ModuleState;

    /// <summary>It is off, and why (game members missing, a module it needs is off).</summary>
    /// <param name="Why">The reason.</param>
    public sealed record Off(Reason Why) : ModuleState;
}

/// <summary>One Lattice module and whether it runs.</summary>
/// <param name="Name">The module.</param>
/// <param name="State">On, or off with the reason.</param>
public sealed record ModuleStatus(ModuleName Name, ModuleState State);
